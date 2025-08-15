using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Tools;
using Microsoft.Win32.TaskScheduler;
using System;
using System.IO;
using System.Security.Principal;
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace ExHyperV.ViewModels
{
    /// <summary>
    /// StatusPage页面的ViewModel, 负责所有逻辑和数据。
    /// </summary>
    public partial class StatusPageViewModel : ObservableObject
    {
        // --- 状态检查属性 ---
        public CheckStatusViewModel SystemStatus { get; }
        public CheckStatusViewModel CpuStatus { get; }
        public CheckStatusViewModel HyperVStatus { get; }
        public CheckStatusViewModel AdminStatus { get; }
        public CheckStatusViewModel VersionStatus { get; }
        public CheckStatusViewModel IommuStatus { get; }

        // --- 开关绑定属性 ---
        [ObservableProperty]
        private bool _isGpuStrategyEnabled;

        [ObservableProperty]
        private bool _isGpuStrategyToggleEnabled = false;

        [ObservableProperty]
        private bool _isAutoTurboEnabled;

        [ObservableProperty]
        private bool _isAutoTurboToggleEnabled = false;


        private const string TaskName = "Auto Turbo Boost - ExhyperV";
        private const string AutoTurboScriptName = "ExHyperV_AutoTurboBoost.ps1";
        private static readonly string ScriptInstallPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExHyperV",
            AutoTurboScriptName);

        public StatusPageViewModel()
        {
            SystemStatus = new CheckStatusViewModel(Properties.Resources.checksys);
            CpuStatus = new CheckStatusViewModel(Properties.Resources.checkcpuct);
            HyperVStatus = new CheckStatusViewModel(Properties.Resources.checkhyperv);
            AdminStatus = new CheckStatusViewModel(Properties.Resources.checkadmin);
            VersionStatus = new CheckStatusViewModel(Properties.Resources.checkversion);
            IommuStatus = new CheckStatusViewModel(Properties.Resources.Status_CheckingBiosIommu);

            _ = LoadInitialStatusAsync();
        }

        private async System.Threading.Tasks.Task LoadInitialStatusAsync()
        {
            await System.Threading.Tasks.Task.WhenAll(
                CheckSystemInfoAsync(),
                CheckCpuInfoAsync(),
                CheckHyperVInfoAsync(),
                CheckServerInfoAsync(),
                CheckIommuAsync(),
                CheckAndSetInitialTurboToggleStateAsync()
            );

            await CheckAdminInfoAsync(); // 管理员检查依赖其他结果，最后运行
        }

        #region 检查逻辑

        private async System.Threading.Tasks.Task CheckSystemInfoAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                int buildVersion = Environment.OSVersion.Version.Build;
                if (buildVersion >= 22000)
                {
                    SystemStatus.StatusText = $"{Properties.Resources.String3}{buildVersion}{Properties.Resources.v19041}";
                    SystemStatus.IsSuccess = true;
                }
                else
                {
                    SystemStatus.StatusText = $"{Properties.Resources.String3}{buildVersion}{Properties.Resources.disablegpu}";
                    SystemStatus.IsSuccess = false;
                }
                SystemStatus.IsChecking = false;
            });
        }

        private async System.Threading.Tasks.Task CheckCpuInfoAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var cpuvt1 = Utils.Run("(Get-CimInstance -Class Win32_Processor).VirtualizationFirmwareEnabled");
                var cpuvt2 = Utils.Run("(Get-CimInstance -Class Win32_ComputerSystem).HypervisorPresent");

                CpuStatus.IsSuccess = cpuvt1.Count > 0 && cpuvt2.Count > 0 &&
                                      (cpuvt1[0].ToString() == "True" || cpuvt2[0].ToString() == "True");
                CpuStatus.StatusText = CpuStatus.IsSuccess == true ? Properties.Resources.GPU1 : Properties.Resources.GPU2;
                CpuStatus.IsChecking = false;
            });
        }

        private async System.Threading.Tasks.Task CheckHyperVInfoAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var hypervstatus = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");
                HyperVStatus.IsSuccess = hypervstatus.Count != 0;
                HyperVStatus.StatusText = HyperVStatus.IsSuccess == true ? Properties.Resources.String1 : Properties.Resources.String2;
                HyperVStatus.IsChecking = false;
            });
        }

        private async System.Threading.Tasks.Task CheckAdminInfoAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

                AdminStatus.IsSuccess = isAdmin;
                AdminStatus.StatusText = isAdmin ? Properties.Resources.Admin1 : Properties.Resources.Admin2;

                if (isAdmin)
                {
                    IsGpuStrategyToggleEnabled = true;
                    IsAutoTurboToggleEnabled = true;
                    CheckGpuStrategyReg();
                }
                AdminStatus.IsChecking = false;
            });
        }

        private async System.Threading.Tasks.Task CheckServerInfoAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var result = Utils.Run("(Get-CimInstance -Class Win32_OperatingSystem).ProductType");
                VersionStatus.IsSuccess = result.Count > 0 && result[0].ToString() == "3";
                VersionStatus.StatusText = VersionStatus.IsSuccess == true ? Properties.Resources.Isserver : Properties.Resources.ddaa;
                VersionStatus.IsChecking = false;
            });
        }

        private async System.Threading.Tasks.Task CheckIommuAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var io = Utils.Run("(Get-CimInstance -Namespace \"Root\\Microsoft\\Windows\\DeviceGuard\" -ClassName \"Win32_DeviceGuard\").AvailableSecurityProperties -contains 3");
                IommuStatus.IsSuccess = io.Count > 0 && io[0].ToString() == "True";
                IommuStatus.StatusText = IommuStatus.IsSuccess == true ? ExHyperV.Properties.Resources.Info_BiosIommuEnabled : ExHyperV.Properties.Resources.Error_BiosIommuDisabled;
                IommuStatus.IsChecking = false;
            });
        }

        #endregion

        #region 开关逻辑

        partial void OnIsGpuStrategyEnabledChanged(bool value)
        {
            if (value) AddGpuStrategyReg(); else RemoveGpuStrategyReg();
        }

        partial void OnIsAutoTurboEnabledChanged(bool value)
        {
            if (value) EnableAutoTurbo(); else DisableAutoTurbo();
        }

        private void CheckGpuStrategyReg()
        {
            string script = @"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and ($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -and ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object { ($k.GetValue($_, $null) -ne $null) }) -notcontains $false)";
            var result = Utils.Run(script);
            // 直接设置属性，避免再次触发OnChanged回调
            SetProperty(ref _isGpuStrategyEnabled, result.Count > 0 && result[0].ToString().ToLower() == "true", nameof(IsGpuStrategyEnabled));
        }

        private void AddGpuStrategyReg()
        {
            string path = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
            string script = $@"
                if (-not (Test-Path '{path}')) {{ New-Item -Path '{path}' -Force }}
                Set-ItemProperty -Path '{path}' -Name 'RequireSecureDeviceAssignment' -Value 0 -Type DWord
                Set-ItemProperty -Path '{path}' -Name 'RequireSupportedDeviceAssignment' -Value 0 -Type DWord";
            Utils.Run(script);
        }

        private void RemoveGpuStrategyReg()
        {
            string path = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
            string script = $@"
                Remove-ItemProperty -Path '{path}' -Name 'RequireSecureDeviceAssignment' -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path '{path}' -Name 'RequireSupportedDeviceAssignment' -ErrorAction SilentlyContinue";
            Utils.Run(script);
        }

        private async System.Threading.Tasks.Task CheckAndSetInitialTurboToggleStateAsync()
        {
            bool isRunning = await System.Threading.Tasks.Task.Run(() => {
                try
                {
                    using var ts = new TaskService();
                    ScheduledTask task = ts.FindTask(TaskName);
                    return task != null && task.State == TaskState.Running;
                }
                catch { return false; }
            });
            SetProperty(ref _isAutoTurboEnabled, isRunning, nameof(IsAutoTurboEnabled));
        }

        private void EnableAutoTurbo()
        {
            try
            {
                using var ts = new TaskService();
                if (ts.FindTask(TaskName) != null) return;

                // 1. 调用新方法，确保脚本文件存在于系统目录，并获取其路径
                string scriptPath = EnsureScriptFileExists();

                // 2. 创建计划任务，引用这个稳定、持久的脚本路径
                var td = ts.NewTask();
                td.RegistrationInfo.Description = ExHyperV.Properties.Resources.Description_HyperVRfScheduler;
                td.Triggers.Add(new BootTrigger { Delay = TimeSpan.FromSeconds(30) });
                td.Actions.Add(new ExecAction("powershell.exe", $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\""));
                td.Principal.UserId = "NT AUTHORITY\\SYSTEM";
                td.Principal.LogonType = TaskLogonType.ServiceAccount;
                td.Principal.RunLevel = TaskRunLevel.Highest;
                td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                ts.RootFolder.RegisterTaskDefinition(TaskName, td).Run();
            }
            catch (Exception ex)
            {
                // 增加更详细的错误输出
                System.Diagnostics.Debug.WriteLine($"[ERROR] EnableAutoTurbo: Failed to create or run task. Exception: {ex}");
                // 可以在这里弹出一个用户友好的错误提示
                Utils.Show2($"创建自动 Turbo 任务失败：\n{ex.Message}");
            }
        }

        private void DisableAutoTurbo()
        {
            try
            {
                using var ts = new TaskService();
                ScheduledTask task = ts.FindTask(TaskName);
                if (task != null)
                {
                    task.Stop();
                    ts.RootFolder.DeleteTask(TaskName);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ERROR] DisableAutoTurbo: {ex.Message}"); }
        }

        private string EnsureScriptFileExists()
        {
            // 如果文件已经存在，直接返回它的路径
            if (File.Exists(ScriptInstallPath))
            {
                return ScriptInstallPath;
            }

            // 如果文件不存在，则从资源中提取
            var resourceUri = new Uri("/assets/autoturboboost.ps1", UriKind.Relative);
            var resourceInfo = System.Windows.Application.GetResourceStream(resourceUri);

            if (resourceInfo == null)
            {
                // 这是一个严重的程序错误，说明资源没打包进来
                throw new FileNotFoundException("无法在 DLL 中找到嵌入的脚本资源。", resourceUri.ToString());
            }

            // 确保目标目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptInstallPath));

            // 将资源流的内容写入到目标文件中
            using (var resourceStream = resourceInfo.Stream)
            using (var fileStream = new FileStream(ScriptInstallPath, FileMode.Create, FileAccess.Write))
            {
                resourceStream.CopyTo(fileStream);
            }

            return ScriptInstallPath;
        }

        #endregion
    }
}