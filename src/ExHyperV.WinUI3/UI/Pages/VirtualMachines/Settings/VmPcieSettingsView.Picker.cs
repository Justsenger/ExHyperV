using System.Collections.Concurrent;
using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmPcieSettingsView
{
    private sealed record Candidate(PcieDeviceInfo Device, string Label, bool Selectable, string? Reason = null, string? Hint = null);
    private IReadOnlyList<Candidate> pool = [];
    private readonly HashSet<PcieDeviceInfo> selected = [];
    private readonly HashSet<string> poolExpanded = new(StringComparer.OrdinalIgnoreCase);
    private string targetName = "";

    private async Task OpenPickerAsync()
    {
        var inventory = Data(await HostPcieService.GetPCIeInfoAsync());
        targetName = inventory.VirtualMachines.FirstOrDefault(vm => vm.VirtualMachineId == VmId)?.VirtualMachineName ?? VmId.ToString("D");
        var owners = new Dictionary<Guid, VmState>();
        foreach (var id in inventory.Devices.Select(d => d.Assignment.VirtualMachineId).OfType<Guid>().Distinct())
        {
            if (id == VmId) continue;
            CancellationToken.ThrowIfCancellationRequested();
            var state = await VmConfigurationState.ReadAsync(id);
            owners[id] = state.HasData ? state.Data.State : VmState.Unknown;
        }

        // Some multifunction controllers use a vendor or System class. Check the actual disks,
        // including for those devices, before making any host-owned function selectable.
        var reasons = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(inventory.Devices.Where(d => d.Assignment.Kind == PcieAssignmentKind.Host),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = CancellationToken }, async (d, _) =>
            {
                try
                {
                    var disks = await HostPcieService.GetControllerDisksAsync(d.InstanceId);
                    if (!disks.HasData) reasons[d.InstanceId] = F("DiskQueryFailed", CoreMessageResolver.ResolveError(disks));
                    else if (disks.Data.Any(disk => disk.IsBoot || disk.IsSystem)) reasons[d.InstanceId] = T("SystemDisk");
                }
                catch (Exception ex) { reasons[d.InstanceId] = F("DiskQueryFailed", CoreMessageResolver.ResolveException(ex)); }
            });
        if (IsStopped) return;
        Candidate Describe(PcieDeviceInfo device)
        {
            var owner = device.Assignment;
            if (owner.VirtualMachineId == VmId || settings!.Devices.Any(d => d.Path.Length > 0 && SameId(d.Path, device.Path)))
                return new(device, T("InUse"), false);
            if (reasons.TryGetValue(device.InstanceId, out var reason))
                return new(device, T("Unavailable"), false, reason);
            if (owner.VirtualMachineId is Guid id)
            {
                var state = owners.GetValueOrDefault(id, VmState.Unknown);
                if (state == VmState.Unknown) return new(device, T("Unavailable"), false, T("OwnerUnknown"));
                bool off = state == VmState.PoweredOff;
                string name = owner.VirtualMachineName ?? id.ToString("D");
                if (!off && state != VmState.Running) return new(device, T("Unavailable"), false, F("OwnerMustStop", name));
                return new(device, T(off ? "OtherVm" : "OtherRunning"), off, Hint: F(off ? "TransferFrom" : "OwnerMustStop", name));
            }
            return new(device, T(owner.Kind == PcieAssignmentKind.Host ? "Host" : "Unassigned"), true);
        }

        var sort = StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        pool = inventory.Devices.Select(Describe).OrderByDescending(c => c.Selectable)
            .ThenBy(c => c.Device.FriendlyName, sort).ThenBy(c => c.Device.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        selected.Clear();
        poolExpanded.Clear();
        RenderPicker();
    }

    private void RenderPicker()
    {
        BeginPage(Page.Picker);
        var add = Command(T("Add"), () => _ = RunFlowSegmentAsync(StartFlowAsync), accent: true);
        add.IsEnabled = selected.Count != 0;
        SetHeaderTrailingAction(Actions(Command(T("Back"), () => _ = RunAsync(LoadAsync)), add));
        if (pool.Count == 0) body.Children.Add(new TextBlock { Text = T("EmptyPool") });
        foreach (var item in pool)
        {
            var d = item.Device;
            var check = new CheckBox
            {
                Content = Identity(d.FriendlyName, d.ClassType), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = item.Selectable,
                IsChecked = selected.Contains(d)
            };
            AutomationProperties.SetName(check, d.FriendlyName);
            var tag = Badge(this, item.Label);
            if (item.Hint != null) ToolTipService.SetToolTip(tag, item.Hint);
            var actions = Actions(4, tag);
            if (item.Reason != null)
                actions.Children.Add(new SettingInfoButton(item.Label, item.Reason) { Width = 22, Height = 22, MinWidth = 22, MinHeight = 22 });
            bool open = poolExpanded.Contains(d.InstanceId);
            actions.Children.Add(ExpandButton(open, () =>
            {
                if (!poolExpanded.Remove(d.InstanceId)) poolExpanded.Add(d.InstanceId);
                RenderPicker();
            }));
            var header = Split(check, actions);
            header.Padding = new(16, 10, 12, 10);
            var card = CardShell(this, header, open ? Details(string.IsNullOrWhiteSpace(d.DisplayClassType) ? d.ClassType : d.DisplayClassType,
                d.Vendor, d.InstanceId, d.Path) : null);
            card.Opacity = item.Selectable ? 1 : 0.48;
            var normalBorder = card.BorderBrush;
            void Highlight() => card.BorderBrush = check.IsChecked == true ? ThemeBrush(this, "VmAccentBrush") : normalBorder;
            Highlight();
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) selected.Add(d);
                else selected.Remove(d);
                add.IsEnabled = selected.Count != 0;
                Highlight();
            };
            body.Children.Add(card);
        }
    }
}
