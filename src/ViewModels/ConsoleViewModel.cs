using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    public partial class ConsoleViewModel : ObservableObject, IDisposable
    {
        private readonly VmPowerService _powerService = new();
        private readonly VmQueryService _queryService = new();
        private DispatcherTimer _statusTimer;

        [ObservableProperty] private string _vmId;
        [ObservableProperty] private string _vmName;
        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _isRunning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotBusy))]
        [NotifyCanExecuteChangedFor(nameof(StartVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(ShutdownVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResetVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(PauseVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(SaveVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(TurnOffVmCommand))]
        private bool _isBusy = false;

        public bool IsNotBusy => !IsBusy;

        public event EventHandler SendCadRequested;

        public ConsoleViewModel(string vmId, string vmName)
        {
            VmId = vmId;
            VmName = vmName;
            StartStatusPolling();
        }

        public ConsoleViewModel() { }

        private void StartStatusPolling()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (s, e) => await SyncVmStateAsync();
            _statusTimer.Start();
            _ = SyncVmStateAsync();
        }

        private async Task SyncVmStateAsync()
        {
            try
            {
                var vms = await _queryService.GetVmListAsync();
                var currentVm = vms.FirstOrDefault(v =>
                    v.Id.ToString().Equals(VmId, StringComparison.OrdinalIgnoreCase) ||
                    v.Name.Equals(VmName, StringComparison.OrdinalIgnoreCase));

                if (currentVm != null)
                {
                    if (currentVm.IsRunning != IsRunning)
                    {
                        IsRunning = currentVm.IsRunning;
                        IsBusy = false;
                    }
                    if (VmName != currentVm.Name) VmName = currentVm.Name;
                    IsLoading = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        private bool CanExecutePowerAction() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task StartVmAsync() => await ExecutePowerActionAsync("Start");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task ShutdownVmAsync() => await ExecutePowerActionAsync("Stop");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task ResetVmAsync() => await ExecutePowerActionAsync("Restart");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task PauseVmAsync() => await ExecutePowerActionAsync("Suspend");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task SaveVmAsync() => await ExecutePowerActionAsync("Save");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task TurnOffVmAsync() => await ExecutePowerActionAsync("TurnOff");

        private async Task ExecutePowerActionAsync(string action)
        {
            try
            {
                IsBusy = true;
                await _powerService.ExecuteControlActionAsync(VmName, action);
                await SyncVmStateAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void SendCad() => SendCadRequested?.Invoke(this, EventArgs.Empty);

        [ObservableProperty] private string _selectedResolution = "-";
        [ObservableProperty] private int _currentWidth;
        [ObservableProperty] private int _currentHeight;

        partial void OnCurrentWidthChanged(int value) => UpdateResolutionString();
        partial void OnCurrentHeightChanged(int value) => UpdateResolutionString();

        private void UpdateResolutionString()
        {
            if (CurrentWidth > 0 && CurrentHeight > 0)
                SelectedResolution = $"{CurrentWidth} x {CurrentHeight}";
        }

        public ObservableCollection<string> Resolutions { get; } = new()
        {
            "3840 x 2160", "2560 x 1600", "2560 x 1440", "1920 x 1200",
            "1920 x 1080", "1680 x 1050", "1600 x 1200", "1600 x 900",
            "1440 x 900",  "1366 x 768",  "1280 x 1024", "1280 x 800",
            "1280 x 720",  "1152 x 864",  "1024 x 768",  "800 x 600"
        };

        [ObservableProperty] private string _selectedSessionMode = "基本会话";
        [ObservableProperty] private bool _isEnhancedMode = false;
        [ObservableProperty] private SymbolRegular _selectedSessionIcon = SymbolRegular.Broom24;

        [RelayCommand]
        private void SwitchSessionMode(string mode) => SelectedSessionMode = mode;

        partial void OnSelectedSessionModeChanged(string value)
        {
            IsEnhancedMode = (value == "增强会话");
            SelectedSessionIcon = IsEnhancedMode ? SymbolRegular.Flash24 : SymbolRegular.Broom24;
            OnPropertyChanged(nameof(CanChangeResolution));
        }

        public bool CanChangeResolution => IsEnhancedMode;

        [ObservableProperty] private int _requestWidth;
        [ObservableProperty] private int _requestHeight;

        [RelayCommand]
        private void ChangeResolution(string resolutionText)
        {
            if (string.IsNullOrEmpty(resolutionText) || !IsEnhancedMode) return;
            var parts = resolutionText.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int w) && int.TryParse(parts[1].Trim(), out int h))
            {
                CurrentWidth = w;
                CurrentHeight = h;
                RequestWidth = w;
                RequestHeight = h;
            }
        }

        public void Dispose() => _statusTimer?.Stop();
    }
}