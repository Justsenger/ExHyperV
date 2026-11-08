using ExHyperV.Application;
using ExHyperV.UI.Shared.Interaction;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.UI.Pages.VirtualMachines;

public partial class VirtualMachinesPageViewModel
{
    private VmImportBatchSession? _vmImportBatchSession;
    private CancellationTokenSource? _vmImportCancellation;
    private readonly List<VmImportBatchSession> _pendingVmImportCleanup = [];
    private readonly Dictionary<VmImportBatchSession, string> _vmImportCleanupErrors = [];

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
    [ObservableProperty] private bool _isCleaningVmImport;
    [ObservableProperty] private string _vmImportCleanupText = string.Empty;

    public ObservableCollection<VmImportPreview> VmImportPreviews { get; } = new();

    public bool CanPrepareVmImport => !IsPreparingVmImport
        && !IsExecutingVmImport
        && !IsCleaningVmImport
        && !string.IsNullOrWhiteSpace(VmImportSourcePath)
        && (VmImportSourceKind == VmImportSourceKind.Folder
            ? Directory.Exists(VmImportSourcePath)
            : File.Exists(VmImportSourcePath)
              && string.Equals(Path.GetExtension(VmImportSourcePath), ".zip", StringComparison.OrdinalIgnoreCase));
    public bool IsImportFolderSource => VmImportSourceKind == VmImportSourceKind.Folder;
    public bool CanStartVmImport => VmImportService.GetPreviewError(VmImportPreviews) == null
        && !IsPreparingVmImport
        && !IsExecutingVmImport
        && !IsCleaningVmImport
        && !IsVmImportCompleted;
    public bool ShowVmImportProgress => IsExecutingVmImport || IsVmImportCompleted;
    public bool CanLeaveVmImport => !IsPreparingVmImport && !IsExecutingVmImport && !IsCleaningVmImport;
    public bool HasPendingVmImportCleanup => _pendingVmImportCleanup.Count > 0;
    public bool CanRetryVmImportCleanup => HasPendingVmImportCleanup && CanLeaveVmImport;

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
        OnPropertyChanged(nameof(CanRetryVmImportCleanup));
        OnPropertyChanged(nameof(IsVmListEnabled));
    }
    partial void OnIsExecutingVmImportChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPrepareVmImport));
        OnPropertyChanged(nameof(CanStartVmImport));
        OnPropertyChanged(nameof(ShowVmImportProgress));
        OnPropertyChanged(nameof(CanLeaveVmImport));
        OnPropertyChanged(nameof(CanRetryVmImportCleanup));
        OnPropertyChanged(nameof(IsVmListEnabled));
    }
    partial void OnIsVmImportCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartVmImport));
        OnPropertyChanged(nameof(ShowVmImportProgress));
    }
    partial void OnIsCleaningVmImportChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPrepareVmImport));
        OnPropertyChanged(nameof(CanStartVmImport));
        OnPropertyChanged(nameof(CanLeaveVmImport));
        OnPropertyChanged(nameof(CanRetryVmImportCleanup));
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
        IsPreparingVmImport = true;
        VmImportStatusText = Resources.VmImport_PreparingPreview;
        try
        {
            await DisposeVmImportSessionAsync();
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
            if (!result.Success || result.Batch == null)
            {
                if (result.Batch != null)
                    RememberVmImportCleanup(result.Batch);
                ShowError(DescribeOperationResult(result.Result.Error, result.Batch?.CleanupError,
                    result.Batch?.RetainedPaths ?? [],
                    result.Batch?.VirtualMachines.Any(session => session.OperationStateUnknown) == true));
                return;
            }

            _vmImportBatchSession = result.Batch;
            VmImportPreviews.Clear();
            foreach (VmImportSession session in result.Batch.VirtualMachines)
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
            VmImportSession[] sessions = _vmImportBatchSession.VirtualMachines.ToArray();
            var progress = new Progress<VmImportBatchProgress>(value =>
            {
                VmImportProgress = value.Percentage;
                string phase = value.Phase switch
                {
                    VmImportPhase.PreparingFiles => Resources.VmImport_PreparingFiles,
                    VmImportPhase.Validating => Resources.VmImport_Validating,
                    _ => Resources.VmImport_Importing
                };
                VmImportStatusText = value.Total == 1 ? phase
                    : string.Format(Resources.VmImport_BatchStatusFormat,
                        phase.TrimEnd('…'), value.Name, value.Current, value.Total);
            });
            var result = await VmImportService.ImportBatchAsync(
                _vmImportBatchSession, progress, _vmImportCancellation.Token);
            string cleanup = await DisposeVmImportSessionAsync(reportFailure: false);
            if (!result.Prepared)
            {
                ShowError(FriendlyError.CleanLines(result.Error ?? string.Empty) + Environment.NewLine + cleanup);
                VmImportStatusText = Resources.VmImport_Failed;
                VmImportPreviews.Clear();
                VmImportPreview = null;
                VmImportStep = 0;
                return;
            }
            if (!result.Success)
            {
                string details = string.Join(Environment.NewLine + Environment.NewLine,
                    result.Items.Where(item => !item.Result.Success).Select(item =>
                        $"{item.Name} ({item.Result.VmId}){Environment.NewLine}" +
                        DescribeOperationResult(item.Result.Error, null, [], item.Result.OperationStateUnknown)));
                string error = string.Join(Environment.NewLine,
                    new[] { FriendlyError.CleanLines(result.Error ?? string.Empty), details, cleanup }
                        .Where(text => !string.IsNullOrWhiteSpace(text)));
                if (result.SuccessfulCount > 0)
                {
                    VmImportProgress = result.SuccessfulCount * 100 / result.Total;
                    VmImportStatusText = string.Format(Resources.VmImport_PartialCompleted,
                        result.SuccessfulCount, result.Total);
                    IsVmImportCompleted = true;
                    await LoadVmsCommand.ExecuteAsync(null);
                    SelectedVm = null;
                    IsVmImportViewVisible = true;
                    ShowError(string.Format(Resources.VmImport_PartialFailure,
                        result.SuccessfulCount, result.Total, result.FailedName, error));
                    return;
                }
                if (result.ImportedIds.Count > 0 || result.Items.Any(item => item.Result.OperationStateUnknown))
                    await LoadVmsCommand.ExecuteAsync(null);
                ShowError(error);
                VmImportStatusText = Resources.VmImport_Failed;
                VmImportPreviews.Clear();
                VmImportPreview = null;
                VmImportStep = 0;
                return;
            }

            VmImportProgress = 100;
            VmImportStatusText = Resources.VmImport_Completed;
            IsVmImportCompleted = true;
            taskbarCompleted = true;
            await LoadVmsCommand.ExecuteAsync(null);
            // LoadVms 会默认选中列表第一项；清空后，完成页上的首次点击一定会触发导航。
            SelectedVm = null;
            IsVmImportViewVisible = true;
            ShowSuccess(result.Total == 1
                ? string.Format(Resources.VmImport_Success, sessions[0].Preview.Name)
                : $"已导入 {result.Total} 台虚拟机。");
            if (result.RemovedMissingGroupCount > 0)
                await Dialogs.ShowAlertAsync(Resources.VmImport_Menu,
                    string.Format(Resources.VmGroup_ImportRemoved, result.RemovedMissingGroupCount));
            if (!string.IsNullOrWhiteSpace(cleanup)) ShowError(cleanup);
        }
        catch (Exception ex)
        {
            VmImportStatusText = Resources.VmImport_Failed;
            ShowError(FriendlyError.CleanLines(ex.Message));
        }
        finally
        {
            await DisposeVmImportSessionAsync();
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
        if (!CanLeaveVmImport) return;
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

    private async Task<string> DisposeVmImportSessionAsync(bool reportFailure = true)
    {
        if (_vmImportBatchSession == null) return string.Empty;
        var batch = _vmImportBatchSession;
        // Keep ownership before awaiting cleanup; a failed release remains reachable.
        RememberVmImportCleanup(batch);
        _vmImportBatchSession = null;
        return await CleanupVmImportBatchAsync(batch, reportFailure);
    }

    private void RememberVmImportCleanup(VmImportBatchSession batch)
    {
        if (batch.CleanupState != VmImportCleanupState.Completed && !_pendingVmImportCleanup.Contains(batch))
            _pendingVmImportCleanup.Add(batch);
        RefreshVmImportCleanupStatus();
    }

    private async Task<string> CleanupVmImportBatchAsync(VmImportBatchSession batch, bool reportFailure)
    {
        IsCleaningVmImport = true;
        string? unexpectedError = null;
        // Exit waits without pumping the dispatcher. Core release must also be
        // able to finish if it was first requested by a normal UI command.
        try { await Task.Run(async () => await batch.DisposeAsync().ConfigureAwait(false)); }
        catch (Exception ex) { unexpectedError = FriendlyError.CleanLines(ex.Message); }
        finally { IsCleaningVmImport = false; }
        if (unexpectedError != null) _vmImportCleanupErrors[batch] = unexpectedError;
        else _vmImportCleanupErrors.Remove(batch);
        if (unexpectedError == null && batch.CleanupState == VmImportCleanupState.Completed)
            _pendingVmImportCleanup.Remove(batch);
        RefreshVmImportCleanupStatus();
        string warning = DescribeOperationResult(null, unexpectedError ?? batch.CleanupError, batch.RetainedPaths,
            batch.VirtualMachines.Any(session => session.OperationStateUnknown));
        if (reportFailure && !_disposed && !string.IsNullOrWhiteSpace(warning)) ShowError(warning);
        return warning;
    }

    private void RefreshVmImportCleanupStatus()
    {
        VmImportCleanupText = string.Join(Environment.NewLine + Environment.NewLine,
            _pendingVmImportCleanup.Select(batch => batch.SourcePath + Environment.NewLine +
                DescribeOperationResult(null,
                    _vmImportCleanupErrors.GetValueOrDefault(batch) ?? batch.CleanupError,
                    batch.RetainedPaths, batch.VirtualMachines.Any(session => session.OperationStateUnknown))));
        OnPropertyChanged(nameof(HasPendingVmImportCleanup));
        OnPropertyChanged(nameof(CanRetryVmImportCleanup));
    }

    [RelayCommand]
    private async Task RetryVmImportCleanupAsync()
    {
        if (!CanRetryVmImportCleanup) return;
        foreach (var batch in _pendingVmImportCleanup.ToArray())
            await CleanupVmImportBatchAsync(batch, reportFailure: false);
        if (!_disposed && HasPendingVmImportCleanup) ShowError(VmImportCleanupText);
    }

    private Task DisposeIdleImportForShutdownAsync()
    {
        // Active imports keep the application's existing exit policy. Failed
        // cleanup batches are separate from them and still receive a final attempt.
        var batches = _pendingVmImportCleanup.ToList();
        if (!IsPreparingVmImport && !IsExecutingVmImport && _vmImportBatchSession != null)
            batches.Add(_vmImportBatchSession);
        if (batches.Count == 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            foreach (var batch in batches.Distinct())
            {
                try
                {
                    await batch.DisposeAsync().ConfigureAwait(false);
                    if (batch.CleanupState != VmImportCleanupState.Completed)
                        System.Diagnostics.Trace.TraceWarning("{0}{1}{2}", batch.SourcePath, Environment.NewLine,
                            DescribeOperationResult(null, batch.CleanupError, batch.RetainedPaths,
                                batch.VirtualMachines.Any(session => session.OperationStateUnknown)));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("Import cleanup failed for {0}: {1}", batch.SourcePath, ex);
                }
            }
        });
    }
}
