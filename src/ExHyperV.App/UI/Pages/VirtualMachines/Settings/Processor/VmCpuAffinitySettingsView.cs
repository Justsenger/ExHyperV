using System.Collections.Concurrent;
using System.Globalization;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;

public sealed partial class VmCpuAffinitySettingsView : VmSettingsView
{
    // 原生任务结果未确认时，离开并重新进入页面不能解除重试限制，需显式点击刷新解锁。
    private static readonly ConcurrentDictionary<Guid, byte> unknownOperations = new();
    private readonly Grid processors = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly List<(int Index, CpuCoreType CoreType, CpuAffinityTile Choice)> choices = [];
    private readonly SettingInfoButton source = new(L10n.Get("Cpu_Group_Affinity"), L10n.Get("Affinity_Desc"));
    private readonly TextBlock statusText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel quickActions = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button selectAllBtn = QuickButton(L10n.Get("Affinity_SelectAll"));
    private readonly Button clearBtn = QuickButton(L10n.Get("Affinity_Clear"));
    private readonly QuickToggleButton perfOnlyBtn = new(L10n.Get("Affinity_PerfOnly"));
    private readonly QuickToggleButton effOnlyBtn = new(L10n.Get("Affinity_EffOnly"));
    private readonly Button save = SettingsActionButton.CreateRect(L10n.Get("Common_Apply"), primary: true);
    private readonly Button revert = SettingsActionButton.CreateRect(L10n.Get("Common_Revert"));
    private HashSet<int> initialSelection = [];
    private bool initialReselectionRequired;
    private bool validRead;
    private bool reselectionRequired;
    private uint? dragPointer;
    private CpuAffinityTile? lastToggled;
    private int arrangedColumns;

    public static bool HasUnknownOperation(Guid vmId) => unknownOperations.ContainsKey(vmId);
    public static void MarkUnknownOperation(Guid vmId) => unknownOperations.TryAdd(vmId, 0);
    public static void ClearUnknownOperation(Guid vmId) => unknownOperations.TryRemove(vmId, out _);

    private Task UnlockAndReloadAsync()
    {
        ClearUnknownOperation(VmId);
        return RunAsync(LoadAsync);
    }

