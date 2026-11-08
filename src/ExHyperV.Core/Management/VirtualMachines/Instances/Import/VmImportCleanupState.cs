using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>导入会话工作资源的清理状态，与虚拟机是否已经导入分别判断。</summary>
public enum VmImportCleanupState
{
    /// <summary>资源仍由会话持有，调用者使用完成后应异步释放。</summary>
    Pending,
    /// <summary>应清理的工作资源已清理，不包含已成功导入的虚拟机文件。</summary>
    Completed,
    /// <summary>原生操作或计划系统移除结果未确认，保留相关资源。</summary>
    Deferred,
    /// <summary>已允许清理，但部分文件或目录未能删除。</summary>
    Failed
}
