using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels;

public partial class VirtualMachinesPageViewModel
{
    [ObservableProperty] private Guid _exportVmId;
    [ObservableProperty] private string _exportVmName = string.Empty;
    [ObservableProperty] private string _exportDestinationPath = string.Empty;
    [ObservableProperty] private bool _exportIncludesVirtualHardDisks = true;
    [ObservableProperty] private bool _exportIncludesCheckpoints;
    [ObservableProperty] private VmExportCheckpointMode _exportCheckpointMode =
        VmExportCheckpointMode.All;
    [ObservableProperty] private VmExportCheckpointItemViewModel? _selectedExportCheckpoint;
    [ObservableProperty] private bool _exportIncludesRuntimeState;
    [ObservableProperty] private bool _exportCreatesPackage;
    [ObservableProperty] private VmExportPackageMode _exportPackageMode = VmExportPackageMode.Store;
    [ObservableProperty] private bool _isLoadingExportOptions;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _exportCompleted;
    [ObservableProperty] private int _exportProgress;
    [ObservableProperty] private string _exportStatusText = string.Empty;

    public ObservableCollection<VmExportDiskItemViewModel> ExportVirtualHardDisks { get; } = new();
    public ObservableCollection<VmExportCheckpointItemViewModel> ExportCheckpoints { get; } = new();

    private bool _singleCheckpointRequirementsApplied;
    private bool _virtualHardDisksBeforeSingleCheckpoint;
    private bool _runtimeStateBeforeSingleCheckpoint;
    private Dictionary<string, bool>? _virtualHardDiskSelectionsBeforeCheckpoints;

    public bool CanConfigureExport =>
        !IsLoadingExportOptions && !IsExporting && !ExportCompleted;
    public bool HasExportCheckpoints => ExportCheckpoints.Count > 0;
    public bool IsSingleCheckpointMode =>
        ExportCheckpointMode == VmExportCheckpointMode.Single;
    public bool IsSingleCheckpointExport =>
        ExportIncludesCheckpoints && ExportCheckpointMode == VmExportCheckpointMode.Single;
    public bool ShowExportVirtualHardDiskSelection =>
        ExportIncludesVirtualHardDisks && !IsSingleCheckpointExport;
    public bool CanConfigureExportVirtualHardDisks =>
        CanConfigureExport && !IsSingleCheckpointExport;
    public bool CanConfigureExportVirtualHardDiskSelection =>
        CanConfigureExport && !ExportIncludesCheckpoints;
    public bool CanConfigureExportRuntimeState =>
        CanConfigureExport && !IsSingleCheckpointExport;
    public bool ShowExportPackageOptions => ExportCreatesPackage;
    public bool IsCompressedExportPackage => ExportPackageMode == VmExportPackageMode.Compress;
    public bool CanLeaveExport => !IsExporting;
    public bool ShowExportProgress => IsExporting || ExportCompleted;
    public bool CanStartExport => CanConfigureExport
        && !string.IsNullOrWhiteSpace(ExportDestinationPath)
        && (!IsSingleCheckpointExport || SelectedExportCheckpoint != null);

