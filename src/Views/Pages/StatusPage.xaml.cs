using System.ComponentModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WPFLocalizeExtension.Engine;

namespace ExHyperV.Views.Pages;

public partial class StatusPage
{
    public StatusPage()
    {
        InitializeComponent();
        Loaded += StatusPage_Loaded;
        Unloaded += StatusPage_Unloaded;
    }

    private void StatusPage_Loaded(object sender, RoutedEventArgs e)
    {
        LocalizeDictionary.Instance.PropertyChanged += Instance_PropertyChanged;
        UpdateAllStatusInfo();
    }

    private void StatusPage_Unloaded(object sender, RoutedEventArgs e)
    {
        LocalizeDictionary.Instance.PropertyChanged -= Instance_PropertyChanged;
    }

    private void Instance_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Culture") UpdateAllStatusInfo();
    }

    private void UpdateAllStatusInfo()
    {
        Dispatcher.Invoke((Action)(() =>
        {
            progressRing1.Visibility = Visibility.Visible;
            progressRing2.Visibility = Visibility.Visible;
            progressRing3.Visibility = Visibility.Visible;
            progressRing4.Visibility = Visibility.Visible;
            progressRing5.Visibility = Visibility.Visible;
        }));

        Task.Run(() => CpuInfo());
        Task.Run(() => SysInfo());
        Task.Run(() => HyperVInfo());
        Task.Run(() => CheckReg());
        Task.Run(() => Admininfo());
        Task.Run(() => ServerInfo());
    }

    private SolidColorBrush GetSuccessColor()
    {
        // Use theme-aware success color
        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? new SolidColorBrush(Color.FromArgb(255, 107, 203, 119)) // Light green for dark theme
            : new SolidColorBrush(Color.FromArgb(255, 16, 124, 16)); // Dark green for light theme
    }

    private SolidColorBrush GetErrorColor()
    {
        // Use theme-aware error color
        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? new SolidColorBrush(Color.FromArgb(255, 255, 153, 164)) // Light red for dark theme
            : new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)); // Dark red for light theme
    }

    private SolidColorBrush GetInfoColor()
    {
        // Use theme-aware info color
        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? new SolidColorBrush(Color.FromArgb(255, 156, 220, 254)) // Light blue for dark theme
            : new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)); // Dark blue for light theme
    }

    private void RemoveExistingIcon(Grid grid)
    {
        var existingIcon = grid.Children.OfType<FontIcon>().FirstOrDefault();
        if (existingIcon != null) grid.Children.Remove(existingIcon);
    }

    private async void HyperVInfo()
    {
        var hypervResult = Utils.RunWithErrorHandling("Get-Module -ListAvailable -Name Hyper-V");
        if (hypervResult.HasErrors)
        {
            hypervResult.ShowErrorsToUser();
            return;
        }

        var hypervstatus = hypervResult.Output;

        Dispatcher.Invoke((Action)(() =>
        {
            progressRing3.Visibility = Visibility.Collapsed;
            RemoveExistingIcon(status3);

            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = GetSuccessColor()
            };
            if (hypervstatus.Count != 0)
            {
                hyperv.Text = LocalizationHelper.GetString("String1");
            }
            else
            {
                hyperv.Text = LocalizationHelper.GetString("String2");
                icons.Glyph = "\xEB90";
                icons.Foreground = GetErrorColor();
            }

            status3.Children.Add(icons);
        }));
    }

    private async void SysInfo()
    {
        var buildVersion = Environment.OSVersion.Version.Build;
        Dispatcher.Invoke((Action)(() =>
        {
            progressRing1.Visibility = Visibility.Collapsed;
            RemoveExistingIcon(status1);

            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xF167",
                Foreground = GetInfoColor()
            };

            if (buildVersion >= 22000) //不允许宿主使用过低的系统版本，WDDM版本低，以及PS命令存在问题。
            {
                icons.Glyph = "\xEC61";
                icons.Foreground = GetSuccessColor();
                var string3 = LocalizationHelper.GetString("String3");
                var v19041 = LocalizationHelper.GetString("v19041");
                win.Text = string3 + buildVersion + v19041;
            }
            else
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                if (ms != null) ms.gpupv.IsEnabled = false;
                var string3 = LocalizationHelper.GetString("String3");
                var disablegpu = LocalizationHelper.GetString("disablegpu");
                win.Text = string3 + buildVersion + disablegpu;
                icons.Glyph = "\xEB90";
                icons.Foreground = GetErrorColor();
            }

            status1.Children.Add(icons);
        }));
    }

    private async void CpuInfo()
    {
        var cpuvt1Result =
            Utils.RunWithErrorHandling("(Get-WmiObject -Class Win32_Processor).VirtualizationFirmwareEnabled");
        if (cpuvt1Result.HasErrors)
        {
            cpuvt1Result.ShowErrorsToUser();
            return;
        }

        var cpuvt1 = cpuvt1Result.Output;

        var cpuvt2Result = Utils.RunWithErrorHandling("(Get-WmiObject -Class Win32_ComputerSystem).HypervisorPresent");
        if (cpuvt2Result.HasErrors)
        {
            cpuvt2Result.ShowErrorsToUser();
            return;
        }

        var cpuvt2 = cpuvt2Result.Output;

        Dispatcher.Invoke((Action)(() =>
        {
            progressRing2.Visibility = Visibility.Collapsed;
            RemoveExistingIcon(status2);

            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = GetSuccessColor()
            };

            if (cpuvt1.Count > 0 &&
                (cpuvt1[0].ToString() == "True" || (cpuvt2.Count > 0 && cpuvt2[0].ToString() == "True")))
            {
                cpu.Text = LocalizationHelper.GetString("GPU1");
            }
            else
            {
                cpu.Text = LocalizationHelper.GetString("GPU2");
                icons.Glyph = "\xEB90";
                icons.Foreground = GetErrorColor();
            }

            status2.Children.Add(icons);
        }));
    }

    private async void CheckReg() //检查是否添加了注册表，已添加则关闭弹窗
    {
        var script = @"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and
        (($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -ne $null) -and
        ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object {
            ($k.GetValue($_, $null) -ne $null) -and ($k.GetValueKind($_) -eq 'DWord')
        }) -notcontains $false)";
        var regResult = Utils.RunWithErrorHandling(script);
        if (regResult.HasErrors)
        {
            regResult.ShowErrorsToUser();
            return;
        }

        var result = regResult.Output;

        Dispatcher.Invoke((Action)(() =>
        {
            if (result.Count > 0 && result[0].ToString().ToLower() == "true")
                gpustrategy.IsChecked = true;
            else
                gpustrategy.IsChecked = false;
        }));
    }

    private async void Admininfo()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var Isadmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        Dispatcher.Invoke((Action)(() =>
        {
            progressRing4.Visibility = Visibility.Collapsed;
            RemoveExistingIcon(status4);

            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = GetSuccessColor()
            };
            if (Isadmin)
            {
                admin.Text = LocalizationHelper.GetString("Admin1");
            }
            else //如果没有管理员权限，关闭GPU虚拟化功能
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                if (ms != null) ms.gpupv.IsEnabled = false;
                admin.Text = LocalizationHelper.GetString("Admin2");
                icons.Glyph = "\xEB90";
                icons.Foreground = GetErrorColor();
            }

            status4.Children.Add(icons);
        }));
    }

    private async void ServerInfo()
    {
        var serverResult = Utils.RunWithErrorHandling("(Get-WmiObject -Class Win32_OperatingSystem).ProductType");
        if (serverResult.HasErrors)
        {
            serverResult.ShowErrorsToUser();
            return;
        }

        var result = serverResult.Output;

        Dispatcher.Invoke((Action)(() =>
        {
            progressRing5.Visibility = Visibility.Collapsed;
            RemoveExistingIcon(status5);

            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = GetSuccessColor()
            };
            if (result.Count > 0 && result[0].ToString() == "3")
            {
                version.Text = LocalizationHelper.GetString("Isserver");
            }
            else
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                //ms.dda.IsEnabled = false;

                version.Text = LocalizationHelper.GetString("ddaa");
                icons.Glyph = "\xEB90";
                icons.Foreground = GetErrorColor();
            }

            status5.Children.Add(icons);
        }));
    }

    public void Addreg()
    {
        // 注册表路径和键名
        var registryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        var key1 = "RequireSecureDeviceAssignment";
        var key2 = "RequireSupportedDeviceAssignment";

        // PowerShell 脚本
        var script = $@"
        # 确保注册表路径存在，如果不存在，则创建它
        if (-not (Test-Path '{registryPath}')) {{
            New-Item -Path '{registryPath}' -Force
        }}

        # 检查并设置注册表键值
        Set-ItemProperty -Path '{registryPath}' -Name '{key1}' -Value 0 -Type DWord -Force
        Set-ItemProperty -Path '{registryPath}' -Name '{key2}' -Value 0 -Type DWord -Force
        ";
        var addRegResult = Utils.RunWithErrorHandling(script);
        if (addRegResult.HasErrors)
        {
            addRegResult.ShowErrorsToUser();
            return;
        }

        CheckReg();
    }

    public void RemoveReg()
    {
        // 注册表路径和键名
        var registryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        var key1 = "RequireSecureDeviceAssignment";
        var key2 = "RequireSupportedDeviceAssignment";

        // PowerShell 脚本
        var script = $@"
        if (Test-Path '{registryPath}') {{
            Remove-ItemProperty -Path '{registryPath}' -Name '{key1}' -ErrorAction SilentlyContinue
            Remove-ItemProperty -Path '{registryPath}' -Name '{key2}' -ErrorAction SilentlyContinue
            if ((Get-ChildItem -Path '{registryPath}' -ErrorAction SilentlyContinue).Count -eq 0) {{
                Remove-Item -Path '{registryPath}' -Force -ErrorAction SilentlyContinue
            }}
        }}";

        var removeRegResult = Utils.RunWithErrorHandling(script);
        if (removeRegResult.HasErrors)
        {
            removeRegResult.ShowErrorsToUser();
            return;
        }
    }

    private void addgs(object sender, RoutedEventArgs e)
    {
        Addreg();
        CheckReg();
    }

    private void deletegs(object sender, RoutedEventArgs e)
    {
        RemoveReg();
        CheckReg();
    }
}