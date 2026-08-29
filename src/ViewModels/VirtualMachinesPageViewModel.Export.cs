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
    [ObservableProperty] private string _exportPackageFileName = string.Empty;
    [ObservableProperty] private bool _isLoadingExportOptions;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _exportCompleted;
    [ObservableProperty] private int _exportProgress;
    [ObservableProperty] private string _exportStatusText = string.Empty;

    public ObservableCollection<VmExportDiskItemViewModel> ExportVirtualHardDisks { get; } = new();
    public ObservableCollection<VmExportCheckpointItemViewModel> ExportCheckpoints { get; } = new();

    private List<VmInstanceViewModel> _exportVms = new();
    private bool _hasExportCheckpoints;
    private bool _singleCheckpointRequirementsApplied;
    private bool _virtualHardDisksBeforeSingleCheckpoint;
    private bool _runtimeStateBeforeSingleCheckpoint;
    private Dictionary<string, bool>? _virtualHardDiskSelectionsBeforeCheckpoints;

    public bool CanConfigureExport =>
        !IsLoadingExportOptions && !IsExporting && !ExportCompleted;
    public IReadOnlyList<VmInstanceViewModel> ExportTargets => _exportVms;
    public string ExportTargetSummary => string.Format(
        Properties.Resources.VmExport_TargetCount,
        _exportVms.Count);
    public bool HasExportCheckpoints => _hasExportCheckpoints;
    public bool IsMultiVmExport => _exportVms.Count > 1;
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
    public bool ShowExportCheckpointModeSelector =>
        ExportIncludesCheckpoints && !IsMultiVmExport;
    public bool IsCompressedExportPackage => ExportPackageMode == VmExportPackageMode.Compress;
    public bool CanLeaveExport => !IsExporting;
    public bool ShowExportProgress => IsExporting || ExportCompleted;
    public bool CanStartExport => CanConfigureExport
        && !string.IsNullOrWhiteSpace(ExportDestinationPath)
        && (!ExportCreatesPackage || NormalizePackageFileName(ExportPackageFileName) != null)
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

    partial void OnExportCreatesPackageChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExportPackageOptions));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportPackageFileNameChanged(string value) =>
        OnPropertyChanged(nameof(CanStartExport));

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
                _virtualHardDiskSelectionsBeforeCheckpoints[disk.SelectionKey] = disk.IsIncluded;
                disk.IsIncluded = true;
            }
        }
        else if (_virtualHardDiskSelectionsBeforeCheckpoints is { } selections)
        {
            foreach (VmExportDiskItemViewModel disk in ExportVirtualHardDisks)
            {
                if (selections.TryGetValue(disk.SelectionKey, out bool isIncluded))
                    disk.IsIncluded = isIncluded;
            }

            _virtualHardDiskSelectionsBeforeCheckpoints = null;
        }

        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(ShowExportCheckpointModeSelector));
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

        var targets = (IsMultiSelect ? _selectedVms : new List<VmInstanceViewModel> { vm })
            .Where(item => item != null)
            .DistinctBy(item => item.Id)
            .ToList();
        if (targets.Count == 0) return;

        IsLoadingExportOptions = true;
        try
        {
            if (targets.Count == 1 && SelectedVm != vm)
            {
                CurrentViewType = VmDetailViewType.Dashboard;
                SelectedVm = vm;
            }

            _exportVms = targets;
            ExportVmId = targets[0].Id;
            ExportVmName = targets[0].Name;
            OnPropertyChanged(nameof(ExportTargets));
            OnPropertyChanged(nameof(ExportTargetSummary));
            OnPropertyChanged(nameof(IsMultiVmExport));
            OnPropertyChanged(nameof(ShowExportCheckpointModeSelector));
            ExportDestinationPath = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            ExportIncludesCheckpoints = false;
            ExportCheckpointMode = VmExportCheckpointMode.All;
            ExportIncludesVirtualHardDisks = true;
            ExportIncludesRuntimeState = false;
            ExportCreatesPackage = false;
            ExportPackageMode = VmExportPackageMode.Store;
            ExportPackageFileName = $"VMExport_{DateTime.Now:yyMMdd_HHmm}.zip";
            ExportProgress = 0;
            ExportStatusText = string.Empty;
            ExportCompleted = false;
            CurrentViewType = VmDetailViewType.Export;

            ExportVirtualHardDisks.Clear();
            ExportCheckpoints.Clear();
            _hasExportCheckpoints = false;
            SelectedExportCheckpoint = null;
            OnPropertyChanged(nameof(HasExportCheckpoints));
            var exportDisks = new List<VmExportDiskItemViewModel>();
            foreach (VmInstanceViewModel target in targets)
            {
                await VmStorageService.LoadVmStorageItemsAsync(target.Model);
                var disksResult = await VmExportService.GetVirtualHardDisksAsync(target.Id);
                if (!disksResult.Success)
                {
                    ShowError(FriendlyError.CleanLines(disksResult.Error));
                    return;
                }

                foreach (VmExportService.VirtualHardDiskInfo diskInfo in
                         disksResult.Data ?? new List<VmExportService.VirtualHardDiskInfo>())
                {
                    string path = diskInfo.Path.Trim('"');
                    var disk = target.Disks.FirstOrDefault(item =>
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

                    var storageItem = target.StorageItems.FirstOrDefault(item =>
                        item.DriveType == "HardDisk"
                        && item.DiskType == "Virtual"
                        && string.Equals(
                            item.PathOrDiskNumber.Trim('"'),
                            path,
                            StringComparison.OrdinalIgnoreCase));

                    exportDisks.Add(new VmExportDiskItemViewModel(
                        target.Id,
                        target.Name,
                        targets.Count > 1,
                        diskInfo.InstanceId,
                        disk,
                        storageItem));
                }

                var checkpointsResult = await VmExportService.GetCheckpointsAsync(target.Id);
                if (!checkpointsResult.Success)
                {
                    ShowError(FriendlyError.CleanLines(checkpointsResult.Error));
                    return;
                }

                var checkpoints = checkpointsResult.Data
                    ?? new List<VmExportService.CheckpointInfo>();
                _hasExportCheckpoints |= checkpoints.Count > 0;
                if (targets.Count == 1)
                {
                    foreach (VmExportCheckpointItemViewModel checkpoint in
                             BuildExportCheckpointTree(checkpoints))
                        ExportCheckpoints.Add(checkpoint);
                }
            }

            foreach (VmExportDiskItemViewModel disk in exportDisks
                         .OrderBy(item => item.VmName)
                         .ThenBy(item => item.ControllerType)
                         .ThenBy(item => item.ControllerNumber)
                         .ThenBy(item => item.ControllerLocation))
                ExportVirtualHardDisks.Add(disk);

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
    private void SelectSingleExportCheckpoint()
    {
        if (!IsMultiVmExport)
            ExportCheckpointMode = VmExportCheckpointMode.Single;
    }

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

        VmInstanceViewModel[] targets = _exportVms.ToArray();
        if (targets.Length == 0)
            return;

        string? packageFileName = NormalizePackageFileName(ExportPackageFileName);
        if (ExportCreatesPackage && packageFileName == null)
        {
            ShowError(Properties.Resources.VmExport_PackageFileNameInvalid);
            return;
        }

        if (packageFileName != null)
            ExportPackageFileName = packageFileName;

        string batchName = ExportCreatesPackage
            ? Path.GetFileNameWithoutExtension(packageFileName!)
            : $"VMExport_{DateTime.Now:yyMMdd_HHmm}";
        string targetArchive = Path.Combine(
            ExportDestinationPath,
            packageFileName ?? batchName + ".zip");
        string outputRoot = ExportCreatesPackage
            ? Path.Combine(ExportDestinationPath, batchName + ".partial")
            : targets.Length > 1
                ? Path.Combine(ExportDestinationPath, batchName)
                : ExportDestinationPath;
        string completedOutputPath = ExportCreatesPackage
            ? targetArchive
            : targets.Length > 1
                ? outputRoot
                : Path.Combine(
                    ExportDestinationPath,
                    targets[0].Id.ToString("D").ToUpperInvariant());

        if (ExportCreatesPackage
            && (Directory.Exists(targetArchive) || File.Exists(targetArchive)))
        {
            ShowError(string.Format(
                Properties.Resources.VmExport_PackageExists,
                Path.GetFileName(targetArchive)));
            return;
        }

        string conflictPath = targets.Length > 1 || ExportCreatesPackage
            ? outputRoot
            : completedOutputPath;
        if (Directory.Exists(conflictPath) || File.Exists(conflictPath))
        {
            ShowError(string.Format(
                Properties.Resources.VmExport_TargetExists,
                Path.GetFileName(conflictPath)));
            return;
        }

        IsExporting = true;
        ExportProgress = 0;
        ExportStatusText = Properties.Resources.VmExport_Preparing;

        try
        {
            if (targets.Length > 1 || ExportCreatesPackage)
                Directory.CreateDirectory(outputRoot);

            for (int index = 0; index < targets.Length; index++)
            {
                VmInstanceViewModel target = targets[index];
                int completedTargets = index;
                var progress = new Progress<int>(value =>
                {
                    int overall = (completedTargets * 100 + value) / targets.Length;
                    ExportProgress = overall;
                    ExportStatusText = string.Format(
                        Properties.Resources.VmExport_Progress,
                        overall);
                });

                VmExportCheckpointMode checkpointMode = !ExportIncludesCheckpoints
                    ? VmExportCheckpointMode.None
                    : IsMultiVmExport
                        ? VmExportCheckpointMode.All
                        : ExportCheckpointMode;
                var result = await VmExportService.ExportAsync(
                    target.Id,
                    target.Name,
                    outputRoot,
                    ExportIncludesVirtualHardDisks,
                    ExportVirtualHardDisks
                        .Where(disk => disk.VmId == target.Id && !disk.IsIncluded)
                        .Select(disk => disk.InstanceId)
                        .ToArray(),
                    checkpointMode,
                    !IsMultiVmExport && IsSingleCheckpointExport
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
                    outputRoot,
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

    private static string? NormalizePackageFileName(string? value)
    {
        string fileName = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            fileName += ".zip";

        return fileName.Length <= 255
               && !string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName))
            ? fileName
            : null;
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
