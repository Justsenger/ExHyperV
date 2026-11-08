using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Text;
using Microsoft.Windows.Storage.Pickers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>检查点树、历史磁盘挂接及当前状态操作。</summary>
public sealed class VmSpacetimeSettingsView : UserControl
{
    private readonly Guid vmId;
    private readonly Grid interactive = new() { RowSpacing = 12 };
    private readonly ContentControl interactiveHost = new() { IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Canvas canvas = new() { Width = 3000, Height = 2000, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled, ZoomMode = ZoomMode.Disabled };
    private readonly StackPanel details = new() { Spacing = 8 };
    private readonly ToggleSwitch checkpoints = new() { Header = "检查点", OnContent = "启用", OffContent = "关闭", IsEnabled = false };
    private readonly InfoBar message = new() { IsClosable = true };
    private readonly Button retry = new() { Content = "重新读取", Visibility = Visibility.Collapsed };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, BitmapImage> thumbnails = [];
    private readonly Dictionary<string, Point> positions = [];
    private List<SpacetimeNode> nodes = [];
    private SpacetimeNode? selected;
    private SpacetimeMode captureMode;
    private Task operation = Task.CompletedTask;
    private Task? stopping;
    private readonly List<string> stopFailures = [];
    private ContentDialog? dialog;
    private bool started, stopped, busy, suppressToggle, checkpointsEnabled, dragging, stateAvailable;
    private Point? pointerPosition;

    public event EventHandler? Changed;

    public VmSpacetimeSettingsView(Guid vmId)
    {
        this.vmId = vmId;
        var root = new Grid { RowSpacing = 10 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.Children.Add(message);
        retry.Click += (_, _) => operation = RunAsync(() => LoadAsync(selected?.Id));
        message.ActionButton = retry;
        Grid.SetRow(progress, 1); root.Children.Add(progress);
        interactive.RowDefinitions.Add(new() { Height = GridLength.Auto });
        interactive.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var toolbar = new Grid();
        toolbar.Children.Add(checkpoints);
        checkpoints.HorizontalAlignment = HorizontalAlignment.Left;
        checkpoints.Toggled += (_, _) =>
        {
            if (suppressToggle || busy || stopped || !stateAvailable) return;
            var requested = checkpoints.IsOn;
            operation = RunAsync(() => MutateAsync(() => VmSpacetimeService.SetCheckpointsEnabledAsync(vmId, requested), selected?.Id));
        };
        var export = new Button { Content = "导出拓扑图", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        export.Click += (_, _) => operation = RunAsync(ExportAsync);
        toolbar.Children.Add(export);
        interactive.Children.Add(toolbar);
        var columns = new Grid { ColumnSpacing = 12 };
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(220) });
        scroll.Content = canvas;
        scroll.PointerPressed += PanPressed;
        scroll.PointerMoved += PanMoved;
        scroll.PointerReleased += PanReleased;
        scroll.PointerCanceled += PanReleased;
        columns.Children.Add(new Border { Child = scroll, Background = Brush("ControlFillColorDefaultBrush"), BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new(1), CornerRadius = new(8) });
        var panel = new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(panel, 1); columns.Children.Add(panel);
        Grid.SetRow(columns, 1); interactive.Children.Add(columns);
        interactiveHost.Content = interactive;
        Grid.SetRow(interactiveHost, 2); root.Children.Add(interactiveHost);
        Content = root;
        Loaded += (_, _) => { if (!started) { started = true; operation = RunAsync(() => LoadAsync()); } };
    }

    public Task StopAsync()
    {
        if (stopping is { IsCompleted: false }) return stopping;
        if (stopped) return Task.CompletedTask;
        return stopping = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        stopFailures.Clear();
        stopped = true;
        dialog?.Hide();
        await operation;
        if (stopFailures.Count == 0) return;

        string failure = string.Join(Environment.NewLine, stopFailures);
        stopFailures.Clear();
        stopped = false;
        IsEnabled = true;
        if (stateAvailable) { DrawTree(); RenderDetails(); }
        ShowError(failure + "\n页面已保留；查看后可以再次返回。");
        throw new InvalidOperationException(failure);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || stopped) return;
        busy = true;
        IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        message.IsOpen = false;
        try { await action(); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { busy = false; IsEnabled = !stopped; progress.Visibility = Visibility.Collapsed; }
    }

    private async Task LoadAsync(string? selectedId = null)
    {
        stateAvailable = false;
        interactiveHost.IsEnabled = checkpoints.IsEnabled = false;
        ToolTipService.SetToolTip(checkpoints, "尚未取得有效的检查点设置，请重新读取。");
        var enabled = await VmSpacetimeService.GetCheckpointsEnabledAsync(vmId);
        if (!enabled.Success || !enabled.HasData) throw new InvalidOperationException(enabled.Error);
        var response = await VmSpacetimeService.GetSpacetimeNodesAsync(vmId, image => image.EncodeJpeg());
        if (!response.Success || !response.HasData) throw new InvalidOperationException(response.Error);
        if (stopped) return;
        checkpointsEnabled = enabled.Data;
        suppressToggle = true;
        try { checkpoints.IsOn = checkpointsEnabled; }
        finally { suppressToggle = false; }
        nodes = response.Data!;
        // 与 WPF 一致：没有历史检查点时，仅显示当前节点。
        if (!nodes.Any(node => node.NodeType == SpacetimeNodeType.Snapshot))
        {
            var genesis = nodes.FirstOrDefault(node => node.NodeType == SpacetimeNodeType.Genesis);
            var current = nodes.FirstOrDefault(node => node.NodeType == SpacetimeNodeType.Current);
            if (genesis != null && current != null) { current.CreatedDate = genesis.CreatedDate; current.ParentId = null; nodes.Remove(genesis); }
        }
        thumbnails.Clear();
        foreach (var node in nodes)
        {
            if (stopped) return;
            if (node.Thumbnail is not { } image) continue;
            try
            {
                var bytes = await Task.Run(() => image.IsEncoded ? image.CopyBytes() : image.EncodeJpeg());
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                thumbnails[node.Id] = bitmap;
            }
            catch (Exception ex) { ShowError($"检查点已读取，但缩略图不可用：{ex.Message}"); }
        }
        if (stopped) return;
        selected = nodes.FirstOrDefault(node => node.Id == selectedId) ?? nodes.FirstOrDefault(node => node.NodeType == SpacetimeNodeType.Current) ?? nodes.FirstOrDefault();
        DrawTree();
        RenderDetails();
        stateAvailable = true;
        interactiveHost.IsEnabled = checkpoints.IsEnabled = true;
        ToolTipService.SetToolTip(checkpoints, null);
        DispatcherQueue.TryEnqueue(CenterSelected);
    }

    private async Task MutateAsync(Func<Task<ApiResponse>> mutation, string? selectedId = null, int settleMilliseconds = 0)
    {
        var result = await mutation();
        // 多步骤操作失败仍可能改变磁盘或 VM 状态，通知摘要重新读取。
        Changed?.Invoke(this, EventArgs.Empty);
        if (!result.Success)
        {
            // Core 会在原节点上保留未完成的虫洞清理信息，不能用新查询对象丢弃重试依据。
            if (!stopped) { DrawTree(); RenderDetails(); }
            ShowError(result.Error);
            suppressToggle = true;
            try { checkpoints.IsOn = checkpointsEnabled; }
            finally { suppressToggle = false; }
            return;
        }
        if (result.Success && settleMilliseconds > 0) await Task.Delay(settleMilliseconds);
        if (!stopped)
        {
            await LoadAsync(selectedId);
        }
    }

    private void RenderDetails()
    {
        details.Children.Clear();
        details.Children.Add(new TextBlock { Text = "时空详情", FontSize = 14, Margin = new(0, 2, 0, 4) });
        var content = new StackPanel { Spacing = 8 };
        details.Children.Add(new Border { Child = content, Padding = new(12), Background = Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new(1), CornerRadius = new(8) });
        if (selected is not { } node) { content.Children.Add(new TextBlock { Text = "未选择锚点", FontSize = 12, Opacity = .4 }); return; }
        var identity = new StackPanel { Spacing = 4 };
        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        nameRow.Children.Add(new TextBlock { Text = node.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        if (VmSpacetimeService.CanOperateHistoricalNode(node))
        {
            var rename = new Button { Content = new FontIcon { Glyph = "\uE70F", FontSize = 12 }, Padding = new(5), MinWidth = 0, MinHeight = 0 };
            ToolTipService.SetToolTip(rename, "重命名");
            AutomationProperties.SetName(rename, "重命名检查点");
            rename.Click += (_, _) => operation = RunAsync(() => RenameAsync(node));
            Grid.SetColumn(rename, 1); nameRow.Children.Add(rename);
        }
        identity.Children.Add(nameRow);
        identity.Children.Add(new TextBlock { Text = node.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 11, Opacity = .6 });
        content.Children.Add(new Border { Child = identity, Padding = new(12), Background = Brush("ControlFillColorTertiaryBrush"), CornerRadius = new(6) });
        content.Children.Add(new TextBlock { Text = "操作", FontSize = 12, Opacity = .5, Margin = new(0, 8, 0, 0) });
        if (VmSpacetimeService.HasOpenWormhole(nodes))
            content.Children.Add(new TextBlock { Text = "虫洞开启中，需先关闭虫洞才能创建 / 删除 / 收束时空。", Foreground = new SolidColorBrush(Colors.Gold), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (node.NodeType == SpacetimeNodeType.Current)
        {
            content.Children.Add(Mode("连续时空", "保存磁盘和内存状态", SpacetimeMode.Continuous));
            content.Children.Add(Mode("静止时空", "保存磁盘状态", SpacetimeMode.Still));
            content.Children.Add(Action("创建时空", "\uE710", VmSpacetimeService.CanCreateMoment(checkpointsEnabled, nodes), () => MutateAsync(() => VmSpacetimeService.CaptureMomentAsync(vmId, captureMode, encodeThumbnail: image => image.EncodeJpeg()), settleMilliseconds: 1000), accent: true));
        }
        else if (VmSpacetimeService.CanOperateHistoricalNode(node))
        {
            if (node.IsWormhole)
            {
                content.Children.Add(new TextBlock { Text = "虫洞干涉中", Foreground = new SolidColorBrush(Colors.Gold), FontSize = 12 });
                content.Children.Add(Action("关闭虫洞", "\uE71B", true, () => MutateAsync(() => VmSpacetimeService.CloseWormholeAsync(vmId, node))));
            }
            else
            {
                content.Children.Add(Action("穿梭", "\uE8AB", true, () => MutateAsync(() => VmSpacetimeService.TeleportAsync(node, vmId, nodes), settleMilliseconds: 500)));
                content.Children.Add(Action("开启虫洞", "\uE71B", true, () => MutateAsync(() => VmSpacetimeService.OpenWormholeAsync(vmId, node), node.Id)));
                var canMutate = VmSpacetimeService.CanMutateHistoricalNode(node, nodes);
                content.Children.Add(Action("时空收束", "\uE97A", canMutate, () => MutateAsync(() => VmSpacetimeService.ConvergeAsync(vmId, node), settleMilliseconds: 800)));
                content.Children.Add(Action("删除", "\uE74D", canMutate, () => MutateAsync(() => VmSpacetimeService.AnnihilateAsync(vmId, node), settleMilliseconds: 800), danger: true));
            }
        }
    }

    private RadioButton Mode(string title, string description, SpacetimeMode mode)
    {
        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(new TextBlock { Text = title, FontSize = 12 });
        label.Children.Add(new TextBlock { Text = description, FontSize = 11, Opacity = .5 });
        var radio = new RadioButton { Content = label, IsChecked = captureMode == mode, GroupName = "SpacetimeCaptureMode" };
        radio.Checked += (_, _) => captureMode = mode;
        return radio;
    }

    private Button Action(string title, string glyph, bool enabled, Func<Task> action, bool accent = false, bool danger = false)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        label.Children.Add(new FontIcon { FontFamily = new("Segoe Fluent Icons"), Glyph = glyph, FontSize = 16 });
        label.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = label, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(10, 8, 10, 8) };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (danger) { button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)); button.Foreground = new SolidColorBrush(Colors.White); }
        button.Click += (_, _) => operation = RunAsync(action);
        return button;
    }

    private async Task RenameAsync(SpacetimeNode node)
    {
        var input = new TextBox { Text = node.Name, MinWidth = 280, Header = "名称" };
        dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "重命名检查点", Content = input, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { dialog = null; }
        if (stopped || result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text) || input.Text == node.Name) return;
        await MutateAsync(() => VmSpacetimeService.RenameSnapshotAsync(vmId, node, input.Text.Trim()), node.Id);
    }

