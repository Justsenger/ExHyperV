
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.Tools;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using static ExHyperV.App.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Storage;

public sealed partial class VmStorageSettingsView
{
    private sealed class StorageTask(string title, string[] steps, string? returnDevice)
    {
        public readonly string Title = title;
        public readonly string[] Steps = steps;
        public readonly string? ReturnDevice = returnDevice;
        public int Step;
        public int? Percent;
        public string Message = "";
        public bool Finished;
        public bool Success;
        public string? ReadbackError;
    }

    private Task RunSingleAsync(string title, Func<Task<ApiResponse>> write, string? returnDevice = null, bool openList = false)
        => RunStorageTaskAsync(title, [title], _ => write(), returnDevice, openList);

    private async Task RunStorageTaskAsync(string title, string[] steps,
        Func<IProgress<(int StepIndex, int Percent, string Message)>, Task<ApiResponse>> write,
        string? returnDevice = null, bool openList = false, DetailDraft? inlineEdit = null)
    {
        if (!CanWrite) return;
        CancellationToken.ThrowIfCancellationRequested();
        draftProbe.Cancel();
        editing?.Cancel();
        var state = new StorageTask(title, steps, returnDevice);
        bool stayOnPage = openList || inlineEdit != null;
        bool fromList = adding == null && editing == null;
        snapshotKnown = false;
        if (!stayOnPage)
        {
            taskView = state;
            adding = null;
            editing = null;
            Render();
            scroll.ChangeView(null, 0, null, true);
        }
        var progress = new Progress<(int StepIndex, int Percent, string Message)>(p =>
        {
            if (IsStopped || taskView != state || state.Finished || p.StepIndex < state.Step) return;
            state.Step = Math.Clamp(p.StepIndex, 0, steps.Length - 1);
            state.Percent = p.Percent < 0 ? null : Math.Clamp(p.Percent, 0, 100);
            state.Message = Clean(p.Message);
            UpdateTaskRows(state);
        });
        ApiResponse result;
        // Once submitted, the base view waits for the real result even if navigation cancels its read token.
        try { result = await Task.Run(() => write(progress)); }
        catch (Exception ex) { result = ApiResponse.Fail(CoreMessageResolver.ResolveException(ex), exception: ex, kind: ApiErrorKind.OutcomeUnknown); }

        NotifyChanged();
        state.Success = result.Success;
        state.Finished = true;
        state.Message = result.Success ? L10n.Get("Common_Completed") : Clean(CoreMessageResolver.ResolveError(result));
        if (result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired)
        {
            string reason = result.ErrorKind == ApiErrorKind.OutcomeUnknown ? L10n.Get("Storage_OutcomeUnknown") : L10n.Get("Storage_RemainingResources");
            unresolvedChanges[VmId] = L10n.Format("Storage_UnconfirmedTask", reason, state.Message);
        }
        if (!result.Success) ShowError(unresolvedChanges.GetValueOrDefault(VmId) ?? state.Message);
        if (IsStopped) return;

        try { await ReadSnapshotAsync(); }
        catch (OperationCanceledException) when (IsStopped) { return; }
        catch (Exception ex)
        {
            state.ReadbackError = L10n.Get("Storage_ReadBackFailed") + Clean(CoreMessageResolver.ResolveException(ex));
            ShowError(state.ReadbackError);
        }
        if (IsStopped) return;
        if (result.Success && state.ReadbackError == null)
        {
            ShowAppliedToast(title + L10n.Get("Common_Completed"));
            if (openList)
            {
                if (fromList) Render();
                else ShowList();
                return;
            }
            if (!stayOnPage)
            {
                var device = snapshotKnown && state.ReturnDevice != null
                    ? attachments.FirstOrDefault(d => d.DeviceToken == state.ReturnDevice) : null;
                if (device != null) await OpenDetailsAsync(device);
                else if (snapshotKnown) ShowList();
                else await LoadAsync();
                return;
            }
        }
        if (stayOnPage)
        {
            if (state.ReadbackError == null && inlineEdit != null)
            {
                var slot = result.Success && inlineEdit.SlotDirty ? inlineEdit.Slot : null;
                var device = slot == null
                    ? attachments.FirstOrDefault(item => item.DeviceToken == inlineEdit.Item.DeviceToken)
                    : attachments.FirstOrDefault(item => item.ControllerType == slot.Type && item.ControllerNumber == slot.Number && item.ControllerLocation == slot.Location);
                if (device != null) await OpenDetailsAsync(device, inlineEdit.PathsTab);
                else ShowList();
            }
            else if (state.ReadbackError == null) ShowList();
            else
            {
                adding = null;
                editing = null;
                page.Children.Clear();
                page.Children.Add(Banner(state.ReadbackError, InfoBarSeverity.Error));
                pivot.Content = null;
                SetHeaderTrailingAction(Command(L10n.Get("Storage_Rescan"), () => _ = RunAsync(LoadAsync)));
            }
            return;
        }
        Render();
    }

