using System.IO;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

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
        var confirm = Command("添加", () => _ = RunAsync(() => SubmitAddAsync(d)), accent: true, enabled: false);
        SetHeaderTrailingAction(Actions(Command("返回", ShowList), confirm));
        void SelectDrive(bool dvd)
        {
            if (dvd == d.Dvd) return;
            d.Dvd = dvd;
            d.Selected.Clear();
            Render();
        }
        var tabs = Actions(4, AddTab("硬盘", "\uEDA2", !d.Dvd, () => SelectDrive(false)),
            AddTab("光驱", "\uE958", d.Dvd, () => SelectDrive(true)));
        pivot.Content = new Border { Child = tabs, BorderBrush = ThemeBrush(this, "DividerStrokeColorDefaultBrush"), BorderThickness = new(0, 0, 0, 1) };

        var source = Pill(d.Source.ToString(), value =>
        {
            d.Source = Enum.Parse<Source>(value);
            Render();
        },
            new("Existing", "连接文件"),
            new("New", d.Dvd ? "新建 ISO" : "新建硬盘"),
            new("Physical", d.Dvd ? "物理光驱" : "物理磁盘", !d.Dvd || generation == 1));
        var rows = new List<UIElement> { SettingRow("添加方式", null, source) };
        var feedback = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var parentFeedback = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var listFeedback = Stack();
        var sizeFeedback = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var batchFeedback = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        bool sourceValid = false;

        bool CapacityValid()
        {
            if (!create || d.Dvd || diff) return true;
            int max = d.Path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) ? 2040 : 65536;
            bool valid = int.TryParse(d.Capacity, out var n) && n >= 1 && n <= max;
            sizeFeedback.Content = valid ? null : max == 2040 && n > max
                ? Banner("VHD 格式上限为 2040 GiB", InfoBarSeverity.Warning,
                    InlineCommand("改存 .vhdx", () => { d.Path = Path.ChangeExtension(d.Path, ".vhdx"); Render(); }))
                : Banner($"容量须为 1 至 {max} GiB 的整数", InfoBarSeverity.Warning);
            return valid;
        }
        void Validate()
        {
            bool sizeValid = CapacityValid();
            bool enoughSlots = !physical || d.Selected.Count <= 1 || PhysicalSlots(d).Count == d.Selected.Count;
            batchFeedback.Content = physical && d.Selected.Count > 1 ? Banner(enoughSlots
                ? $"已选 {d.Selected.Count} 项，将从所选插槽起依次挂接" : "可用插槽不足，请减少所选设备",
                enoughSlots ? InfoBarSeverity.Informational : InfoBarSeverity.Warning) : null;
            confirm.IsEnabled = CanWrite && powerState is (VmState.PoweredOff or VmState.Running)
                && !gen2Optical && !gen1RunningOptical
                && d.Slot.Error == null && sizeValid && sourceValid && enoughSlots && (!physical || d.Selected.Count > 0);
        }

        void SetFeedback(FileCheck check)
        {
            sourceValid = check.Valid;
            feedback.Content = check.Message.Length > 0 ? Banner(check.Message, check.Severity) : null;
            Validate();
        }

        void CheckSource()
        {
            sourceValid = false;
            Validate();
            if (gen2Optical)
            {
                feedback.Content = null;
                parentFeedback.Content = null;
                listFeedback.Children.Clear();
                return;
            }
            if (physical)
            {
                listFeedback.Children.Clear();
                listFeedback.Children.Add(Banner("正在检测宿主设备…"));
            }
            else
            {
                feedback.Content = d.Path.Length > 0 ? Banner("正在检查路径…") : null;
                if (diff) parentFeedback.Content = Path.IsPathFullyQualified(d.Parent)
                    ? Banner("正在核验父链…") : Banner("未选择父盘");
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
                    if (candidates.Count == 0) listFeedback.Children.Add(Banner(dvd ? "未检测到宿主机物理光驱" : "未检测到宿主机物理磁盘"));
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
                    result = FileCheck.Error("请选择可读取的 ISO 来源目录。");
                VmDiskChainInspection? chain = null;
                if (diff && Path.IsPathFullyQualified(parent))
                    chain = Data(await VmStorageService.InspectDifferencingChainAsync(parent).WaitAsync(token));
                token.ThrowIfCancellationRequested();
                SetFeedback(result);
                if (diff)
                {
                    if (chain == null) parentFeedback.Content = Banner("未选择父盘");
                    else parentFeedback.Content = ChainSummary(chain);
                    sourceValid &= chain?.IsComplete == true;
                    if (chain?.Nodes.FirstOrDefault()?.Format is string format && Path.IsPathFullyQualified(path)
                        && !SamePath(Path.GetExtension(path), format))
                    {
                        sourceValid = false;
                        parentFeedback.Content = Banner($"保存后缀与父盘 {format.TrimStart('.').ToUpperInvariant()} 不一致", InfoBarSeverity.Warning,
                            InlineCommand($"改为 {format}", () => { d.Path = Path.ChangeExtension(path, format); Render(); }));
                    }
                    Validate();
                }
            }, message => SetFeedback(FileCheck.Error(message)));
        }

        if (physical)
        {
            if (gen2Optical)
            {
                rows.Add(CardRow(Banner("第 2 代虚拟机不支持物理光驱直通（仅第 1 代 IDE 支持）", InfoBarSeverity.Warning,
                    InlineCommand("改用连接文件", () => { d.Source = Source.Existing; Render(); }))));
            }
            else
            {
                var hdr = Split(new TextBlock { Text = d.Dvd ? "宿主光驱" : "宿主磁盘", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    InlineCommand("重新检测", () => { d.Candidates = null; d.Selected.Clear(); CheckSource(); }));
                rows.Add(CardRow(Stack(hdr, listFeedback, batchFeedback)));
            }
        }
        else if (create && d.Dvd)
        {
            rows.Add(FieldRowCard("来源目录", null, null,
                PathInput(d.Folder, @"C:\Drivers\Package", StoragePickers.FolderAsync, v => { d.Folder = v; CheckSource(); }, out _)));
            var labelBox = new TextBox { Text = d.Label, Width = 240, PlaceholderText = "NewISO" };
            labelBox.TextChanged += (_, _) => d.Label = labelBox.Text.Trim();
            rows.Add(SettingRow("卷标", null, labelBox));
            rows.Add(FieldRowCard("输出路径", null, null,
                PathInput(d.Path, @"D:\Hyper-V\ISO\output.iso", () => SaveMedia(true), v => { d.Path = v; CheckSource(); }, out _, "保存到"),
                feedback));
        }
        else if (create)
        {
            rows.Add(FieldRowCard("虚拟磁盘文件路径", null, "按文件扩展名区分 .vhd / .vhdx",
                PathInput(d.Path, @"D:\Hyper-V\Virtual Hard Disks\new_disk.vhdx", () => SaveMedia(false), v => { d.Path = v; CheckSource(); }, out _, "保存到"),
                feedback));
            rows.Add(SettingRow("磁盘类型",
                "动态扩展：按写入块动态分配宿主机空间。\n固定大小：创建时按逻辑容量一次性预分配全部空间。\n差异磁盘：链接只读父盘，仅记录增量写入块。",
                Pill(d.Type, v => { d.Type = v; Render(); },
                    new("Dynamic", "动态扩展"), new("Fixed", "固定大小"), new("Differencing", "差异磁盘"))));
            if (diff)
            {
                rows.Add(FieldRowCard("父盘路径", null, "容量与扇区格式自动继承父盘",
                    PathInput(d.Parent, "选择只读父盘文件 (.vhd / .vhdx)", () => OpenMedia(false), v => { d.Parent = v; CheckSource(); }, out _),
                    parentFeedback));
            }
            else
            {
                rows.Add(CapacitySection("虚拟容量", "暴露给来宾系统的逻辑容量（GiB），.vhd 上限 2040 GiB，.vhdx 上限 64 TiB。",
                    d.Capacity, v => { d.Capacity = v; Validate(); }, sizeFeedback));
                var advChevron = new FontIcon
                {
                    Glyph = d.ShowAdvanced ? "\uE70E" : "\uE70D",
                    FontFamily = new FontFamily(SegoeFont), FontSize = 12,
                    Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush")
                };
                var advRow = CardRow(Split(new TextBlock { Text = "高级格式参数", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, advChevron));
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                var hover = ThemeBrush(this, "SubtleFillColorTertiaryBrush");
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
                        "物理扇区格式",
                        "默认不指定参数。512n 为 512/512，512e 为 512/4096，4Kn 为 4096/4096。",
                        ["默认", "512n", "512e", "4Kn"],
                        Array.IndexOf(Sectors, d.Sector), i => d.Sector = Sectors[i]);
                    var blockCol = StopSelector(
                        "虚拟块大小",
                        ".vhdx 块分配表单个有效载荷块大小。块越小粒度越细，块越大元数据开销越低。",
                        Blocks.Select(n => n == 0 ? "默认" : $"{n} MiB").ToArray(),
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
                d.Dvd ? "ISO 映像文件" : "虚拟磁盘文件路径",
                null,
                null,
                PathInput(d.Path, d.Dvd ? @"D:\ISO\system.iso（可留空以添加空光驱）" : @"D:\Hyper-V\Virtual Hard Disks\disk.vhdx",
                    () => OpenMedia(d.Dvd), v => { d.Path = v; CheckSource(); }, out _),
                feedback));
        }

        page.Children.Add(GroupedCard([.. rows]));
        if (gen1RunningOptical)
            page.Children.Add(Banner("第 1 代虚拟机运行中不支持热添加光驱，请先关机", InfoBarSeverity.Warning));
        if (powerState is not (VmState.PoweredOff or VmState.Running))
            page.Children.Add(Banner("请在虚拟机已关机或正在运行时添加设备。", InfoBarSeverity.Warning));
        if (!gen2Optical && !gen1RunningOptical)
            page.Children.Add(SlotCard(d.Slot, d.DriveType, null, true, Validate));
        CheckSource();
    }

    private Grid AddTab(string label, string glyph, bool active, Action select)
    {
        var button = Command(label, select);
        button.Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"];
        button.Content = Actions(6, new FontIcon { Glyph = glyph, FontFamily = new(SegoeFont), FontSize = 14 }, Text(label, 13));
        var grid = new Grid { Children = { button } };
        if (active) grid.Children.Add(new Border { Height = 3, CornerRadius = new(2), Margin = new(9, 0, 9, 0),
            Background = ThemeBrush(this, "VmAccentBrush"), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false });
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
            Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
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
            ? [$"磁盘 {d3.Number}", Clean(c.Message)]
            : [c.OpticalDrive!.Drive, Clean(c.Message)];
        string status = c.Availability switch
        {
            VmPassthroughAvailability.Available => "可直通",
            VmPassthroughAvailability.Assigned => c.OwnerVmId == VmId ? "已分配给当前虚拟机" : "已分配给其他虚拟机",
            _ => "不可直通"
        };

        var checkbox = new Border
        {
            Width = 18, Height = 18, CornerRadius = new(4), BorderThickness = new(1.5),
            BorderBrush = ThemeBrush(this, selected ? "VmAccentBrush" : "TextFillColorTertiaryBrush"),
            Background = selected ? ThemeBrush(this, "VmAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = selected ? new FontIcon
            {
                Glyph = "\uE73E", FontFamily = new FontFamily(SegoeFont), FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            } : null
        };

        var subRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var part in subParts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            subRow.Children.Add(new TextBlock
            {
                Text = part, FontSize = 12,
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
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
            Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
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
            Background = ThemeBrush(this, selected ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorTertiaryBrush"),
            BorderBrush = ThemeBrush(this, selected ? "VmAccentBrush" : "CardStrokeColorDefaultBrush"),
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
        string title = create && d.Dvd ? "打包并挂接 ISO" : "添加存储设备";
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
            throw new InvalidOperationException("设备身份或分配状态已变化，请重新选择直通设备。");
        }
        var slots = PhysicalSlots(d);
        if (slots.Count == 0 || slots.Count != d.Selected.Count ||
            slots[0] != new VmStorageSlot(d.Slot.Type, d.Slot.Number, d.Slot.Location))
        {
            Render();
            throw new InvalidOperationException("可用插槽已变化，请核对位置后重新提交。");
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
        var labels = d.Selected.Select(c => c.Disk is { } disk ? $"挂接磁盘 {disk.Number}（{disk.FriendlyName}）"
            : $"挂接光驱 {c.OpticalDrive!.Drive}").ToArray();
        await RunStorageTaskAsync("添加物理直通设备", labels, async progress =>
        {
            for (int i = 0; i < requests.Count; i++)
            {
                progress.Report((i, -1, labels[i]));
                var result = await VmStorageService.AddDriveAsync(VmId, requests[i]);
                if (!result.Success) return Failure(result);
                progress.Report((i, 100, labels[i] + "已完成"));
            }
            return ApiResponse.Ok();
        }, openList: true);
    }
}
