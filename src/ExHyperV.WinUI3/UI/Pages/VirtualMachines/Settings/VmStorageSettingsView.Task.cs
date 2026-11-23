using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

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
        string? returnDevice = null, bool openList = false)
    {
        if (!CanWrite) return;
        CancellationToken.ThrowIfCancellationRequested();
        draftProbe.Cancel();
        editing?.Cancel();
        var state = new StorageTask(title, steps, returnDevice);
        taskView = state;
        snapshotKnown = false;
        adding = null;
        editing = null;
        Render();
        scroll.ChangeView(null, 0, null, true);
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
        state.Message = result.Success ? "已完成" : Clean(CoreMessageResolver.ResolveError(result));
        if (result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired)
        {
            string reason = result.ErrorKind == ApiErrorKind.OutcomeUnknown ? "操作结果尚未确认" : "需要处理剩余资源";
            unresolvedChanges[VmId] = $"{reason}。{state.Message}\n请核对底层任务和实际资源。本次程序运行期间已停止对此虚拟机的存储修改。";
        }
        if (!result.Success) ShowError(unresolvedChanges.GetValueOrDefault(VmId) ?? state.Message);
        if (IsStopped) return;

        try { await ReadSnapshotAsync(); }
        catch (OperationCanceledException) when (IsStopped) { return; }
        catch (Exception ex)
        {
            state.ReadbackError = "回读存储状态失败。" + Clean(CoreMessageResolver.ResolveException(ex));
            ShowError(state.ReadbackError);
        }
        if (IsStopped) return;
        if (result.Success && state.ReadbackError == null)
        {
            ShowAppliedToast(title + "已完成");
            if (openList) { ShowList(); return; }
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
                Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
            var pct = new TextBlock
            {
                FontSize = 12,
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
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
                Background = ThemeBrush(this, "CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"),
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
        var done = Command(device == null ? "返回存储列表" : "返回编辑", () => _ = RunAsync(async () =>
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