    private void DrawTree()
    {
        canvas.Children.Clear();
        positions.Clear();
        var children = nodes.Where(n => n.ParentId != null).GroupBy(n => n.ParentId!).ToDictionary(g => g.Key, g => g.OrderBy(n => n.CreatedDate).ToList());
        var root = nodes.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId)) ?? nodes.FirstOrDefault();
        if (root == null) return;
        var leaves = new Dictionary<string, int>();
        var visiting = new HashSet<string>();
        int Count(SpacetimeNode node)
        {
            if (!visiting.Add(node.Id)) throw new InvalidOperationException("检查点关系存在循环，无法绘制。");
            var count = children.TryGetValue(node.Id, out var list) && list.Count > 0 ? list.Sum(Count) : 1;
            visiting.Remove(node.Id);
            return leaves[node.Id] = count;
        }
        var total = Count(root);
        int Depth(SpacetimeNode node) => children.TryGetValue(node.Id, out var list) && list.Count > 0 ? 1 + list.Max(Depth) : 0;
        canvas.Width = Math.Max(1200, Depth(root) * 280 + 400);
        canvas.Height = Math.Max(700, total * 200 + 240);
        AddGrid();
        var top = (canvas.Height - total * 200) / 2;
        Draw(root, 150, top, top + total * 200);
        if (positions.TryGetValue(SpacetimeNode.CurrentId, out var current))
            foreach (var node in nodes.Where(n => n.IsWormhole))
                if (positions.TryGetValue(node.Id, out var point)) AddLine(point, current, true);

        void Draw(SpacetimeNode node, double x, double start, double end)
        {
            var point = new Point(x, (start + end) / 2);
            positions[node.Id] = point;
            AddNode(node, point);
            if (!children.TryGetValue(node.Id, out var list)) return;
            var offset = start;
            foreach (var child in list)
            {
                var sector = leaves[child.Id] * (end - start) / leaves[node.Id];
                AddLine(point, new(x + 280, offset + sector / 2));
                Draw(child, x + 280, offset, offset + sector);
                offset += sector;
            }
        }
    }

    private void AddNode(SpacetimeNode node, Point point)
    {
        bool isSelected = selected?.Id == node.Id, current = node.NodeType == SpacetimeNodeType.Current;
        var preview = new Border { Width = 140, Height = 80, Background = new SolidColorBrush(Colors.Black), BorderBrush = node.IsWormhole ? new SolidColorBrush(Colors.Gold) : isSelected || current ? Brush("VmAccentBrush") : Brush("TextFillColorTertiaryBrush"), BorderThickness = new(isSelected || node.IsWormhole ? 3 : 1), CornerRadius = new(4) };
        if (thumbnails.TryGetValue(node.Id, out var bitmap)) preview.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        var content = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(preview);
        content.Children.Add(new TextBlock { Text = node.Name, FontSize = 12, FontWeight = current ? FontWeights.Bold : FontWeights.Normal, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180, Opacity = current || isSelected ? 1 : .6 });
        var button = new Button { Content = content, Width = 200, Height = 160, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0), Padding = new(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, node.Name);
        button.Click += (_, _) => { if (dragging || busy || stopped) return; selected = node; DrawTree(); RenderDetails(); };
        Canvas.SetLeft(button, point.X - 100); Canvas.SetTop(button, point.Y - 80); Canvas.SetZIndex(button, isSelected ? 100 : current ? 80 : 50);
        canvas.Children.Add(button);
    }

    private void AddLine(Point from, Point to, bool wormhole = false)
    {
        var line = new Line { X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y, Stroke = wormhole ? new SolidColorBrush(Colors.Gold) : Brush("TextFillColorPrimaryBrush"), StrokeThickness = wormhole ? 4 : 1, Opacity = wormhole ? .95 : .4, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
        Canvas.SetZIndex(line, wormhole ? 10 : 5); canvas.Children.Add(line);
    }

    private void AddGrid()
    {
        var geometry = new PathGeometry();
        for (double x = 0; x < canvas.Width; x += 69.28)
            for (double y = 0; y < canvas.Height; y += 120)
            {
                Segment(new(x, y + 20), new(x + 34.64, y), new(x + 69.28, y + 20));
                Segment(new(x, y + 60), new(x + 34.64, y + 80), new(x + 69.28, y + 60));
                Segment(new(x, y + 20), new(x, y + 60));
                Segment(new(x + 69.28, y + 20), new(x + 69.28, y + 60));
                Segment(new(x + 34.64, y + 80), new(x + 34.64, y + 120));
            }
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)), StrokeThickness = 1, IsHitTestVisible = false });
        void Segment(params Point[] points)
        {
            var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
            foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = point });
            geometry.Figures.Add(figure);
        }
    }

    private void CenterSelected()
    {
        if (!stopped && selected != null && positions.TryGetValue(selected.Id, out var point))
            scroll.ChangeView(Math.Max(0, point.X - scroll.ActualWidth / 2), Math.Max(0, point.Y - scroll.ActualHeight / 2), null, true);
    }

    private void PanPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
        pointerPosition = args.GetCurrentPoint(scroll).Position;
        dragging = false;
    }

    private void PanMoved(object sender, PointerRoutedEventArgs args)
    {
        if (pointerPosition is not { } previous || !args.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
        var current = args.GetCurrentPoint(scroll).Position;
        var dx = previous.X - current.X; var dy = previous.Y - current.Y;
        if (!dragging && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        dragging = true;
        scroll.CapturePointer(args.Pointer);
        scroll.ChangeView(Math.Max(0, scroll.HorizontalOffset + dx), Math.Max(0, scroll.VerticalOffset + dy), null, true);
        pointerPosition = current;
        args.Handled = true;
    }

    private void PanReleased(object sender, PointerRoutedEventArgs args)
    {
        pointerPosition = null;
        scroll.ReleasePointerCapture(args.Pointer);
        if (dragging) { args.Handled = true; dragging = false; }
    }

    private async Task ExportAsync()
    {
        if (nodes.Count == 0 || XamlRoot == null) return;
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = $"时空拓扑_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}" };
        picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
        var file = await picker.PickSaveFileAsync();
        if (file == null || stopped) return;
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(canvas);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var png = new byte[checked((int)stream.Size)];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)png.Length);
        reader.ReadBytes(png);
        await System.IO.File.WriteAllBytesAsync(file.Path, png);
    }

    private void ShowError(string error)
    {
        string detail = string.IsNullOrWhiteSpace(error) ? "无法读取或修改检查点。" : error;
        if (stopped)
        {
            if (!stopFailures.Contains(detail)) stopFailures.Add(detail);
            return;
        }
        // A failed mutation may retain wormhole cleanup state on the original nodes.
        // Offer a reload for failed reads only; do not replace those recovery objects.
        retry.Visibility = stateAvailable ? Visibility.Collapsed : Visibility.Visible;
        message.Severity = InfoBarSeverity.Error;
        message.Message = detail;
        message.IsOpen = true;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
