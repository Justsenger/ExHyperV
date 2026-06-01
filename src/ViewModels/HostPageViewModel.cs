using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;
using System.Collections.ObjectModel;
using System.Windows;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public record SchedulerMode(string Name, HyperVSchedulerType Type);

    public partial class HostPageViewModel : ObservableObject
    {
        private readonly HyperVHostService _hostService = new();
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

        private async Task CheckSystemInfoAsync() => await Task.Run(() =>
        {
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
            CpuStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsVirtualizationEnabled());
            CpuStatus.IsChecking = false;
        }

        private async Task CheckHyperVInfoAsync()
        {
            var (isReady, isInstalled, statusText) = await _hostService.GetHyperVStatusAsync();
            HyperVStatus.IsInstalled = isInstalled;
            HyperVStatus.IsSuccess = isReady;
            HyperVStatus.StatusText = statusText;
            HyperVStatus.IsChecking = false;
        }

        private async Task CheckIommuAsync()
        {
            IommuStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsIommuEnabled());
            IommuStatus.IsChecking = false;
        }

        private async Task CheckServerInfoAsync()
        {
            SystemStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsServerSystem());
            SystemStatus.IsChecking = false;
        }

        private async Task InitializeVersionPolicyAsync()
        {
            IsGpuStrategyEnabled = await Task.Run(() => _hostService.GetGpuStrategyEnabled());
            InitializeProductType();
            await LoadAdvancedConfigAsync();
            IsGpuStrategyToggleEnabled = true;
            IsSystemSwitchEnabled = true;
        }

        private async Task LoadAdvancedConfigAsync()
        {
            try
            {
                bool numa = await HyperVNUMAService.GetNumaSpanningEnabledAsync();
                var sched = await Task.Run(() => HyperVSchedulerService.GetSchedulerType());
                IsNumaSpanningEnabled = numa;
                CurrentSchedulerType = sched == HyperVSchedulerType.Unknown ? HyperVSchedulerType.Classic : sched;
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
            _ = Task.Run(async () =>
            {
                var (ok, msg) = await HyperVNUMAService.SetNumaSpanningEnabledAsync(value);
                if (!ok)
                {
                    ShowSnackbar(Translate("Status_Title_Error"), msg, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _isInitialized = false;
                        IsNumaSpanningEnabled = !value;
                        _isInitialized = true;
                    });
                }
            });
        }

        partial void OnCurrentSchedulerTypeChanged(HyperVSchedulerType value)
        {
            if (!_isInitialized) return;
            _ = Task.Run(async () =>
            {
                if (await HyperVSchedulerService.SetSchedulerTypeAsync(value))
                    ShowRestartPrompt(ExHyperV.Properties.Resources.Msg_Host_SchedulerChanged);
                else
                {
                    ShowSnackbar(Translate("Status_Title_Error"), ExHyperV.Properties.Resources.Error_Host_SchedulerFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    var actual = HyperVSchedulerService.GetSchedulerType();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _isInitialized = false;
                        CurrentSchedulerType = actual;
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
        private async Task DisableHyperVAsync()
        {
            ShowSnackbar(Translate("Status_Title_Info"), Properties.Resources.HostPageViewModel_DisablingHyperV, ControlAppearance.Info, SymbolRegular.Settings24);
            bool ok = await _hostService.DisableHyperVAsync();
            if (!ok)
            {
                ShowSnackbar(Translate("Status_Title_Error"), Properties.Resources.HostPageViewModel_DisableFailed, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                return;
            }
            ShowRestartPrompt(Properties.Resources.HostPageViewModel_DisableSuccess);
        }

        [RelayCommand]
        private async Task EnableHyperVAsync()
        {
            ShowSnackbar(Translate("Status_Title_Info"), ExHyperV.Properties.Resources.Msg_Host_EnableHyperV, ControlAppearance.Info, SymbolRegular.Settings24);
            bool ok = await _hostService.EnableHyperVAsync();
            if (!ok)
            {
                ShowSnackbar(Translate("Status_Title_Error"), ExHyperV.Properties.Resources.Error_Host_EnableFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                return;
            }
            ShowRestartPrompt(ExHyperV.Properties.Resources.Msg_Host_EnableSuccess);
        }

        private void InitializeProductType()
        {
            IsServerSystem = HyperVHostService.IsServerSystem();
            UpdateSystemDesc(IsServerSystem);
        }

        private void UpdateSystemDesc(bool isServer) =>
            SystemVersionDesc = $"{Translate("Status_Msg_CurrentVer")}: {(isServer ? Translate("Status_Edition_Server") : Translate("Status_Edition_Workstation"))}";

        private async void SwitchSystemVersion(bool toServer)
        {
            try
            {
                IsSystemSwitchEnabled = false;

                if (SystemTypeService.HasPendingTask())
                {
                    ShowSnackbar(Translate("Status_Title_Warning"),
                        Translate("Status_Msg_RestartRequired"),
                        ControlAppearance.Caution,
                        SymbolRegular.Warning24);
                    _isInitialized = false;
                    IsServerSystem = !toServer;
                    _isInitialized = true;
                    return;
                }

                string result = await Task.Run(() => SystemTypeService.ApplySwitch(toServer));
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
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Application.Current.MainWindow?.FindName("SnackbarPresenter") is SnackbarPresenter p)
                    new Snackbar(p) { Title = title, Content = msg, Appearance = app, Icon = new SymbolIcon(icon) { FontSize = 20 }, Timeout = TimeSpan.FromSeconds(4) }.Show();
            });
        }

        private void ShowRestartPrompt(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Application.Current.MainWindow?.FindName("SnackbarPresenter") is not SnackbarPresenter p) return;

                var grid = new System.Windows.Controls.Grid();
                grid.VerticalAlignment = VerticalAlignment.Center;
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

                var icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24) { FontSize = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var textStack = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var titleTxt = new Wpf.Ui.Controls.TextBlock { Text = Translate("Status_Title_Success"), FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0) };
                var msgTxt = new Wpf.Ui.Controls.TextBlock { Text = message, FontSize = 12, Margin = new Thickness(0, -2, 0, 0) };
                textStack.Children.Add(titleTxt);
                textStack.Children.Add(msgTxt);

                var btn = new Wpf.Ui.Controls.Button { Content = Translate("Global_Restart"), Appearance = ControlAppearance.Primary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
                btn.Click += (s, e) => System.Diagnostics.Process.Start("shutdown", "-r -t 0");

                System.Windows.Controls.Grid.SetColumn(icon, 0);
                System.Windows.Controls.Grid.SetColumn(textStack, 1);
                System.Windows.Controls.Grid.SetColumn(btn, 2);

                grid.Children.Add(icon);
                grid.Children.Add(textStack);
                grid.Children.Add(btn);

                new Snackbar(p) { Content = grid, Appearance = ControlAppearance.Success, Timeout = TimeSpan.FromSeconds(15) }.Show();
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