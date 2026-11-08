using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>本次 PCIe 修改的目标类型。</summary>
public enum VmPcieChangeKind {
        /// <summary>永久启用 PCIe 模拟。</summary>
        EnableEmulation,
        /// <summary>修改 PCIe 拓扑；按需先启用模拟。</summary>
        Topology,
        /// <summary>修改单个 PCIe 设备的来宾模式；按需先启用模拟。</summary>
        DeviceMode }
/// <summary>PCIe 一次修改的取消标记和底层执行响应。</summary>
/// <param name="Cancelled">是否因为用户未确认永久开启模拟而取消。</param>
/// <param name="Response">底层操作的响应；取消状态还需检查 Cancelled。</param>
public sealed record VmPcieChangeResult(bool Cancelled, ApiResponse Response)
{
    /// <summary>未取消且底层响应成功。</summary>
    public bool Success => !Cancelled && Response.Success;
    /// <summary>底层响应的错误文本。</summary>
    public string Error => Response.Error;
}

/// <summary>保存一次 PCIe 修改的输入、永久开启模拟确认要求和执行结果。</summary>
/// <remarks>ApplyAsync 的第一次调用确定执行任务；同一实例不能靠第二次批准来重试首次取消，需要创建新请求。</remarks>
public sealed class VmPcieChangeSession
{
    private readonly VmPcieChangeKind _kind;
    private readonly Guid _vmId;
    private readonly ushort _topology;
    private readonly VmPcieDeviceSetting? _device;
    private readonly VmPcieGuestMode _mode;
    private readonly VmPcieChangeBackend _backend;
    private readonly object _gate = new();
    private Task<VmPcieChangeResult>? _execution;

    /// <summary>本次修改是否需要用户先确认永久启用 PCIe 模拟；执行前重新读取配置时会更新。</summary>
    public bool RequiresConfirmation { get; private set; }
    // Retains a successful first step even when the subsequent device write fails.
    /// <summary>会话已知的模拟启用状态；后续设备修改失败时仍可能为 true。</summary>
    public bool EmulationEnabled { get; private set; }

    /// <summary>建立一次 PCIe 修改请求，不立即修改主机或虚拟机。</summary>
    /// <param name="kind">本次请求修改模拟开关、拓扑或单个设备模式。</param>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="emulationEnabled">会话建立时已知的 PCIe 模拟状态。</param>
    /// <param name="topology">Hyper-V 原始 PCIe 拓扑编码；当前 Core 未提供有效值目录，提交时由提供者校验。</param>
    /// <param name="device">GetSettingsAsync 返回的目标 PCIe 设备；非设备模式修改可省略。</param>
    /// <param name="deviceMode">待设置的 PCIe 设备来宾模式。</param>
    public VmPcieChangeSession(VmPcieChangeKind kind, Guid vmId, bool emulationEnabled,
        ushort topology, VmPcieDeviceSetting? device = null, VmPcieGuestMode deviceMode = VmPcieGuestMode.Paravirtualized)
        : this(kind, vmId, emulationEnabled, topology, device, deviceMode, VmPcieChangeBackend.System) { }

    internal VmPcieChangeSession(VmPcieChangeKind kind, Guid vmId, bool emulationEnabled,
        ushort topology, VmPcieDeviceSetting? device, VmPcieGuestMode deviceMode, VmPcieChangeBackend backend)
    {
        _kind = kind; _vmId = vmId; _topology = topology;
        _device = device; _mode = deviceMode; _backend = backend;
        EmulationEnabled = emulationEnabled;
        RequiresConfirmation = NeedsEmulationConfirmation(emulationEnabled);
    }

    private bool NeedsEmulationConfirmation(bool emulationEnabled) => !emulationEnabled &&
        (_kind == VmPcieChangeKind.EnableEmulation ||
         (_kind == VmPcieChangeKind.Topology && _topology == 1) ||
         (_kind == VmPcieChangeKind.DeviceMode && _mode == VmPcieGuestMode.Emulated));

