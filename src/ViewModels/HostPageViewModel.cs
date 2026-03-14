using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Management;
using System.Security.Principal;
using System.Windows;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public record SchedulerMode(string Name, HyperVSchedulerType Type);


    public partial class HostPageViewModel : ObservableObject
    {

        private bool _isInitialized = false;

        public CheckStatusViewModel SystemStatus { get; } = new("");
        public CheckStatusViewModel CpuStatus { get; } = new("");
        public CheckStatusViewModel HyperVStatus { get; } = new("");
        public CheckStatusViewModel VersionStatus { get; } = new("");
        public CheckStatusViewModel IommuStatus { get; } = new("");

        [ObservableProperty] private bool _isGpuStrategyEnabled;
        [ObservableProperty] private bool _isGpuStrategyToggleEnabled = false;
        [ObservableProperty] private bool _isServerSystem;
        [ObservableProperty] private bool _isSystemSwitchEnabled = false;
        [ObservableProperty] private string _systemVersionDesc;
        [ObservableProperty] private bool _isNumaSpanningEnabled;
        [ObservableProperty] private HyperVSchedulerType _currentSchedulerType;

        public ObservableCollection<SchedulerMode> SchedulerModes { get; } = new()
        {
            new SchedulerMode(ExHyperV.Properties.Resources.Scheduler_Classic, HyperVSchedulerType.Classic),
            new SchedulerMode(ExHyperV.Properties.Resources.Scheduler_Core, HyperVSchedulerType.Core),
            new SchedulerMode(ExHyperV.Properties.Resources.Scheduler_Root, HyperVSchedulerType.Root)
        };

        public HostPageViewModel() => _ = LoadInitialStatusAsync();

        private async Task LoadInitialStatusAsync()
        {
            await Task.WhenAll(CheckSystemInfoAsync(), CheckCpuInfoAsync(), CheckHyperVInfoAsync(), CheckServerInfoAsync(), CheckIommuAsync());
            
            await InitializeVersionPolicyAsync();
            _isInitialized = true;
        }

        private async Task CheckSystemInfoAsync() => await Task.Run(() => {
            int buildNumber = Environment.OSVersion.Version.Build;
            string baseVersion = buildNumber.ToString();

            const int MinimumBuild = 17134;

            if (buildNumber >= MinimumBuild)
            {
                VersionStatus.IsSuccess = true;
                VersionStatus.StatusText = baseVersion;
            }
            else
            {
                VersionStatus.IsSuccess = false;
                VersionStatus.StatusText = baseVersion + ExHyperV.Properties.Resources.Status_Msg_GpuPvNotSupported;
            }

            VersionStatus.IsChecking = false;
        });
        private async Task CheckCpuInfoAsync()
        {
            CpuStatus.IsSuccess = await Task.Run(() => HyperVEnvironmentService.IsVirtualizationEnabled());
            CpuStatus.IsChecking = false;
        }

        private async Task CheckHyperVInfoAsync()
        {
            var hTask = Task.Run(() => HyperVEnvironmentService.IsHypervisorPresent());
            var vTask = Task.Run(() => HyperVEnvironmentService.GetVmmsStatus());
            var moduleTask = Task.Run(IsHyperVPowerShellModuleAvailable);
            var wmiTask = Task.Run(IsHyperVWmiNamespaceAvailable);

            await Task.WhenAll(hTask, vTask, moduleTask, wmiTask);

            bool hypervisor = hTask.Result;
            int vmms = vTask.Result;
            bool moduleReady = moduleTask.Result;
            bool wmiReady = wmiTask.Result;

            HyperVStatus.IsInstalled = (vmms != 0);
            HyperVStatus.IsSuccess = hypervisor && (vmms == 1) && moduleReady && wmiReady;
            HyperVStatus.StatusText = BuildHyperVStatusText(hypervisor, vmms, moduleReady, wmiReady);
            HyperVStatus.IsChecking = false;
        }

        private async Task CheckIommuAsync()
        {
            IommuStatus.IsSuccess = await Task.Run(() => HyperVEnvironmentService.IsIommuEnabled());
            IommuStatus.IsChecking = false;
        }

        private async Task InitializeVersionPolicyAsync()
        {

            CheckGpuStrategyReg();
            InitializeProductType();
            await LoadAdvancedConfigAsync();
            IsGpuStrategyToggleEnabled = true;
            IsSystemSwitchEnabled = true;

            //string currentId = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "")?.ToString() ?? "";

            //bool isServer = currentId.StartsWith("Server", StringComparison.OrdinalIgnoreCase);

            //if (isServer)
            //{
            //    IsSystemSwitchEnabled = false;
            //}
            //else
            //{
            //    var restricted = new List<string> { "Professional", "Core", "Enterprise" };
            //    IsSystemSwitchEnabled = !restricted.Contains(currentId);
            //}
        }

        private async Task CheckServerInfoAsync()
        {
            // 调用统一逻辑
            SystemStatus.IsSuccess = await Task.Run(() => HyperVEnvironmentService.IsServerSystem());
            SystemStatus.IsChecking = false;
        }

        private async Task LoadAdvancedConfigAsync()
        {
            try
            {
                bool numa = await HyperVNUMAService.GetNumaSpanningEnabledAsync();
                var sched = await Task.Run(() => HyperVSchedulerService.GetSchedulerType());
                IsNumaSpanningEnabled = numa;
                CurrentSchedulerType = (sched == HyperVSchedulerType.Unknown) ? HyperVSchedulerType.Classic : sched;
            }
            catch { }
        }

        partial void OnIsGpuStrategyEnabledChanged(bool value)
        {
            if (!_isInitialized) return;
            if (value) Utils.AddGpuAssignmentStrategyReg(); else Utils.RemoveGpuAssignmentStrategyReg();
        }

        partial void OnIsNumaSpanningEnabledChanged(bool value)
        {
            if (!_isInitialized) return;
            _ = Task.Run(async () => {
                var (ok, msg) = await HyperVNUMAService.SetNumaSpanningEnabledAsync(value);
                if (!ok)
                {
                    ShowSnackbar(Translate("Status_Title_Error"), msg, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    Application.Current.Dispatcher.Invoke(() => {
                        _isInitialized = false;
                        IsNumaSpanningEnabled = !value; // 遭遇错误回滚按钮
                        _isInitialized = true;
                    });
                }
            });
        }

        partial void OnCurrentSchedulerTypeChanged(HyperVSchedulerType value)
        {
            if (!_isInitialized) return;
            _ = Task.Run(async () => {
                if (await HyperVSchedulerService.SetSchedulerTypeAsync(value))
                    ShowSnackbar(Translate("Status_Title_Info"), ExHyperV.Properties.Resources.Msg_Host_SchedulerChanged, ControlAppearance.Info, SymbolRegular.Info24);
                else
                {
                    ShowSnackbar(Translate("Status_Title_Error"), ExHyperV.Properties.Resources.Error_Host_SchedulerFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    var actual = HyperVSchedulerService.GetSchedulerType();
                    Application.Current.Dispatcher.Invoke(() => {
                        _isInitialized = false;
                        CurrentSchedulerType = actual; // 遭遇错误回滚选项
                        _isInitialized = true;
                    });
                }
            });
        }

        partial void OnIsServerSystemChanged(bool value)
        {
            if (!_isInitialized) return;
            SwitchSystemVersion(value);
        }


        // 禁用 Hyper-V
        [RelayCommand]
        private async Task DisableHyperVAsync()
        {
            // 1. 发送提示
            ShowSnackbar(Translate("Status_Title_Info"), Properties.Resources.HostPageViewModel_1, ControlAppearance.Info, SymbolRegular.Settings24);

            bool ok = false;
            try
            {
                string script = @"
$ErrorActionPreference = 'Stop'
$features = @(
  'Microsoft-Hyper-V-All',
  'Microsoft-Hyper-V',
  'Microsoft-Hyper-V-Services',
  'Microsoft-Hyper-V-Management-PowerShell',
  'Microsoft-Hyper-V-Management-Clients'
)
foreach ($f in $features) {
  $feat = Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue
  if ($null -ne $feat -and $feat.State -eq 'Enabled') {
    Disable-WindowsOptionalFeature -Online -FeatureName $f -NoRestart -Remove -ErrorAction SilentlyContinue | Out-Null
  }
}
'OK'
";
                var result = await Utils.Run2(script);
                ok = result.Count > 0 && string.Equals(result[0].ToString(), "OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Disable Error: {ex.Message}");
                ok = false;
            }

            // 2. 结果判定
            if (!ok)
            {
                ShowSnackbar(Translate("Status_Title_Error"), Properties.Resources.HostPageViewModel_2, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                return;
            }

            // 3. 提示重启
            ShowRestartPrompt(Properties.Resources.HostPageViewModel_3);
        }

        // 启用 Hyper-V

        [RelayCommand]
        private async Task EnableHyperVAsync()
        {
            // 1. 发送提示：正在开启
            ShowSnackbar(Translate("Status_Title_Info"), ExHyperV.Properties.Resources.Msg_Host_EnableHyperV, ControlAppearance.Info, SymbolRegular.Settings24);

            bool ok = false;
            try
            {
                string script = @"
$ErrorActionPreference = 'Stop'
$features = @(
  'Microsoft-Hyper-V-All',
  'Microsoft-Hyper-V',
  'Microsoft-Hyper-V-Services',
  'Microsoft-Hyper-V-Management-PowerShell',
  'Microsoft-Hyper-V-Management-Clients'
)
foreach ($f in $features) {
  $feat = Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue
  if ($null -ne $feat -and $feat.State -ne 'Enabled') {
    Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart -ErrorAction Stop | Out-Null
  }
}
'OK'
";
                // 调用你项目里的 Utils 执行脚本
                var result = await Utils.Run2(script);

                // 检查脚本最后是否输出了 "OK"
                ok = result.Count > 0 && string.Equals(result[0].ToString(), "OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Enable Error: {ex.Message}");
                ok = false;
            }

            // 2. 结果判定
            if (!ok)
            {
                ShowSnackbar(Translate("Status_Title_Error"), ExHyperV.Properties.Resources.Error_Host_EnableFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                return;
            }

            // 3. 提示重启
            ShowRestartPrompt(ExHyperV.Properties.Resources.Msg_Host_EnableSuccess);
        }
        
        // 检查 PowerShell 模块是否真的装上了
        private static bool IsHyperVPowerShellModuleAvailable()
        {
            try
            {
                // 如果能查到模块名，说明管理工具（Management-PowerShell）已就绪
                return Utils.Run("Get-Module -ListAvailable -Name Hyper-V | Select-Object -First 1 Name").Count > 0;
            }
            catch
            {
                return false;
            }
        }

        // 检查 WMI 命名空间
        private static bool IsHyperVWmiNamespaceAvailable()
        {
            try
            {
                // 尝试连接并查询 Hyper-V 管理服务实例
                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
                using var collection = searcher.Get();
                return collection.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildHyperVStatusText(bool hypervisor, int vmmsStatus, bool moduleReady, bool wmiReady)
        {
            if (hypervisor && vmmsStatus == 1 && moduleReady && wmiReady)
            {
                return string.Empty;
            }

            var missing = new List<string>();
            if (!hypervisor) missing.Add(Properties.Resources.HostPageViewModel_5);
            if (vmmsStatus == 0) missing.Add(Properties.Resources.HostPageViewModel_6);
            else if (vmmsStatus != 1) missing.Add(Properties.Resources.HostPageViewModel_7);
            if (!moduleReady) missing.Add(Properties.Resources.HostPageViewModel_8);
            if (!wmiReady) missing.Add(@Properties.Resources.HostPageViewModel_9);

            return missing.Count > 0 ? string.Format(Properties.Resources.HostPageViewModel_10, string.Join("；", missing)) : Properties.Resources.HostPageViewModel_11;
        }

        private void CheckGpuStrategyReg()
        {
            var result = Utils.Run(@"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and ($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -and ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object { ($k.GetValue($_, $null) -ne $null) }) -notcontains $false)");
            IsGpuStrategyEnabled = result.Count > 0 && result[0].ToString().ToLower() == "true";
        }

        private void InitializeProductType()
        {
            // 调用统一逻辑
            IsServerSystem = HyperVEnvironmentService.IsServerSystem();
            UpdateSystemDesc(IsServerSystem);
        }

        private void UpdateSystemDesc(bool isServer) =>
            SystemVersionDesc = $"{Translate("Status_Msg_CurrentVer")}: {(isServer ? Translate("Status_Edition_Server") : Translate("Status_Edition_Workstation"))}";

        private async void SwitchSystemVersion(bool toServer)
        {
            try
            {
                IsSystemSwitchEnabled = false;
                string result = await Task.Run(() => SystemSwitcher.ExecutePatch(toServer ? 1 : 2));
                if (result == "SUCCESS") ShowRestartPrompt(Translate("Status_Msg_RestartNow"));
                else
                {
                    ShowSnackbar(Translate("Status_Title_Error"), result, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
                }
            }
            finally { IsSystemSwitchEnabled = true; }
        }

        private string Translate(string key) => ExHyperV.Properties.Resources.ResourceManager.GetString(key) ?? key;

        public void ShowSnackbar(string title, string msg, ControlAppearance app, SymbolRegular icon)
        {
            Application.Current.Dispatcher.Invoke(() => {
                if (Application.Current.MainWindow?.FindName("SnackbarPresenter") is SnackbarPresenter p)
                    new Snackbar(p) { Title = title, Content = msg, Appearance = app, Icon = new SymbolIcon(icon) { FontSize = 20 }, Timeout = TimeSpan.FromSeconds(4) }.Show();
            });
        }

        private void ShowRestartPrompt(string message)
        {
            Application.Current.Dispatcher.Invoke(() => {
                if (Application.Current.MainWindow?.FindName("SnackbarPresenter") is not SnackbarPresenter p) return;

                var grid = new System.Windows.Controls.Grid();
                grid.VerticalAlignment = VerticalAlignment.Center;
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

                var icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24)
                {
                    FontSize = 24,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };

                var textStack = new System.Windows.Controls.StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var titleTxt = new Wpf.Ui.Controls.TextBlock
                {
                    Text = Translate("Status_Title_Success"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Margin = new Thickness(0)
                };

                var msgTxt = new Wpf.Ui.Controls.TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    Margin = new Thickness(0, -2, 0, 0)
                };

                textStack.Children.Add(titleTxt);
                textStack.Children.Add(msgTxt);

                var btn = new Wpf.Ui.Controls.Button
                {
                    Content = Translate("Global_Restart"),
                    Appearance = ControlAppearance.Primary,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 10, 0)
                };
                btn.Click += (s, e) => System.Diagnostics.Process.Start("shutdown", "-r -t 0");

                System.Windows.Controls.Grid.SetColumn(icon, 0);
                System.Windows.Controls.Grid.SetColumn(textStack, 1);
                System.Windows.Controls.Grid.SetColumn(btn, 2);

                grid.Children.Add(icon);
                grid.Children.Add(textStack);
                grid.Children.Add(btn);

                var snackbar = new Snackbar(p)
                {
                    Content = grid,
                    Appearance = ControlAppearance.Success,
                    Timeout = TimeSpan.FromSeconds(15)
                };

                snackbar.Show();
            });
        }
    }

    public partial class CheckStatusViewModel : ObservableObject
    {
        [ObservableProperty] private bool _isChecking = true;
        [ObservableProperty] private string _statusText;
        [ObservableProperty] private bool? _isSuccess;
        [ObservableProperty] private bool _isInstalled;
        public string IconGlyph => IsSuccess switch { true => "\uEC61", false => "\uEB90", _ => "\uE946" };
        public System.Windows.Media.Brush IconColor => IsSuccess switch
        {
            true => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 0, 138, 23)),
            false => System.Windows.Media.Brushes.Red,
            _ => System.Windows.Media.Brushes.Gray
        };
        public CheckStatusViewModel(string initialText) => _statusText = initialText;
        partial void OnIsSuccessChanged(bool? value) { OnPropertyChanged(nameof(IconGlyph)); OnPropertyChanged(nameof(IconColor)); }
    }
}
