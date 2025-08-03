namespace ExHyperV.Views.Pages;

using System.Management.Automation;
using System.Security.Principal;
using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using ExHyperV;
using ExHyperV.Tools;
using Microsoft.Win32.TaskScheduler;
using Wpf.Ui.Controls;


using Task = System.Threading.Tasks.Task; // 确保这个 using 存在
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;
using MessageBox = Wpf.Ui.Controls.MessageBox;

public partial class StatusPage
{

    private const string TaskName = "Auto Turbo Boost - ExhyperV";
    private static readonly string ScriptFileName = Path.Combine("Assets", "AutoTurboBoost.ps1");

    public StatusPage()
    {
        InitializeComponent();

        Task.Run(() => CpuInfo());
        Task.Run(() => SysInfo());
        Task.Run(() => HyperVInfo());

        Task.Run(() => Admininfo());
        Task.Run(() => ServerInfo());
        Task.Run(() => Iommu());


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
            if (buildVersion >= 22000) //不允许宿主使用过低的系统版本，WDDM版本低，以及PS命令存在问题。
            {
                icons.Glyph = "\xEC61";
                icons.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23));
                win.Text = ExHyperV.Properties.Resources.String3 + buildVersion.ToString() + ExHyperV.Properties.Resources.v19041;
            }
            else
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                ms.gpupv.IsEnabled = false;
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

            if (cpuvt1[0].ToString() == "True" || cpuvt2[0].ToString() == "True")
            { cpu.Text = ExHyperV.Properties.Resources.GPU1; }
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

    private async void Iommu() //检查IOMMU功能
    {
        var io = Utils.Run("(Get-CimInstance -Namespace \"Root\\Microsoft\\Windows\\DeviceGuard\" -ClassName \"Win32_DeviceGuard\").AvailableSecurityProperties -contains 3");
        Dispatcher.Invoke(() =>
        {
            status6.Children.Remove(progressRing6);
            FontIcon icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23)),

            };

            if (io[0].ToString() == "True")
            { iommu.Text = "BIOS已启用IOMMU。"; }
            else
            {
                iommu.Text = "BIOS未启用IOMMU，DDA和GPU-PV不可用。";
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            status6.Children.Add(icons);
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
                Task.Run(() => CheckReg());
            }
            else //如果没有管理员权限，关闭所有功能
            {
                var ms = Application.Current.MainWindow as MainWindow; //获取主窗口
                ms.gpupv.IsEnabled = false;
                //ms.dda.IsEnabled = false;
                ms.VMnet.IsEnabled = false;
                admin.Text = ExHyperV.Properties.Resources.Admin2;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
                status4.Children.Add(icons);
                gpustrategy.IsEnabled = false;

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
            if (result[0].ToString() == "3") { version.Text = ExHyperV.Properties.Resources.Isserver; }
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
    /// <summary>
    /// 【重构】处理开关状态变化的核心事件。
    /// </summary>
    private void OnTurboToggleSwitchClicked(object sender, RoutedEventArgs e)
    {
        var toggleSwitch = sender as ToggleSwitch;
        if (toggleSwitch == null) return;

        // IsChecked 是 nullable bool (bool?)，所以要和 true 比较
        if (toggleSwitch.IsChecked == true)
        {
            EnableAutoTurbo();
        }
        else
        {
            DisableAutoTurbo();
        }
    }

    /// <summary>
    /// 【重构】负责开启服务的健壮逻辑。
    /// </summary>
    private void EnableAutoTurbo()
    {
        string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ScriptFileName);
        if (!File.Exists(scriptPath)) return;

        try
        {
            using (var ts = new TaskService())
            {
                var task = ts.FindTask(TaskName);
                if (task != null)
                {
                    // **健壮性处理 1: 任务已存在**
                    // 如果任务没有在运行，则直接运行它。
                    if (task.State != TaskState.Running)
                    {
                        task.Run();
                    }
                    // 如果已在运行，则什么也不做。
                }
                else
                {
                    // **健壮性处理 2: 任务不存在，则创建并运行**
                    var td = ts.NewTask();
                    td.RegistrationInfo.Description = "Hyper-V 极简电源调度器，由 ExhyperV 程序创建和管理。";
                    td.Triggers.Add(new BootTrigger { Delay = TimeSpan.FromSeconds(30) });

                    string arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"";
                    string workingDirectory = Path.GetDirectoryName(scriptPath);
                    td.Actions.Add(new ExecAction("powershell.exe", arguments, workingDirectory));

                    td.Principal.UserId = "NT AUTHORITY\\SYSTEM";
                    td.Principal.LogonType = TaskLogonType.ServiceAccount;
                    td.Principal.RunLevel = TaskRunLevel.Highest;

                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                    td.Settings.MultipleInstances = TaskInstancesPolicy.Queue;

                    var newTask = ts.RootFolder.RegisterTaskDefinition(TaskName, td);
                    newTask.Run();
                }
            }
        }
        catch (Exception ex)
        {
            // 静默操作，但可以在调试时打印日志
            System.Diagnostics.Debug.WriteLine($"[ERROR] EnableAutoTurbo failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 【重构】负责关闭服务的健壮逻辑。
    /// </summary>
    private void DisableAutoTurbo()
    {
        try
        {
            using (var ts = new TaskService())
            {
                var task = ts.FindTask(TaskName);
                if (task != null)
                {
                    task.Stop(); // 停止所有正在运行的实例
                    ts.RootFolder.DeleteTask(TaskName); // 删除计划任务
                }
            }
        }
        catch (Exception ex)
        {
            // 静默操作，但可以在调试时打印日志
            System.Diagnostics.Debug.WriteLine($"[ERROR] DisableAutoTurbo failed: {ex.Message}");
        }
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // 调用异步检查方法
        await CheckAndSetInitialToggleStateAsync();
    }
    private async Task CheckAndSetInitialToggleStateAsync()
    {
        // 在后台线程上执行检查，避免阻塞UI
        bool isCurrentlyRunning = await Task.Run(() =>
        {
            try
            {
                using (var ts = new TaskService())
                {
                    var task = ts.FindTask(TaskName);
                    // “开启”状态的唯一定义：任务存在并且正在运行。
                    return task != null && task.State == TaskState.Running;
                }
            }
            catch
            {
                // 如果检查时发生任何错误（如权限问题），都默认为关闭状态
                return false;
            }
        });

        // 在UI线程上更新ToggleButton的状态
        // 使用 Dispatcher 是因为我们是从后台线程返回来更新UI
        await Dispatcher.InvokeAsync(() =>
        {
            // 关键修复：暂时禁用 Click 事件
            TurboToggleSwitch.Click -= OnTurboToggleSwitchClicked;
            // 关键修复：使用正确的属性 IsChecked
            TurboToggleSwitch.IsChecked = isCurrentlyRunning;
            // 关键修复：重新启用 Click 事件
            TurboToggleSwitch.Click += OnTurboToggleSwitchClicked;
        });
    }
}
