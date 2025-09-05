using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using ExHyperV.Models;
using ExHyperV.Services;
// 注意：您可能需要为 DhcpConfig 和 DhcpService 创建或调整相应的模型和服务
// 为了使代码可编译，这里假设它们存在于合适的命名空间下

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

        // 假设 DhcpService 仍然是您后台逻辑的一部分
        private static DhcpService _dhcpService;

        protected override void OnStartup(StartupEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();

            if (args.Length == 0)
            {
                // 这是正常的 GUI 启动模式
                InitializeLanguage();
                base.OnStartup(e);
                return;
            }

            // 这是命令行后台模式
            AttachConsole(ATTACH_PARENT_PROCESS);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Console.CancelKeyPress += (s, ev) => OnProcessExit(s, ev);

            string command = args[0].ToLower();
            PerformBackgroundTask(command, args.Skip(1).ToArray());

            if (command != "--background")
            {
                FreeConsole();
                Environment.Exit(0);
            }
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            Console.WriteLine("正在关闭服务...");
            _dhcpService?.Dispose();
            FreeConsole();
        }

        private void InitializeLanguage()
        {
            var configService = new ConfigurationService();
            var config = configService.LoadConfiguration();

            string targetLanguage = config.Language;

            if (!IsLanguageSupported(targetLanguage))
            {
                var systemLang = GetSystemLanguageViaAPI();
                targetLanguage = IsLanguageSupported(systemLang) ? systemLang : DefaultLanguage;

                config.Language = targetLanguage;
                configService.SaveConfiguration(config);

            }

            SetLanguage(targetLanguage);
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

        private static void PerformBackgroundTask(string command, string[] parameters)
        {
            switch (command)
            {
                case "--background":
                    Console.WriteLine("正在启动 ExHyperV 后台服务...");
                    bool success = StartDhcpServer();

                    if (success)
                    {
                        Console.WriteLine("服务正在运行。按 Ctrl+C 停止。");
                        Thread.Sleep(Timeout.Infinite);
                    }
                    else
                    {
                        Console.WriteLine("服务启动失败，程序即将退出。");
                    }
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

        private static bool StartDhcpServer()
        {
            // 注意: 这里的后台服务逻辑可能需要根据新的 AppConfig 模型进行调整。
            // 这是一个基于旧逻辑的示例实现。
            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.xml");

            if (!File.Exists(configFilePath))
            {
                Console.WriteLine($"错误: 找不到配置文件 '{configFilePath}'。");
                return false;
            }

            // 假设您有一个 DhcpConfig 类可以从 AppConfig 中提取或转换
            // var configService = new ConfigurationService();
            // var appConfig = configService.LoadConfiguration();
            // var dhcpConfig = YourDhcpConfigConverter.FromAppConfig(appConfig);

            // 为了保持代码可编译，我们暂时保留旧的 DhcpConfig.Load 模式
            // 您需要根据实际情况替换这部分逻辑
            var dhcpConfig = DhcpConfig.Load(configFilePath);

            if (dhcpConfig == null || !dhcpConfig.Enabled)
            {
                Console.WriteLine("信息: DHCP服务未配置或被禁用，后台服务未启动。");
                return false;
            }

            bool needsUserChoice = false;

            if (!string.IsNullOrEmpty(dhcpConfig.InterfaceName))
            {
                var specifiedInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(x => x.Name == dhcpConfig.InterfaceName);

                if (specifiedInterface == null)
                {
                    Console.WriteLine($"警告: 配置文件中的网络接口 '{dhcpConfig.InterfaceName}' 未找到。");
                    needsUserChoice = true;
                }
            }
            else
            {
                needsUserChoice = true;
            }

            if (needsUserChoice)
            {
                var selectedInterface = ChooseNetworkInterface();
                if (selectedInterface == null)
                {
                    return false;
                }
                dhcpConfig.InterfaceName = selectedInterface.Name;
            }

            try
            {
                _dhcpService = new DhcpService(dhcpConfig);
                return _dhcpService.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"启动DHCP服务时发生致命错误: {ex.Message}");
                return false;
            }
        }

        private static NetworkInterface ChooseNetworkInterface()
        {
            var availableInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            if (availableInterfaces.Count == 0)
            {
                Console.WriteLine("错误: 未找到任何可用的网络接口。");
                return null;
            }

            Console.WriteLine("请选择一个网络接口来运行DHCP服务:");
            for (int i = 0; i < availableInterfaces.Count; i++)
            {
                var ni = availableInterfaces[i];
                Console.WriteLine($"  {i + 1}. {ni.Name} [{ni.Description}]");
            }
            Console.Write("请输入选项编号: ");

            while (true)
            {
                string input = Console.ReadLine();
                if (int.TryParse(input, out int choice) && choice > 0 && choice <= availableInterfaces.Count)
                {
                    return availableInterfaces[choice - 1];
                }
                else
                {
                    Console.Write("无效的输入，请重新输入一个有效的编号: ");
                }
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("ExHyperV 命令行帮助:");
            Console.WriteLine("  --background   以后台模式运行服务 (例如DHCP)。");
            Console.WriteLine("  --version      显示应用程序的版本信息。");
            Console.WriteLine("  --help         显示此帮助信息。");
        }

        private static void ShowVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Console.WriteLine($"ExHyperV 版本: {version}");
        }
    }
}