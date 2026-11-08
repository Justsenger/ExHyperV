using ExHyperV.Services;

namespace ExHyperV.Tools;

// One budget for the entire preparation batch, including parallel child imports.
internal sealed class ImportArchiveContext
{
    private readonly object gate = new();
    private long consumed;
    internal string? Password { get; private set; }
    internal int? MaxEntries { get; }
    internal long? MaxEntryBytes { get; }
    internal long? MaxTotalBytes { get; }
    internal string TemporaryDirectory { get; }

    internal ImportArchiveContext(VmImportArchiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxEntries < 0 || options.MaxEntryBytes < 0 || options.MaxTotalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ZIP 解压额度不能为负数。");
        Password = options.Password;
        MaxEntries = options.MaxEntries;
        MaxEntryBytes = options.MaxEntryBytes;
        MaxTotalBytes = options.MaxTotalBytes;
        if (options.TemporaryDirectory != null
            && (string.IsNullOrWhiteSpace(options.TemporaryDirectory)
                || !Path.IsPathFullyQualified(options.TemporaryDirectory)))
            throw new ArgumentException("ZIP 预览工作目录必须是绝对路径。", nameof(options));
        TemporaryDirectory = Path.GetFullPath(options.TemporaryDirectory ?? Path.GetTempPath());
        if (!Directory.Exists(TemporaryDirectory))
            throw new DirectoryNotFoundException("ZIP 预览工作目录不存在：" + TemporaryDirectory);
        for (DirectoryInfo? parent = new(TemporaryDirectory); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ZIP 预览工作目录不能经过重解析点：" + parent.FullName);
    }

    internal void ClearPassword() => Password = null;

    internal static void Check(long value, long? limit, string name, string? entry = null)
    {
        if (limit is long maximum && value > maximum)
            throw new VmImportArchiveException(VmImportArchiveError.LimitExceeded,
                $"ZIP 超过 {name} 上限（{maximum}），本次读取已停止。", entry, name, maximum, value);
    }

    internal void CheckRemaining(long declaredBytes)
    {
        lock (gate)
            Check(checked(consumed + declaredBytes), MaxTotalBytes, nameof(VmImportArchiveOptions.MaxTotalBytes));
    }

    internal void Consume(int bytes, string entry)
    {
        lock (gate)
        {
            consumed = checked(consumed + bytes);
            Check(consumed, MaxTotalBytes, nameof(VmImportArchiveOptions.MaxTotalBytes), entry);
        }
    }
}
