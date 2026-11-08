namespace ExHyperV.Services;

/// <summary>控制台连接当前处于连接中、已连接还是断开阶段。</summary>
/// <remarks>新对象的初始值和完成断开后的值都是 Disconnected。
/// 每个对象只能启动一次；重新连接需要创建新对象。</remarks>
public enum ConsoleConnectionState
{
    /// <summary>尚未启动，或已断开并完成底层清理；断开后的对象不能重启。</summary>
    Disconnected,
    /// <summary>底层已报告连接建立，不等同于用户登录已完成。</summary>
    Connected,
    /// <summary>已开始连接，尚未报告连接建立。</summary>
    Connecting,
    /// <summary>已请求断开，底层仍在退出。</summary>
    Disconnecting
}
