using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Tools;
using Microsoft.Win32;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.ViewModels
{
    public partial class StatusPageViewModel : ObservableObject
    {
        private bool _isInitialized = false;

        public CheckStatusViewModel SystemStatus { get; }
        public CheckStatusViewModel CpuStatus { get; }
        public CheckStatusViewModel HyperVStatus { get; }
        public CheckStatusViewModel AdminStatus { get; }
        public CheckStatusViewModel VersionStatus { get; }
        public CheckStatusViewModel IommuStatus { get; }

        [ObservableProperty] private bool _isGpuStrategyEnabled;
        [ObservableProperty] private bool _isGpuStrategyToggleEnabled = false;
        [ObservableProperty] private bool _isServerSystem;
        [ObservableProperty] private bool _isSystemSwitchEnabled = false;
        [ObservableProperty] private string _systemVersionDesc;

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

        private async Task LoadInitialStatusAsync()
        {
            await Task.WhenAll(CheckSystemInfoAsync(), CheckCpuInfoAsync(), CheckHyperVInfoAsync(), CheckServerInfoAsync(), CheckIommuAsync());
            await CheckAdminInfoAsync();
        }

        private async Task CheckSystemInfoAsync()
        {
            await Task.Run(() =>
            {
                int buildVersion = Environment.OSVersion.Version.Build;
                bool success = buildVersion >= 17134;
                SystemStatus.IsSuccess = success;
                SystemStatus.StatusText = $"{Properties.Resources.String3}{buildVersion}{(success ? Properties.Resources.v19041 : Properties.Resources.disablegpu)}";
                SystemStatus.IsChecking = false;
            });
        }

        private async Task CheckCpuInfoAsync()
        {
            await Task.Run(() =>
            {
                var cpuvt1 = Utils.Run("(Get-CimInstance -Class Win32_Processor).VirtualizationFirmwareEnabled");
                var cpuvt2 = Utils.Run("(Get-CimInstance -Class Win32_ComputerSystem).HypervisorPresent");
                bool success = cpuvt1.Count > 0 && cpuvt2.Count > 0 && (cpuvt1[0].ToString() == "True" || cpuvt2[0].ToString() == "True");
                CpuStatus.IsSuccess = success;
                CpuStatus.StatusText = success ? Properties.Resources.GPU1 : Properties.Resources.GPU2;
                CpuStatus.IsChecking = false;
            });
        }

        private async Task CheckHyperVInfoAsync()
        {
            await Task.Run(() =>
            {
                var hypervstatus = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");
                bool success = hypervstatus.Count != 0;
                HyperVStatus.IsSuccess = success;
                HyperVStatus.StatusText = success ? Properties.Resources.String1 : Properties.Resources.String2;
                HyperVStatus.IsChecking = false;
            });
        }

        private async Task CheckAdminInfoAsync()
        {
            await Task.Run(() =>
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
                    IsSystemSwitchEnabled = true;
                    InitializeProductType();
                }
                AdminStatus.IsChecking = false;
            });
        }

        private async Task CheckServerInfoAsync()
        {
            await Task.Run(() =>
            {
                var result = Utils.Run("(Get-CimInstance -Class Win32_OperatingSystem).ProductType");
                bool success = result.Count > 0 && result[0].ToString() == "3";
                VersionStatus.IsSuccess = success;
                VersionStatus.StatusText = success ? Properties.Resources.Isserver : Properties.Resources.ddaa;
                VersionStatus.IsChecking = false;
            });
        }

        private async Task CheckIommuAsync()
        {
            await Task.Run(() =>
            {
                var io = Utils.Run("(Get-CimInstance -Namespace \"Root\\Microsoft\\Windows\\DeviceGuard\" -ClassName \"Win32_DeviceGuard\").AvailableSecurityProperties -contains 3");
                bool success = io.Count > 0 && io[0].ToString() == "True";
                IommuStatus.IsSuccess = success;
                IommuStatus.StatusText = success ? ExHyperV.Properties.Resources.Info_BiosIommuEnabled : ExHyperV.Properties.Resources.Error_BiosIommuDisabled;
                IommuStatus.IsChecking = false;
            });
        }

        partial void OnIsGpuStrategyEnabledChanged(bool value)
        {
            if (value) Utils.AddGpuAssignmentStrategyReg(); else Utils.RemoveGpuAssignmentStrategyReg();
        }

        private void CheckGpuStrategyReg()
        {
            string script = @"[bool]((Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV') -and ($k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV' -EA 0) -and ('RequireSecureDeviceAssignment', 'RequireSupportedDeviceAssignment' | ForEach-Object { ($k.GetValue($_, $null) -ne $null) }) -notcontains $false)";
            var result = Utils.Run(script);
            SetProperty(ref _isGpuStrategyEnabled, result.Count > 0 && result[0].ToString().ToLower() == "true", nameof(IsGpuStrategyEnabled));
        }

        private void InitializeProductType()
        {
            bool isServer = false;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("ProductType")?.ToString();
                        isServer = val != null && val.Contains("Server");
                    }
                }
            }
            catch { }
            _isServerSystem = isServer;
            UpdateSystemDesc(isServer);
            _isInitialized = true;
            OnPropertyChanged(nameof(IsServerSystem));
        }

        private void UpdateSystemDesc(bool isServer)
        {
            string current = isServer ? Translate("Status_Edition_Server") : Translate("Status_Edition_Workstation");
            SystemVersionDesc = $"{Translate("Status_Msg_CurrentVer")}: {current}";
        }

        partial void OnIsServerSystemChanged(bool value)
        {
            if (!_isInitialized) return;
            SwitchSystemVersion(value);
        }

        private async void SwitchSystemVersion(bool toServer)
        {
            try
            {
                IsSystemSwitchEnabled = false;
                string result = await Task.Run(() => SystemSwitcher.ExecutePatch(toServer ? 1 : 2));

                if (result == "SUCCESS")
                {
                    SystemVersionDesc = Translate("Status_Msg_OperationPending");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var presenter = Application.Current.MainWindow?.FindName("SnackbarPresenter") as SnackbarPresenter;
                        if (presenter == null) return;
                        var grid = new Grid();
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var txt = new TextBlock { Text = Translate("Status_Msg_RestartNow"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 15, 0) };
                        Grid.SetColumn(txt, 0);
                        var btn = new Wpf.Ui.Controls.Button { Content = Translate("Global_Restart"), Appearance = ControlAppearance.Primary };
                        btn.Click += (s, e) => System.Diagnostics.Process.Start("shutdown", "-r -t 0");
                        Grid.SetColumn(btn, 1);
                        grid.Children.Add(txt); grid.Children.Add(btn);
                        var snack = new Snackbar(presenter) { Title = Translate("Status_Title_Success"), Content = grid, Appearance = ControlAppearance.Success, Icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24), Timeout = TimeSpan.FromSeconds(15) };
                        snack.Show();
                    });
                }
                else if (result == "PENDING")
                {
                    ShowSnackbar(Translate("Status_Title_Info"), Translate("Status_Msg_OperationPending"), ControlAppearance.Info, SymbolRegular.Info24);
                }
                else
                {
                    ShowSnackbar(Translate("Status_Title_Error"), result, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    _isInitialized = false;
                    IsServerSystem = !toServer;
                    _isInitialized = true;
                    IsSystemSwitchEnabled = true;
                }
            }
            catch { IsSystemSwitchEnabled = true; }
        }

        private string Translate(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            try { return ExHyperV.Properties.Resources.ResourceManager.GetString(key) ?? key; } catch { return key; }
        }

        public void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var presenter = Application.Current.MainWindow?.FindName("SnackbarPresenter") as SnackbarPresenter;
                if (presenter != null)
                {
                    var snack = new Snackbar(presenter) { Title = title, Content = message, Appearance = appearance, Icon = new SymbolIcon(icon) { FontSize = 20 }, Timeout = TimeSpan.FromSeconds(4) };
                    snack.Show();
                }
            });
        }
    }
}