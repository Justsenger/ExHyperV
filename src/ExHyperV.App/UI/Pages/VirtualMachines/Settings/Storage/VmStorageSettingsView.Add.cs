
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using static ExHyperV.App.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Storage;

public sealed partial class VmStorageSettingsView
{
    private enum Source { Existing, New, Physical }
    private sealed class AddDraft
    {
        public bool Dvd, ShowAdvanced;
        public Source Source;
        public string DriveType => Dvd ? "DvdDrive" : "HardDisk";
        public readonly SlotDraft Slot = new();
        public string DiskPath = "", NewDisk = "", IsoPath = "", NewIso = "", Folder = "", Label = "NewISO";
        public string Parent = "", Type = "Dynamic", Capacity = "128", Sector = "Default";
        public int BlockMiB;
        public IReadOnlyList<VmPassthroughCandidate>? Candidates;
        public readonly List<VmPassthroughCandidate> Selected = [];
        public string Path
        {
            get => (Dvd, Source) switch
            {
                (true, Source.New) => NewIso, (true, _) => IsoPath, (false, Source.New) => NewDisk, _ => DiskPath
            };
            set
            {
                if (Dvd && Source == Source.New) NewIso = value;
                else if (Dvd) IsoPath = value;
                else if (Source == Source.New) NewDisk = value;
                else DiskPath = value;
            }
        }
    }

    private void RenderAdd(AddDraft d)
    {
        draftProbe.Cancel();
        bool physical = d.Source == Source.Physical;
        bool create = d.Source == Source.New;
        bool diff = create && !d.Dvd && d.Type == "Differencing";
        bool gen2Optical = physical && d.Dvd && generation != 1;
        bool gen1RunningOptical = generation == 1 && running && d.Dvd;
        var confirm = Command(L10n.Get("Pcie_Add"), () => _ = RunAsync(() => SubmitAddAsync(d)), accent: true, enabled: false);
        SetHeaderTrailingAction(Actions(Command(L10n.Get("Pcie_Back"), ShowList), confirm));
        void SelectDrive(bool dvd)
        {
            if (dvd == d.Dvd) return;
            d.Dvd = dvd;
            d.Selected.Clear();
            Render();
        }
        var tabs = Actions(4, AddTab(L10n.Get("Storage_HardDisk"), "\uEDA2", !d.Dvd, () => SelectDrive(false)),
            AddTab(L10n.Get("Storage_OpticalDrive"), "\uE958", d.Dvd, () => SelectDrive(true)));
        pivot.Content = new Border { Child = tabs, BorderBrush = ThemeHelper.ThemeBrush(this, "DividerStrokeColorDefaultBrush"), BorderThickness = new(0, 0, 0, 1) };

        var source = Pill(d.Source.ToString(), value =>
        {
            d.Source = Enum.Parse<Source>(value);
            Render();
        },
            new("Existing", L10n.Get("Storage_AttachFile")),
            new("New", d.Dvd ? L10n.Get("Storage_CreateIso") : L10n.Get("Storage_CreateDisk")),
            new("Physical", d.Dvd ? L10n.Get("Dev_PhysicalOpticalDrive") : L10n.Get("Dev_PhysicalDisk"), !d.Dvd || generation == 1));
        var rows = new List<UIElement> { SettingRow(L10n.Get("Storage_AddMethod"), null, source) };
        var feedback = new Border();
        var parentFeedback = new Border();
        var listFeedback = Stack();
        var sizeFeedback = new Border();
        var batchFeedback = new Border();
        bool sourceValid = false;

        bool CapacityValid()
        {
            if (!create || d.Dvd || diff) return true;
            int max = d.Path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) ? 2040 : 65536;
            bool valid = int.TryParse(d.Capacity, out var n) && n >= 1 && n <= max;
            sizeFeedback.Child = valid ? null : max == 2040 && n > max
                ? Banner(L10n.Get("Storage_VhdCapacityLimit"), InfoBarSeverity.Warning,
                    InlineCommand(L10n.Get("Storage_UseVhdx"), () => { d.Path = Path.ChangeExtension(d.Path, ".vhdx"); Render(); }))
                : Banner(L10n.Format("Storage_CapacityRange", max), InfoBarSeverity.Warning);
            return valid;
        }
        void Validate()
        {
            bool sizeValid = CapacityValid();
            bool enoughSlots = !physical || d.Selected.Count <= 1 || PhysicalSlots(d).Count == d.Selected.Count;
            batchFeedback.Child = physical && d.Selected.Count > 1 ? Banner(enoughSlots
                ? L10n.Format("Storage_SequentialSlots", d.Selected.Count) : L10n.Get("Storage_NotEnoughSlots"),
                enoughSlots ? InfoBarSeverity.Informational : InfoBarSeverity.Warning) : null;
            confirm.IsEnabled = CanWrite && powerState is (VmState.PoweredOff or VmState.Running)
                && !gen2Optical && !gen1RunningOptical
                && d.Slot.Error == null && sizeValid && sourceValid && enoughSlots && (!physical || d.Selected.Count > 0);
        }

