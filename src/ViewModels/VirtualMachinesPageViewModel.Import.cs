using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using ExHyperV.Interaction;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels;

public partial class VirtualMachinesPageViewModel
{
    private VmImportBatchSession? _vmImportBatchSession;
    private CancellationTokenSource? _vmImportCancellation;

    [ObservableProperty] private bool _isVmImportViewVisible;
    [ObservableProperty] private int _vmImportStep;
    [ObservableProperty] private string _vmImportSourcePath = string.Empty;
    [ObservableProperty] private VmImportSourceKind _vmImportSourceKind = VmImportSourceKind.Folder;
    [ObservableProperty] private bool _vmImportUsesExistingDirectory;
    [ObservableProperty] private bool _isPreparingVmImport;
    [ObservableProperty] private bool _isExecutingVmImport;
    [ObservableProperty] private bool _isVmImportCompleted;
    [ObservableProperty] private int _vmImportProgress;
    [ObservableProperty] private string _vmImportStatusText = string.Empty;
    [ObservableProperty] private VmImportPreview? _vmImportPreview;

    public ObservableCollection<VmImportPreview> VmImportPreviews { get; } = new();

    public bool CanPrepareVmImport => !IsPreparingVmImport
        && !IsExecutingVmImport
        && !string.IsNullOrWhiteSpace(VmImportSourcePath)
        && (VmImportSourceKind == VmImportSourceKind.Folder
            ? Directory.Exists(VmImportSourcePath)
            : File.Exists(VmImportSourcePath)
              && string.Equals(Path.GetExtension(VmImportSourcePath), ".zip", StringComparison.OrdinalIgnoreCase));
    public bool IsImportFolderSource => VmImportSourceKind == VmImportSourceKind.Folder;
    public bool CanStartVmImport => VmImportPreviews.Count > 0
        && VmImportPreviews.All(preview => preview.CompatibilityIssues.Count == 0)
        && !IsPreparingVmImport
        && !IsExecutingVmImport
        && !IsVmImportCompleted;
    public bool ShowVmImportProgress => IsExecutingVmImport || IsVmImportCompleted;
    public bool CanLeaveVmImport => !IsPreparingVmImport && !IsExecutingVmImport;

