using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class MemoryPageViewModel : ObservableObject
    {
        private readonly IMemoryService _memoryService;
        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private ObservableCollection<MemoryBankGroupViewModel> _memoryBankGroups = new();

        [ObservableProperty]
        private ObservableCollection<VirtualMachineMemoryViewModel> _virtualMachinesMemory = new();

        private readonly DispatcherTimer _liveDataTimer;

        public MemoryPageViewModel()
        {
            _memoryService = new MemoryService();

            _liveDataTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _liveDataTimer.Tick += OnLiveDataTimerTick;

            _ = LoadAllDataCommand.ExecuteAsync(null);
        }

        private async void OnLiveDataTimerTick(object sender, EventArgs e)
        {
            if (IsLoading) return;
            try
            {
                var liveDataList = await _memoryService.GetVirtualMachinesMemoryAsync();
                var vmLookup = VirtualMachinesMemory.ToDictionary(vm => vm.VMName);
                foreach (var liveData in liveDataList)
                {
                    if (vmLookup.TryGetValue(liveData.VMName, out var targetVm))
                    {
                        targetVm.UpdateLiveData(liveData);
                        vmLookup.Remove(liveData.VMName); 
                    }
                    else
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            VirtualMachinesMemory.Add(new VirtualMachineMemoryViewModel(liveData));
                        });
                    }
                }
                foreach (var vmToRemove in vmLookup.Values)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        VirtualMachinesMemory.Remove(vmToRemove);
                    });
                }
            }
            catch (Exception ex) {}
        }
        public void Cleanup()
        {
            _liveDataTimer.Stop();
            _liveDataTimer.Tick -= OnLiveDataTimerTick;
        }
        [RelayCommand]
        private async Task RefreshVmDataAsync()
        {
            await LoadAllDataAsync();
        }
        [RelayCommand]
        private async Task LoadAllDataAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                var hostMemoryTask = _memoryService.GetHostMemoryAsync();
                var vmMemoryTask = _memoryService.GetVirtualMachinesMemoryAsync();
                await Task.WhenAll(hostMemoryTask, vmMemoryTask);
                var hostMemoryModels = await hostMemoryTask;
                var vmMemoryModels = await vmMemoryTask;
                var newMemoryBankGroups = new ObservableCollection<MemoryBankGroupViewModel>(
                    hostMemoryModels.Select(m => new HostMemoryViewModel(m))
                                    .GroupBy(m => m.BankLabel)
                                    .Select(g => new MemoryBankGroupViewModel(g.Key, g.ToList()))
                                    .OrderBy(g => g.BankLabel)
                );

                var newVirtualMachinesMemory = new ObservableCollection<VirtualMachineMemoryViewModel>(
                    vmMemoryModels.Select(vm => new VirtualMachineMemoryViewModel(vm))
                );
                MemoryBankGroups = newMemoryBankGroups;
                VirtualMachinesMemory = newVirtualMachinesMemory;
                if (!_liveDataTimer.IsEnabled)
                {
                    _liveDataTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Utils.Show(string.Format(Properties.Resources.LoadDataFailed, ex.Message));
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}