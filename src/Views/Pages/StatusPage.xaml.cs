namespace ExHyperV.Views.Pages;

using System.Management.Automation;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

public partial class StatusPage
{

    public StatusPage() {
        InitializeComponent();

        Task.Run(() => CpuInfo());
        Task.Run(() => SysInfo());
        Task.Run(() => HyperVInfo());
        Task.Run(() => CheckReg());
        Task.Run(() => Admininfo());
        Task.Run(() => ServerInfo());
    }

    private async void HyperVInfo()
    {

        string message = Properties.Resources.exhyperv;

        var hypervstatus = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");
        Dispatcher.Invoke(() =>
        {
            status3.Children.Remove(progressRing3);
            FontIcon icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23)),
            };
            if (hypervstatus.Count != 0) { hyperv.Text = Properties.Resources.String1; }
            else
            {
                hyperv.Text = ExHyperV.Properties.Resources.String2;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            status3.Children.Add(icons);
        });
    }

    private async void SysInfo()
    {
        int buildVersion = Environment.OSVersion.Version.Build;
        Dispatcher.Invoke(() =>
        {
            status1.Children.Remove(progressRing1);
            FontIcon icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xF167",
                Foreground = new SolidColorBrush(Colors.DodgerBlue),
            };
            status1.Children.Add(icons);
            if (buildVersion >= 22000) //不允许宿主使用过低的系统版本，WDDM版本低，以及PS存在命令问题。
            {
                icons.Glyph = "\xEC61";
                icons.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23));
                win.Text = ExHyperV.Properties.Resources.String3 + buildVersion.ToString() + ExHyperV.Properties.Resources.v19041;
            }
            else
            {
                win.Text = ExHyperV.Properties.Resources.String3 + buildVersion.ToString() + ExHyperV.Properties.Resources.disablegpu;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
        });
    }
    private async void CpuInfo()
    {

        var cpuvt1 = Utils.Run("(Get-CimInstance -Class Win32_Processor).VirtualizationFirmwareEnabled");
        var cpuvt2 = Utils.Run("(Get-CimInstance -Class Win32_ComputerSystem).HypervisorPresent");


        Dispatcher.Invoke(() =>
        {
            status2.Children.Remove(progressRing2);
            FontIcon icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23)),
         
            };

            if (cpuvt1[0].ToString() == "True"|| cpuvt2[0].ToString() == "True")
            {cpu.Text = ExHyperV.Properties.Resources.GPU1;}
            else
            {
                cpu.Text = ExHyperV.Properties.Resources.GPU2;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            status2.Children.Add(icons);
        });


    }

    private async void CheckReg() //检查是否添加了注册表，已添加则关闭弹窗
    {
        string script = $@"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and 
        (($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -ne $null) -and 
        ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object {{
            ($k.GetValue($_, $null) -ne $null) -and ($k.GetValueKind($_) -eq 'DWord')
        }}) -notcontains $false)";
        var result = Utils.Run(script);

        Dispatcher.Invoke(() =>
        {
            if (result[0].ToString().ToLower() == "true")
            { 
                gpustrategy.IsChecked = true;
            }
        });

    }

    private async void Admininfo() 
    {

        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        bool Isadmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        Dispatcher.Invoke(() =>
        {

            status4.Children.Remove(progressRing4);
            FontIcon icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23)),
            };
            if (Isadmin) 
            {
                admin.Text = ExHyperV.Properties.Resources.Admin1;
                status4.Children.Add(icons);
            }
            else //如果没有管理员权限，关闭GPU虚拟化功能
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                ms.gpupv.IsEnabled = false;
                admin.Text = ExHyperV.Properties.Resources.Admin2;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
                status4.Children.Add(icons);
            }
        });

    }

    private async void ServerInfo()
    {

        var result = Utils.Run("(Get-CimInstance -Class Win32_OperatingSystem).ProductType");
        Dispatcher.Invoke(() =>
        {
            status5.Children.Remove(progressRing5);
            FontIcon icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23)),
            };
            if (result[0].ToString()=="3") { version.Text = ExHyperV.Properties.Resources.Isserver; }
            else
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                //ms.dda.IsEnabled = false;

                version.Text = ExHyperV.Properties.Resources.ddaa;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            status5.Children.Add(icons);
        });
    }



    public void Addreg()
    {
        // 注册表路径和键名
        string registryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        string key1 = "RequireSecureDeviceAssignment";
        string key2 = "RequireSupportedDeviceAssignment";

        // PowerShell 脚本
        string script = $@"
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
        string registryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        string key1 = "RequireSecureDeviceAssignment";
        string key2 = "RequireSupportedDeviceAssignment";

        // PowerShell 脚本
        string script = $@"
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
