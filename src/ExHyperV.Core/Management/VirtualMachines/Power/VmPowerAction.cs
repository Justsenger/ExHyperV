namespace ExHyperV.Models;

/// <summary>虚拟机支持的电源动作；强制断电必须由调用方明确选择。</summary>
public enum VmPowerAction
{
    /// <summary>启动虚拟机。</summary>
    Start,
    /// <summary>请求来宾正常关机。</summary>
    Stop,
    /// <summary>强制关闭虚拟机；必要时可终止对应的工作进程。</summary>
    TurnOff,
    /// <summary>请求来宾重启。</summary>
    Restart,
    /// <summary>保存虚拟机运行状态。</summary>
    Save,
    /// <summary>暂停虚拟机运行。</summary>
    Suspend
}
