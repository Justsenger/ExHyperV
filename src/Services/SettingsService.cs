using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using ExHyperV.Properties;
using Wpf.Ui.Appearance;
using System.Net.Http;

namespace ExHyperV.Services
{

    public record UpdateResult(bool IsUpdateAvailable, string LatestVersion);
    internal class GitHubRelease
    {
        public string tag_name { get; set; }
    }
    public static class SettingsService
    {

        private static readonly HttpClient _httpClient = new HttpClient();
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
                    // 建议在这里添加日志记录，方便调试
                    Debug.WriteLine($"GitHub API request failed: {response.StatusCode} - {errorContent}");
                }
            }
            catch (Exception ex)
            {
                // 建议在这里添加日志记录
                Debug.WriteLine($"Error checking for update from GitHub: {ex.Message}");
            }

            if (string.IsNullOrEmpty(latestVersionTag))
            {
                try
                {
                    latestVersionTag = (await _httpClient.GetStringAsync(FallbackUrl))?.Trim();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error checking for update from Fallback: {ex.Message}");
                    throw new Exception("检查更新失败，请检查网络连接。");
                }
            }

            if (string.IsNullOrEmpty(latestVersionTag))
            {
                // 如果两个源都失败了，就直接认为没有更新
                return new UpdateResult(false, currentVersion);
            }

            // --- 以下是核心修改 ---

            // 1. 同时处理大写 'V' 和小写 'v'
            var cleanCurrentStr = currentVersion.TrimStart('V', 'v').Split('-')[0];
            var cleanLatestStr = latestVersionTag.TrimStart('V', 'v').Split('-')[0];

            // 2. 尝试解析，如果成功，则进行正确的版本比较
            if (Version.TryParse(cleanCurrentStr, out var currentVer) && Version.TryParse(cleanLatestStr, out var latestVer))
            {
                // 使用严格大于 ">" 来判断是否有新版本
                bool isUpdateAvailable = latestVer > currentVer;
                return new UpdateResult(isUpdateAvailable, latestVersionTag);
            }

            // 3. 如果解析失败（作为最后的保险措施），才使用不区分大小写的字符串比较
            bool updateAvailableByString = !string.Equals(latestVersionTag, currentVersion, StringComparison.OrdinalIgnoreCase);
            return new UpdateResult(updateAvailableByString, latestVersionTag);
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
            string languageCode = languageName == "中文" ? "zh-CN" : "en-US";

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
                Process.Start(exePath);
            }
            Application.Current.Shutdown();
        }

        // 获取当前主题
        public static string GetTheme()
        {
            return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? Resources.dark : Resources.light;
        }

        // 应用新主题
        public static void ApplyTheme(string themeName)
        {
            var theme = themeName == Resources.dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme);
        }
    }
}