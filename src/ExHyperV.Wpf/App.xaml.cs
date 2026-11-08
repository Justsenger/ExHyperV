using ExHyperV.UI.Shared.Behaviors;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Xml.Linq;

namespace ExHyperV;

public partial class App
{
    private const string DefaultLanguage = "en-US";
    private static string ConfigFilePath => ExHyperV.Application.UiSettingsStore.ConfigFilePath;

    // 性能模式：启动即读，供窗口/预加载/动画判定。改动需重启生效。
    public static bool PerformanceMode { get; private set; }

    // Desktop-owned monitoring instances register their own shutdown here.
    // Core remains independent of this application and its dispatcher.
    internal event Func<Task>? StopDesktopMonitoring;

    // 静态构造早于 App.xaml/wpf-ui 字典 parse，此时置位才能让模板里的 {controls:Motion} 取到正确 flag；
    // 软件渲染同样必须在首帧前设。
    static App()
    {
        PerformanceMode = ExHyperV.Application.SettingsService.GetPerformanceMode();
        if (!PerformanceMode) return;

        // 定制控件随源码构建，在模板加载前关闭动画。
        Wpf.Ui.Controls.UiPerformance.Reduced = true;

        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 正常模式下为窗口内页面统一启用平滑滚轮；轻量模式仍遵循“关闭动画”的约定。
        if (!PerformanceMode)
            SmoothScroll.Initialize();

        // 后台功能跟随宿主进程运行，页面只决定显示刷新，不决定业务维护是否继续。
        _ = Task.Run(() => ExHyperV.Services.ArpSnoopService.Instance.Start());

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
        // Start cancellation on the UI thread before it stops dispatching work.
        // Monitor dispatches carry that token, so queued UI updates can abort
        // while we wait for the worker loops to finish without pumping the UI.
        var monitorStops = new List<Task>();
        foreach (var stop in StopDesktopMonitoring?.GetInvocationList() ?? [])
        {
            try { monitorStops.Add(((Func<Task>)stop)()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ExHyperV] Monitor shutdown failed: {ex.Message}"); }
        }
        StopDesktopMonitoring = null;
        try
        {
            Task.Run(async () =>
            {
                try { await Task.WhenAll(monitorStops).ConfigureAwait(false); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ExHyperV] Monitor shutdown failed: {ex.Message}"); }
                await ExHyperV.Services.SharedBackgroundServices.ShutdownAsync().ConfigureAwait(false);
            })
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExHyperV] Shutdown cleanup failed: {ex.Message}");
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
            var configDoc = ExHyperV.Application.UiSettingsStore.Load();
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
            ExHyperV.Application.UiSettingsStore.Update(root => root.SetElementValue("Language", cultureCode));
        }
        catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern int GetUserDefaultLocaleName(
        [Out] StringBuilder lpLocaleName,
        int cchLocaleName
    );
}
