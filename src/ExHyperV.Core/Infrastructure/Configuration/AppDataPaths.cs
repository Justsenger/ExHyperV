using System.IO;

namespace ExHyperV.Services;

/// <summary>提供当前用户共用的 ExHyperV 应用数据位置。</summary>
internal static class AppDataPaths
{
    /// <summary>当前用户 LocalApplicationData/ExHyperV/Config.xml 的完整路径；读取属性会尝试创建父目录。</summary>
    public static string ConfigFilePath { get; } = ResolveConfigFilePath();

    private static string ResolveConfigFilePath()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExHyperV");

        // 不迁移、不读取、也不回退到 EXE 或工作目录旁的旧配置。
        try { Directory.CreateDirectory(appDataDirectory); }
        catch { }

        return Path.Combine(appDataDirectory, "Config.xml");
    }
}