    private readonly List<(ContentControl Icon, StackPanel ProgressHost, ProgressBar Bar, TextBlock Pct)> taskRows = [];
    private (int Step, bool Finished)? drawnStep;

    private void RenderTask(StorageTask state)
    {
        taskRows.Clear();
        drawnStep = null;
        var list = new StackPanel { Spacing = 8 };
        foreach (var step in state.Steps)
        {
            var icon = new ContentControl { Width = 22, VerticalContentAlignment = VerticalAlignment.Center };
            var label = new TextBlock
            {
                Text = step, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
            var pct = new TextBlock
            {
                FontSize = 12,
                Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var bar = new ProgressBar { Width = 140, Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                Children = { pct, bar }
            };
            var row = Split(WithLeading(icon, label), right);
            row.MinHeight = 24;
            taskRows.Add((icon, right, bar, pct));
            list.Children.Add(new Border
            {
                Background = ThemeHelper.ThemeBrush(this, "CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeHelper.ThemeBrush(this, "CardStrokeColorDefaultBrush"),
                BorderThickness = new(1),
                CornerRadius = new(8),
                Padding = new(16, 12, 16, 12),
                Child = row
            });
        }
        page.Children.Add(list);
        UpdateTaskRows(state);
        if (!state.Finished) return;
        if (!state.Success && !unresolvedChanges.ContainsKey(VmId)) page.Children.Add(Banner(state.Message, InfoBarSeverity.Error));
        if (state.ReadbackError != null) page.Children.Add(Banner(state.ReadbackError, InfoBarSeverity.Warning));
        var device = snapshotKnown && state.ReturnDevice != null
            ? attachments.FirstOrDefault(d => d.DeviceToken == state.ReturnDevice) : null;
        var done = Command(device == null ? L10n.Get("Storage_BackToList") : L10n.Get("Storage_BackToEdit"), () => _ = RunAsync(async () =>
        {
            if (device != null) await OpenDetailsAsync(device);
            else if (snapshotKnown) ShowList();
            else await LoadAsync();
        }), accent: state.Success && state.ReadbackError == null);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        page.Children.Add(done);
    }

    private void UpdateTaskRows(StorageTask state)
    {
        if (drawnStep != (state.Step, state.Finished))
        {
            for (int i = 0; i < taskRows.Count; i++)
            {
                bool active = !state.Finished && i == state.Step;
                taskRows[i].Icon.Content = BuildStepIcon(false, (state.Finished && state.Success) || i < state.Step,
                    state.Finished && !state.Success && i == state.Step, active);
                taskRows[i].ProgressHost.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
            drawnStep = (state.Step, state.Finished);
        }
        if (!state.Finished && state.Step >= 0 && state.Step < taskRows.Count)
        {
            var (_, _, bar, pct) = taskRows[state.Step];
            bar.IsIndeterminate = state.Percent == null;
            bar.Value = state.Percent ?? 0;
            pct.Text = state.Percent.HasValue ? $"{state.Percent}%" : "";
        }
    }
}
