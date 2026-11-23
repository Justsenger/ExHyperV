using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmPcieSettingsView
{
    private readonly Queue<PcieDeviceInfo> queue = new();
    private readonly List<string> failures = [];
    private ILookup<PcieDeviceInfo, PcieDeviceInfo>? confirmedRelated;
    private int activeStep, assigned, skipped;

    private Task RunFlowSegmentAsync(Func<Task> action) => RunAsync(async () =>
    {
        try { await action(); }
        catch (OperationCanceledException) when (IsStopped) { }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
            FinishFlow();
        }
    });

    private async Task StartFlowAsync()
    {
        if (page != Page.Picker || selected.Count == 0) return;
        queue.Clear();
        failures.Clear();
        confirmedRelated = null;
        assigned = skipped = activeStep = 0;
        foreach (var c in pool.Where(c => c.Selectable && selected.Contains(c.Device))) queue.Enqueue(c.Device);
        RenderSteps();
        power = (await ReadContextAsync()).State;
        if (IsStopped) return;
        if (power != VmState.PoweredOff)
        {
            RenderSteps(right: Actions(Command(T("Back"), RenderPicker),
                Command(T("PowerContinue"), () => _ = RunFlowSegmentAsync(PowerOffAndPrepareAsync), true)));
            return;
        }
        await PrepareAsync();
    }

    private async Task PowerOffAndPrepareAsync()
    {
        RenderSteps();
        var state = (await ReadContextAsync()).State;
        if (state != VmState.PoweredOff)
        {
            CancellationToken.ThrowIfCancellationRequested();
            RequireSuccess(await VmPowerService.ExecuteControlActionAsync(VmId, VmPowerAction.TurnOff));
            NotifyChanged();
        }
        await EnsureOffAsync(VmId);
        await PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        activeStep = 1;
        RenderSteps();
        var mmio = Data(await Task.Run(() => VmMmioService.CheckMmioSpaceAsync(VmId)));
        CancellationToken.ThrowIfCancellationRequested();
        if (mmio == MmioCheckResultType.NeedsExpansion)
        {
            await EnsureOffAsync(VmId);
            RequireSuccess(await Task.Run(() => VmMmioService.UpdateMmioSpaceAsync(VmId)));
            NotifyChanged();
        }
        CancellationToken.ThrowIfCancellationRequested();
        activeStep = 2;
        var related = FindRelated();
        if (related.Count > 0)
        {
            RenderSteps(below: RelatedDecision(related));
            return;
        }
        await DrainQueueAsync();
    }

    private List<(PcieDeviceInfo Device, PcieDeviceInfo Source)> FindRelated()
    {
        // The final PCI node encodes the slot in its first byte and the function in its second.
        // Keep the entire upstream path so equal slot numbers on different buses stay distinct.
        static string? Slot(string path)
        {
            var match = Regex.Match(path, @"#PCI\([0-9a-f]{4}\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? path[..^3] : null;
        }
        var sources = new Dictionary<string, PcieDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in queue)
            if (Slot(device.Path) is { } slot) sources.TryAdd(slot, device);
        var result = new List<(PcieDeviceInfo, PcieDeviceInfo)>();
        var seen = queue.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in pool.Where(c => c.Selectable))
        {
            var d = candidate.Device;
            if (Slot(d.Path) is { } slot && sources.TryGetValue(slot, out var source) && seen.Add(d.Path))
                result.Add((d, source));
        }
        return result;
    }

    private Border RelatedDecision(IReadOnlyList<(PcieDeviceInfo Device, PcieDeviceInfo Source)> related)
    {
        var panel = new StackPanel { Spacing = 8, Padding = new(16, 12, 16, 12) };
        panel.Children.Add(new TextBlock { Text = F("RelatedPrompt", targetName), FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var (device, source) in related)
        {
            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(Identity(device.FriendlyName, device.ClassType));
            info.Children.Add(new TextBlock { Text = F("SameDevice", source.FriendlyName), Margin = new(50, 0, 0, 0),
                FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
            var row = Split(info, Badge(this, T("Related")));
            row.Padding = new(12, 8, 12, 8);
            panel.Children.Add(CardShell(this, row));
        }
        var actions = Actions(Command(T("Skip"), () => _ = RunFlowSegmentAsync(() => DrainQueueAsync())),
            Command(L10n.Get("Common_Confirm"), () => _ = RunFlowSegmentAsync(async () =>
            {
                confirmedRelated = related.ToLookup(item => item.Source, item => item.Device);
                await DrainQueueAsync();
            }), true));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(actions);
        return CardShell(this, panel);
    }

    private async Task EnsureOffAsync(Guid id)
    {
        var state = Data(await VmConfigurationState.ReadAsync(id));
        if (!state.IsPoweredOff)
            throw new InvalidOperationException(id == VmId ? T("AddOff") : T("SourceChanged"));
        CancellationToken.ThrowIfCancellationRequested();
    }

    private async Task DrainQueueAsync(PcieAssignmentSession? resumed = null, PcieAssignmentDecision? answer = null)
    {
        RenderSteps();
        while (queue.TryPeek(out var d) && !IsStopped)
        {
            try
            {
                var session = resumed;
                if (session == null)
                {
                    var inventory = Data(await HostPcieService.GetPCIeInfoAsync());
                    var current = inventory.Devices.SingleOrDefault(c => SameId(c.InstanceId, d.InstanceId)
                        && SameId(c.Path, d.Path) && c.Assignment == d.Assignment);
                    if (current == null) throw new InvalidOperationException(T("IdentityChanged"));
                    session = new PcieAssignmentSession(current, inventory.Devices, PcieAssignmentTarget.ForVirtualMachine(VmId, targetName));
                }
                // Recheck both ends after a decision as well. A source VM must never be powered off for a transfer.
                if (d.Assignment.VirtualMachineId is Guid owner) await EnsureOffAsync(owner);
                await EnsureOffAsync(VmId);
                while (!session.State.IsFinished)
                {
                    CancellationToken.ThrowIfCancellationRequested();
                    var state = await Task.Run(() => session.AdvanceAsync(answer));
                    answer = null;
                    if (state.RefreshRequired) NotifyChanged();
                    if (!state.RequiresDecision) continue;
                    Guid request = state.RequestId!.Value;
                    switch (state.Step)
                    {
                        case PcieAssignmentStep.ConfirmLastGpu:
                            void Continue(bool approve) => _ = RunFlowSegmentAsync(() => DrainQueueAsync(session, new(request, approve)));
                            var panel = new StackPanel { Padding = new(16, 12, 16, 12), Spacing = 8 };
                            panel.Children.Add(new TextBlock { Text = state.Message, TextWrapping = TextWrapping.Wrap });
                            var actions = Actions(Command(T("Skip"), () => Continue(false)), Command(L10n.Get("Common_Confirm"), () => Continue(true), true));
                            actions.HorizontalAlignment = HorizontalAlignment.Right;
                            panel.Children.Add(actions);
                            RenderSteps(below: CardShell(this, panel));
                            return;
                        case PcieAssignmentStep.ConfirmMmio:
                        case PcieAssignmentStep.ConfirmOfflineDisks:
                            // The session offlines disks inside the same operation that can restore them on failure.
                            answer = new(request, true);
                            break;
                        case PcieAssignmentStep.ConfirmCompanions:
                            // Confirmed related functions have their own queue entries and independent results.
                            answer = new(request, false);
                            break;
                        default:
                            throw new InvalidOperationException(state.Message);
                    }
                }
                if (session.State.Step == PcieAssignmentStep.Completed)
                {
                    assigned++;
                    if (confirmedRelated != null)
                        foreach (var related in confirmedRelated[d]) queue.Enqueue(related);
                }
                else if (session.State.Step == PcieAssignmentStep.Cancelled) skipped++;
                else failures.Add(F("DeviceError", d.FriendlyName, session.State.Message));
            }
            catch (OperationCanceledException) when (IsStopped) { break; }
            catch (Exception ex) { failures.Add(F("DeviceError", d.FriendlyName, ex.Message)); }
            queue.Dequeue();
            resumed = null;
            answer = null;
        }
        NotifyChanged();
        FinishFlow();
    }

    private void FinishFlow()
    {
        bool failed = failures.Count != 0;
        string? status = failed ? (assigned == 0 ? T("Failed") : F("Partial", assigned))
            : skipped > 0 ? F("SkippedCount", assigned, skipped) : null;
        RenderSteps(finished: true, failed: failed, right: status == null ? null : new TextBlock
        {
            Text = status, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush")
        });
        if (failed) ShowError(string.Join(Environment.NewLine, failures));
    }

    private void RenderSteps(bool finished = false, bool failed = false, FrameworkElement? right = null, UIElement? below = null)
    {
        BeginPage(Page.Workflow);
        string[] labels = [T("PowerOff"), T("Adjust"), T("AddDevices")];
        for (int i = 0; i < labels.Length; i++)
        {
            bool current = i == activeStep;
            var icon = BuildStepIcon(isSkipped: false, isDone: i < activeStep || (current && finished && !failed),
                isFailed: current && failed, isActive: current && !finished);
            var iconHost = new Grid { Width = 28, Height = 28, Children = { icon } };
            var title = new TextBlock { Text = labels[i], FontSize = 14.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { ColumnSpacing = 14, Padding = new(16, 12, 16, 12) };
            row.ColumnDefinitions.Add(new() { Width = new(28) });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(iconHost);
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            if (current && right != null) { Grid.SetColumn(right, 2); row.Children.Add(right); }
            body.Children.Add(CardShell(this, row));
            if (current && below != null) body.Children.Add(below);
        }
        if (finished)
        {
            var back = Command(T("BackList"), () => _ = RunAsync(LoadAsync), true);
            back.HorizontalAlignment = HorizontalAlignment.Right;
            body.Children.Add(back);
        }
    }
}
