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
    private const string ConfigFilePath = "Config.xml";

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
        // 主动停掉 ARP 嗅探的 ETW 会话：赶在 CLR 硬终止后台线程之前、在受控时机清理，
        // 否则 pump 线程卡在 native ProcessTrace 会吊死整个进程退出。Service 内 ProcessExit 注册留作兜底。
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
        // 配置写不了(首次运行遇只读目录/权限不足/文件损坏)绝不能让启动崩溃——静默跳过持久化，语言已在内存生效。
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