    partial void OnIsLoadingExportOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureExport));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportDestinationPathChanged(string value) =>
        OnPropertyChanged(nameof(CanStartExport));

    partial void OnExportIncludesVirtualHardDisksChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
    }

    partial void OnExportCreatesPackageChanged(bool value) =>
        OnPropertyChanged(nameof(ShowExportPackageOptions));

    partial void OnExportPackageModeChanged(VmExportPackageMode value) =>
        OnPropertyChanged(nameof(IsCompressedExportPackage));

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureExport));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(CanLeaveExport));
        OnPropertyChanged(nameof(ShowExportProgress));
        OnPropertyChanged(nameof(IsVmListEnabled));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureExport));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(ShowExportProgress));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportIncludesCheckpointsChanged(bool value)
    {
        if (value)
        {
            _virtualHardDiskSelectionsBeforeCheckpoints = new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);
            foreach (VmExportDiskItemViewModel disk in ExportVirtualHardDisks)
            {
                _virtualHardDiskSelectionsBeforeCheckpoints[disk.InstanceId] = disk.IsIncluded;
                disk.IsIncluded = true;
            }
        }
        else if (_virtualHardDiskSelectionsBeforeCheckpoints is { } selections)
        {
            foreach (VmExportDiskItemViewModel disk in ExportVirtualHardDisks)
            {
                if (selections.TryGetValue(disk.InstanceId, out bool isIncluded))
                    disk.IsIncluded = isIncluded;
            }

            _virtualHardDiskSelectionsBeforeCheckpoints = null;
        }

        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        UpdateSingleCheckpointRequirements();
    }

    partial void OnExportCheckpointModeChanged(VmExportCheckpointMode value)
    {
        OnPropertyChanged(nameof(IsSingleCheckpointMode));
        UpdateSingleCheckpointRequirements();
    }

    partial void OnSelectedExportCheckpointChanged(
        VmExportCheckpointItemViewModel? value) =>
        OnPropertyChanged(nameof(CanStartExport));

    [RelayCommand]
    private async Task GoToExportVmAsync(VmInstanceViewModel vm)
    {
        if (vm == null || IsExporting) return;

        IsLoadingExportOptions = true;
        try
        {
            if (SelectedVm != vm)
            {
                CurrentViewType = VmDetailViewType.Dashboard;
                SelectedVm = vm;
            }

            ExportVmId = vm.Id;
            ExportVmName = vm.Name;
            ExportDestinationPath = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            ExportIncludesCheckpoints = false;
            ExportCheckpointMode = VmExportCheckpointMode.All;
            ExportIncludesVirtualHardDisks = true;
            ExportIncludesRuntimeState = false;
            ExportCreatesPackage = false;
            ExportPackageMode = VmExportPackageMode.Store;
            ExportProgress = 0;
            ExportStatusText = string.Empty;
            ExportCompleted = false;
            CurrentViewType = VmDetailViewType.Export;

            ExportVirtualHardDisks.Clear();
            ExportCheckpoints.Clear();
            SelectedExportCheckpoint = null;
            OnPropertyChanged(nameof(HasExportCheckpoints));
            await VmStorageService.LoadVmStorageItemsAsync(vm.Model);
            var disksResult = await VmExportService.GetVirtualHardDisksAsync(vm.Id);
            if (!disksResult.Success)
            {
                ShowError(FriendlyError.CleanLines(disksResult.Error));
                return;
            }

            var exportDisks = new List<VmExportDiskItemViewModel>();
            foreach (VmExportService.VirtualHardDiskInfo diskInfo in
                     disksResult.Data ?? new List<VmExportService.VirtualHardDiskInfo>())
            {
                string path = diskInfo.Path.Trim('"');
                var disk = vm.Disks.FirstOrDefault(item =>
                    string.Equals(
                        item.Path.Trim('"'),
                        path,
                        StringComparison.OrdinalIgnoreCase));

                if (disk == null)
                {
                    long size = 0;
                    try
                    {
                        if (File.Exists(path))
                            size = new FileInfo(path).Length;
                    }
                    catch { }

                    disk = new Models.VmDiskItem
                    {
                        Name = Path.GetFileName(path),
                        Path = path,
                        CurrentSize = size,
                        MaxSize = size,
                        DiskType = "Virtual"
                    };
                }

                var storageItem = vm.StorageItems.FirstOrDefault(item =>
                    item.DriveType == "HardDisk"
                    && item.DiskType == "Virtual"
                    && string.Equals(
                        item.PathOrDiskNumber.Trim('"'),
                        path,
                        StringComparison.OrdinalIgnoreCase));

                exportDisks.Add(
                    new VmExportDiskItemViewModel(diskInfo.InstanceId, disk, storageItem));
            }

            foreach (VmExportDiskItemViewModel disk in exportDisks
                         .OrderBy(item => item.ControllerType)
                         .ThenBy(item => item.ControllerNumber)
                         .ThenBy(item => item.ControllerLocation))
                ExportVirtualHardDisks.Add(disk);

            var checkpointsResult = await VmExportService.GetCheckpointsAsync(vm.Id);
            if (!checkpointsResult.Success)
            {
                ShowError(FriendlyError.CleanLines(checkpointsResult.Error));
                return;
            }

            foreach (VmExportCheckpointItemViewModel checkpoint in BuildExportCheckpointTree(
                         checkpointsResult.Data ?? new List<VmExportService.CheckpointInfo>()))
                ExportCheckpoints.Add(checkpoint);

            SelectedExportCheckpoint = ExportCheckpoints.FirstOrDefault();
            OnPropertyChanged(nameof(HasExportCheckpoints));
        }
        finally
        {
            IsLoadingExportOptions = false;
        }
    }

    [RelayCommand]
    private void BrowseExportFolder()
    {
        if (!CanConfigureExport) return;

        string? selected = Dialogs.PickFolder(
            Properties.Resources.VmExport_SelectDestination,
            string.IsNullOrWhiteSpace(ExportDestinationPath)
                ? null
                : ExportDestinationPath);
        if (!string.IsNullOrWhiteSpace(selected))
            ExportDestinationPath = selected;
    }

    [RelayCommand]
    private void SelectAllExportCheckpoints() =>
        ExportCheckpointMode = VmExportCheckpointMode.All;

    [RelayCommand]
    private void SelectSingleExportCheckpoint() =>
        ExportCheckpointMode = VmExportCheckpointMode.Single;

    [RelayCommand]
    private void SelectStoredExportPackage() =>
        ExportPackageMode = VmExportPackageMode.Store;

    [RelayCommand]
    private void SelectCompressedExportPackage() =>
        ExportPackageMode = VmExportPackageMode.Compress;

    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (!CanStartExport) return;

        if (!Directory.Exists(ExportDestinationPath))
        {
            ShowError(Properties.Resources.VmExport_PathRequired);
            return;
        }

        string targetDirectory = Path.Combine(ExportDestinationPath, ExportVmName);
        string targetArchive = Path.Combine(ExportDestinationPath, ExportVmName + ".zip");
        if (Directory.Exists(targetDirectory) || File.Exists(targetDirectory))
        {
            ShowError(string.Format(
                Properties.Resources.VmExport_TargetExists, ExportVmName));
            return;
        }

        if (ExportCreatesPackage
            && (Directory.Exists(targetArchive) || File.Exists(targetArchive)))
        {
            ShowError(string.Format(
                Properties.Resources.VmExport_PackageExists,
                Path.GetFileName(targetArchive)));
            return;
        }

        IsExporting = true;
        ExportProgress = 0;
        ExportStatusText = Properties.Resources.VmExport_Preparing;

        string completedOutputPath = targetDirectory;
        try
        {
            var progress = new Progress<int>(value =>
            {
                ExportProgress = value;
                ExportStatusText = string.Format(
                    Properties.Resources.VmExport_Progress, value);
            });

            var result = await VmExportService.ExportAsync(
                ExportVmId,
                ExportVmName,
                ExportDestinationPath,
                ExportIncludesVirtualHardDisks,
                ExportVirtualHardDisks
                    .Where(disk => !disk.IsIncluded)
                    .Select(disk => disk.InstanceId)
                    .ToArray(),
                ExportIncludesCheckpoints
                    ? ExportCheckpointMode
                    : VmExportCheckpointMode.None,
                IsSingleCheckpointExport
                    ? SelectedExportCheckpoint?.Path
                    : null,
                ExportIncludesRuntimeState,
                progress);

            if (!result.Success)
            {
                string error = FriendlyError.CleanLines(result.Error);
                ExportStatusText = string.Format(Properties.Resources.VmExport_Failed, error);
                ShowError(ExportStatusText);
                return;
            }

            if (ExportCreatesPackage)
            {
                ExportProgress = 0;
                ExportStatusText = string.Format(Properties.Resources.VmExport_PackageProgress, 0);
                var packageProgress = new Progress<int>(value =>
                {
                    ExportProgress = value;
                    ExportStatusText = string.Format(
                        Properties.Resources.VmExport_PackageProgress, value);
                });

                var packageResult = await VmExportPackagingService.CreatePackageAsync(
                    result.Data ?? targetDirectory,
                    targetArchive,
                    ExportPackageMode,
                    packageProgress);
                if (!packageResult.Success)
                {
                    string error = FriendlyError.CleanLines(packageResult.Error);
                    ExportStatusText = string.Format(
                        Properties.Resources.VmExport_PackageFailed, error);
                    ShowError(ExportStatusText);
                    return;
                }

                VmExportPackageResult package = packageResult.Data!;
                completedOutputPath = package.ArchivePath;
                if (!package.SourceDirectoryRemoved)
                {
                    ExportProgress = 100;
                    ExportStatusText = string.Format(
                        Properties.Resources.VmExport_PackageCleanupWarning,
                        FriendlyError.CleanLines(package.CleanupError ?? string.Empty));
                    ExportCompleted = true;
                    ShowError(ExportStatusText);
                    Shell.Reveal(completedOutputPath);
                    return;
                }
            }

            ExportProgress = 100;
            ExportStatusText = Properties.Resources.VmExport_Completed;
            ExportCompleted = true;
            ShowSuccess(Properties.Resources.VmExport_Completed);
            Shell.Reveal(completedOutputPath);
        }
        catch (Exception ex)
        {
            string error = FriendlyError.CleanLines(ex.Message);
            ExportStatusText = string.Format(Properties.Resources.VmExport_Failed, error);
            ShowError(ExportStatusText);
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void CloseExport()
    {
        if (!CanLeaveExport) return;
        CurrentViewType = VmDetailViewType.Dashboard;
    }

    private void UpdateSingleCheckpointRequirements()
    {
        bool shouldApply = IsSingleCheckpointExport;
        if (shouldApply && !_singleCheckpointRequirementsApplied)
        {
            _virtualHardDisksBeforeSingleCheckpoint = ExportIncludesVirtualHardDisks;
            _runtimeStateBeforeSingleCheckpoint = ExportIncludesRuntimeState;
            _singleCheckpointRequirementsApplied = true;

            ExportIncludesVirtualHardDisks = true;
            ExportIncludesRuntimeState = true;
            foreach (VmExportDiskItemViewModel disk in ExportVirtualHardDisks)
                disk.IsIncluded = true;

            SelectedExportCheckpoint ??= ExportCheckpoints.FirstOrDefault();
        }
        else if (!shouldApply && _singleCheckpointRequirementsApplied)
        {
            _singleCheckpointRequirementsApplied = false;
            ExportIncludesVirtualHardDisks = _virtualHardDisksBeforeSingleCheckpoint;
            ExportIncludesRuntimeState = _runtimeStateBeforeSingleCheckpoint;
        }

        OnPropertyChanged(nameof(IsSingleCheckpointExport));
        OnPropertyChanged(nameof(ShowExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(CanStartExport));
    }

    private static IReadOnlyList<VmExportCheckpointItemViewModel> BuildExportCheckpointTree(
        IEnumerable<VmExportService.CheckpointInfo> checkpoints)
    {
        var items = checkpoints
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<VmExportService.CheckpointInfo>>(
            StringComparer.OrdinalIgnoreCase);
        var roots = new List<VmExportService.CheckpointInfo>();

        foreach (VmExportService.CheckpointInfo item in items.Values)
        {
            if (string.IsNullOrWhiteSpace(item.ParentId)
                || !items.ContainsKey(item.ParentId))
            {
                roots.Add(item);
                continue;
            }

            if (!children.TryGetValue(item.ParentId, out var siblings))
            {
                siblings = new List<VmExportService.CheckpointInfo>();
                children[item.ParentId] = siblings;
            }
            siblings.Add(item);
        }

        static IOrderedEnumerable<VmExportService.CheckpointInfo> Sort(
            IEnumerable<VmExportService.CheckpointInfo> source) =>
            source.OrderBy(item => item.CreatedDate).ThenBy(item => item.Name);

        var result = new List<VmExportCheckpointItemViewModel>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Append(
            VmExportService.CheckpointInfo item,
            string ancestorPrefix,
            bool isRoot)
        {
            if (!visited.Add(item.Id)) return;

            string treePrefix = isRoot
                ? string.Empty
                : ancestorPrefix + "└─ ";
            result.Add(new VmExportCheckpointItemViewModel(item, treePrefix));

            if (!children.TryGetValue(item.Id, out var childItems)) return;

            var orderedChildren = Sort(childItems).ToList();
            string nextPrefix = isRoot
                ? string.Empty
                : ancestorPrefix + "   ";
            for (int index = 0; index < orderedChildren.Count; index++)
                Append(
                    orderedChildren[index],
                    nextPrefix,
                    false);
        }

        var orderedRoots = Sort(roots).ToList();
        for (int index = 0; index < orderedRoots.Count; index++)
            Append(orderedRoots[index], string.Empty, true);

        foreach (VmExportService.CheckpointInfo unvisited in Sort(
                     items.Values.Where(item => !visited.Contains(item.Id))))
            Append(unvisited, string.Empty, true);

        return result;
    }
}
