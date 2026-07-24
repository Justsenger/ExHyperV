using System.IO;

namespace ExHyperV.Services;

internal static class AppDataPaths
{
    internal static string ConfigFilePath { get; } = ResolveConfigFilePath();

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
