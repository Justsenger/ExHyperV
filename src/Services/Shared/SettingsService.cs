using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using Wpf.Ui.Appearance;
using System.Net.Http;

namespace ExHyperV.Services
{

    internal class GitHubRelease
    {
        public string tag_name { get; set; } = string.Empty;
    }
    public static class SettingsService
    {

        private static readonly HttpClient _httpClient = new HttpClient();
        public record UpdateResult(bool IsUpdateAvailable, string LatestVersion, bool IsInnerTest = false);
        private const string GitHubApiUrl = "https://api.github.com/repos/Justsenger/ExHyperV/releases/latest";
        private const string FallbackUrl = "https://update.shalingye.workers.dev/";

        static SettingsService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ExHyperV-App-Check");
        }

        public static async Task<UpdateResult> CheckForUpdateAsync(string currentVersion)
        {
            string latestVersionTag = null;
            try
            {
                var response = await _httpClient.GetAsync(GitHubApiUrl);
                if (response.IsSuccessStatusCode)
                {
                    var jsonStream = await response.Content.ReadAsStreamAsync();
                    var release = await System.Text.Json.JsonSerializer.DeserializeAsync<GitHubRelease>(jsonStream);
                    latestVersionTag = release?.tag_name;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                }
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(latestVersionTag))
            {
                try
                {
                    latestVersionTag = (await _httpClient.GetStringAsync(FallbackUrl))?.Trim();
                }
                catch (Exception)
                {
                    throw new Exception(Properties.Resources.Error_CheckForUpdateFailed);
                }
            }

            if (string.IsNullOrEmpty(latestVersionTag))
            {
                return new UpdateResult(false, currentVersion);
            }

            var cleanCurrentStr = currentVersion.TrimStart('V', 'v').Split('-')[0];
            var cleanLatestStr = latestVersionTag.TrimStart('V', 'v').Split('-')[0];

            if (Version.TryParse(cleanCurrentStr, out var currentVer) && Version.TryParse(cleanLatestStr, out var latestVer))
            {
                // 合并逻辑：
                bool isUpdateAvailable = latestVer > currentVer;  // 服务器大 -> 有更新
                bool isInnerTest = currentVer > latestVer;        // 本地大 -> 内测版

                return new UpdateResult(isUpdateAvailable, latestVersionTag, isInnerTest);
            }

            // 字符串退化处理逻辑
            bool isSame = string.Equals(latestVersionTag, currentVersion, StringComparison.OrdinalIgnoreCase);
            return new UpdateResult(!isSame, latestVersionTag, false);
        }
        private const string ConfigFilePath = "config.xml";

        // 从XML加载语言设置
        public static string GetLanguage()
        {
            if (!File.Exists(ConfigFilePath)) return "en-US"; // 默认英文

            try
            {
                XDocument configDoc = XDocument.Load(ConfigFilePath);
                return configDoc.Root?.Element("Language")?.Value ?? "en-US";
            }
            catch
            {
                return "en-US"; // 文件损坏则返回默认值
            }
        }

