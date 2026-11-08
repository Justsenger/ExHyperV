using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace ExHyperV.Application;

/// <summary>读写当前 Windows 用户共用的 ExHyperV Config.xml；每次更新重新加载并保留未修改字段。</summary>
/// <remarks>仅由桌面程序读写界面偏好。沿用配置文件路径和命名互斥体，与其他持有者协调更新并保留其字段。</remarks>
internal static class UiSettingsStore
{
    internal static string ConfigFilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExHyperV", "Config.xml");
    private static readonly UiSettingsFile File = new(ConfigFilePath);

    /// <summary>读取最新配置文档，不保存调用方随后对文档的修改。</summary>
    /// <returns>独立 XDocument；缺少配置文件时使用初始文档，读取或 XML 格式错误可传播。</returns>
    public static XDocument Load() => File.Load();
    /// <summary>在互斥保护下重新读取配置，调用同步更新回调，然后写入完整文档。</summary>
    /// <remarks>获取互斥体最多等待 5 秒，超时抛出 TimeoutException；嵌套更新被拒绝。回调异常时不保存，写入失败可传播；不会自动用默认内容覆盖损坏的 XML。</remarks>
    /// <param name="update">修改配置根元素的同步回调；只修改自己负责的字段，不得嵌套更新同一配置文件。</param>
    /// <exception cref="TimeoutException">未能在等待上限内取得共享配置互斥体。</exception>
    /// <exception cref="InvalidOperationException">回调嵌套更新同一个配置文件。</exception>
    public static void Update(Action<XElement> update) => File.Update(update);
}

internal sealed class UiSettingsFile
{
    [ThreadStatic] private static HashSet<string>? _updatingPaths;
    private readonly string _path;
    private readonly string _mutexName;

    internal UiSettingsFile(string path)
    {
        _path = Path.GetFullPath(path);
        // The same user configuration is also shared with other host programs.
        // A path-derived name coordinates callers in the same Windows session.
        _mutexName = @"Local\ExHyperV.Config." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
    }

    internal XDocument Load() => WithLock(ReadDocument);

    internal void Update(Action<XElement> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var activeUpdates = _updatingPaths ??= new(StringComparer.OrdinalIgnoreCase);
        if (!activeUpdates.Add(_path)) throw new InvalidOperationException("Nested updates to the same configuration are not supported.");
        try
        {
            WithLock(() =>
            {
                var document = ReadDocument();
                var root = document.Root ?? throw new InvalidDataException("Configuration has no root element.");
                update(root);

                // Replace only after the complete new document is on disk. Failure
                // leaves the existing file intact, including a malformed old file.
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        document.Save(stream);
                        stream.Flush(flushToDisk: true);
                    }
                    if (System.IO.File.Exists(_path)) System.IO.File.Replace(temporaryPath, _path, null);
                    else System.IO.File.Move(temporaryPath, _path);
                }
                finally
                {
                    if (System.IO.File.Exists(temporaryPath)) System.IO.File.Delete(temporaryPath);
                }
                return true;
            });
        }
        finally { activeUpdates.Remove(_path); }
    }

    private XDocument ReadDocument() => System.IO.File.Exists(_path)
        ? XDocument.Load(_path)
        : new XDocument(new XElement("Config"));

    private T WithLock<T>(Func<T> operation)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Configuration is being updated by another caller.");
            return operation();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