    partial void OnVmImportSourcePathChanged(string value) => OnPropertyChanged(nameof(CanPrepareVmImport));
    partial void OnVmImportSourceKindChanged(VmImportSourceKind value)
    {
        OnPropertyChanged(nameof(IsImportFolderSource));
        OnPropertyChanged(nameof(CanPrepareVmImport));
        if (value == VmImportSourceKind.Zip) VmImportUsesExistingDirectory = false;

        bool sourceStillMatches = value == VmImportSourceKind.Folder
            ? Directory.Exists(VmImportSourcePath)
            : File.Exists(VmImportSourcePath)
              && string.Equals(Path.GetExtension(VmImportSourcePath), ".zip", StringComparison.OrdinalIgnoreCase);
        if (!sourceStillMatches) VmImportSourcePath = string.Empty;
    }
    partial void OnIsPreparingVmImportChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPrepareVmImport));
        OnPropertyChanged(nameof(CanStartVmImport));
        OnPropertyChanged(nameof(CanLeaveVmImport));
        OnPropertyChanged(nameof(IsVmListEnabled));
    }
    partial void OnIsExecutingVmImportChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPrepareVmImport));
        OnPropertyChanged(nameof(CanStartVmImport));
        OnPropertyChanged(nameof(ShowVmImportProgress));
        OnPropertyChanged(nameof(CanLeaveVmImport));
        OnPropertyChanged(nameof(IsVmListEnabled));
    }
    partial void OnIsVmImportCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartVmImport));
        OnPropertyChanged(nameof(ShowVmImportProgress));
    }
    partial void OnVmImportProgressChanged(int value)
    {
        if (IsExecutingVmImport)
            TaskbarProgressService.Report(TaskbarProgressOperation.VmImport, value);
    }
    partial void OnVmImportPreviewChanged(VmImportPreview? value) => OnPropertyChanged(nameof(CanStartVmImport));

    [RelayCommand]
    private void OpenVmImport()
    {
        IsCreatingVm = false;
        if (_vmImportBatchSession == null)
        {
            VmImportPreviews.Clear();
            VmImportPreview = null;
            VmImportStep = 0;
            IsVmImportCompleted = false;
            VmImportSourceKind = VmImportSourceKind.Folder;
            VmImportUsesExistingDirectory = false;
            VmImportSourcePath = string.Empty;
            VmImportProgress = 0;
            VmImportStatusText = string.Empty;
        }
        IsVmImportViewVisible = true;
        SelectedVm = null;
    }

    [RelayCommand]
    private void BrowseVmImportSource()
    {
        if (VmImportSourceKind == VmImportSourceKind.Folder)
            BrowseVmImportFolder();
        else
            BrowseVmImportZip();
    }

    [RelayCommand]
    private void BrowseVmImportFolder()
    {
        string? selected = Dialogs.PickFolder(Resources.VmImport_SelectFolder,
            Directory.Exists(VmImportSourcePath) ? VmImportSourcePath : null);
        if (selected == null) return;
        VmImportSourceKind = VmImportSourceKind.Folder;
        VmImportSourcePath = selected;
    }

    [RelayCommand]
    private void BrowseVmImportZip()
    {
        string? selected = Dialogs.PickOpenFile(
            Resources.VmImport_SelectZip,
            Resources.VmImport_ZipFilter,
            File.Exists(VmImportSourcePath) ? Path.GetDirectoryName(VmImportSourcePath) : null);
        if (selected == null) return;
        VmImportSourceKind = VmImportSourceKind.Zip;
        VmImportSourcePath = selected;
    }

    [RelayCommand]
    private void SelectVmImportHostDirectories() => VmImportUsesExistingDirectory = false;

    [RelayCommand]
    private void SelectVmImportExistingDirectory()
    {
        if (VmImportSourceKind == VmImportSourceKind.Folder)
            VmImportUsesExistingDirectory = true;
    }

    [RelayCommand]
    private async Task PrepareVmImportAsync()
    {
        if (!CanPrepareVmImport) return;
        await DisposeVmImportSessionAsync();
        IsPreparingVmImport = true;
        VmImportStatusText = Resources.VmImport_PreparingPreview;
        try
        {
            VmImportPlacementMode placement = VmImportUsesExistingDirectory
                ? VmImportPlacementMode.ExistingDirectory
                : VmImportPlacementMode.HostDirectories;
            var previewProgress = new Progress<(int Current, int Total)>(value =>
            {
                VmImportStatusText = value.Total <= 1
                    ? Resources.VmImport_PreparingPreview
                    : string.Format(Resources.VmImport_PreparingPreviewBatch, value.Current, value.Total);
            });
            var result = await VmImportService.PreparePreviewsAsync(
                VmImportSourcePath,
                placement,
                previewProgress);
            if (!result.Success || result.Data == null)
            {
                ShowError(FriendlyError.CleanLines(result.Error));
                return;
            }

            _vmImportBatchSession = result.Data;
            VmImportPreviews.Clear();
            foreach (VmImportSession session in result.Data.VirtualMachines)
                VmImportPreviews.Add(session.Preview);
            VmImportPreview = VmImportPreviews.FirstOrDefault();
            VmImportStep = 1;
        }
        finally
        {
            IsPreparingVmImport = false;
            VmImportStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task StartVmImportAsync()
    {
        if (!CanStartVmImport || _vmImportBatchSession == null) return;
        IsExecutingVmImport = true;
        TaskbarProgressService.Start(TaskbarProgressOperation.VmImport);
        IsVmImportCompleted = false;
        VmImportProgress = 0;
        VmImportStatusText = Resources.VmImport_PreparingFiles;
        _vmImportCancellation = new CancellationTokenSource();
        bool taskbarCompleted = false;
        try
        {
            var prepareBatch = await VmImportService.PrepareBatchImportAsync(
                _vmImportBatchSession,
                _vmImportCancellation.Token);
            if (!prepareBatch.Success)
            {
                ShowError(FriendlyError.CleanLines(prepareBatch.Error));
                VmImportStatusText = Resources.VmImport_Failed;
                return;
            }

            VmImportSession[] sessions = _vmImportBatchSession.VirtualMachines.ToArray();
            int successfulCount = 0;
            for (int index = 0; index < sessions.Length; index++)
            {
                VmImportSession session = sessions[index];
                int completed = index;
                var progress = new Progress<int>(value =>
                {
                    VmImportProgress = (completed * 100 + value) / sessions.Length;
                    string phase = value < 82
                        ? Resources.VmImport_PreparingFiles
                        : value < 88 ? Resources.VmImport_Validating : Resources.VmImport_Importing;
                    VmImportStatusText = sessions.Length == 1
                        ? phase
                        : string.Format(
                            Resources.VmImport_BatchStatusFormat,
                            phase,
                            session.Preview.Name,
                            completed + 1,
                            sessions.Length);
                });
                var result = await VmImportService.ImportAsync(
                    session,
                    progress,
                    _vmImportCancellation.Token);
                if (!result.Success)
                {
                    string error = FriendlyError.CleanLines(result.Error);
                    await DisposeVmImportSessionAsync();

                    if (successfulCount > 0)
                    {
                        VmImportProgress = successfulCount * 100 / sessions.Length;
                        VmImportStatusText = string.Format(
                            Resources.VmImport_PartialCompleted,
                            successfulCount,
                            sessions.Length);
                        IsVmImportCompleted = true;
                        await LoadVmsCommand.ExecuteAsync(null);
                        // 完成页保持没有选中项，让用户点击任意虚拟机时都能触发详情页切换。
                        SelectedVm = null;
                        IsVmImportViewVisible = true;
                        ShowError(string.Format(
                            Resources.VmImport_PartialFailure,
                            successfulCount,
                            sessions.Length,
                            session.Preview.Name,
                            error));
                        return;
                    }

                    ShowError(error);
                    VmImportStatusText = Resources.VmImport_Failed;
                    VmImportPreviews.Clear();
                    VmImportPreview = null;
                    VmImportStep = 0;
                    return;
                }

                successfulCount++;
            }

            VmImportProgress = 100;
            VmImportStatusText = Resources.VmImport_Completed;
            IsVmImportCompleted = true;
            taskbarCompleted = true;
            await DisposeVmImportSessionAsync();
            await LoadVmsCommand.ExecuteAsync(null);
            // LoadVms 会默认选中列表第一项；清空后，完成页上的首次点击一定会触发导航。
            SelectedVm = null;
            IsVmImportViewVisible = true;
            ShowSuccess(sessions.Length == 1
                ? string.Format(Resources.VmImport_Success, sessions[0].Preview.Name)
                : $"已导入 {sessions.Length} 台虚拟机。");
        }
        finally
        {
            IsExecutingVmImport = false;
            if (taskbarCompleted)
                TaskbarProgressService.Complete(TaskbarProgressOperation.VmImport);
            else
                TaskbarProgressService.Fail(TaskbarProgressOperation.VmImport);
            _vmImportCancellation?.Dispose();
            _vmImportCancellation = null;
        }
    }

    [RelayCommand]
    private async Task BackFromVmImportAsync()
    {
        if (IsExecutingVmImport) return;
        if (VmImportStep == 1 && !IsVmImportCompleted)
        {
            await DisposeVmImportSessionAsync();
            VmImportPreviews.Clear();
            VmImportPreview = null;
            VmImportStep = 0;
            return;
        }

        await DisposeVmImportSessionAsync();
        VmImportPreviews.Clear();
        VmImportPreview = null;
        VmImportStep = 0;
        IsVmImportCompleted = false;
        VmImportProgress = 0;
        VmImportStatusText = string.Empty;
        IsVmImportViewVisible = false;
        if (SelectedVm == null && VmList.Count > 0) SelectedVm = VmList.First();
    }

    private async Task DisposeVmImportSessionAsync()
    {
        if (_vmImportBatchSession == null) return;
        await _vmImportBatchSession.DisposeAsync();
        _vmImportBatchSession = null;
    }
}