        void SetFeedback(FileCheck check)
        {
            sourceValid = check.Valid;
            feedback.Child = check.Message.Length > 0 ? Banner(check.Message, check.Severity) : null;
            Validate();
        }

        void CheckSource()
        {
            if (d.Dvd && !physical && !create && d.Path.Length == 0)
            {
                draftProbe.Cancel();
                SetFeedback(FileCheck.Ok());
                return;
            }
            sourceValid = false;
            Validate();
            if (gen2Optical || (!physical && string.IsNullOrWhiteSpace(d.Path) && (!d.Dvd || create)))
            {
                draftProbe.Cancel();
                feedback.Child = null;
                parentFeedback.Child = null;
                listFeedback.Children.Clear();
                return;
            }
            if (physical)
            {
                listFeedback.Children.Clear();
                listFeedback.Children.Add(Banner(L10n.Get("Storage_ProbingDevices")));
            }
            else
            {
                feedback.Child = d.Path.Length > 0 ? Banner(L10n.Get("Storage_CheckingPath")) : null;
                if (diff) parentFeedback.Child = Path.IsPathFullyQualified(d.Parent)
                    ? Banner(L10n.Get("Storage_CheckingParentChain")) : Banner(L10n.Get("Storage_NoParentSelected"));
            }
            string path = d.Path, parent = d.Parent, folder = d.Folder;
            bool dvd = d.Dvd;
            _ = draftProbe.StartAsync(CancellationToken, async token =>
            {
                if (physical)
                {
                    d.Candidates ??= Data(await VmStorageService.GetPassthroughCandidatesAsync(VmId).WaitAsync(token));
                    token.ThrowIfCancellationRequested();
                    listFeedback.Children.Clear();
                    var candidates = d.Candidates.Where(c => dvd ? c.OpticalDrive != null : c.Disk != null).ToList();
                    if (candidates.Count == 0) listFeedback.Children.Add(Banner(dvd ? L10n.Get("Storage_NoHostOptical") : L10n.Get("Storage_NoHostDisks")));
                    foreach (var c in candidates)
                    {
                        var card = CandidateCard(c, d.Selected.Contains(c), () =>
                        {
                            if (!d.Selected.Remove(c)) d.Selected.Add(c);
                            Render();
                        });
                        listFeedback.Children.Add(card);
                    }
                    sourceValid = true;
                    Validate();
                    return;
                }
                var result = await Task.Run(() => CheckMedia(path, dvd, create), token).WaitAsync(token);
                if (create && dvd && !await Task.Run(() => Path.IsPathFullyQualified(folder) && Directory.Exists(folder), token).WaitAsync(token))
                    result = FileCheck.Error(L10n.Get("Storage_SelectIsoFolder"));
                VmDiskChainInspection? chain = null;
                if (diff && Path.IsPathFullyQualified(parent))
                    chain = Data(await VmStorageService.InspectDifferencingChainAsync(parent).WaitAsync(token));
                token.ThrowIfCancellationRequested();
                SetFeedback(result);
                if (diff)
                {
                    if (chain == null) parentFeedback.Child = Banner(L10n.Get("Storage_NoParentSelected"));
                    else parentFeedback.Child = ChainSummary(chain);
                    sourceValid &= chain?.IsComplete == true;
                    if (chain?.Nodes.FirstOrDefault()?.Format is string format && Path.IsPathFullyQualified(path)
                        && !SamePath(Path.GetExtension(path), format))
                    {
                        sourceValid = false;
                        parentFeedback.Child = Banner(L10n.Format("Storage_ParentExtensionMismatch", format.TrimStart('.').ToUpperInvariant()), InfoBarSeverity.Warning,
                            InlineCommand(L10n.Format("Storage_ChangeExtension", format), () => { d.Path = Path.ChangeExtension(path, format); Render(); }));
                    }
                    Validate();
                }
            }, message => SetFeedback(FileCheck.Error(message)));
        }

