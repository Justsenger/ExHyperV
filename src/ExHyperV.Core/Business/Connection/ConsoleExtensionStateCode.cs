namespace ExHyperV.Services;

/// <summary>四段探测结果的通用解释。Known extension stage codes.</summary>
public enum ConsoleExtensionStateCode
{
    /// <summary>状态未知。</summary>
    Unknown = 0,
    /// <summary>尚未证实。</summary>
    Unverified = 1,
    /// <summary>服务不可达。</summary>
    Unreachable = 2,
    /// <summary>访问被拒绝。</summary>
    AccessDenied = 3,
    /// <summary>组件缺失。</summary>
    Missing = 4,
    /// <summary>版本不兼容。</summary>
    Incompatible = 5,
    /// <summary>已就绪。</summary>
    Ready = 6
}
