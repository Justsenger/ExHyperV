using System.Security.Cryptography;
using System.Text;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// 原生路径只保留在 Core 内；公开调用方只回传与 VM、挂接和介质配置绑定的摘要。
internal sealed record VmStorageResourceIdentity(string InstanceId, string ObjectPath, int ResourceType,
    string Parent, string AddressOnParent, string ResourceSubType, string[] HostResources)
{
    internal VmStorageResourceIdentity Snapshot() => this with { HostResources = HostResources.ToArray() };

    internal bool Matches(VmStorageResourceIdentity other) =>
        Same(InstanceId, other.InstanceId) && ResourceType == other.ResourceType &&
        Same(Parent, other.Parent) && Same(AddressOnParent, other.AddressOnParent) &&
        Same(ResourceSubType, other.ResourceSubType) &&
        HostResources.SequenceEqual(other.HostResources, StringComparer.OrdinalIgnoreCase);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

internal sealed class VmStorageSelection
{
    internal Guid VmId { get; }
    internal VmStorageResourceIdentity Controller { get; }
    internal VmStorageResourceIdentity Slot { get; }
    internal VmStorageResourceIdentity[] Media { get; }
    internal HostDiskSnapshot? PhysicalDisk { get; }
    internal string Token { get; }
    internal string DeviceToken { get; }

    internal VmStorageSelection(Guid vmId, VmStorageResourceIdentity controller, VmStorageResourceIdentity slot,
        IEnumerable<VmStorageResourceIdentity> media, HostDiskSnapshot? physicalDisk = null)
    {
        VmId = vmId;
        Controller = controller.Snapshot();
        Slot = slot.Snapshot();
        Media = media.OrderBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Snapshot()).ToArray();
        PhysicalDisk = physicalDisk;
        Token = CreateToken();
        DeviceToken = CreateDeviceToken();
    }

    internal bool IsValid => VmId != Guid.Empty && Valid(Controller) && Valid(Slot) &&
        Media.Length <= 1 && Media.All(Valid);

    private static bool Valid(VmStorageResourceIdentity item) =>
        !string.IsNullOrWhiteSpace(item.InstanceId) && !string.IsNullOrWhiteSpace(item.ObjectPath);

    internal bool SameSlot(VmStorageSelection other) => VmId == other.VmId &&
        string.Equals(Slot.InstanceId, other.Slot.InstanceId, StringComparison.OrdinalIgnoreCase);

    internal bool Matches(VmStorageSelection other, bool mediaRemoved) => SameSlot(other) &&
        Controller.Matches(other.Controller) && Slot.Matches(other.Slot) &&
        (mediaRemoved ? other.Media.Length == 0 : Token == other.Token);

    private string CreateDeviceToken()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(VmId.ToByteArray());
            foreach (var item in new[] { Controller, Slot })
            {
                writer.Write(item.InstanceId.ToUpperInvariant());
                writer.Write(item.ResourceType);
                writer.Write(item.Parent.ToUpperInvariant());
                writer.Write(item.AddressOnParent.ToUpperInvariant());
                writer.Write(item.ResourceSubType.ToUpperInvariant());
            }
        }
        return "device.v1." + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private string CreateToken()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(VmId.ToByteArray());
            Write(Controller); Write(Slot);
            writer.Write(Media.Length);
            foreach (var item in Media) Write(item);
            writer.Write(PhysicalDisk != null);
            if (PhysicalDisk is { } disk)
            {
                writer.Write(disk.Number); Text(disk.UniqueId); Text(disk.SerialNumber); writer.Write(disk.SizeBytes);
            }

            void Text(string value) => writer.Write(value.ToUpperInvariant());
            void Write(VmStorageResourceIdentity item)
            {
                Text(item.InstanceId); writer.Write(item.ResourceType);
                Text(item.Parent); Text(item.AddressOnParent); Text(item.ResourceSubType);
                writer.Write(item.HostResources.Length);
                foreach (string resource in item.HostResources) Text(resource);
            }
        }
        return "v1." + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

