using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Models;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class MemoryPageViewModel : ObservableObject
    {
        public static MemoryPageViewModel Instance { get; } = new MemoryPageViewModel();

        private readonly IMemoryService _memoryService;
        private readonly DispatcherTimer _liveDataTimer;
        private bool _isUpdating = false;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private ObservableCollection<VMMemoryViewModel> _virtualMachinesMemory = new();

        public MemoryPageViewModel()
        {
            _memoryService = new MemoryService();

            _liveDataTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _liveDataTimer.Tick += OnLiveDataTimerTick;

            _ = LoadAllDataAsync();
        }

        public void StartTimer()
        {
            if (!_liveDataTimer.IsEnabled) _liveDataTimer.Start();
        }

        public void StopTimer()
        {
            if (_liveDataTimer.IsEnabled) _liveDataTimer.Stop();
        }

        private async void OnLiveDataTimerTick(object sender, EventArgs e)
        {
            if (_isUpdating) return;
            _isUpdating = true;

            try
            {
                var liveDataList = await _memoryService.GetVirtualMachinesMemoryUsageAsync();
                if (liveDataList == null) return;

                var liveDataLookup = liveDataList.ToDictionary(d => d.VMName);
                bool needsResort = false;

                foreach (var vmViewModel in VirtualMachinesMemory)
                {
                    bool wasRunning = vmViewModel.IsVmRunning;
                    if (liveDataLookup.TryGetValue(vmViewModel.VMName, out var liveData))
                    {
                        vmViewModel.UpdateLiveData(liveData);
                    }
                    else
                    {
                        vmViewModel.MarkAsOff();
                    }

                    if (wasRunning != vmViewModel.IsVmRunning)
                    {
                        needsResort = true;
                    }
                }

                if (needsResort)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var sorted = VirtualMachinesMemory
                            .OrderByDescending(v => v.IsVmRunning)
                            .ThenBy(v => v.VMName)
                            .ToList();

                        for (int i = 0; i < sorted.Count; i++)
                        {
                            int oldIndex = VirtualMachinesMemory.IndexOf(sorted[i]);
                            if (oldIndex != i)
                            {
                                VirtualMachinesMemory.Move(oldIndex, i);
                            }
                        }
                    });
                }
            }
            catch { }
            finally
            {
                _isUpdating = false;
            }
        }

        [RelayCommand]
        public async Task LoadAllDataAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                var vmConfigs = await _memoryService.GetVirtualMachinesMemoryConfigurationAsync();

                var sortedConfigs = vmConfigs
                    .OrderByDescending(c => c.State == "Running")
                    .ThenBy(c => c.VMName)
                    .ToList();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var currentVms = VirtualMachinesMemory.ToDictionary(vm => vm.VMName);
                    VirtualMachinesMemory.Clear();

                    foreach (var config in sortedConfigs)
                    {
                        if (currentVms.TryGetValue(config.VMName, out var existingVm))
                        {
                            existingVm.UpdateConfiguration(config);
                            VirtualMachinesMemory.Add(existingVm);
                        }
                        else
                        {
                            VirtualMachinesMemory.Add(new VMMemoryViewModel(config, this, _memoryService));
                        }
                    }
                });

                StartTimer();
            }
            catch (Exception ex)
            {
                ShowSnackbar(
                    ExHyperV.Properties.Resources.error,
                    string.Format(ExHyperV.Properties.Resources.Error_GenericFormat, ex.Message),
                    ControlAppearance.Danger,
                    SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon, double seconds = 3)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    var presenter = mainWindow.FindName("SnackbarPresenter") as SnackbarPresenter;
                    if (presenter != null)
                    {
                        var snackbar = new Snackbar(presenter)
                        {
                            Title = title,
                            Content = message,
                            Appearance = appearance,
                            Icon = new SymbolIcon(icon) { FontSize = 20 },
                            Timeout = TimeSpan.FromSeconds(seconds)
                        };
                        snackbar.Show();
                    }
                }
            });
        }
    }
}