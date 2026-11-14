namespace ExHyperV.Services;

/// <summary>导出 ZIP 的压缩级别；压缩级别与是否加密互相独立。</summary>
public enum VmExportCompressionLevel
{
    /// <summary>不压缩，优先降低处理器开销。</summary>
    Store,
    /// <summary>优先压缩速度。</summary>
    Fastest,
    /// <summary>在压缩时间和文件体积之间取均衡。</summary>
    Optimal,
    /// <summary>优先减小文件体积，通常需要更长时间。</summary>
    SmallestSize
}

/// <summary>新建 ZIP 的内容加密方式。</summary>
public enum VmExportEncryption
{
    /// <summary>不加密，不应同时提供密码。</summary>
    None,
    /// <summary>WinZip AES-256；加密文件内容，文件名、大小等目录信息仍可见。</summary>
    Aes256
}

/// <summary>归档通过完整性校验后的源目录处理方式。</summary>
public enum VmExportSourceHandling
{
    /// <summary>保留源目录和文件。</summary>
    Keep,
    /// <summary>删除本次归档的原对象；源目录新增内容或清理失败时保留剩余内容并报告。</summary>
    DeleteAfterValidation
}

/// <summary>创建导出 ZIP 的选项，默认均衡压缩、不加密并保留源目录。</summary>
/// <remarks>旧的 VmExportPackageMode 重载仍保留原行为：Store 不压缩，Compress 使用最小体积档，成功后清理暂存源目录。密码不写入日志、结果或归档注释；Core 不弹出密码窗口。源目录权限补充仍按打包服务的既有规则执行。</remarks>
public sealed class VmExportPackageOptions
{
    /// <summary>压缩级别；默认 Optimal。即使 Store 也可以使用 AES-256。</summary>
    public VmExportCompressionLevel CompressionLevel { get; init; } = VmExportCompressionLevel.Optimal;
    /// <summary>内容加密方式；默认 None。选择 Aes256 时必须提供非空密码。</summary>
    public VmExportEncryption Encryption { get; init; }
    /// <summary>本次归档的密码，按 UTF-8 编码，默认 null；None 加密模式要求密码为 null。</summary>
    /// <remarks>与其他 ZIP 软件交换时建议使用 ASCII 密码。ZIP 没有统一的密码字符编码标记；部分 Windows 工具使用系统代码页，可能无法用相同的中文密码打开 UTF-8 加密包。Core 可使用相同的 Unicode 密码读取自己的包。</remarks>
    public string? Password { get; init; }
    /// <summary>校验成功后的源目录处理；默认 Keep。只有明确指定 DeleteAfterValidation 才会删除源文件。</summary>
    public VmExportSourceHandling SourceHandling { get; init; } = VmExportSourceHandling.Keep;

    internal void Validate()
    {
        if (!Enum.IsDefined(CompressionLevel) || !Enum.IsDefined(Encryption) || !Enum.IsDefined(SourceHandling))
            throw new ArgumentException("ZIP 打包选项包含无效枚举值。");
        if (Encryption == VmExportEncryption.Aes256 && string.IsNullOrEmpty(Password))
            throw new ArgumentException("AES-256 加密需要非空密码。");
        if (Encryption == VmExportEncryption.None && Password != null)
            throw new ArgumentException("提供密码时必须明确选择 AES-256 加密。");
    }
}
