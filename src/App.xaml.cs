using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Xml.Linq;
using ExHyperV.Tools;

namespace ExHyperV
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern int GetUserDefaultLocaleName([Out] StringBuilder lpLocaleName, int cchLocaleName);

        private const string DefaultLanguage = "en-US";
        private const string ConfigFilePath = "Config.xml";

        protected override void OnStartup(StartupEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();

            if (args.Length == 0)
            {
                InitializeLanguage();
                base.OnStartup(e);
                return;
            }

            AttachConsole(ATTACH_PARENT_PROCESS);

            string command = args[0].ToLower();
            PerformBackgroundTask(command, args.Skip(1).ToArray());

            FreeConsole();
            Environment.Exit(0);
        }

        private void InitializeLanguage()
        {
            string targetLanguage;
            if (File.Exists(ConfigFilePath))
            {
                var configLanguage = ReadLanguageFromConfig();
                targetLanguage = IsLanguageSupported(configLanguage) ? configLanguage : GetValidSystemLanguageAndSave();
            }
            else
            {
                targetLanguage = GetValidSystemLanguageAndSave();
            }
            SetLanguage(targetLanguage);
        }

        private string GetValidSystemLanguageAndSave()
        {
            var lang = GetValidSystemLanguage();
            WriteLanguageToConfig(lang);
            return lang;
        }

        private string GetValidSystemLanguage()
        {
            var systemLang = GetSystemLanguageViaAPI();
            return IsLanguageSupported(systemLang) ? systemLang : DefaultLanguage;
        }

        private bool IsLanguageSupported(string languageCode)
        {
            return !string.IsNullOrEmpty(languageCode) && (languageCode == "en-US" || languageCode == "zh-CN");
        }

        private string GetSystemLanguageViaAPI()
        {
            var localeName = new StringBuilder(85);
            int result = GetUserDefaultLocaleName(localeName, localeName.Capacity);
            return result > 0 ? localeName.ToString() : DefaultLanguage;
        }

        private void SetLanguage(string cultureCode)
        {
            try
            {
                var culture = new CultureInfo(cultureCode);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }
            catch (CultureNotFoundException)
            {
                var defaultCulture = new CultureInfo(DefaultLanguage);
                Thread.CurrentThread.CurrentCulture = defaultCulture;
                Thread.CurrentThread.CurrentUICulture = defaultCulture;
            }
        }

        private string ReadLanguageFromConfig()
        {
            try
            {
                var configDoc = XDocument.Load(ConfigFilePath);
                return configDoc.Root?.Element("Language")?.Value ?? DefaultLanguage;
            }
            catch
            {
                return DefaultLanguage;
            }
        }

        private void WriteLanguageToConfig(string cultureCode)
        {
            try
            {
                var configDoc = new XDocument(new XElement("Config", new XElement("Language", cultureCode)));
                configDoc.Save(ConfigFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入配置文件失败: {ex.Message}");
            }
        }

        private static void PerformBackgroundTask(string command, string[] parameters)
        {
            switch (command)
            {
                case "--silent":
                    Console.WriteLine("正在以静默模式运行后台任务...");
                    Thread.Sleep(3000);
                    Console.WriteLine("静默任务完成。");
                    break;

                case "--help":
                    ShowHelp();
                    break;

                case "--version":
                    ShowVersion();
                    break;

                default:
                    Console.WriteLine($"错误: 未识别的命令 '{command}'");
                    Console.WriteLine("使用 '--help' 查看可用命令。");
                    break;
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("ExHyperV 命令行帮助:");
            Console.WriteLine("  --silent       以后台静默模式运行默认任务。");
            Console.WriteLine("  --version      显示应用程序的版本信息。");
            Console.WriteLine("  --help         显示此帮助信息。");
        }

        private static void ShowVersion()
        {
            Console.WriteLine($"ExHyperV 版本: {Utils.Version}");
        }
    }
}