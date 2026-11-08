using System.Collections.Concurrent;
using ExHyperV.Models;
using ExHyperV.WinUI3.UI.Shared;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.Foundation;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed class VmCpuAffinitySettingsView : VmSettingsView
{
    // 原生任务结果未确认时，离开并重新进入页面不能解除重试限制。
    private static readonly ConcurrentDictionary<Guid, byte> unknownOperations = new();
    private readonly Grid processors = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly List<(int Index, CpuAffinityTile Choice)> choices = [];
    private readonly SettingInfoButton source = new("核心绑定", "按住左键拖动可多选。全部取消选择表示解除绑定。");
    private readonly TextBlock model = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button save = SettingsActionButton.Create("应用核心绑定", "\uE73E", primary: true, iconSize: 24);
    private readonly Button revert = SettingsActionButton.Create("撤销核心绑定修改", "\uE7A7");
    private HashSet<int> initialSelection = [];
    private bool initialReselectionRequired;
    private bool validRead;
    private bool reselectionRequired;
    private uint? dragPointer;
    private CpuAffinityTile? lastToggled;
    private int arrangedColumns;

    public static bool HasUnknownOperation(Guid vmId) => unknownOperations.ContainsKey(vmId);
    public static void MarkUnknownOperation(Guid vmId) => unknownOperations.TryAdd(vmId, 0);

    public VmCpuAffinitySettingsView(Guid vmId) : base(vmId)
    {
        save.IsEnabled = revert.IsEnabled = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        actions.Children.Add(revert);
        actions.Children.Add(save);
        var content = new StackPanel { Margin = new Thickness(17), Spacing = 16 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(model);
        heading.Children.Add(source);
        content.Children.Add(heading);
        model.Foreground = Brush("TextFillColorSecondaryBrush");
        content.Children.Add(processors);
        content.Children.Add(actions);
        SetBody(content);
        processors.SizeChanged += (_, _) => ArrangeProcessors();
        processors.PointerMoved += MoveSelection;
        processors.PointerReleased += EndSelection;
        processors.PointerCanceled += EndSelection;
        processors.PointerCaptureLost += (_, _) => { dragPointer = null; lastToggled = null; };
        save.Click += async (_, _) => await RunAsync(SaveAsync);
        revert.Click += (_, _) =>
        {
            foreach (var (index, choice) in choices) choice.Selected = initialSelection.Contains(index);
            reselectionRequired = initialReselectionRequired;
            UpdateActions();
        };
    }

    private void Toggle(CpuAffinityTile tile)
    {
        if (!validRead || IsBusy || IsStopped) return;
        tile.Selected = !tile.Selected;
        reselectionRequired = false;
        UpdateActions();
    }

    private void BeginSelection(CpuAffinityTile tile, PointerRoutedEventArgs args)
    {
        if (!validRead || IsBusy || IsStopped || HasUnknownOperation(VmId)) return;
        if (!processors.CapturePointer(args.Pointer)) return;
        dragPointer = args.Pointer.PointerId;
        lastToggled = tile;
        tile.Focus(FocusState.Pointer);
        Toggle(tile);
    }

    private void MoveSelection(object sender, PointerRoutedEventArgs args)
    {
        if (dragPointer != args.Pointer.PointerId) return;
        var position = args.GetCurrentPoint(processors).Position;
        foreach (var (_, tile) in choices)
        {
            Point origin = tile.TransformToVisual(processors).TransformPoint(new Point());
            if (!new Rect(origin.X, origin.Y, tile.ActualWidth, tile.ActualHeight).Contains(position)) continue;
            if (lastToggled != tile) { lastToggled = tile; Toggle(tile); }
            break;
        }
        args.Handled = true;
    }

    private void EndSelection(object sender, PointerRoutedEventArgs args)
    {
        if (dragPointer != args.Pointer.PointerId) return;
        dragPointer = null;
        lastToggled = null;
        processors.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void UpdateActions()
    {
        bool editable = validRead && !HasUnknownOperation(VmId) && !IsStopped;
        bool pending = !initialSelection.SetEquals(choices.Where(p => p.Choice.Selected).Select(p => p.Index))
            || initialReselectionRequired && !reselectionRequired;
        save.IsEnabled = editable && !reselectionRequired && pending;
        revert.IsEnabled = editable && pending;
        foreach (var (_, choice) in choices) choice.IsEnabled = editable;
    }

    protected override async Task LoadAsync()
    {
        validRead = false;
        UpdateActions();
        var processorsTask = HostProcessorService.GetAffinityProcessorsAsync();
        var stateTask = CpuAffinityService.ReadStateAsync(VmId);
        var modelTask = HostSystemInfoService.GetSystemInfoAsync();
        await Task.WhenAll(processorsTask, stateTask, modelTask);
        var available = (await processorsTask).RequireData();
        var state = (await stateTask).RequireData();
        if (IsStopped) return;
        if (available.Count == 0) throw new InvalidOperationException("宿主没有提供可用于亲和性设置的处理器。");
        reselectionRequired = state.AppliedProcessors == null && state.SavedPreferenceStatus is
            CpuAffinityService.PreferenceStatus.EnvironmentChanged or CpuAffinityService.PreferenceStatus.EnvironmentUnverified or CpuAffinityService.PreferenceStatus.InvalidConfiguration;
        var selected = state.AppliedProcessors ?? (reselectionRequired ? [] : state.SavedProcessors ?? []);
        initialSelection = selected.ToHashSet();
        initialReselectionRequired = reselectionRequired;
        string description = state.AppliedProcessors != null ? "选择处理器 · 当前实际亲和性。"
            : "选择处理器 · 下次启动时应用的偏好。尚不表示已应用到运行中的虚拟机。";
        description += "\n按住左键拖动可多选；每进入一个新格子切换选择。全部不选表示解除绑定。";
        if (!string.IsNullOrWhiteSpace(state.SavedPreferenceIssue)) description += "\n" + state.SavedPreferenceIssue;
        source.SetDescription(description);
        model.Text = (await modelTask).CpuModel.Split(" @", 2, StringSplitOptions.None)[0];
        ToolTipService.SetToolTip(model, model.Text);
        Updating = true;
        try
        {
            choices.Clear();
            processors.Children.Clear();
            arrangedColumns = 0;
            foreach (var item in available)
            {
                var choice = new CpuAffinityTile(item.Index, item.CoreType) { Selected = selected.Contains(item.Index) };
                var details = new List<string> { $"逻辑处理器 {item.Index}" };
                if (item.PackageId is { } package) details.Add($"插槽 {package}");
                if (item.CoreId is { } core) details.Add($"物理核心 {core}");
                if (item.NumaNode is { } numa) details.Add($"NUMA 节点 {numa}");
                details.Add(item.CoreType.ToString());
                ToolTipService.SetToolTip(choice, string.Join(" · ", details));
                choice.MouseSelectionStarted += BeginSelection;
                choice.Click += (_, _) => Toggle(choice);
                choices.Add((item.Index, choice));
                processors.Children.Add(choice);
            }
            ArrangeProcessors();
            validRead = true;
            UpdateActions();
        }
        finally { Updating = false; }
        if (reselectionRequired) ShowInformation("已保存的选择与当前环境无法匹配。请重新选择；如需解除绑定，选择后再取消全部核心，并点击应用。");
        if (HasUnknownOperation(VmId)) ShowError("此前的亲和性修改结果尚未确认；本次应用会话中已停止对此虚拟机继续提交。读取当前值不会解除此限制。");
    }

    private void ArrangeProcessors()
    {
        if (choices.Count == 0) return;
        int columns = Math.Min(choices.Count, 4);
        if (choices.Count > 4)
        {
            double best = double.PositiveInfinity;
            for (int candidate = 4; candidate <= 10; candidate++)
            {
                int rows = (choices.Count + candidate - 1) / candidate;
                int remainder = (candidate - choices.Count % candidate) % candidate;
                double score = 2d * remainder / candidate + Math.Abs((double)candidate / rows - 1.5);
                if (score < best) { best = score; columns = candidate; }
            }
        }
        if (processors.ActualWidth > 0)
            columns = Math.Min(columns, Math.Max(1, (int)((processors.ActualWidth + 8) / 64)));
        if (columns == arrangedColumns) return;
        arrangedColumns = columns;
        processors.ColumnDefinitions.Clear();
        processors.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) processors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < (choices.Count + columns - 1) / columns; i++) processors.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < choices.Count; i++) { Grid.SetColumn(choices[i].Choice, i % columns); Grid.SetRow(choices[i].Choice, i / columns); }
    }

    private async Task SaveAsync()
    {
        if (!validRead || reselectionRequired || HasUnknownOperation(VmId)) return;
        var selected = choices.Where(p => p.Choice.Selected).Select(p => p.Index).ToList();
        CpuAffinityService.SaveResult? result = null;
        Exception? failure = null;
        try
        {
            result = await CpuAffinityService.SaveAsync(VmId, selected);
            if (result.ErrorKind == ApiErrorKind.OutcomeUnknown || result.CleanupErrorKind == ApiErrorKind.OutcomeUnknown) MarkUnknownOperation(VmId);
            if (result.Status != CpuAffinityService.SaveStatus.Failed || result.MetadataSaved) NotifyChanged();
        }
        catch (Exception ex)
        {
            failure = ex;
            if (ex is CoreOperationException operation && operation.Result.ErrorKind == ApiErrorKind.OutcomeUnknown) MarkUnknownOperation(VmId);
        }
        try { await LoadAsync(); }
        catch (Exception ex) { failure ??= ex; }
        if (result != null)
        {
            var details = new List<string>
            {
                result.Status switch
                {
                    CpuAffinityService.SaveStatus.Applied => "亲和性已应用。",
                    CpuAffinityService.SaveStatus.Queued => "亲和性偏好已保存，等待虚拟机启动后应用。",
                    _ => "亲和性未能完成。"
                },
                result.MetadataSaved ? "启动偏好已保存。" : "启动偏好未保存。"
            };
            if (!string.IsNullOrWhiteSpace(result.Error)) details.Add(result.Error);
            if (!string.IsNullOrWhiteSpace(result.CleanupError)) details.Add("恢复或清理：" + result.CleanupError);
            if (!string.IsNullOrWhiteSpace(result.SavedPreferenceIssue)) details.Add(result.SavedPreferenceIssue);
            if (result.CurrentGroupId is { } current) details.Add($"当前 CPU 组：{current:D}");
            if (result.RetainedGroupId is { } retained) details.Add($"保留的 CPU 组：{retained:D}");
            if (HasUnknownOperation(VmId)) details.Add("结果尚未确认；本次应用会话中已停止继续提交。");
            if (failure != null) details.Add("回读失败：" + failure.Message);
            if (result.Status == CpuAffinityService.SaveStatus.Failed || !result.MetadataSaved || HasUnknownOperation(VmId) || failure != null)
                ShowError(string.Join(Environment.NewLine, details));
            else ShowInformation(string.Join(Environment.NewLine, details));
        }
        else if (failure != null) throw failure;
    }
}

