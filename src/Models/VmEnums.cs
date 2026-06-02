namespace ExHyperV.Models
{
    /// <summary>SMT（同时多线程）模式：继承宿主 / 单线程 / 多线程。</summary>
    public enum SmtMode { Inherit, SingleThread, MultiThread }

    /// <summary>CPU 核心类型：未知 / 性能核 / 能效核（用于异构 CPU 显示）。</summary>
    public enum CoreType { Unknown, Performance, Efficient }
}
