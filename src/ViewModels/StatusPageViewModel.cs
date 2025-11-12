using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Tools;

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
                CheckIommuAsync()
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
            if (value) Utils.AddGpuAssignmentStrategyReg(); else Utils.RemoveGpuAssignmentStrategyReg();
        }

        private void CheckGpuStrategyReg()
        {
            string script = @"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and ($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -and ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object { ($k.GetValue($_, $null) -ne $null) }) -notcontains $false)";
            var result = Utils.Run(script);
            // 直接设置属性，避免再次触发OnChanged回调
            SetProperty(ref _isGpuStrategyEnabled, result.Count > 0 && result[0].ToString().ToLower() == "true", nameof(IsGpuStrategyEnabled));
        }
        #endregion
    }
}