    /// <summary>在需要时取得永久开启 PCIe 模拟的确认，然后执行本次请求；同一实例只执行一次。</summary>
    /// <param name="permanentEmulationConfirmed">是否已得到永久启用 PCIe 模拟的用户确认；未确认且需要模拟时返回取消。</param>
    /// <returns>首次执行的结果；后续调用返回同一任务，包括首次拒绝后再次批准的情况。写入前核对最新归属与能力；后续步骤失败不撤销已开启的模拟。</returns>
    public Task<VmPcieChangeResult> ApplyAsync(bool permanentEmulationConfirmed = false)
    {
        lock (_gate) return _execution ??= ApplyCoreAsync(permanentEmulationConfirmed);
    }

    private async Task<VmPcieChangeResult> ApplyCoreAsync(bool confirmed)
    {
        if (_vmId == Guid.Empty || !Enum.IsDefined(_kind) || (_kind == VmPcieChangeKind.DeviceMode && !Enum.IsDefined(_mode)))
            return new(false, ApiResponse.Fail("无效的 PCIe 修改请求。", kind: ApiErrorKind.InvalidArgument));
        if (RequiresConfirmation && !confirmed) return new(true, ApiResponse.Fail("用户未确认永久启用 PCIe 模拟。", kind: ApiErrorKind.Cancelled));
        if (_kind == VmPcieChangeKind.DeviceMode && string.IsNullOrEmpty(_device?.WmiInstanceId))
            return new(false, ApiResponse.Fail("请使用查询返回的 PCIe 设备。", kind: ApiErrorKind.InvalidArgument));

        var current = await _backend.ReadSettings(_vmId);
        if (!current.Success)
            return new(false, ApiResponse.Fail(current.Error, current.Code, current.ErrorSource, current.Exception, current.ErrorKind));
        if (!current.HasData)
            return new(false, ApiResponse.Fail(Properties.Resources.VmPcie_SettingsNotFound, kind: ApiErrorKind.NotFound));

        VmPcieDeviceSetting? target = null;
        if (_kind == VmPcieChangeKind.DeviceMode)
        {
            target = current.Data!.Devices.FirstOrDefault(item =>
                string.Equals(item.WmiInstanceId, _device!.WmiInstanceId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new(false, ApiResponse.Fail("设备已不属于此虚拟机，请重新查询。", kind: ApiErrorKind.Conflict));
            if (!target.GuestModeAvailable)
                return new(false, ApiResponse.Fail("此设备不支持修改来宾模式。", kind: ApiErrorKind.Unsupported));
        }

        EmulationEnabled = current.Data!.EmulationEnabled;
        RequiresConfirmation = NeedsEmulationConfirmation(EmulationEnabled);
        if (RequiresConfirmation && !confirmed) return new(true, ApiResponse.Fail("用户未确认永久启用 PCIe 模拟。", kind: ApiErrorKind.Cancelled));
        if (_kind != VmPcieChangeKind.DeviceMode || RequiresConfirmation)
        {
            if (!current.Data.SystemSettingsAvailable)
                return new(false, ApiResponse.Fail("此虚拟机不支持 PCIe 系统设置。", kind: ApiErrorKind.Unsupported));
            var system = await _backend.SetSystem(_vmId, EmulationEnabled || RequiresConfirmation, _topology);
            if (!system.Success) return new(false, system);
            if (RequiresConfirmation) EmulationEnabled = true;
        }
        return new(false, _kind == VmPcieChangeKind.DeviceMode
            ? await _backend.SetDevice(_vmId, target!, _mode) : ApiResponse.Ok());
    }
}

internal sealed record VmPcieChangeBackend(
    Func<Guid, Task<ApiResponse<VmPcieSettings>>> ReadSettings,
    Func<Guid, bool, ushort, Task<ApiResponse>> SetSystem,
    Func<Guid, VmPcieDeviceSetting, VmPcieGuestMode, Task<ApiResponse>> SetDevice)
{
    internal static VmPcieChangeBackend System { get; } = new(
        VmPcieService.GetSettingsAsync, VmPcieService.SetSystemSettingsAsync, VmPcieService.SetDeviceModeAsync);
}
