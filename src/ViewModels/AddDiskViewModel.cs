using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace ExHyperV.ViewModels
{
    public partial class AddDiskViewModel : ObservableObject
    {
        private readonly IStorageService _storageService;
        private readonly string _vmName;
        private readonly int _vmGeneration;
        private readonly bool _isVmRunning;
        private readonly List<VmStorageControllerInfo> _currentStorage;
        private List<HostDiskInfo> _cachedHostDisks = new();
        private bool _isInternalUpdating = false;

        public AddDiskViewModel(string vmName, int generation, bool isVmRunning, List<VmStorageControllerInfo> currentStorage)
        {
            _storageService = new StorageService();
            _vmName = vmName;
            _vmGeneration = generation;
            _isVmRunning = isVmRunning;
            _currentStorage = currentStorage ?? new List<VmStorageControllerInfo>();

            AvailableControllerTypes = new ObservableCollection<string>();
            AvailableControllerNumbers = new ObservableCollection<int>();
            AvailableLocations = new ObservableCollection<int>();

            _ = InitialLoadAsync();
        }

        private async Task InitialLoadAsync()
        {
            await InitialDiskScanAsync();
            UpdateControllerTypeOptions();
            await RefreshControllerLayoutAsync(true);
        }

        public ObservableCollection<string> AvailableControllerTypes { get; }
        public ObservableCollection<int> AvailableControllerNumbers { get; }
        public ObservableCollection<int> AvailableLocations { get; }

        [ObservableProperty] private string _selectedControllerType = "SCSI";
        [ObservableProperty] private int _selectedControllerNumber = 0;
        [ObservableProperty] private int _selectedLocation = 0;
        [ObservableProperty] private bool _autoAssign = true;

        async partial void OnAutoAssignChanged(bool value) => await RefreshControllerLayoutAsync(value);

        async partial void OnSelectedControllerTypeChanged(string value)
        {
            if (_isInternalUpdating || string.IsNullOrEmpty(value)) return;
            await RefreshControllerLayoutAsync(AutoAssign);
        }

        async partial void OnSelectedControllerNumberChanged(int value)
        {
            if (_isInternalUpdating || value < 0) return;
            await RefreshControllerLayoutAsync(AutoAssign);
        }

        private void UpdateControllerTypeOptions()
        {
            _isInternalUpdating = true;
            AvailableControllerTypes.Clear();

            if (_vmGeneration == 2)
            {
                AvailableControllerTypes.Add("SCSI");
                SelectedControllerType = "SCSI";
            }
            else
            {
                if (DeviceType == "DvdDrive")
                {
                    AvailableControllerTypes.Add("IDE");
                    SelectedControllerType = "IDE";
                }
                else
                {
                    if (_isVmRunning)
                    {
                        AvailableControllerTypes.Add("SCSI");
                        SelectedControllerType = "SCSI";
                    }
                    else
                    {
                        AvailableControllerTypes.Add("IDE");
                        AvailableControllerTypes.Add("SCSI");
                        SelectedControllerType = "IDE";
                    }
                }
            }
            _isInternalUpdating = false;
        }

        private async Task RefreshControllerLayoutAsync(bool useAutoAssign)
        {
            if (_isInternalUpdating) return;
            _isInternalUpdating = true;

            try
            {
                string targetType = SelectedControllerType;
                int targetNumber = SelectedControllerNumber;
                int targetLocation = SelectedLocation;

                if (useAutoAssign)
                {
                    var bestSlot = FindFirstAvailableSlot();
                    targetType = bestSlot.Type;
                    targetNumber = bestSlot.Number;
                    targetLocation = bestSlot.Location;
                }

                if (targetType == "IDE")
                {
                    if (AvailableControllerNumbers.Count != 2)
                    {
                        AvailableControllerNumbers.Clear();
                        AvailableControllerNumbers.Add(0);
                        AvailableControllerNumbers.Add(1);
                    }
                }
                else
                {
                    var existingScsiNums = _currentStorage
                        .Where(c => c.ControllerType == "SCSI")
                        .Select(c => c.ControllerNumber)
                        .OrderBy(n => n).ToList();

                    if (_isVmRunning)
                    {
                        AvailableControllerNumbers.Clear();
                        foreach (var n in existingScsiNums) AvailableControllerNumbers.Add(n);
                        if (AvailableControllerNumbers.Count == 0) AvailableControllerNumbers.Add(0);
                    }
                    else
                    {
                        if (AvailableControllerNumbers.Count != 4 || AvailableControllerNumbers.Max() < 3)
                        {
                            AvailableControllerNumbers.Clear();
                            for (int i = 0; i < 4; i++) AvailableControllerNumbers.Add(i);
                        }
                    }
                }

                int maxSlots = (targetType == "IDE") ? 2 : 64;
                var occupied = _currentStorage
                    .Where(c => c.ControllerType == targetType && c.ControllerNumber == targetNumber)
                    .SelectMany(c => c.AttachedDrives)
                    .Select(d => d.ControllerLocation).ToList();

                var validLocations = new List<int>();
                for (int i = 0; i < maxSlots; i++)
                {
                    if (useAutoAssign || !occupied.Contains(i) || i == targetLocation)
                        validLocations.Add(i);
                }

                if (!AvailableLocations.SequenceEqual(validLocations))
                {
                    AvailableLocations.Clear();
                    foreach (var loc in validLocations) AvailableLocations.Add(loc);
                }

                SelectedControllerType = targetType;
                SelectedControllerNumber = AvailableControllerNumbers.Contains(targetNumber) ? targetNumber : AvailableControllerNumbers.FirstOrDefault();
                SelectedLocation = AvailableLocations.Contains(targetLocation) ? targetLocation : AvailableLocations.FirstOrDefault();

                OnPropertyChanged(nameof(SelectedControllerNumber));
                OnPropertyChanged(nameof(SelectedLocation));
            }
            finally { _isInternalUpdating = false; }
            ConfirmCommand.NotifyCanExecuteChanged();
        }
        private (string Type, int Number, int Location) FindFirstAvailableSlot()
        {
            var usedSlots = _currentStorage.SelectMany(c =>
                c.AttachedDrives.Select(d => $"{c.ControllerType}-{c.ControllerNumber}-{d.ControllerLocation}")
            ).ToHashSet();

            if (_vmGeneration == 1)
            {
                if (DeviceType == "DvdDrive" || !_isVmRunning)
                {
                    for (int n = 0; n < 2; n++)
                        for (int l = 0; l < 2; l++)
                            if (!usedSlots.Contains($"IDE-{n}-{l}")) return ("IDE", n, l);
                }
                if (DeviceType == "DvdDrive")
                {
                    return ("IDE", 0, 0);
                }
            }

            if (_isVmRunning)
            {
                var existingScsiNums = _currentStorage
                    .Where(c => c.ControllerType == "SCSI")
                    .Select(c => c.ControllerNumber)
                    .OrderBy(n => n).ToList();

                if (existingScsiNums.Count == 0) existingScsiNums.Add(0);

                foreach (var n in existingScsiNums)
                {
                    for (int l = 0; l < 64; l++)
                    {
                        if (!usedSlots.Contains($"SCSI-{n}-{l}")) return ("SCSI", n, l);
                    }
                }
            }
            else
            {
                for (int n = 0; n < 4; n++)
                {
                    for (int l = 0; l < 64; l++)
                    {
                        if (!usedSlots.Contains($"SCSI-{n}-{l}")) return ("SCSI", n, l);
                    }
                }
            }

            return ("SCSI", 0, 0);
        }

        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private string _deviceType = "HardDisk";
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private bool _isPhysicalSource = false;
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private string _filePath = string.Empty;
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private HostDiskInfo? _selectedPhysicalDisk;
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private bool _isNewDisk = false;
        [ObservableProperty] private int _newDiskSize = 256;
        [ObservableProperty] private string _selectedVhdType = "Dynamic";
        [ObservableProperty] private string _parentPath = "";
        [ObservableProperty] private string _sectorFormat = "Default";
        [ObservableProperty] private string _blockSize = "Default";

        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private string _isoSourceFolderPath = string.Empty;
        [ObservableProperty] private string _isoVolumeLabel = string.Empty;

        public ObservableCollection<int> NewDiskSizePresets { get; } = new ObservableCollection<int> { 64, 128, 256, 512, 1024 };

        public string FilePathPlaceholder => IsNewDisk ? Properties.Resources.AddDisk_Placeholder_SavePath : Properties.Resources.AddDisk_Placeholder_FilePath;

        async partial void OnDeviceTypeChanged(string value)
        {
            if (value == "DvdDrive")
            {
            }
            else
            {
            }
            SyncHostDiskDisplay();
            UpdateControllerTypeOptions();
            await RefreshControllerLayoutAsync(AutoAssign);
            ConfirmCommand.NotifyCanExecuteChanged();
        }

        async partial void OnIsPhysicalSourceChanged(bool value)
        {
            if (value) IsNewDisk = false;
            SyncHostDiskDisplay();
            await RefreshControllerLayoutAsync(AutoAssign);
            ConfirmCommand.NotifyCanExecuteChanged();
        }

        async partial void OnParentPathChanged(string value)
        {
            if (!string.IsNullOrEmpty(value) && System.IO.File.Exists(value))
            {
                var size = await _storageService.GetVhdSizeGbAsync(value);
                if (size > 0) NewDiskSize = (int)Math.Ceiling(size);
            }
        }

        partial void OnIsNewDiskChanged(bool value)
        {
            if (!value) ParentPath = string.Empty;
            OnPropertyChanged(nameof(FilePathPlaceholder));
            ConfirmCommand.NotifyCanExecuteChanged();
        }

        public ObservableCollection<HostDiskInfo> HostDisks { get; } = new();

        private async Task InitialDiskScanAsync()
        {
            try
            {
                var disks = await _storageService.GetHostDisksAsync();
                _cachedHostDisks = disks.ToList();
                SyncHostDiskDisplay();
            }
            catch { }
        }

        private void SyncHostDiskDisplay()
        {
            HostDisks.Clear();
            if (IsPhysicalSource && DeviceType == "HardDisk")
                foreach (var disk in _cachedHostDisks) HostDisks.Add(disk);
        }

        [RelayCommand]
        private void BrowseFile()
        {
            Microsoft.Win32.FileDialog dialog = IsNewDisk ? (Microsoft.Win32.FileDialog)new Microsoft.Win32.SaveFileDialog() : new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = DeviceType == "HardDisk" ? $"{Properties.Resources.AddDisk_Filter_VirtualDisk}|*.vhdx;*.vhd" : $"{Properties.Resources.AddDisk_Filter_OpticalImage}|*.iso";
            if (dialog.ShowDialog() == true) FilePath = dialog.FileName;
            ConfirmCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void BrowseParentFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = Properties.Resources.AddDisk_Title_SelectParentDisk, Filter = $"{Properties.Resources.AddDisk_Filter_VirtualDisk}|*.vhdx;*.vhd" };
            if (dialog.ShowDialog() == true) ParentPath = dialog.FileName;
            ConfirmCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void BrowseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Properties.Resources.AddDisk_Title_SelectSourceFolder,
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                IsoSourceFolderPath = dialog.FolderName;
                try
                {
                    var dirInfo = new System.IO.DirectoryInfo(dialog.FolderName);
                    IsoVolumeLabel = dirInfo.Name.ToUpper();
                }
                catch { }
            }
            ConfirmCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanConfirmAction))]
        private void Confirm(Window window)
        {
            if (window != null)
            {
                window.DialogResult = true;
                window.Close();
            }
        }

        private bool CanConfirmAction()
        {
            if (_vmGeneration == 1 && _isVmRunning && SelectedControllerType == "IDE") return false;
            if (_vmGeneration == 1 && DeviceType == "DvdDrive" && SelectedControllerType == "SCSI") return false;

            if (IsPhysicalSource) return SelectedPhysicalDisk != null;

            if (IsNewDisk && DeviceType == "DvdDrive")
            {
                return !string.IsNullOrEmpty(FilePath) && !string.IsNullOrEmpty(IsoSourceFolderPath);
            }

            return !string.IsNullOrEmpty(FilePath);
        }
    }
}