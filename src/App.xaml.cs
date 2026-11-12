using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using ExHyperV.Tools;
using ExHyperV.Views;

namespace ExHyperV;

public partial class App
{
    private const string DefaultLanguage = "en-US";
    private static string ConfigFilePath => ExHyperV.Services.AppDataPaths.ConfigFilePath;

    // 性能模式：启动即读，供窗口/预加载/动画判定。改动需重启生效。
    public static bool PerformanceMode { get; private set; }

    // 静态构造早于 App.xaml/wpf-ui 字典 parse，此时置位才能让模板里的 {controls:Motion} 取到正确 flag；
    // 软件渲染同样必须在首帧前设。
    static App()
    {
        PerformanceMode = ExHyperV.Services.SettingsService.GetPerformanceMode();
        if (!PerformanceMode) return;

        // UiPerformance 只在重编的 src/libs DLL 里；编译期引用的是 nuget 包(无此类型)，故反射置位。
        // dev 跑用官方全量 DLL→反射空转(动画不关)，仅 publish 版真生效——与"模板改动只 publish 暴露"一致。
        var t = System.Type.GetType("Wpf.Ui.Controls.UiPerformance, Wpf.Ui");
        t?.GetField("Reduced", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.SetValue(null, true);

        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例门禁必须最先执行：本进程若不是首个实例，继续初始化会复位
        // AzureFeatureSet、抢注 ETW/USB 等机器级全局资源，干扰已在运行的实例。
        // 也不能走 Application.Shutdown()，它会触发 OnExit 里的全局清理（停 ETW/USB
        // 隧道、复位主机状态），把首个实例的资源一起清掉；故直接终止本进程。
        if (!SingleInstanceGuard.TryAcquire())
        {
            Environment.Exit(0);
            return;
        }

        // 正常模式下为窗口内页面统一启用平滑滚轮；轻量模式仍遵循“关闭动画”的约定。
        if (!PerformanceMode)
            SmoothScroll.Initialize();

        // 旧版本曾把 AzureFeatureSet 暴露为持久化主机选项，临时操作异常中断时也可能留下该值。
        // 此暂存开关由 ExHyperV 管理，因此必须在任何 Hyper-V 操作开始前恢复为关闭状态。
        ExHyperV.Services.HostAzureFeatureSetService.EnsureDisabledAtRest();

        base.OnStartup(e);

        string targetLanguage;
        if (File.Exists(ConfigFilePath))
        {
            var configLanguage = ReadLanguageFromConfig();
            if (IsLanguageSupported(configLanguage))
            {
                targetLanguage = configLanguage;
            }
            else
            {
                targetLanguage = GetValidSystemLanguage();
                WriteLanguageToConfig(targetLanguage);
            }
        }
        else
        {
            targetLanguage = GetValidSystemLanguage();
            WriteLanguageToConfig(targetLanguage);
        }
        SetLanguage(targetLanguage);

        // StartupUri 已移除：门禁通过且语言就绪后才创建主窗口，
        // 避免第二个实例在退出前闪出一个窗口。
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            // 建窗/首帧失败时若继续留在这个进程，ShutdownMode=OnMainWindowClose 会因
            // 无主窗口而永不退出，变成一个没有界面的僵尸进程。这里显式终止，
            // 并跳过 OnExit（其清理逻辑见下方），避免机器级清理在异常路径二次抛错。
            System.Diagnostics.Debug.WriteLine($"[ExHyperV] Failed to create main window: {ex}");
            Environment.Exit(1);
            return;
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // The dispatcher is stopping; finish USB cleanup on a worker before
            // process exit. This also requires already-running service gate
            // owners to remain dispatcher-independent (ConfigureAwait(false));
            // Task.Run here alone cannot release a gate held by a UI continuation.
            Task.Run(() => ExHyperV.Services.UsbVmbusService.StopAllTunnelsAsync())
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExHyperV-USB] Shutdown cleanup failed: {ex.Message}");
        }
        ExHyperV.Services.HostAzureFeatureSetService.EnsureDisabledAtRest();
        try
        {
            // 主动停掉 ARP 嗅探的 ETW 会话：赶在 CLR 硬终止后台线程之前、在受控时机清理，
            // 否则 ProcessTrace 线程会阻止进程退出；服务层的 ProcessExit 处理仅作后备。
            ExHyperV.Services.ArpSnoopService.Instance.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExHyperV-ARP] Shutdown cleanup failed: {ex.Message}");
        }
        base.OnExit(e);
    }

    private string GetValidSystemLanguage()
    {
        var systemLang = GetSystemLanguageViaAPI();
        return IsLanguageSupported(systemLang) ? systemLang : DefaultLanguage;
    }

    private bool IsLanguageSupported(string languageCode)
    {
        return languageCode == "en-US" || languageCode == "zh-CN";
    }

    private string GetSystemLanguageViaAPI()
    {
        var localeName = new StringBuilder(85);
        var result = GetUserDefaultLocaleName(localeName, localeName.Capacity);
        return result > 0 ? localeName.ToString().Substring(0, result - 1) : DefaultLanguage;
    }

    private void SetLanguage(string cultureCode)
    {
        var culture = new CultureInfo(cultureCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
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
        // 配置不可写时跳过持久化，语言设置仍在当前进程内生效。
        try
        {
            var configDoc = File.Exists(ConfigFilePath)
                ? XDocument.Load(ConfigFilePath)
                : new XDocument(new XElement("Config"));

            var root = configDoc.Root;
            var langElement = root?.Element("Language");

            if (langElement == null)
                root?.Add(new XElement("Language", cultureCode));
            else
                langElement.Value = cultureCode;

            configDoc.Save(ConfigFilePath);
        }
        catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern int GetUserDefaultLocaleName(
        [Out] StringBuilder lpLocaleName,
        int cchLocaleName
    );
}
