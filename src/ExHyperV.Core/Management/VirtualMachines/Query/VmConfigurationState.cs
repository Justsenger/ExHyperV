using ExHyperV.Tools;
using ExHyperV.Models;
using System.Management;

namespace ExHyperV.Services;

/// <summary>一次设置操作需要的当前 VM 状态；无后台任务或进程级会话。</summary>
public sealed record VmConfigurationState
{
    /// <summary>虚拟机的 GUID 文本。</summary>
    public string Id { get; }
    /// <summary>虚拟机代数，1 或 2；读取时未识别配置子类型则为 0。</summary>
    public int Generation { get; }
    /// <summary>当前运行状态，不包含后端状态码。</summary>
    public VmState State { get; }
    internal ushort StateCode { get; }

    /// <summary>创建配置校验使用的状态快照；不读取或修改虚拟机。</summary>
    /// <param name="id">虚拟机的稳定 GUID 文本。</param>
    /// <param name="generation">虚拟机代数。</param>
    /// <param name="state">虚拟机运行状态。</param>
    public VmConfigurationState(string id, int generation, VmState state)
        : this(id, generation, VmMapper.ToStateCode(state)) { }

    internal VmConfigurationState(string id, int generation, ushort stateCode)
    {
        Id = id;
        Generation = generation;
        StateCode = stateCode;
        State = VmMapper.MapState(stateCode);
    }

    /// <summary>当前是否处于运行或电源切换等活动状态。</summary>
    public bool IsRunning => VmMapper.IsActiveState(StateCode);
    /// <summary>Hyper-V 状态码为 3，表示完全关机。</summary>
    public bool IsPoweredOff => StateCode == 3;

    /// <summary>读取用于配置校验的虚拟机标识、代数与当前 Hyper-V 状态。</summary>
    /// <param name="vmName">目标虚拟机的显示名称。</param>
    /// <returns>配置状态快照；虚拟机不存在或读取失败时返回错误。</returns>
    public static async Task<ApiResponse<VmConfigurationState>> ReadAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return ApiResponse<VmConfigurationState>.Fail(identity.Error,
            identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind);
        return await ReadAsync(identity.Data);
    }

    /// <summary>按稳定身份读取虚拟机配置校验状态。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>当前状态；查询失败通过结果报告。</returns>
    public static Task<ApiResponse<VmConfigurationState>> ReadAsync(Guid vmId)
        => WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            Read);

    internal static VmConfigurationState Read(ManagementObject vm)
    {
        using var settings = WmiApi.GetVmSettings(vm);
        string subtype = settings?["VirtualSystemSubType"]?.ToString() ?? "";
        int generation = subtype.EndsWith(":1") ? 1 : subtype.EndsWith(":2") ? 2 : 0;
        return new(vm["Name"]?.ToString() ?? "", generation, Convert.ToUInt16(vm["EnabledState"]));
    }
}