    public VmCpuAffinitySettingsView(Guid vmId) : base(vmId)
    {
        save.IsEnabled = revert.IsEnabled = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(revert);
        actions.Children.Add(save);

        var content = new Grid { Margin = new Thickness(17), RowSpacing = 14 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statusGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        statusText.Foreground = Brush("TextFillColorSecondaryBrush");
        statusGroup.Children.Add(statusText);
        statusGroup.Children.Add(source);

        quickActions.Children.Add(selectAllBtn);
        quickActions.Children.Add(clearBtn);
        quickActions.Children.Add(perfOnlyBtn);
        quickActions.Children.Add(effOnlyBtn);
        perfOnlyBtn.Visibility = effOnlyBtn.Visibility = Visibility.Collapsed;

        heading.Children.Add(statusGroup);
        Grid.SetColumn(quickActions, 1);
        heading.Children.Add(quickActions);

        Grid.SetRow(heading, 0);
        Grid.SetRow(processors, 1);
        Grid.SetRow(actions, 2);
        content.Children.Add(heading);
        content.Children.Add(processors);
        content.Children.Add(actions);
        SetBody(content);

        processors.SizeChanged += (_, _) => ArrangeProcessors();
        processors.PointerMoved += MoveSelection;
        processors.PointerReleased += EndSelection;
        processors.PointerCanceled += EndSelection;
        processors.PointerCaptureLost += (_, _) => { dragPointer = null; lastToggled = null; };
        selectAllBtn.Click += (_, _) => ApplyQuickSelection(_ => true);
        clearBtn.Click += (_, _) => ApplyQuickSelection(_ => false);
        perfOnlyBtn.Click += (_, _) => ApplyQuickSelection(t => t == CpuCoreType.Performance);
        effOnlyBtn.Click += (_, _) => ApplyQuickSelection(t => t == CpuCoreType.Efficient);
        save.Click += async (_, _) => await RunAsync(SaveAsync);
        revert.Click += (_, _) =>
        {
            foreach (var (index, _, choice) in choices) choice.Selected = initialSelection.Contains(index);
            reselectionRequired = initialReselectionRequired;
            UpdateActions();
        };
    }

    private static Button QuickButton(string label) => new()
    {
        Content = label,
        Height = 26,
        Padding = new Thickness(10, 0, 10, 0),
        CornerRadius = new CornerRadius(4),
        FontSize = 12,
        FontWeight = FontWeights.Normal
    };

    private void ApplyQuickSelection(Func<CpuCoreType, bool> predicate)
    {
        if (!validRead || IsBusy || IsStopped || HasUnknownOperation(VmId)) return;
        foreach (var (_, coreType, choice) in choices) choice.Selected = predicate(coreType);
        reselectionRequired = false;
        UpdateActions();
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
        foreach (var (_, _, tile) in choices)
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
        int selectedCount = choices.Count(p => p.Choice.Selected);
        statusText.Text = choices.Count == 0
            ? string.Empty
            : selectedCount == 0 ? L10n.Get("Affinity_FreeSchedule") : L10n.Format("Affinity_BoundCount", selectedCount, choices.Count);
        bool pending = !initialSelection.SetEquals(choices.Where(p => p.Choice.Selected).Select(p => p.Index))
            || initialReselectionRequired && !reselectionRequired;
        save.IsEnabled = editable && !reselectionRequired && pending;
        revert.IsEnabled = editable && pending;
        selectAllBtn.IsEnabled = clearBtn.IsEnabled = perfOnlyBtn.IsEnabled = effOnlyBtn.IsEnabled = editable;
        perfOnlyBtn.IsChecked = editable && choices.Count > 0
            && choices.Any(p => p.CoreType == CpuCoreType.Performance)
            && choices.All(p => p.Choice.Selected == (p.CoreType == CpuCoreType.Performance));
        effOnlyBtn.IsChecked = editable && choices.Count > 0
            && choices.Any(p => p.CoreType == CpuCoreType.Efficient)
            && choices.All(p => p.Choice.Selected == (p.CoreType == CpuCoreType.Efficient));
        foreach (var (_, _, choice) in choices) choice.IsEnabled = editable;
    }

    protected override async Task LoadAsync()
    {
        validRead = false;
        UpdateActions();
        var processorsTask = Task.Run(() => HostProcessorService.GetAffinityProcessorsAsync());
        var stateTask = Task.Run(() => CpuAffinityService.ReadStateAsync(VmId));
        await Task.WhenAll(processorsTask, stateTask).WaitAsync(CancellationToken);
        var available = (await processorsTask).RequireData();
        var state = (await stateTask).RequireData();
        if (IsStopped) return;
        if (available.Count == 0) throw new InvalidOperationException(L10n.Get("Affinity_NoHostProcessors"));
        reselectionRequired = state.AppliedProcessors == null && state.SavedPreferenceStatus is
            CpuAffinityService.PreferenceStatus.EnvironmentChanged or CpuAffinityService.PreferenceStatus.EnvironmentUnverified or CpuAffinityService.PreferenceStatus.InvalidConfiguration;
        var selected = state.AppliedProcessors ?? (reselectionRequired ? [] : state.SavedProcessors ?? []);
        initialSelection = selected.ToHashSet();
        initialReselectionRequired = reselectionRequired;
        string affinityDesc = L10n.Get("Affinity_Desc");
        string description = string.IsNullOrWhiteSpace(state.SavedPreferenceIssue)
            ? affinityDesc
            : $"{affinityDesc}\n\n{state.SavedPreferenceIssue}";
        source.SetDescription(description);
        bool hasHybrid = available.Any(x => x.CoreType == CpuCoreType.Performance) && available.Any(x => x.CoreType == CpuCoreType.Efficient);
        perfOnlyBtn.Visibility = effOnlyBtn.Visibility = hasHybrid ? Visibility.Visible : Visibility.Collapsed;
        Updating = true;
        try
        {
            choices.Clear();
            processors.Children.Clear();
            arrangedColumns = 0;
            string sep = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "，" : ", ";
            foreach (var item in available)
            {
                var choice = new CpuAffinityTile(item.Index, item.CoreType) { Selected = selected.Contains(item.Index) };
                var details = new List<string> { L10n.Format("Affinity_Tooltip_Logical", item.Index) };
                if (item.PackageId is { } package) details.Add(L10n.Format("Affinity_Tooltip_Socket", package));
                if (item.CoreId is { } core) details.Add(L10n.Format("Affinity_Tooltip_Core", core));
                if (item.NumaNode is { } numa) details.Add(L10n.Format("Affinity_Tooltip_Numa", numa));
                if (item.CoreType == CpuCoreType.Performance) details.Add(L10n.Get("Host_Cpu_PerfCore"));
                if (item.CoreType == CpuCoreType.Efficient) details.Add(L10n.Get("Host_Cpu_EffCore"));
                ToolTipService.SetToolTip(choice, string.Join(sep, details));
                choice.MouseSelectionStarted += BeginSelection;
                choice.Click += (_, _) => Toggle(choice);
                choices.Add((item.Index, item.CoreType, choice));
                processors.Children.Add(choice);
            }
            ArrangeProcessors();
            validRead = true;
            UpdateActions();
        }
        finally { Updating = false; }
        if (reselectionRequired) ShowInformation(L10n.Get("Affinity_ReselectRequired"));
        if (HasUnknownOperation(VmId)) ShowError(L10n.Get("Err_UncertainLock"), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
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
        int rowCount = (choices.Count + columns - 1) / columns;
        for (int i = 0; i < columns; i++) processors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rowCount; i++) processors.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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
            result = await Task.Run(() => CpuAffinityService.SaveAsync(VmId, selected));
            if (result.ErrorKind == ApiErrorKind.OutcomeUnknown || result.CleanupErrorKind == ApiErrorKind.OutcomeUnknown) MarkUnknownOperation(VmId);
            if (result.Status != CpuAffinityService.SaveStatus.Failed || result.MetadataSaved) NotifyChanged();
        }
        catch (Exception ex)
        {
            failure = ex;
            if (ex is CoreOperationException operation && operation.Result.ErrorKind == ApiErrorKind.OutcomeUnknown) MarkUnknownOperation(VmId);
        }
        if (result is { MetadataSaved: true, Error: null or "", CleanupError: null or "", SavedPreferenceIssue: null or "" }
            && result.Status is CpuAffinityService.SaveStatus.Applied or CpuAffinityService.SaveStatus.Queued
            && selected.Count > 0 && !HasUnknownOperation(VmId))
        {
            initialSelection = selected.ToHashSet();
            initialReselectionRequired = false;
            source.SetDescription(L10n.Get("Affinity_Desc"));
            UpdateActions();
        }
        else if (!IsStopped)
        {
            try { await LoadAsync(); }
            catch (OperationCanceledException) when (IsStopped) { }
            catch (Exception ex) { failure ??= ex; }
        }
        if (result != null)
        {
            var details = new List<string>
            {
                result.Status switch
                {
                    CpuAffinityService.SaveStatus.Applied => L10n.Get("Affinity_SavedApplied"),
                    CpuAffinityService.SaveStatus.Queued => L10n.Get("Affinity_SavedQueued"),
                    _ => L10n.Get("Affinity_SaveIncomplete")
                },
                L10n.Get(result.MetadataSaved ? "Affinity_PrefSaved" : "Affinity_PrefNotSaved")
            };
            if (!string.IsNullOrWhiteSpace(result.Error)) details.Add(result.Error);
            if (!string.IsNullOrWhiteSpace(result.CleanupError)) details.Add(L10n.Format("Affinity_Cleanup", result.CleanupError));
            if (!string.IsNullOrWhiteSpace(result.SavedPreferenceIssue)) details.Add(result.SavedPreferenceIssue);
            if (result.CurrentGroupId is { } current) details.Add(L10n.Format("Affinity_CurrentGroup", current));
            if (result.RetainedGroupId is { } retained) details.Add(L10n.Format("Affinity_RetainedGroup", retained));
            if (HasUnknownOperation(VmId)) details.Add(L10n.Get("Err_UncertainLock"));
            if (failure != null) details.Add(L10n.Format("Err_ReloadFailed", CoreMessageResolver.ResolveException(failure)));
            if (result.Status == CpuAffinityService.SaveStatus.Failed || !result.MetadataSaved || HasUnknownOperation(VmId) || failure != null)
            {
                if (HasUnknownOperation(VmId))
                    ShowError(string.Join(Environment.NewLine, details), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
                else
                    ShowError(string.Join(Environment.NewLine, details));
            }
            else ShowAppliedToast();
        }
        else if (failure != null) throw failure;
    }
}

internal sealed partial class CpuAffinityTile : ToggleButton
{
    private readonly int index;
    public event Action<CpuAffinityTile, PointerRoutedEventArgs>? MouseSelectionStarted;

