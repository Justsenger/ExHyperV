using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public record SchedulerMode(string Name, HyperVSchedulerType Type);

    public record WindowsSku(string Name, string EditionId, string ProductKey);


    public partial class HostPageViewModel : ObservableObject
    {
        // 1. 系统 SKU 列表（使用通用安装密钥）
        public ObservableCollection<WindowsSku> SkuList { get; } = new()
        {
            new WindowsSku("专业工作站版", "ProfessionalWorkstation", "DXG7C-N36C4-C4HTG-X4T3X-2YV77"),
            new WindowsSku("专业版", "Professional", "VK7JG-NPHTM-C97JM-9MPGT-3V66T"),
            new WindowsSku("企业版", "Enterprise", "XGVPP-NMH47-7TTHJ-W3FW7-8HV2C"),
            new WindowsSku("企业 LTSC 版", "EnterpriseS", "M7XTQ-FN8P6-TTKYV-9D4CC-J462D"),
            new WindowsSku("家庭版", "Core", "YTMG3-N6DKC-DKB77-7M9GH-8HVX7"),
            new WindowsSku("教育版", "Education", "YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY")
        };

        [ObservableProperty] private WindowsSku _selectedSku;

        // 2. 处理 SKU 切换 (使用 changepk 执行官方转换流程)
        partial void OnSelectedSkuChanged(WindowsSku value)
        {
            // _isInitialized 确保在程序启动加载初始值时不触发切换
            if (!_isInitialized || value == null) return;

            _ = Task.Run(() => {
                try
                {
                    // 关键：调用系统 changepk.exe 配合通用密钥触发版本升级/转换
                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "changepk.exe",
                        Arguments = $"/ProductKey {value.ProductKey}",
                        UseShellExecute = true,
                        Verb = "runas" // 必须管理员权限
                    };

                    System.Diagnostics.Process.Start(processInfo);

                    // 注意：changepk 是异步弹窗，它自己会提示用户重启。
                    // 这里我们还是弹个提示告知用户后续操作。
                    Application.Current.Dispatcher.Invoke(() => {
                        ShowSnackbar("SKU 切换已启动", "请按照系统弹出的窗口指示完成版本转换。", ControlAppearance.Info, SymbolRegular.Checkmark24);
                    });
                }
                catch (Exception ex)
                {
                    ShowSnackbar("切换失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            });
        }

        private bool _isInitialized = false;

        public CheckStatusViewModel SystemStatus { get; } = new("");
        public CheckStatusViewModel CpuStatus { get; } = new("");
        public CheckStatusViewModel HyperVStatus { get; } = new("");
        public CheckStatusViewModel AdminStatus { get; } = new("");
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
            InitializeCurrentSku();
            await CheckAdminInfoAsync();
            _isInitialized = true;
        }

        private void InitializeCurrentSku()
        {
            try
            {
                // 1. 从注册表读取真实的 EditionID
                string currentId = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "EditionID",
                    "Unknown")?.ToString();

                // 2. 在现有列表中查找匹配项
                var match = SkuList.FirstOrDefault(x => x.EditionId.Equals(currentId, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    SelectedSku = match;
                }
                else
                {
                    var customSku = new WindowsSku($"{currentId}", currentId, "");
                    SkuList.Insert(0, customSku); // 插到第一条
                    SelectedSku = customSku;
                }
            }
            catch
            {
                // 降级处理
            }
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

        private async Task CheckAdminInfoAsync()
        {
            bool isAdmin = await Task.Run(() => {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            });
            AdminStatus.IsSuccess = isAdmin;
            AdminStatus.IsChecking = false;

            if (isAdmin)
            {
                CheckGpuStrategyReg();
                InitializeProductType();
                await LoadAdvancedConfigAsync();
                IsGpuStrategyToggleEnabled = true;

                // --- 修改开始：增加 SKU 限制逻辑 ---
                string currentId = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "EditionID", "")?.ToString() ?? "";

                // 定义禁用的关键字/ID列表
                var forbiddenSkus = new List<string> {
            "Professional",        // 专业版
            "Core",                // 家庭版
            "Enterprise",          // 企业版
            "CoreSingleLanguage",  // 家庭单语言版
            "CoreCountrySpecific"  // 家庭中文版
        };

                // 检查：如果在禁用列表中，或者是服务器版(以Server开头)，则禁用按钮
                bool isUnsupported = forbiddenSkus.Contains(currentId) || currentId.StartsWith("Server", StringComparison.OrdinalIgnoreCase);

                IsSystemSwitchEnabled = !isUnsupported;
                // --- 修改结束 ---
            }
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

        [RelayCommand]
        private async Task EnableHyperVAsync()
        {
            if (AdminStatus.IsSuccess != true) return;
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
                var result = await Utils.Run2(script);
                ok = result.Count > 0 && string.Equals(result[0].ToString(), "OK", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                ok = false;
            }

            if (!ok)
            {
                ShowSnackbar(Translate("Status_Title_Error"), ExHyperV.Properties.Resources.Error_Host_EnableFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                return;
            }

            ShowRestartPrompt(ExHyperV.Properties.Resources.Msg_Host_EnableSuccess);
        }

        private static bool IsHyperVPowerShellModuleAvailable()
        {
            try
            {
                return Utils.Run("Get-Module -ListAvailable -Name Hyper-V | Select-Object -First 1 Name").Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHyperVWmiNamespaceAvailable()
        {
            try
            {
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
                return "状态正常（vmms / Hyper-V 模块 / WMI 已就绪）";
            }

            var missing = new List<string>();
            if (!hypervisor) missing.Add("Hypervisor 未激活");
            if (vmmsStatus == 0) missing.Add("vmms 服务缺失");
            else if (vmmsStatus != 1) missing.Add("vmms 未运行");
            if (!moduleReady) missing.Add("缺少 Hyper-V PowerShell 模块");
            if (!wmiReady) missing.Add(@"缺少 WMI 命名空间 root\virtualization\v2");

            return missing.Count > 0 ? $"缺失组件：{string.Join("；", missing)}" : "Hyper-V 状态未知";
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
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                var txt = new Wpf.Ui.Controls.TextBlock { Text = message, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap };
                var btn = new Wpf.Ui.Controls.Button { Content = Translate("Global_Restart"), Appearance = ControlAppearance.Primary };
                btn.Click += (s, e) => System.Diagnostics.Process.Start("shutdown", "-r -t 0");
                System.Windows.Controls.Grid.SetColumn(btn, 1); grid.Children.Add(txt); grid.Children.Add(btn);
                new Snackbar(p) { Title = Translate("Status_Title_Success"), Content = grid, Appearance = ControlAppearance.Success, Icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24), Timeout = TimeSpan.FromSeconds(15) }.Show();
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
