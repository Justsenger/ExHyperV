using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>操作结束时已确认的虚拟机注册状态；未知不等于仍在册或已经注销。</summary>
public enum VmRegistrationState
{
    /// <summary>无法确认当前是否仍有注册的虚拟机。</summary>
    Unknown,
    /// <summary>已确认虚拟机仍注册在主机上。</summary>
    Registered,
    /// <summary>已确认虚拟机没有注册，或操作尚未提交创建。</summary>
    Unregistered
}