internal sealed class CpuAffinityTile : Button
{
    private bool selected;
    private readonly int index;
    public event Action<CpuAffinityTile, PointerRoutedEventArgs>? MouseSelectionStarted;

    public bool Selected
    {
        get => selected;
        set { selected = value; UpdateSelection(); }
    }

    public CpuAffinityTile(int index, CpuCoreType type)
    {
        this.index = index;
        MinWidth = 48;
        MinHeight = 62;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(8);
        CornerRadius = new CornerRadius(4);
        var face = new Grid();
        face.Children.Add(new TextBlock { Text = index.ToString(), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (type != CpuCoreType.Unknown)
            face.Children.Add(new FontIcon
            {
                Glyph = type == CpuCoreType.Efficient ? "\uE8BE" : "\uE945", FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Foreground = (Brush)Application.Current.Resources[type == CpuCoreType.Efficient ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"]
            });
        Content = face;
        ActualThemeChanged += (_, _) => UpdateSelection();
        AutomationProperties.SetName(this, $"逻辑处理器 {index}");
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        Background = (Brush)Application.Current.Resources[selected ? "VmChartFillBrush" : "ControlFillColorDefaultBrush"];
        BorderBrush = (Brush)Application.Current.Resources[selected ? "VmAccentBrush" : "ControlStrokeColorDefaultBrush"];
        AutomationProperties.SetItemStatus(this, selected ? "已选择" : "未选择");
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse && args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            args.Handled = true;
            MouseSelectionStarted?.Invoke(this, args);
            return;
        }
        base.OnPointerPressed(args);
    }
}
