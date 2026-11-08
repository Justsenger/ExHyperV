namespace ExHyperV.Services;

/// <summary>从 ZIP 准备预览及导入时使用的密码、解压额度和预览工作目录。</summary>
/// <remarks>在 PreparePreviewsAsync 提交一次，之后所有单机会话沿用同一组选项和累计额度。null 额度表示不设置该项上限，保持旧调用行为；Core 不替调用方规定虚拟磁盘容量。额度限制解压数据，不等同于整个进程的内存或耗时上限。选项不包含虚拟机名称、GUID 或目标目录，那些由 VmImportOptions 指定。</remarks>
public sealed class VmImportArchiveOptions
{
    /// <summary>读取加密条目使用的 UTF-8 密码；默认 null。缺少密码时返回 PasswordRequired；Core 不弹窗、不保存密码。一个归档中的加密条目应使用同一密码。</summary>
    /// <remarks>部分 ZIP 工具使用系统代码页编码非 ASCII 密码，与本接口不兼容；ASCII 密码不受这类编码差异影响。</remarks>
    public string? Password { get; init; }
    /// <summary>归档允许包含的条目总数，包括目录；null 不限制，0 仅允许空归档。准备预览及正式解压前都会检查。</summary>
    public int? MaxEntries { get; init; }
    /// <summary>单个条目的最大未压缩字节数；null 不限制。检查目录记录中的声明大小，并在读取时再次限制实际字节数。</summary>
    public long? MaxEntryBytes { get; init; }
    /// <summary>本次导入批次累计允许解出的字节数；null 不限制。包括配置预览、磁盘头读取和正式解压；重复读取也计入，并行单机会话共用额度。</summary>
    /// <remarks>准备时还检查整个归档声明的未压缩总量。预览与导入会重复读取部分内容，因此额度应大于归档声明总量；失败读取已消耗的额度不返还。</remarks>
    public long? MaxTotalBytes { get; init; }
    /// <summary>预览工作目录的父目录，必须是已存在的绝对路径；Core 在其中创建并清理独立子目录，不删除父目录。null 使用系统临时目录。</summary>
    /// <remarks>正式导入的配置和磁盘暂存目录仍靠近各自目标目录，以便校验后移动提交，不受此选项影响。</remarks>
    public string? TemporaryDirectory { get; init; }
}

/// <summary>ZIP 读取失败中需要调用方区分的原因。</summary>
public enum VmImportArchiveError
{
    /// <summary>遇到加密条目，但调用方尚未提供密码。</summary>
    PasswordRequired,
    /// <summary>密码不正确或加密数据已损坏；不能仅凭此错误区分二者。</summary>
    DecryptionFailed,
    /// <summary>条目采用当前不支持的加密方式。</summary>
    UnsupportedEncryption,
    /// <summary>条目数量、单文件大小或累计解压量超过调用方设置。</summary>
    LimitExceeded
}

/// <summary>可序列化的 ZIP 失败详情；用于导入结果，完整错误说明仍从结果的 Failure 读取。</summary>
/// <param name="Reason">密码、加密能力或额度方面的失败原因。</param>
/// <param name="EntryName">涉及的归档内相对路径；归档整体失败时可以为 null。</param>
/// <param name="LimitName">触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。</param>
/// <param name="Limit">调用方设置的上限；非额度失败为 null。</param>
/// <param name="Actual">触发失败的声明值或已读取量；非额度失败为 null。</param>
public sealed record VmImportArchiveFailure(VmImportArchiveError Reason, string? EntryName,
    string? LimitName, long? Limit, long? Actual);

/// <summary>ZIP 读取失败的异常附件，可从准备结果的 Result.Exception 取得；正式导入通过 ArchiveFailure 返回可序列化的详情。</summary>
public sealed class VmImportArchiveException : IOException
{
    internal VmImportArchiveException(VmImportArchiveError reason, string message,
        string? entryName = null, string? limitName = null, long? limit = null, long? actual = null,
        Exception? cause = null) : base(message, cause)
    { Reason = reason; EntryName = entryName; LimitName = limitName; Limit = limit; Actual = actual; }
    /// <summary>密码、格式能力或额度方面的失败原因。</summary>
    public VmImportArchiveError Reason { get; }
    /// <summary>涉及的归档内相对路径；归档整体限制失败时可以为 null。</summary>
    public string? EntryName { get; }
    /// <summary>触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。</summary>
    public string? LimitName { get; }
    /// <summary>调用方设置的上限；非额度失败为 null。</summary>
    public long? Limit { get; }
    /// <summary>触发失败的声明值或已读取量；非额度失败为 null。</summary>
    public long? Actual { get; }
    internal VmImportArchiveFailure ToFailure() => new(Reason, EntryName, LimitName, Limit, Actual);
}
