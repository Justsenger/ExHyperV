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
                }
            }
            catch (Exception ex)
            {
            }

            if (string.IsNullOrEmpty(latestVersionTag))
            {
                try
                {
                    latestVersionTag = (await _httpClient.GetStringAsync(FallbackUrl))?.Trim();
                }
                catch (Exception ex)
                {
                    throw new Exception(ExHyperV.Properties.Resources.Error_CheckForUpdateFailed);
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
                bool isUpdateAvailable = latestVer > currentVer;
                return new UpdateResult(isUpdateAvailable, latestVersionTag);
            }
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