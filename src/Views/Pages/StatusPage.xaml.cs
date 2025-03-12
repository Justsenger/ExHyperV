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


        //����Ҫ���WSL��ϵͳ���Ա����ͻ��Microsoft-Windows-Subsystem-Linux


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
                { hyperv.Text = "HyperV�����Ѱ�װ��"; }
                else
                {
                    hyperv.Text = "HyperV����δ��װ��";
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
                win.Text = "����Windows�汾Ϊ" + buildVersion.ToString() + "������������汾��С��19041��";
            }
            else
            {
                win.Text = "����Windows�汾Ϊ" + buildVersion.ToString() + "��GPU���⻯�޷�ѡ��GPU�������ѽ��á�";
                icons.Glyph = "\xEA39";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            //(Get-Command Add-VMGpuPartitionAdapter).Parameters ʹ�ø�������Բ鿴�Ƿ����InstancePath����
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
            {cpu.Text = "CPU���⻯�����á�";}
            else
            {
                cpu.Text = "CPU���⻯δ���á�";
                icons.Glyph = "\xEA39";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            status2.Children.Add(icons);
        });


    }

    private async void CheckReg() //����Ƿ������ע����������رյ���
    {
        
        // PowerShell �ű������ע�����Ƿ����
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
            if (Isadmin) //���û�й���ԱȨ�ޣ��ر����⻯����
            {
                admin.Text = "�ѻ�ù���ԱȨ�ޡ�";
                status4.Children.Add(icons);
            }
            else
            {
                var ms = Application.Current.MainWindow as MainWindow; //��ȡ������
                ms.gpupv.IsEnabled = false;
                admin.Text = "δ��ù���ԱȨ�ޡ�";
                icons.Glyph = "\xEA39";
                icons.Foreground = new SolidColorBrush(Colors.Red);
                status4.Children.Add(icons);
            }
        });

    }



    public void Addreg()
    {
        // ע���·���ͼ���
        string registryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        string key1 = "RequireSecureDeviceAssignment";
        string key2 = "RequireSupportedDeviceAssignment";

        // PowerShell �ű�
        string script = $@"
        # ȷ��ע���·�����ڣ���������ڣ��򴴽���
        if (-not (Test-Path '{registryPath}')) {{
            New-Item -Path '{registryPath}' -Force
        }}

        # ��鲢����ע����ֵ
        if (-not (Test-Path '{registryPath}\\{key1}')) {{
            Set-ItemProperty -Path '{registryPath}' -Name '{key1}' -Value 0 -Type DWord
        }}

        if (-not (Test-Path '{registryPath}\\{key2}')) {{
            Set-ItemProperty -Path '{registryPath}' -Name '{key2}' -Value 0 -Type DWord
        }}";
        try
        {
            // ���� PowerShell ʵ��
            using (PowerShell powerShell = PowerShell.Create())
            {
                // ��ӽű�
                powerShell.AddScript(script);
                // ִ�нű�
                powerShell.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"��������: {ex.Message}");
        }
    }

    public void RemoveReg()
    {
        // ע���·���ͼ���
        string registryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        string key1 = "RequireSecureDeviceAssignment";
        string key2 = "RequireSupportedDeviceAssignment";

        // PowerShell �ű�
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
            System.Windows.MessageBox.Show($"��������: {ex.Message}");
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