        if (physical)
        {
            if (gen2Optical)
            {
                rows.Add(CardRow(Banner(L10n.Get("Storage_PhysicalDvdRequiresIde"), InfoBarSeverity.Warning,
                    InlineCommand(L10n.Get("Storage_SwitchToFile"), () => { d.Source = Source.Existing; Render(); }))));
            }
            else
            {
                var hdr = Split(new TextBlock { Text = d.Dvd ? L10n.Get("Storage_HostDvd") : L10n.Get("Storage_HostDisk"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    InlineCommand(L10n.Get("Storage_Rescan"), () => { d.Candidates = null; d.Selected.Clear(); CheckSource(); }));
                rows.Add(CardRow(Stack(hdr, listFeedback, batchFeedback)));
            }
        }
        else if (create && d.Dvd)
        {
            rows.Add(FieldRowCard(L10n.Get("Storage_SourceFolder"), null, null,
                PathInput(d.Folder, @"C:\Drivers\Package", StoragePickers.FolderAsync, v => { d.Folder = v; CheckSource(); }, out _)));
            var labelBox = new TextBox { Text = d.Label, Width = 240, PlaceholderText = "NewISO" };
            labelBox.TextChanged += (_, _) => d.Label = labelBox.Text.Trim();
            rows.Add(SettingRow(L10n.Get("Storage_VolumeLabel"), null, labelBox));
            rows.Add(FieldRowCard(L10n.Get("Storage_OutputPath"), null, null,
                PathInput(d.Path, @"D:\Hyper-V\ISO\output.iso", () => SaveMedia(true), v => { d.Path = v; CheckSource(); }, out _, L10n.Get("Storage_SaveTo")),
                feedback));
        }
        else if (create)
        {
            rows.Add(FieldRowCard(L10n.Get("Storage_DiskFilePath"), null, L10n.Get("Storage_DiskExtensionHelp"),
                PathInput(d.Path, @"D:\Hyper-V\Virtual Hard Disks\new_disk.vhdx", () => SaveMedia(false), v => { d.Path = v; CheckSource(); }, out _, L10n.Get("Storage_SaveTo")),
                feedback));
            rows.Add(SettingRow(L10n.Get("Storage_DiskType"),
                L10n.Get("Storage_DiskTypeHelp"),
                Pill(d.Type, v => { d.Type = v; Render(); },
                    new("Dynamic", L10n.Get("Dev_VhdDynamic")), new("Fixed", L10n.Get("Dev_VhdFixed")), new("Differencing", L10n.Get("Storage_DifferencingDisk")))));
            if (diff)
            {
                rows.Add(FieldRowCard(L10n.Get("Storage_ParentPath"), null, L10n.Get("Storage_InheritParentFormat"),
                    PathInput(d.Parent, L10n.Get("Storage_SelectReadOnlyParent"), () => OpenMedia(false), v => { d.Parent = v; CheckSource(); }, out _),
                    parentFeedback));
            }
            else
            {
                rows.Add(CapacitySection(L10n.Get("Storage_VirtualCapacity"), L10n.Get("Storage_VirtualCapacityHelp"),
                    d.Capacity, v => { d.Capacity = v; Validate(); }, sizeFeedback));
                var advChevron = new FontIcon
                {
                    Glyph = d.ShowAdvanced ? "\uE70E" : "\uE70D",
                    FontFamily = AppFonts.SegoeFluent, FontSize = 12,
                    Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush")
                };
                var advRow = CardRow(Split(new TextBlock { Text = L10n.Get("Storage_AdvancedFormat"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, advChevron));
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                var hover = ThemeHelper.ThemeBrush(this, "SubtleFillColorTertiaryBrush");
                advRow.Background = transparent;
                advRow.PointerEntered += (_, _) => advRow.Background = hover;
                advRow.PointerExited += (_, _) => advRow.Background = transparent;
                advRow.PointerCanceled += (_, _) => advRow.Background = transparent;
                advRow.Tapped += (_, _) => { d.ShowAdvanced = !d.ShowAdvanced; Render(); };
                rows.Add(advRow);
                if (d.ShowAdvanced)
                {
                    var advGrid = new Grid { ColumnSpacing = 20 };
                    advGrid.ColumnDefinitions.Add(new());
                    advGrid.ColumnDefinitions.Add(new());
                    var sectorCol = StopSelector(
                        L10n.Get("Storage_PhysicalSectorFormat"),
                        L10n.Get("Storage_PhysicalSectorFormatHelp"),
                        [L10n.Get("Common_Default"), "512n", "512e", "4Kn"],
                        Array.IndexOf(Sectors, d.Sector), i => d.Sector = Sectors[i]);
                    var blockCol = StopSelector(
                        L10n.Get("Storage_BlockSize"),
                        L10n.Get("Storage_BlockSizeHelp"),
                        Blocks.Select(n => n == 0 ? L10n.Get("Common_Default") : $"{n} MiB").ToArray(),
                        Array.IndexOf(Blocks, d.BlockMiB), i => d.BlockMiB = Blocks[i]);
                    Grid.SetColumn(blockCol, 1);
                    advGrid.Children.Add(sectorCol);
                    advGrid.Children.Add(blockCol);
                    rows.Add(CardRow(advGrid));
                }
            }
        }
        else
        {
            rows.Add(FieldRowCard(
                d.Dvd ? L10n.Get("Storage_IsoImageFile") : L10n.Get("Storage_DiskFilePath"),
                null,
                null,
                PathInput(d.Path, d.Dvd ? L10n.Get("Storage_EmptyDvdPlaceholder") : @"D:\Hyper-V\Virtual Hard Disks\disk.vhdx",
                    () => OpenMedia(d.Dvd), v => { d.Path = v; CheckSource(); }, out _),
                feedback));
        }

        page.Children.Add(GroupedCard([.. rows]));
        if (gen1RunningOptical)
            page.Children.Add(Banner(L10n.Get("Storage_OfflineGen1Dvd"), InfoBarSeverity.Warning));
        if (powerState is not (VmState.PoweredOff or VmState.Running))
            page.Children.Add(Banner(L10n.Get("Storage_AddStateRequired"), InfoBarSeverity.Warning));
        if (!gen2Optical && !gen1RunningOptical)
            page.Children.Add(SlotCard(d.Slot, d.DriveType, null, true, Validate));
        CheckSource();
    }

    private Grid AddTab(string label, string glyph, bool active, Action select)
    {
        var button = Command(label, select);
        button.Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"];
        button.Content = Actions(6, new FontIcon { Glyph = glyph, FontFamily = AppFonts.SegoeFluent, FontSize = 14 }, Text(label, 13));
        var grid = new Grid { Children = { button } };
        if (active) grid.Children.Add(new Border { Height = 3, CornerRadius = new(2), Margin = new(9, 0, 9, 0),
            Background = ThemeHelper.ThemeBrush(this, "VmAccentBrush"), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false });
        return grid;
    }

    private static readonly string[] Sectors = ["Default", "512n", "512e", "4kn"];
    private static readonly int[] Blocks = [0, 1, 2, 4, 8, 16, 32, 64, 128, 256];
    private StackPanel StopSelector(string title, string description, string[] labels, int index, Action<int> change)
    {
        int safeIdx = Math.Clamp(index, 0, labels.Length - 1);
        var label = SettingLabel(title, description);
        var readout = new TextBlock
        {
            Text = labels[safeIdx], FontSize = 13,
            Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: true) { CustomHintFormatter = i => labels[i] };
        slider.SetRange(0, labels.Length - 1);
        slider.Value = safeIdx;
        slider.ValueChanged += i => { readout.Text = labels[i]; change(i); };
        AutomationProperties.SetName(slider, title);
        return new StackPanel { Spacing = 6, Children = { Split(label, readout), slider } };
    }

    private Button CandidateCard(VmPassthroughCandidate c, bool selected, Action select)
    {
        string title = c.Disk is { } disk ? disk.FriendlyName : c.OpticalDrive!.Model;
        string sizeText = c.Disk is { } d2 ? Size(d2.SizeGiB) : c.OpticalDrive!.Drive;
        string[] subParts = c.Disk is { } d3
            ? [L10n.Format("Overview_DiskIndexed", d3.Number), Clean(c.Message)]
            : [c.OpticalDrive!.Drive, Clean(c.Message)];
        string status = c.Availability switch
        {
            VmPassthroughAvailability.Available => L10n.Get("Storage_AvailablePassthrough"),
            VmPassthroughAvailability.Assigned => c.OwnerVmId == VmId ? L10n.Get("Storage_AssignedHere") : L10n.Get("Storage_AssignedElsewhere"),
            _ => L10n.Get("Pcie_Unavailable")
        };

        var checkbox = new Border
        {
            Width = 18, Height = 18, CornerRadius = new(4), BorderThickness = new(1.5),
            BorderBrush = ThemeHelper.ThemeBrush(this, selected ? "VmAccentBrush" : "TextFillColorTertiaryBrush"),
            Background = selected ? ThemeHelper.ThemeBrush(this, "VmAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = selected ? new FontIcon
            {
                Glyph = "\uE73E", FontFamily = AppFonts.SegoeFluent, FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            } : null
        };

        var subRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var part in subParts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            subRow.Children.Add(new TextBlock
            {
                Text = part, FontSize = 12,
                Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
            });
        }
        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
                subRow
            }
        };
        var badge = Badge(this, status);
        badge.Height = 20;
        badge.Padding = new(7, 0, 7, 0);
        ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
        var right = Actions(8, badge, new TextBlock
        {
            Text = sizeText, FontSize = 12,
            Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var b = new Button
        {
            IsEnabled = c.Availability == VmPassthroughAvailability.Available,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new(6),
            BorderThickness = new(1),
            Padding = new(12, 10, 12, 10),
            Background = ThemeHelper.ThemeBrush(this, selected ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorTertiaryBrush"),
            BorderBrush = ThemeHelper.ThemeBrush(this, selected ? "VmAccentBrush" : "CardStrokeColorDefaultBrush"),
            Content = Split(WithLeading(checkbox, info), right)
        };
        AutomationProperties.SetName(b, $"{title} {status}");
        b.Click += (_, _) => select();
        return b;
    }

    private List<VmStorageSlot> PhysicalSlots(AddDraft d)
    {
        var first = new VmStorageSlot(d.Slot.Type, d.Slot.Number, d.Slot.Location);
        var available = from type in VmStorageService.GetControllerTypes(generation, running, d.DriveType)
                        from number in VmStorageService.GetControllerNumbers(type)
                        from location in VmStorageService.GetAvailableLocations(type, number, attachments)
                        select new VmStorageSlot(type, number, location);
        // A manual batch uses the selected slot, then the same controller's remaining slots.
        return available.OrderByDescending(s => s == first)
            .ThenByDescending(s => !d.Slot.Automatic && s.ControllerType == first.ControllerType && s.ControllerNumber == first.ControllerNumber)
            .Take(d.Selected.Count).ToList();
    }

    private async Task SubmitAddAsync(AddDraft d)
    {
        if (!CanWrite || d != adding) return;
        bool create = d.Source == Source.New;
        if (d.Source == Source.Physical) { await SubmitPhysicalAsync(d); return; }
        bool disk = create && !d.Dvd, independent = disk && d.Type != "Differencing";
        var request = new VmStorageAddRequest
        {
            DriveType = d.DriveType, ControllerType = d.Slot.Type, ControllerNumber = d.Slot.Number, Location = d.Slot.Location,
            IsNew = create, PathOrNumber = d.Path,
            VhdType = disk ? d.Type : "Dynamic", ParentPath = disk && !independent ? d.Parent : null,
            SizeGiB = independent ? int.Parse(d.Capacity) : null, SectorFormat = independent ? d.Sector : "Default",
            BlockSizeBytes = independent && d.BlockMiB > 0 ? (uint)d.BlockMiB * 1048576 : null,
            IsoSourcePath = create && d.Dvd ? d.Folder : null, IsoOutputPath = create && d.Dvd ? d.Path : null,
            IsoVolumeLabel = create && d.Dvd ? d.Label : null
        };
        await ReadSnapshotAsync();
        var prepared = await Task.Run(() => VmStorageService.PrepareAddRequest(request, generation, running, attachments));
        if (!prepared.Success) { Render(); throw new InvalidOperationException(Clean(prepared.Error!)); }
        CancellationToken.ThrowIfCancellationRequested();
        string title = create && d.Dvd ? L10n.Get("Storage_PackAttachIso") : L10n.Get("Storage_AddDevice");
        await RunSingleAsync(title, async () =>
        {
            var result = await VmStorageService.AddDriveAsync(VmId, prepared.Request!);
            return result.Success ? ApiResponse.Ok() : Failure(result);
        }, openList: !(create && d.Dvd));
    }

    private async Task SubmitPhysicalAsync(AddDraft d)
    {
        await ReadSnapshotAsync();
        var candidates = Data(await VmStorageService.GetPassthroughCandidatesAsync(VmId).WaitAsync(CancellationToken));
        if (d.Selected.Any(selected => !candidates.Any(current => current.Availability == VmPassthroughAvailability.Available &&
            (selected.Disk is { } disk ? current.Disk is { } fresh && disk.Number == fresh.Number &&
                disk.SizeBytes == fresh.SizeBytes && SamePath(disk.UniqueId, fresh.UniqueId) && SamePath(disk.SerialNumber, fresh.SerialNumber)
                : SamePath(selected.OpticalDrive!.PnpDeviceId, current.OpticalDrive?.PnpDeviceId)))))
        {
            d.Candidates = candidates;
            d.Selected.Clear();
            Render();
            throw new InvalidOperationException(L10n.Get("Storage_PassthroughChanged"));
        }
        var slots = PhysicalSlots(d);
        if (slots.Count == 0 || slots.Count != d.Selected.Count ||
            slots[0] != new VmStorageSlot(d.Slot.Type, d.Slot.Number, d.Slot.Location))
        {
            Render();
            throw new InvalidOperationException(L10n.Get("Storage_SlotChanged"));
        }
        var requests = new List<VmStorageAddRequest>();
        for (int i = 0; i < slots.Count; i++)
        {
            var c = d.Selected[i];
            var slot = slots[i];
            var request = new VmStorageAddRequest
            {
                DriveType = d.DriveType, IsPhysical = true, PhysicalDisk = c.Disk, PathOrNumber = c.OpticalDrive?.PnpDeviceId,
                ControllerType = slot.ControllerType, ControllerNumber = slot.ControllerNumber, Location = slot.Location
            };
            var prepared = VmStorageService.PrepareAddRequest(request, generation, running, attachments);
            if (!prepared.Success) throw new InvalidOperationException(Clean(prepared.Error!));
            requests.Add(prepared.Request!);
        }
        var labels = d.Selected.Select(c => c.Disk is { } disk ? L10n.Format("Storage_AttachDiskStep", disk.Number, disk.FriendlyName)
            : L10n.Format("Storage_AttachDvdStep", c.OpticalDrive!.Drive)).ToArray();
        await RunStorageTaskAsync(L10n.Get("Storage_AddPhysicalDevice"), labels, async progress =>
        {
            for (int i = 0; i < requests.Count; i++)
            {
                progress.Report((i, -1, labels[i]));
                var result = await VmStorageService.AddDriveAsync(VmId, requests[i]);
                if (!result.Success) return Failure(result);
                progress.Report((i, 100, labels[i] + L10n.Get("Common_Completed")));
            }
            return ApiResponse.Ok();
        }, openList: true);
    }
}
