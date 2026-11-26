namespace ExHyperV.Services;

/// <summary>本地登记的固定动作。Fixed extension action identifiers.</summary>
public enum ConsoleExtensionActionId
{
    /// <summary>未知动作。</summary>
    Unknown = 0,
    /// <summary>激活宿主组件。</summary>
    ActivateHost = 1,
    /// <summary>维护宿主组件。</summary>
    MaintainHost = 2,
    /// <summary>部署来宾组件。</summary>
    DeployGuestComponent = 3,
    /// <summary>重试目标连接。</summary>
    RetryConnection = 4
}