        // 保存语言设置并重启应用
        public static void SetLanguageAndRestart(string languageName)
        {
            string languageCode = languageName == Properties.Resources.Lang_Chinese ? "zh-CN" : "en-US";

            // 配置写不了(只读目录/权限/文件损坏)不应崩溃：无法持久化就不重启(重启也读不到新语言)，静默放弃。
            try
            {
                XDocument configDoc;
                if (File.Exists(ConfigFilePath))
                {
                    configDoc = XDocument.Load(ConfigFilePath);
                    var languageElement = configDoc.Root?.Element("Language");
                    if (languageElement != null)
                        languageElement.Value = languageCode;
                    else
                        configDoc.Root?.Add(new XElement("Language", languageCode));
                }
                else
                {
                    configDoc = new XDocument(new XElement("Config", new XElement("Language", languageCode)));
                }
                configDoc.Save(ConfigFilePath);
            }
            catch { return; }

            // 重启应用
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath != null)
            {
                // 重启失败（被杀软拦截/文件锁）则不关闭当前实例，避免关到无实例可用
                try { Process.Start(exePath); }
                catch { return; }
            }
            Application.Current.Shutdown();
        }

        // 获取当前主题
        public static string GetTheme()
        {
            if (_isFollowingSystem)
                return Properties.Resources.Theme_System;

            return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
                ? Properties.Resources.Theme_Dark
                : Properties.Resources.Theme_Light;
        }

        // 从XML加载主题设置
        public static string GetSavedThemeCode()
        {
            if (!File.Exists(ConfigFilePath)) return "system";

            try
            {
                XDocument configDoc = XDocument.Load(ConfigFilePath);
                return configDoc.Root?.Element("Theme")?.Value ?? "system";
            }
            catch
            {
                return "system";
            }
        }

        // 保存主题设置
        private static void SaveThemeCode(string themeCode)
        {
            // 配置写不了不应崩溃：主题已应用到界面，仅静默跳过持久化。
            try
            {
                XDocument configDoc;
                if (File.Exists(ConfigFilePath))
                {
                    configDoc = XDocument.Load(ConfigFilePath);
                    var themeElement = configDoc.Root?.Element("Theme");
                    if (themeElement != null)
                        themeElement.Value = themeCode;
                    else
                        configDoc.Root?.Add(new XElement("Theme", themeCode));
                }
                else
                {
                    configDoc = new XDocument(new XElement("Config", new XElement("Theme", themeCode)));
                }
                configDoc.Save(ConfigFilePath);
            }
            catch { }
        }

        // ===== 控制台默认缩放档 =====
        // 保存用户上次手动选择的缩放（如 "150%" 或本地化"适应窗口"）；下次打开控制台优先用它。

        public static string? GetDefaultZoom()
        {
            if (!File.Exists(ConfigFilePath)) return null;
            try
            {
                XDocument configDoc = XDocument.Load(ConfigFilePath);
                return configDoc.Root?.Element("DefaultZoom")?.Value;
            }
            catch { return null; }
        }

        public static void SaveDefaultZoom(string zoom)
        {
            try
            {
                XDocument configDoc;
                if (File.Exists(ConfigFilePath))
                {
                    configDoc = XDocument.Load(ConfigFilePath);
                    var el = configDoc.Root?.Element("DefaultZoom");
                    if (el != null) el.Value = zoom;
                    else configDoc.Root?.Add(new XElement("DefaultZoom", zoom));
                }
                else
                {
                    configDoc = new XDocument(new XElement("Config", new XElement("DefaultZoom", zoom)));
                }
                configDoc.Save(ConfigFilePath);
            }
            catch { }
        }

        // ===== 宿主 MMIO 上限缓存（MB） =====
        // 首次 boot-probe 测得的宿主物理地址上限，持久化后不再重探（只认第一次测得的结果）。

        public static ulong? GetMmioCeilingMb()
        {
            if (!File.Exists(ConfigFilePath)) return null;
            try
            {
                XDocument configDoc = XDocument.Load(ConfigFilePath);
                var v = configDoc.Root?.Element("MmioCeilingMb")?.Value;
                return ulong.TryParse(v, out ulong mb) && mb > 0 ? mb : null;
            }
            catch { return null; }
        }

        public static void SaveMmioCeilingMb(ulong ceilingMb)
        {
            try
            {
                XDocument configDoc;
                if (File.Exists(ConfigFilePath))
                {
                    configDoc = XDocument.Load(ConfigFilePath);
                    var el = configDoc.Root?.Element("MmioCeilingMb");
                    if (el != null) el.Value = ceilingMb.ToString();
                    else configDoc.Root?.Add(new XElement("MmioCeilingMb", ceilingMb.ToString()));
                }
                else
                {
                    configDoc = new XDocument(new XElement("Config", new XElement("MmioCeilingMb", ceilingMb.ToString())));
                }
                configDoc.Save(ConfigFilePath);
            }
            catch { }
        }

        // 是否正在跟随系统主题
        private static bool _isFollowingSystem = true;

        // 启动时按保存偏好上色。故意不在此挂系统主题监听——启动挂会与预加载抢渲染致 Mica 不生效(#146);
        // 监听改由 EnableSystemThemeWatch 在预加载后延后挂。
        public static void ApplySavedTheme()
        {
            var themeName = GetSavedThemeCode() switch
            {
                "dark" => Properties.Resources.Theme_Dark,
                "light" => Properties.Resources.Theme_Light,
                _ => Properties.Resources.Theme_System,
            };

            ApplyTheme(themeName, window: null, saveTheme: false);   // window=null:只上色、设 _isFollowingSystem,不挂监听
        }

        // 预加载/首帧渲染完成后再挂系统主题监听(仅跟随模式);延后是为错开 #146 的启动渲染竞争
        public static void EnableSystemThemeWatch(Window window)
        {
            if (_isFollowingSystem && window != null)
                SystemThemeWatcher.Watch(window);
        }

        // 应用新主题
        public static void ApplyTheme(string themeName, Window? window = null, bool saveTheme = true)
        {
            if (themeName == Properties.Resources.Theme_System)
            {
                // 跟随系统主题
                _isFollowingSystem = true;

                var sysTheme = SystemThemeManager.GetCachedSystemTheme() == SystemTheme.Dark
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
                ApplicationThemeManager.Apply(sysTheme);

                if (window != null)
                    SystemThemeWatcher.Watch(window);

                if (saveTheme)
                    SaveThemeCode("system");
            }
            else
            {
                // 手动选择 Light/Dark
                _isFollowingSystem = false;

                if (window != null)
                    SystemThemeWatcher.UnWatch(window);

                var theme = themeName == Properties.Resources.Theme_Dark
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
                ApplicationThemeManager.Apply(theme);

                if (saveTheme)
                    SaveThemeCode(themeName == Properties.Resources.Theme_Dark ? "dark" : "light");
            }
        }
    }
}