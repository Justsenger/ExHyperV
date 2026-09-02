using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using ExHyperV.Tools; 

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
    }
    protected override void OnExit(ExitEventArgs e)
    {
        ExHyperV.Services.HostAzureFeatureSetService.EnsureDisabledAtRest();
        // 主动停掉 ARP 嗅探的 ETW 会话：赶在 CLR 硬终止后台线程之前、在受控时机清理，
        // 否则 ProcessTrace 线程会阻止进程退出；服务层的 ProcessExit 处理仅作后备。
        ExHyperV.Services.ArpSnoopService.Instance.Dispose();
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
