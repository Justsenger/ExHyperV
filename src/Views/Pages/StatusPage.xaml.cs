namespace ExHyperV.Views.Pages;

using System.Diagnostics;
using System.Management.Automation;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using Wpf.Ui.Appearance;
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


        //还需要检测WSL子系统。以避免冲突：Microsoft-Windows-Subsystem-Linux


    }

    private async void HyperVInfo()
    {
        PowerShell ps = PowerShell.Create();
        ps.AddScript("Get-Module -ListAvailable -Name Hyper-V");
        var hypervstatus = ps.Invoke();
            Dispatcher.Invoke(() =>
            {
                status3.Children.Remove(progressRing3);
                FontIcon icons = new FontIcon
                {
                    FontSize = 20,
                    FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                    Glyph = "\xE930",
                    Foreground = new SolidColorBrush(Colors.Green),
                };
                if (hypervstatus.Count != 0)
                { hyperv.Text = "HyperV功能已安装。"; }
                else
                {
                    hyperv.Text = "HyperV功能未安装。";
                    icons.Glyph = "\xEA39";
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
            if (buildVersion >= 22000)
            {
                icons.Glyph = "\xE930";
                icons.Foreground = new SolidColorBrush(Colors.Green);
                win.Text = "宿主Windows版本为" + buildVersion.ToString() + "，建议虚拟机版本不小于19041。";
            }
            else
            {
                win.Text = "宿主Windows版本为" + buildVersion.ToString() + "，GPU虚拟化无法选择GPU，功能已禁用。";
                icons.Glyph = "\xEA39";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            //(Get-Command Add-VMGpuPartitionAdapter).Parameters 使用该命令可以查看是否存在InstancePath命令
        });
    }
    private async void CpuInfo()
    {
        PowerShell ps = PowerShell.Create();
        ps.AddScript("(Get-WmiObject -Class Win32_Processor).VirtualizationFirmwareEnabled");
        var cpuvt = ps.Invoke();

        Dispatcher.Invoke(() =>
        {
            status2.Children.Remove(progressRing2);
            FontIcon icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xE930",
                Foreground = new SolidColorBrush(Colors.Green),
            }; 
            if (cpuvt[0].ToString() == "True")
            {cpu.Text = "CPU虚拟化已启用。";}
            else
            {
                cpu.Text = "CPU虚拟化未启用。";
                icons.Glyph = "\xEA39";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            status2.Children.Add(icons);
        });


    }

    private async void CheckReg() //检查是否添加了注册表，已添加则关闭弹窗
    {
        
        // PowerShell 脚本，检查注册表键是否存在
        string script = $@"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and 
        (($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -ne $null) -and 
        ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object {{
            ($k.GetValue($_, $null) -ne $null) -and ($k.GetValueKind($_) -eq 'DWord')
        }}) -notcontains $false)";

        PowerShell ps = PowerShell.Create();
        ps.AddScript(script);
        var result = ps.Invoke();


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
                Glyph = "\xE930",
                Foreground = new SolidColorBrush(Colors.Green),
            };
            if (Isadmin) //如果没有管理员权限，关闭虚拟化功能
            {
                admin.Text = "已获得管理员权限。";
                status4.Children.Add(icons);
            }
            else
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                ms.gpupv.IsEnabled = false;
                admin.Text = "未获得管理员权限。";
                icons.Glyph = "\xEA39";
                icons.Foreground = new SolidColorBrush(Colors.Red);
                status4.Children.Add(icons);
            }
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
        try
        {
            // 创建 PowerShell 实例
            using (PowerShell powerShell = PowerShell.Create())
            {
                // 添加脚本
                powerShell.AddScript(script);
                // 执行脚本
                powerShell.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"发生错误: {ex.Message}");
        }
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

        try
        {
            using (PowerShell powerShell = PowerShell.Create())
            {
                powerShell.AddScript(script);
                powerShell.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"发生错误: {ex.Message}");
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
