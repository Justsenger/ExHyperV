using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ExHyperV.Views.Pages;

public partial class StatusPage
{
    public StatusPage()
    {
        InitializeComponent();

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

    private async void HyperVInfo()
    {
        var message = LocalizationHelper.GetString("exhyperv");

        var hypervstatus = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");
        Dispatcher.Invoke(() =>
        {
            status3.Children.Remove(progressRing3);
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
        });
    }

    private async void SysInfo()
    {
        var buildVersion = Environment.OSVersion.Version.Build;
        Dispatcher.Invoke(() =>
        {
            status1.Children.Remove(progressRing1);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xF167",
                Foreground = GetInfoColor()
            };
            status1.Children.Add(icons);
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
                ms.gpupv.IsEnabled = false;
                var string3 = LocalizationHelper.GetString("String3");
                var disablegpu = LocalizationHelper.GetString("disablegpu");
                win.Text = string3 + buildVersion + disablegpu;
                icons.Glyph = "\xEB90";
                icons.Foreground = GetErrorColor();
            }
        });
    }

    private async void CpuInfo()
    {
        var cpuvt1 = Utils.Run("(Get-WmiObject -Class Win32_Processor).VirtualizationFirmwareEnabled");
        var cpuvt2 = Utils.Run("(Get-WmiObject -Class Win32_ComputerSystem).HypervisorPresent");


        Dispatcher.Invoke(() =>
        {
            status2.Children.Remove(progressRing2);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = GetSuccessColor()
            };

            if (cpuvt1[0].ToString() == "True" || cpuvt2[0].ToString() == "True")
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
        });
    }

    private async void CheckReg() //检查是否添加了注册表，已添加则关闭弹窗
    {
        var script = @"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and 
        (($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -ne $null) -and 
        ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object {
            ($k.GetValue($_, $null) -ne $null) -and ($k.GetValueKind($_) -eq 'DWord')
        }) -notcontains $false)";
        var result = Utils.Run(script);

        Dispatcher.Invoke(() =>
        {
            if (result[0].ToString().ToLower() == "true") gpustrategy.IsChecked = true;
        });
    }

    private async void Admininfo()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var Isadmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        Dispatcher.Invoke(() =>
        {
            status4.Children.Remove(progressRing4);
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
                status4.Children.Add(icons);
            }
            else //如果没有管理员权限，关闭GPU虚拟化功能
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                ms.gpupv.IsEnabled = false;
                admin.Text = LocalizationHelper.GetString("Admin2");
                icons.Glyph = "\xEB90";
                icons.Foreground = GetErrorColor();
                status4.Children.Add(icons);
            }
        });
    }

    private async void ServerInfo()
    {
        var result = Utils.Run("(Get-WmiObject -Class Win32_OperatingSystem).ProductType");
        Dispatcher.Invoke(() =>
        {
            status5.Children.Remove(progressRing5);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = GetSuccessColor()
            };
            if (result[0].ToString() == "3")
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
        });
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
        if (-not (Test-Path '{registryPath}\\{key1}')) {{
            Set-ItemProperty -Path '{registryPath}' -Name '{key1}' -Value 0 -Type DWord
        }}

        if (-not (Test-Path '{registryPath}\\{key2}')) {{
            Set-ItemProperty -Path '{registryPath}' -Name '{key2}' -Value 0 -Type DWord
        }}";
        Utils.Run(script);
    }

    public void RemoveReg()
    {
        // 注册表路径和键名
        var registryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        var key1 = "RequireSecureDeviceAssignment";
        var key2 = "RequireSupportedDeviceAssignment";

        // PowerShell 脚本
        var script = $@"
        Remove-ItemProperty -Path '{registryPath}' -Name '{key1}'
        Remove-ItemProperty -Path '{registryPath}' -Name '{key2}'
        Remove-Item -Path '{registryPath}' -Force";

        Utils.Run(script);
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