    public bool Selected
    {
        get => IsChecked == true;
        set
        {
            if (IsChecked != value) IsChecked = value;
            AutomationProperties.SetItemStatus(this, L10n.Get(value ? "Settings_Selected" : "Settings_Unselected"));
        }
    }

    private const string PerfBoltData = "M9.5 1.5L3.8 8.8h3.7L6.5 14.5l5.7-7.3H8.5l1-5.7z";
    private const string EffLeafData = "M17 8C8 10 5.9 16.17 3.82 21.34l1.89.66.95-2.3c.48.17.98.3 1.34.3C19 20 22 3 22 3c-1 2-8 2.25-13 3.25S2 11.5 2 13.5s1.75 3.75 1.75 3.75C7 8 17 8 17 8z";

    public CpuAffinityTile(int index, CpuCoreType type)
    {
        this.index = index;
        MinWidth = 64;
        MinHeight = 76;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Padding = new Thickness(8);
        CornerRadius = new CornerRadius(4);
        var face = new Grid();
        face.Children.Add(new TextBlock
        {
            Text = index.ToString(),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (type != CpuCoreType.Unknown)
        {
            bool isEff = type == CpuCoreType.Efficient;
            string data = isEff ? EffLeafData : PerfBoltData;
            int vb = isEff ? 24 : 16;
            var path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = ThemeHelper.ParseGeometry(data),
                Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round
            };
            path.Fill = new SolidColorBrush(isEff ? Color.FromArgb(255, 0x6C, 0xCB, 0x5F) : Color.FromArgb(255, 0xFA, 0xA1, 0x46));
            var vbHost = new Viewbox
            {
                Width = 13,
                Height = 13,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Grid { Width = vb, Height = vb, Children = { path } }
            };
            face.Children.Add(vbHost);
        }
        Content = face;
        AutomationProperties.SetName(this, L10n.Format("Affinity_Aria_Logical", index));
        AutomationProperties.SetItemStatus(this, L10n.Get("Settings_Unselected"));
    }

    protected override void OnToggle()
    {
        // Selection state is managed by VmCpuAffinitySettingsView via MouseSelectionStarted and Click.
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

internal sealed partial class QuickToggleButton : ToggleButton
{
    public QuickToggleButton(string label)
    {
        Content = label;
        Height = 26;
        Padding = new Thickness(10, 0, 10, 0);
        CornerRadius = new CornerRadius(4);
        FontSize = 12;
        FontWeight = FontWeights.Normal;
    }

    protected override void OnToggle()
    {
        // Selection state is synchronized by VmCpuAffinitySettingsView.UpdateActions().
    }
}
