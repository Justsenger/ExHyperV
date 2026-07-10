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

            XDocument configDoc;
            if (File.Exists(ConfigFilePath))
            {
                configDoc = XDocument.Load(ConfigFilePath);
                var languageElement = configDoc.Root?.Element("Language");
                if (languageElement != null)
                {
                    languageElement.Value = languageCode;
                }
                else
                {
                    configDoc.Root?.Add(new XElement("Language", languageCode));
                }
            }
            else
            {
                configDoc = new XDocument(new XElement("Config", new XElement("Language", languageCode)));
            }
            configDoc.Save(ConfigFilePath);

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
            XDocument configDoc;
            if (File.Exists(ConfigFilePath))
            {
                configDoc = XDocument.Load(ConfigFilePath);
                var themeElement = configDoc.Root?.Element("Theme");
                if (themeElement != null)
                {
                    themeElement.Value = themeCode;
                }
                else
                {
                    configDoc.Root?.Add(new XElement("Theme", themeCode));
                }
            }
            else
            {
                configDoc = new XDocument(new XElement("Config", new XElement("Theme", themeCode)));
            }
            configDoc.Save(ConfigFilePath);
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