internal static class VmStorageRemoval
{
    internal static ApiResponse<VmStorageAttachment> SelectDevice(Guid vmId, string token,
        ApiResponse<List<VmStorageAttachment>> inventory)
    {
        if (!inventory.HasData)
            return ApiResponse<VmStorageAttachment>.Fail(inventory.Error, inventory.Code, inventory.ErrorSource,
                inventory.Exception, inventory.ErrorKind);
        var matches = inventory.Data.Where(item => item.DeviceToken == token &&
            item.Selection is { IsValid: true } selection && selection.VmId == vmId).ToArray();
        return matches.Length == 1 ? ApiResponse<VmStorageAttachment>.Ok(matches[0]) :
            ApiResponse<VmStorageAttachment>.Fail("所选驱动器身份已变化或不存在，请重新查询。", kind: ApiErrorKind.Conflict);
    }

    internal static ApiResponse Failure<T>(ApiResponse<T> response) => ApiResponse.Fail(
        response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);

    internal static ApiResponse<VmStorageAttachment> Select(Guid vmId, string token,
        ApiResponse<List<VmStorageAttachment>> inventory)
    {
        if (!inventory.HasData)
            return ApiResponse<VmStorageAttachment>.Fail(inventory.Error, inventory.Code, inventory.ErrorSource,
                inventory.Exception, inventory.ErrorKind);
        var matches = inventory.Data!.Where(item => item.SelectionToken == token &&
            item.Selection is { IsValid: true } selection && selection.VmId == vmId).ToArray();
        return matches.Length == 1 ? ApiResponse<VmStorageAttachment>.Ok(matches[0]) :
            ApiResponse<VmStorageAttachment>.Fail("所选存储挂接已变化或已不存在，请重新查询。", kind: ApiErrorKind.Conflict);
    }

    // WMI 没有原子比较并删除接口。这里只按原对象身份及配置复核，绝不退回槽位定位；
    // 无法识别外部在最后一次核对后替换对象或按完全相同配置重建的 ABA。
    internal static async Task<ApiResponse> ExecuteAsync(VmStorageSelection expected,
        Func<Task<ApiResponse<List<VmStorageAttachment>>>> read,
        Func<string, Task<ApiResponse>> remove)
    {
        bool mediaRemoved = false;
        try
        {
            if (!expected.IsValid)
                return ApiResponse.Fail("存储挂接身份不完整，请重新查询。", kind: ApiErrorKind.InvalidArgument);
            var before = await CurrentAsync();
            if (!before.HasData) return Failure(before);
            if (expected.Media.Length != 0)
            {
                var result = await remove(before.Data!.Media[0].ObjectPath);
                if (!result.Success) return result;
                mediaRemoved = true;
            }

            // 介质卸除后再查原槽位；若出现新介质，保留槽位和新介质。
            var slot = await CurrentAsync();
            if (!slot.HasData) return Failure(slot);
            var removed = await remove(slot.Data!.Slot.ObjectPath);
            if (!removed.Success) return mediaRemoved ? Partial(removed) : removed;
            var after = await read();
            if (!after.HasData || after.Data!.Any(item => item.Selection is { } value && expected.SameSlot(value)))
                return ApiResponse.Fail("移除请求已提交，但尚未确认原存储挂接已消失。" + after.Error,
                    kind: ApiErrorKind.CleanupRequired);
            return ApiResponse.Ok();
        }
        catch (Exception error)
        {
            return ApiResponse.Fail(error.Message, exception: error,
                kind: mediaRemoved ? ApiErrorKind.CleanupRequired : ApiErrorKind.OperationFailed);
        }

        async Task<ApiResponse<VmStorageSelection>> CurrentAsync()
        {
            var inventory = await read();
            if (!inventory.HasData)
                return ApiResponse<VmStorageSelection>.Fail(inventory.Error, inventory.Code, inventory.ErrorSource,
                    inventory.Exception, inventory.ErrorKind);
            var current = inventory.Data!.Select(item => item.Selection).Where(item => item != null && expected.SameSlot(item)).ToArray();
            if (current.Length != 1 || !expected.Matches(current[0]!, mediaRemoved))
                return ApiResponse<VmStorageSelection>.Fail("原存储挂接或介质已变化，未移除替代对象。", kind: ApiErrorKind.Conflict);
            return ApiResponse<VmStorageSelection>.Ok(current[0]!);
        }

        ApiResponse Failure<T>(ApiResponse<T> response) => mediaRemoved
            ? Partial(VmStorageRemoval.Failure(response)) : VmStorageRemoval.Failure(response);
        static ApiResponse Partial(ApiResponse response) => ApiResponse.Fail(
            "原介质已移除，但槽位清理未完成：" + response.Error,
            response.Code, response.ErrorSource, response.Exception,
            response.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
    }
}
