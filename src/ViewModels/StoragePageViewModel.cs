using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ExHyperV.ViewModels
{
    public partial class StoragePageViewModel : ObservableObject
    {
        public static StoragePageViewModel Instance { get; } = new StoragePageViewModel();

        private readonly IStorageService _storageService;

        public ObservableCollection<VMInfo> VmList { get; } = new();

        [ObservableProperty]
        private ObservableCollection<UiDriveModel> _allDrives = new();

        [ObservableProperty]
        private VMInfo? _selectedVm;

        [ObservableProperty]
        private bool _isLoading;

        public StoragePageViewModel()
        {
            _storageService = new StorageService();
            _ = InitializeVmListAsync();
        }

        async partial void OnSelectedVmChanged(VMInfo? value)
        {
            if (value != null)
            {
                await RefreshVmStorageAsync();
            }
            else
            {
                AllDrives.Clear();
            }
        }

        private async Task InitializeVmListAsync()
        {
            IsLoading = true;
            try
            {
                var vms = await Task.Run(() =>
                {
                    using (var ps = System.Management.Automation.PowerShell.Create())
                    {
                        ps.AddScript("Get-VM | Select-Object Name, State, Generation");
                        var results = ps.Invoke();
                        return results.Select(r => new VMInfo(
                            r.Properties["Name"].Value?.ToString() ?? "",
                            "", "", "", new Dictionary<string, string>(),
                            r.Properties["Generation"].Value != null ? Convert.ToInt32(r.Properties["Generation"].Value) : 0,
                            r.Properties["State"].Value?.ToString() == "Running"
                        )).ToList();
                    }
                });

                VmList.Clear();
                foreach (var vm in vms) VmList.Add(vm);

                if (SelectedVm == null) SelectedVm = VmList.FirstOrDefault();
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task RefreshVmStorageAsync()
        {
            if (SelectedVm == null) return;

            try
            {
                var storageInfo = await _storageService.GetVmStorageInfoAsync(SelectedVm.Name);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    AllDrives.Clear();
                    foreach (var ctrl in storageInfo)
                    {
                        if (ctrl.AttachedDrives != null)
                        {
                            foreach (var drive in ctrl.AttachedDrives)
                            {
                                AllDrives.Add(new UiDriveModel
                                {
                                    DriveType = drive.DriveType,
                                    DiskType = drive.DiskType,
                                    PathOrDiskNumber = drive.PathOrDiskNumber?.ToString() ?? "",
                                    ControllerLocation = drive.ControllerLocation,
                                    ControllerType = ctrl.ControllerType,
                                    ControllerNumber = ctrl.ControllerNumber
                                });
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }
    }
}