using System.Globalization;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmStorageSettingsView
{
    private StorageForm? form;

    private async Task OpenAddAsync()
    {
        if (!CanChange()) return;
        await LoadAsync();
        if (!CanChange()) return;
        form = new StorageForm(this);
        await form.ReadHostSourcesAsync();
        if (IsStopped) return;
        editor.Content = form.Content;
        deviceHost.Visibility = Visibility.Collapsed;
        add.Visibility = refresh.Visibility = Visibility.Collapsed;
    }

    private async Task CloseAddAsync()
    {
        form = null;
        editor.Content = null;
        deviceHost.Visibility = Visibility.Visible;
        add.Visibility = refresh.Visibility = Visibility.Visible;
        await LoadAsync();
    }

    private static Task<string?> PickOpenAsync(params string[] extensions) => StoragePickers.OpenAsync(extensions);

    private static async Task RevealMediaAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var folder = await file.GetParentAsync();
        if (folder is null) throw new InvalidOperationException("未找到介质所在文件夹。");
        var options = new FolderLauncherOptions();
        options.ItemsToSelect.Add(file);
        if (!await Launcher.LaunchFolderAsync(folder, options))
            throw new InvalidOperationException("无法打开介质所在的文件夹。");
    }

    private sealed class StorageForm
    {
        private readonly VmStorageSettingsView owner;
        private bool updating;
        private bool sourcesKnown;
        private readonly ComboBox source = Choices(("虚拟介质文件", "Virtual"), ("宿主物理设备", "Physical"));
        private readonly ComboBox drive = Choices(("硬盘", "HardDisk"), ("光驱", "DvdDrive"));
        private readonly ToggleSwitch create = new() { OnContent = "", OffContent = "" };
        private readonly ToggleSwitch automatic = new() { IsOn = true, OnContent = "", OffContent = "" };
        private readonly ComboBox controller = new() { MinWidth = 92 };
        private readonly ComboBox number = new() { MinWidth = 68 };
        private readonly ComboBox location = new() { MinWidth = 68 };
        private readonly TextBox path = new() { PlaceholderText = "选择虚拟磁盘文件" };
        private readonly Button browse = new() { Content = "浏览" };
        private readonly ComboBox vhdType = Choices(("动态扩展", "Dynamic"), ("固定大小", "Fixed"), ("差分磁盘", "Differencing"));
        private readonly ComboBox size = new() { IsEditable = true, Text = "128", ItemsSource = new[] { "32", "64", "128", "256", "512", "1024" }, MinWidth = 120 };
        private readonly ComboBox sector = Choices(("默认", "Default"), ("512n", "512n"), ("512e", "512e"), ("4Kn", "4kn"));
        private readonly ComboBox block = Choices(("默认", "Default"), ("1 MB", "1048576"), ("2 MB", "2097152"), ("4 MB", "4194304"),
            ("8 MB", "8388608"), ("16 MB", "16777216"), ("32 MB", "33554432"), ("64 MB", "67108864"), ("128 MB", "134217728"), ("256 MB", "268435456"));
        private readonly TextBox parent = new() { PlaceholderText = "选择父磁盘" };
        private readonly TextBox isoSource = new() { PlaceholderText = "选择打包为 ISO 的文件夹" };
        private readonly TextBox volume = new() { Text = "NewISO" };
        private readonly TextBox isoOutput = new() { PlaceholderText = "选择 ISO 保存路径" };
        private readonly ComboBox physicalDisk = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox physicalOptical = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly InfoBar sourceWarning = new() { Severity = InfoBarSeverity.Warning, IsClosable = false };
        private readonly InfoBar slotWarning = new() { Severity = InfoBarSeverity.Warning, IsClosable = false };
        private readonly Button confirm = new() { Content = "添加" };
        private readonly Grid newRow;
        private readonly Border pathRow;
        private readonly Border diskOptions;
        private readonly Border parentRow;
        private readonly Border isoOptions;
        private readonly Border physicalOptions;
        private readonly StackPanel slotEditors = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        public StackPanel Content { get; } = new() { Spacing = 12 };
        private string DriveType => Selected(drive);
        private bool Physical => Selected(source) == "Physical";

        public StorageForm(VmStorageSettingsView owner)
        {
            this.owner = owner;
            Content.Children.Add(new TextBlock { Text = "添加存储", FontSize = 20, Margin = new Thickness(0, 0, 0, 4) });
            slotEditors.Children.Add(Field("接口", controller));
            slotEditors.Children.Add(Field("控制器编号", number));
            slotEditors.Children.Add(Field("位置", location));
            var slots = new StackPanel { Spacing = 12, Margin = new Thickness(24, 0, 24, 20) };
            slots.Children.Add(slotEditors);
            slots.Children.Add(slotWarning);
            Content.Children.Add(Card(Row("自动选择插槽", "根据虚拟机代数、运行状态和已占用插槽分配。", automatic), slots));
            newRow = Row("新建介质", null, create);
            Content.Children.Add(Card(Row("来源", null, source), Row("设备类型", null, drive), newRow));

            pathRow = FieldCard("介质路径", PathEditor(path, browse));
            Content.Children.Add(pathRow);
            diskOptions = Card(Row("容量（GB）", null, size), Row("磁盘类型", null, vhdType), Row("扇区格式", null, sector), Row("块大小", null, block));
            Content.Children.Add(diskOptions);
            var parentBrowse = new Button { Content = "浏览" };
            parentRow = FieldCard("父磁盘", PathEditor(parent, parentBrowse));
            Content.Children.Add(parentRow);
            var folderBrowse = new Button { Content = "选择" };
            var isoBrowse = new Button { Content = "保存为" };
            var isoFields = new StackPanel { Spacing = 16, Margin = new Thickness(24, 20, 24, 20) };
            isoFields.Children.Add(Field("来源文件夹", PathEditor(isoSource, folderBrowse)));
            isoFields.Children.Add(Field("卷标", volume));
            isoFields.Children.Add(Field("ISO 保存路径", PathEditor(isoOutput, isoBrowse)));
            isoOptions = Card(isoFields);
            Content.Children.Add(isoOptions);

            var reloadPhysical = new Button { Content = "刷新物理设备", HorizontalAlignment = HorizontalAlignment.Right };
            var physicalFields = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
            physicalFields.Children.Add(physicalDisk);
            physicalFields.Children.Add(physicalOptical);
            physicalFields.Children.Add(sourceWarning);
            physicalFields.Children.Add(reloadPhysical);
            physicalOptions = Card(physicalFields);
            Content.Children.Add(physicalOptions);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var cancel = new Button { Content = "取消" };
            confirm.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            Content.Children.Add(buttons);

            source.SelectionChanged += (_, _) => SourceChanged();
            drive.SelectionChanged += (_, _) => SourceChanged();
            create.Toggled += (_, _) => SourceChanged();
            vhdType.SelectionChanged += (_, _) => UpdateVisibility();
            automatic.Toggled += (_, _) => RefreshSlots();
            controller.SelectionChanged += (_, _) => RefreshSlots(false);
            number.SelectionChanged += (_, _) => RefreshSlots(false);
            location.SelectionChanged += (_, _) => UpdateSlotWarning();
            physicalDisk.SelectionChanged += (_, _) => UpdateSlotWarning();
            physicalOptical.SelectionChanged += (_, _) => UpdateSlotWarning();
            browse.Click += async (_, _) => await owner.RunAsync(BrowseMediaAsync);
            parentBrowse.Click += async (_, _) => await owner.RunAsync(async () =>
            {
                var value = await PickOpenAsync(".vhd", ".vhdx", ".avhd", ".avhdx");
                if (value is not null && !owner.IsStopped) parent.Text = value;
            });
            folderBrowse.Click += async (_, _) => await owner.RunAsync(async () =>
            {
                var value = await StoragePickers.FolderAsync();
                if (value is null || owner.IsStopped) return;
                isoSource.Text = value;
                volume.Text = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            });
            isoBrowse.Click += async (_, _) => await owner.RunAsync(async () =>
            {
                var value = await StoragePickers.SaveAsync("NewISO.iso", "ISO 映像", ".iso");
                if (value is not null && !owner.IsStopped) isoOutput.Text = value;
            });
            reloadPhysical.Click += async (_, _) => await owner.RunAsync(ReadHostSourcesAsync);
            cancel.Click += async (_, _) => await owner.RunAsync(owner.CloseAddAsync);
            confirm.Click += async (_, _) => await owner.RunAsync(ConfirmAsync);
            SourceChanged();
        }

        public async Task ReadHostSourcesAsync()
        {
            sourcesKnown = false;
            static async Task<ApiResponse<List<T>>> ReadAsync<T>(Func<Task<ApiResponse<List<T>>>> query)
            {
                try { return await Task.Run(query); }
                catch (Exception error) { return ApiResponse<List<T>>.Fail(error.Message); }
            }
            var disksTask = ReadAsync(HostDiskService.GetHostDisksAsync);
            var opticalTask = owner.generation == 1 ? ReadAsync(HostDiskService.GetHostOpticalDrivesAsync) : null;
            var disks = await disksTask;
            var opticals = opticalTask is null ? null : await opticalTask;
            if (owner.IsStopped) return;
            physicalDisk.Items.Clear();
            physicalOptical.Items.Clear();
            var errors = new List<string>();
            if (disks.HasData)
                foreach (var disk in disks.Data)
                    physicalDisk.Items.Add(new ComboBoxItem { Tag = disk, IsEnabled = disk.CanPassthrough,
                        Content = $"磁盘 {disk.Number} · {disk.FriendlyName} · {disk.SizeGiB:N1} GB · {disk.Status}" });
            else if (!disks.Success) errors.Add("物理磁盘：" + disks.Error);
            if (opticals?.HasData == true)
                foreach (var optical in opticals.Data)
                    physicalOptical.Items.Add(new ComboBoxItem { Tag = optical, Content = $"{optical.Model} ({optical.Drive})" });
            else if (opticals is { Success: false }) errors.Add("物理光驱：" + opticals.Error);
            sourcesKnown = true;
            sourceWarning.Message = string.Join(Environment.NewLine, errors);
            sourceWarning.IsOpen = errors.Count > 0;
            UpdateVisibility();
        }

        private void SourceChanged()
        {
            if (updating) return;
            updating = true;
            try
            {
                var choice = VmStorageService.GetSourceChoice(owner.generation, Physical, DriveType, create.IsOn);
                Select(drive, choice.DriveType);
                create.IsOn = choice.IsNew;
                ((ComboBoxItem)drive.Items[1]).IsEnabled = choice.SupportsOptical;
                path.Text = string.Empty;
                isoOutput.Text = string.Empty;
            }
            finally { updating = false; }
            UpdateVisibility();
            RefreshSlots();
        }

        private void UpdateVisibility()
        {
            if (updating) return;
            bool newDisk = !Physical && create.IsOn && DriveType == "HardDisk";
            bool newIso = !Physical && create.IsOn && DriveType == "DvdDrive";
            bool differencing = newDisk && Selected(vhdType) == "Differencing";
            newRow.Visibility = Physical ? Visibility.Collapsed : Visibility.Visible;
            pathRow.Visibility = !Physical && !newIso ? Visibility.Visible : Visibility.Collapsed;
            diskOptions.Visibility = newDisk ? Visibility.Visible : Visibility.Collapsed;
            parentRow.Visibility = differencing ? Visibility.Visible : Visibility.Collapsed;
            size.IsEnabled = sector.IsEnabled = block.IsEnabled = !differencing;
            isoOptions.Visibility = newIso ? Visibility.Visible : Visibility.Collapsed;
            physicalOptions.Visibility = Physical ? Visibility.Visible : Visibility.Collapsed;
            physicalDisk.Visibility = DriveType == "HardDisk" ? Visibility.Visible : Visibility.Collapsed;
            physicalOptical.Visibility = DriveType == "DvdDrive" ? Visibility.Visible : Visibility.Collapsed;
            physicalDisk.PlaceholderText = physicalDisk.Items.Count == 0 ? "没有可用的物理磁盘" : "选择物理磁盘";
            physicalOptical.PlaceholderText = physicalOptical.Items.Count == 0 ? "没有可用的物理光驱" : "选择物理光驱";
            browse.Content = create.IsOn ? "保存到" : "浏览";
            path.PlaceholderText = DriveType == "HardDisk" ? "虚拟磁盘文件路径" : "ISO 文件路径（留空时添加空光驱）";
            UpdateSlotWarning();
        }

        private void RefreshSlots(bool chooseBest = true)
        {
            if (updating) return;
            updating = true;
            try
            {
                string? oldType = controller.SelectedItem as string;
                int? oldNumber = number.SelectedItem as int?;
                int? oldLocation = location.SelectedItem as int?;
                var plan = automatic.IsOn && chooseBest
                    ? VmStorageService.GetBestSlot(owner.generation, owner.running, DriveType, owner.attachments) : null;
                var types = VmStorageService.GetControllerTypes(owner.generation, owner.running, DriveType);
                controller.ItemsSource = types;
                controller.SelectedItem = plan?.Slot?.ControllerType ?? (oldType is not null && types.Contains(oldType) ? oldType : types.FirstOrDefault());
                var numbers = VmStorageService.GetControllerNumbers(controller.SelectedItem as string ?? string.Empty);
                number.ItemsSource = numbers;
                number.SelectedItem = plan?.Slot?.ControllerNumber ?? (oldNumber is int n && numbers.Contains(n) ? n : numbers.FirstOrDefault());
                var locations = VmStorageService.GetAvailableLocations(controller.SelectedItem as string ?? string.Empty,
                    number.SelectedItem is int selectedNumber ? selectedNumber : -1, owner.attachments);
                location.ItemsSource = locations;
                location.SelectedItem = plan?.Slot?.Location ?? (oldLocation is int l && locations.Contains(l) ? l : locations.Count > 0 ? locations[0] : null);
                controller.IsEnabled = number.IsEnabled = location.IsEnabled = !automatic.IsOn;
            }
            finally { updating = false; }
            UpdateSlotWarning();
        }

        private void UpdateSlotWarning()
        {
            if (updating) return;
            string? error = null;
            if (controller.SelectedItem is not string type || number.SelectedItem is not int controllerNumber || location.SelectedItem is not int slot)
                error = "当前没有可用插槽。";
            else error = VmStorageService.GetAddSlotError(owner.generation, owner.running,
                new VmStorageAddRequest { ControllerType = type, ControllerNumber = controllerNumber, Location = slot, DriveType = DriveType }, owner.attachments);
            slotWarning.Message = error ?? string.Empty;
            slotWarning.IsOpen = error is not null;
            bool sourceSelected = !Physical || sourcesKnown && (DriveType == "HardDisk"
                ? (physicalDisk.SelectedItem as ComboBoxItem)?.Tag is HostDiskInfo { CanPassthrough: true }
                : (physicalOptical.SelectedItem as ComboBoxItem)?.Tag is HostOpticalDriveInfo);
            confirm.IsEnabled = error is null && sourceSelected && owner.CanChange();
        }

        private async Task BrowseMediaAsync()
        {
            string? value = create.IsOn && DriveType == "HardDisk"
                ? await StoragePickers.SaveAsync("NewDisk.vhdx", "虚拟硬盘", ".vhdx", ".vhd")
                : await PickOpenAsync(DriveType == "HardDisk" ? [".vhd", ".vhdx", ".avhd", ".avhdx"] : [".iso"]);
            if (value is not null && !owner.IsStopped) path.Text = value;
        }

        private async Task ConfirmAsync()
        {
            if (!owner.CanChange()) return;
            bool newDisk = !Physical && create.IsOn && DriveType == "HardDisk";
            bool newIso = !Physical && create.IsOn && DriveType == "DvdDrive";
            bool differencing = newDisk && Selected(vhdType) == "Differencing";
            if (newDisk && !differencing && (!int.TryParse(size.Text, out int parsedSize) || parsedSize <= 0))
                throw new InvalidOperationException("磁盘容量必须为正整数 GB。");
            if (controller.SelectedItem is not string type || number.SelectedItem is not int controllerNumber || location.SelectedItem is not int slot)
                throw new InvalidOperationException("请选择有效的控制器和插槽。");
            var request = new VmStorageAddRequest
            {
                ControllerType = type, ControllerNumber = controllerNumber, Location = slot, DriveType = DriveType,
                IsPhysical = Physical, IsNew = create.IsOn,
                PathOrNumber = Physical
                    ? DriveType == "DvdDrive" ? ((physicalOptical.SelectedItem as ComboBoxItem)?.Tag as HostOpticalDriveInfo)?.PnpDeviceId
                        : ((physicalDisk.SelectedItem as ComboBoxItem)?.Tag as HostDiskInfo)?.Number.ToString(CultureInfo.InvariantCulture)
                    : newIso ? isoOutput.Text : path.Text,
                VhdType = newDisk ? Selected(vhdType) : "Dynamic",
                ParentPath = differencing ? parent.Text : null,
                SizeGiB = newDisk && !differencing ? int.Parse(size.Text, CultureInfo.InvariantCulture) : null,
                SectorFormat = newDisk && !differencing ? Selected(sector) : "Default",
                BlockSizeBytes = newDisk && !differencing && Selected(block) != "Default" ? uint.Parse(Selected(block), CultureInfo.InvariantCulture) : null,
                IsoSourcePath = newIso ? isoSource.Text : null, IsoOutputPath = newIso ? isoOutput.Text : null,
                IsoVolumeLabel = newIso ? volume.Text : null,
                PhysicalDisk = Physical && DriveType == "HardDisk" ? (physicalDisk.SelectedItem as ComboBoxItem)?.Tag as HostDiskInfo : null
            };
            // 确认前重读上下文，但不改写用户已经选中的插槽或物理设备身份。
            await owner.LoadAsync();
            if (!owner.CanChange()) return;
            owner.CancellationToken.ThrowIfCancellationRequested();
            var prepared = await Task.Run(() => VmStorageService.PrepareAddRequest(request, owner.generation, owner.running, owner.attachments));
            if (!prepared.Success) throw new InvalidOperationException(prepared.Error);
            owner.CancellationToken.ThrowIfCancellationRequested();
            ApiResponse<VmStorageSlot> result;
            try { result = await Task.Run(() => VmStorageService.AddDriveAsync(owner.VmId, prepared.Request!)); }
            catch (Exception error) { result = ApiResponse<VmStorageSlot>.Fail(error.Message, kind: ApiErrorKind.OutcomeUnknown); }
            if (owner.IsStopped)
            {
                await owner.CompleteChangeAsync(result.Success, result.ErrorKind, result.Error, string.Empty);
                return;
            }
            if (result.Success)
            {
                owner.form = null;
                owner.editor.Content = null;
                owner.deviceHost.Visibility = Visibility.Visible;
                owner.add.Visibility = owner.refresh.Visibility = Visibility.Visible;
            }
            await owner.CompleteChangeAsync(result.Success, result.ErrorKind, result.Error,
                result.HasData ? $"已连接至 {result.Data.ControllerType} 控制器 {result.Data.ControllerNumber}，位置 {result.Data.Location}。" : "已添加存储设备。");
            if (owner.IsStopped) return;
            if (!result.Success) RefreshSlots();
        }

        private static ComboBox Choices(params (string Label, string Value)[] items)
        {
            var combo = new ComboBox { MinWidth = 150 };
            foreach (var (label, value) in items) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            combo.SelectedIndex = 0;
            return combo;
        }

        private static string Selected(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        private static void Select(ComboBox combo, string value) => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == value);

        private static StackPanel Field(string title, FrameworkElement input)
        {
            var field = new StackPanel { Spacing = 6 };
            field.Children.Add(new TextBlock { Text = title, FontSize = 13 });
            field.Children.Add(input);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, title);
            return field;
        }

        private static Border FieldCard(string title, FrameworkElement input)
        {
            var content = Field(title, input);
            content.Margin = new Thickness(24, 20, 24, 20);
            return Card(content);
        }

        private static Grid PathEditor(TextBox text, Button button)
        {
            var layout = new Grid { ColumnSpacing = 8 };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.Children.Add(text);
            Grid.SetColumn(button, 1);
            layout.Children.Add(button);
            return layout;
        }
    }
}
