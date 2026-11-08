using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal sealed record RegistrationSnapshot(string Revision, string StoragePath, InspectionConfiguration Configuration);
internal sealed record SaveRegistrationRequest(string Revision, string ResourceRunId,
    string? TemplateVmId, string[] PcieIds, string? PhysicalDiskId = null, string? OpticalDriveId = null);

internal sealed class ResourceRegistration(string path)
{
    internal RegistrationSnapshot Read()
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(Encoding.UTF8.Preamble)) json = json[Encoding.UTF8.Preamble.Length..];
        var configuration = bytes.Length == 0 ? new InspectionConfiguration()
            : JsonSerializer.Deserialize<InspectionConfiguration>(json, InspectionJson.Options)
                ?? throw new InvalidDataException("资源登记文件为空。");
        return new(Convert.ToHexString(SHA256.HashData(bytes)), path, configuration.WithDefaults());
    }

    internal RegistrationSnapshot Save(SaveRegistrationRequest request, InspectionReport report)
    {
        using var mutex = new Mutex(false, "Local\\ExHyperVTestResources-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("另一处正在保存资源，请稍后重试。");
            var current = Read();
            if (request.Revision != current.Revision)
                throw new InvalidOperationException("资源设置已在另一处更新，请重新载入后保存。");
            if (report.Running || report.Finished is null || report.Error is not null || report.RunId != request.ResourceRunId)
                throw new InvalidOperationException("请等本轮资源检查完成后保存。");
            var old = current.Configuration;
            string? Select(string? value, string? previous, string source, string label)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                if (!Guid.TryParse(value, out var id)) throw new ArgumentException($"{label}标识无效，请从列表选择。");
                var normalized = id.ToString("D");
                if (!string.Equals(normalized, previous, StringComparison.OrdinalIgnoreCase)
                    && !(Choices(report, source).Any(c => string.Equals(c.Id, normalized, StringComparison.OrdinalIgnoreCase))))
                    throw new ArgumentException($"{label}已离开本轮清单，请重新检查后选择。");
                return normalized;
            }
            var template = Select(request.TemplateVmId, old.TemplateVmId, "vms", "测试模板");
            RegisteredDevice? SelectDevice(string? id, RegisteredDevice? saved, string source, string label)
            {
                if (string.IsNullOrWhiteSpace(id)) return null;
                var candidate = Choices(report, source).SingleOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (candidate is null || candidate.UnavailableReason is not null || string.IsNullOrWhiteSpace(candidate.Fingerprint))
                    throw new ArgumentException($"{label}当前无法选择，请重新检查。");
                if (saved?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true && saved.Fingerprint != candidate.Fingerprint)
                    throw new ArgumentException($"{label}身份发生变化，请清除原选择、保存后重新指定。");
                return new(candidate.Id, candidate.Fingerprint, candidate.Name);
            }
            var disk = SelectDevice(request.PhysicalDiskId, old.PhysicalDisk, "disks", "物理磁盘");
            var optical = SelectDevice(request.OpticalDriveId, old.OpticalDrive, "optical", "光驱");
            if (request.PcieIds is null || request.PcieIds.Length > 32)
                throw new ArgumentException("请检查设备数量。");
            var devices = request.PcieIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
            {
                var saved = old.PcieDevices.SingleOrDefault(p => p.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase));
                var candidate = Choices(report, "pcie").SingleOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (saved is not null) return saved; // A changed identity remains blocked until explicitly reselected.
                if (candidate is null || string.IsNullOrEmpty(candidate.LocationPath))
                    throw new ArgumentException("选定 PCIe 设备已离开本轮清单，请重新检查。");
                return new SelectedPcieDevice(candidate.Id, candidate.LocationPath);
            }).ToArray();
            var directory = NormalizeDirectory(old.WorkDirectory);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                VerifyDirectory(directory);
            }
            var next = old with { WorkDirectory = directory, TemplateVmId = template, GpuTemplateVmId = template,
                PcieDevices = devices, PhysicalDisk = disk, OpticalDrive = optical };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, InspectionJson.Options), new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Read();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    internal static ResourceChoice[] Choices(InspectionReport report, string id) =>
        report.Items.SingleOrDefault(i => i.Check.Id == id)?.Result.Choices ?? [];

    internal static string? NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 1024 || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException(@"请填写本机完整目录，例如 D:\ExHyperV.Tests。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请在盘符下指定专用文件夹。");
        return full;
    }

    internal static void VerifyDirectory(string directory)
    {
        var probe = Path.Combine(directory, ".exhyperv-write-check-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.DeleteOnClose);
        stream.WriteByte(0x5A);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        if (stream.ReadByte() != 0x5A) throw new IOException("目录写入后的读取校验失败。");
    }
}
