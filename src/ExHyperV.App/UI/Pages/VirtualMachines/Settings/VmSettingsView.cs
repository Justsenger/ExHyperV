
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings;

/// <summary>设置页的加载、错误显示及离开时等待；标题与返回由仪表盘宿主提供。</summary>
public abstract partial class VmSettingsView : UserControl
{
    private CancellationTokenSource cancellation = new();
    private readonly Grid layout = new();
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private IDashboardFeedbackHost? feedbackHost;
    private FloatingNoticeBanner? standaloneNotice;
    private FloatingSuccessToast? standaloneToast;
    private Task running = Task.CompletedTask;
    private Task? stopping;
    private readonly List<string> stopFailures = [];
    private VmProcessorService.HostCapabilities? hostCapabilities;
    private bool changedDuringOperation;
    private bool loaded;
    private bool stopped;
    private bool operationBusy;
    private bool nestedBusy;
    private (string Title, Border Content)[] sections = [];
    private ContentControl? sectionHost;
    private ScrollViewer? sectionScroll;

    protected VmSettingsView(Guid vmId)
    {
        VmId = vmId;
        layout.Children.Add(body);
        toastTimer.Tick += (_, _) => HideAppliedToast();

        Content = layout;
        Loaded += async (_, _) =>
        {
            if (loaded || stopped) return;
            loaded = true;
            await RunAsync(LoadAsync);
        };
    }

    internal void BindFeedbackHost(IDashboardFeedbackHost host) => feedbackHost = host;
    protected void SetHeaderTrailingAction(UIElement? content) => feedbackHost?.SetPageTabsTrailingAction(content);

    public Guid VmId { get; }
    internal bool HandlesScrolling { get; private set; }
    internal IEnumerable<string> SectionTitles => sections.Select(section => section.Title);
    internal event Action<bool>? BusyChanged;
    public event EventHandler? Changed;
    protected bool Updating { get; set; }
    protected bool IsStopped => stopped;
    protected bool IsBusy => operationBusy || nestedBusy;
    protected CancellationToken CancellationToken => cancellation.Token;
    protected void SetBody(UIElement element, bool handlesScrolling = false)
    {
        HandlesScrolling = handlesScrolling;
        body.Content = element;
    }
    protected void SetSectionedBody(IEnumerable<(string Title, Border Content)> items)
    {
        sections = items.ToArray();
        sectionHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        sectionScroll = new ScrollViewer { Content = sectionHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        sectionScroll.AttachScrollbarClearance(sectionHost, bottomSpacing: 0);
        ShowSection(0);
        SetBody(sectionScroll, handlesScrolling: true);
    }

    internal void ShowSection(int index)
    {
        if (sectionHost == null || index < 0 || index >= sections.Length) return;
        sectionHost.Content = sections[index].Content;
        sectionScroll!.ChangeView(null, 0, null, disableAnimation: true);
        OnSectionShown(index);
    }
    protected virtual void OnSectionShown(int index) { }
    protected void SetNestedBusy(bool busy)
    {
        nestedBusy = busy;
        body.IsEnabled = !stopped && !IsBusy;
        BusyChanged?.Invoke(IsBusy);
    }
    protected void NotifyProgressBar(bool active) => BusyChanged?.Invoke(active || IsBusy);
    protected abstract Task LoadAsync();
    protected void NotifyChanged()
    {
        // Finish the settings read-back before the dashboard starts its broader queries.
        if (IsBusy) changedDuringOperation = true;
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    public virtual Task StopAsync()
    {
        if (stopping is { IsCompleted: false }) return stopping;
        if (stopped) return Task.CompletedTask;
        return stopping = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        stopFailures.Clear();
        stopped = true;
        cancellation.Cancel();
        await running;
        if (stopFailures.Count == 0)
        {
            SetHeaderTrailingAction(null);
            return;
        }

        // 离页等待期间发生的失败必须先交给用户；下一次离页不重复拦截已显示的结果。
        string failure = string.Join(Environment.NewLine, stopFailures);
        stopFailures.Clear();
        stopped = false;
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
        body.IsEnabled = true;
        ShowError(L10n.Format("SettingsView_LeavePending", failure));
        throw new InvalidOperationException(failure);
    }

    protected Task RunAsync(Func<Task> operation)
    {
        if (stopped || IsBusy) return Task.CompletedTask;
        running = RunOperationAsync(operation);
        return running;
    }

    protected sealed class ActionableException(string message, string actionLabel, Func<Task> onAction) : Exception(message)
    {
        public string ActionLabel { get; } = actionLabel;
        public Func<Task> OnAction { get; } = onAction;
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        operationBusy = true;
        body.IsEnabled = false;
        BusyChanged?.Invoke(true);
        HideAppliedToast();
        standaloneNotice?.Hide();
        feedbackHost?.HideSuccessToast();
        feedbackHost?.ClearSettingsNotice();
        try { await operation(); }
        catch (OperationCanceledException) when (stopped)
        {
            // Leaving the page cancels its pending read.
        }
        catch (ActionableException ex) { ShowError(ex.Message, ex.ActionLabel, ex.OnAction); }
        catch (Exception ex) { ShowError(CoreMessageResolver.ResolveException(ex)); }
        finally
        {
            operationBusy = false;
            if (!stopped)
            {
                body.IsEnabled = !IsBusy;
            }
            BusyChanged?.Invoke(IsBusy);
            if (changedDuringOperation)
            {
                changedDuringOperation = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    protected void ShowError(string? text) => ShowError(text, null, null);

    protected void ShowError(string? text, string? actionLabel, Func<Task>? onAction)
    {
        string detail = string.IsNullOrWhiteSpace(text) ? L10n.Get("Err_NoValidData") : text;
        if (stopped)
        {
            if (!stopFailures.Contains(detail)) stopFailures.Add(detail);
            return;
        }
        HideAppliedToast();
        var button = CreateInfoBarActionButton(actionLabel, onAction);
        if (feedbackHost != null)
            feedbackHost.ShowSettingsNotice(detail, InfoBarSeverity.Error, button);
        else
            EnsureStandaloneNotice().Show(detail, InfoBarSeverity.Error, button);
    }

    private Button? CreateInfoBarActionButton(string? label, Func<Task>? onAction)
    {
        if (string.IsNullOrWhiteSpace(label) || onAction == null) return null;
        var button = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
        button.Click += async (_, _) =>
        {
            if (stopped || IsBusy) return;
            button.IsEnabled = false;
            try { await onAction(); }
            catch (ActionableException ex) { ShowError(ex.Message, ex.ActionLabel, ex.OnAction); }
            catch (Exception ex) { ShowError(CoreMessageResolver.ResolveException(ex)); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    protected void ShowInformation(string text)
    {
        HideAppliedToast();
        if (feedbackHost != null)
            feedbackHost.ShowSettingsNotice(text, InfoBarSeverity.Informational);
        else
            EnsureStandaloneNotice().Show(text, InfoBarSeverity.Informational);
    }

    protected void ShowAppliedToast(string? text = null)
    {
        bool hasError = feedbackHost != null
            ? feedbackHost.HasSettingsError
            : standaloneNotice is { IsOpen: true, Severity: InfoBarSeverity.Error };
        if (hasError) return;
        toastTimer.Stop();
        string applied = L10n.Get("Common_Applied");
        string labelText = string.IsNullOrWhiteSpace(text)
            || string.Equals(text, applied, StringComparison.Ordinal)
            || string.Equals(text, L10n.Get("Common_Applied"), StringComparison.Ordinal)
            ? applied
            : text;
        if (feedbackHost != null)
        {
            feedbackHost.ShowSuccessToast(labelText);
            return;
        }
        standaloneNotice?.Hide();
        EnsureStandaloneToast().Show(labelText, elevated: true);
        toastTimer.Start();
    }

    private void HideAppliedToast()
    {
        toastTimer.Stop();
        standaloneToast?.Hide();
    }

    private FloatingNoticeBanner EnsureStandaloneNotice()
    {
        if (standaloneNotice == null)
        {
            standaloneNotice = new FloatingNoticeBanner
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(8, 0, 8, 8)
            };
            layout.Children.Add(standaloneNotice);
        }
        return standaloneNotice;
    }

    private FloatingSuccessToast EnsureStandaloneToast()
    {
        if (standaloneToast == null)
        {
            standaloneToast = new FloatingSuccessToast
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 780,
                Margin = new Thickness(12, 0, 12, 12)
            };
            layout.Children.Add(standaloneToast);
        }
        return standaloneToast;
    }

    protected static void RequireSuccess(ApiResponse result)
    {
        if (!result.Success) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
    }

    protected async Task ReloadAfterWriteAsync(ApiResponse result)
    {
        try { await LoadAsync(); }
        catch (Exception ex) when (!result.Success)
        {
            // 离页可能取消回读，但不能让取消异常覆盖已经取得的写入失败。
            throw new InvalidOperationException(
                L10n.Format("SettingsView_ReadbackSuffix", CoreMessageResolver.ResolveError(result), CoreMessageResolver.ResolveException(ex)),
                ex);
        }
        RequireSuccess(result);
        ShowAppliedToast();
    }

    protected async Task<VmQueryRow> QueryAsync(params VmQueryField[] fields)
    {
        return await Task.Run(async () =>
        {
            await using var query = new VmQueryService();
            var result = await query.QueryAsync(new VmQueryRequest(fields, (Guid[])[VmId]), CancellationToken);
            if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
            return result.Data!.VirtualMachines.FirstOrDefault()
                ?? throw new InvalidOperationException(L10n.Get("SettingsView_VmNotFound"));
        }, CancellationToken);
    }

    protected async Task<(int Generation, VmState State, bool IsRunning)> ReadContextAsync()
    {
        // Settings need configuration state, not an enhanced-console availability probe.
        var result = await VmConfigurationState.ReadAsync(VmId);
        if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
        var state = result.Data!;
        return (state.Generation, state.State, state.IsRunning);
    }

    protected async Task<VmProcessorService.HostCapabilities> ReadHostCapabilitiesAsync()
    {
        // Hardware capabilities only guide this page's editors. Core revalidates writes
        // against the current host; reopening the page also reads a fresh snapshot.
        if (hostCapabilities != null) return hostCapabilities;
        var result = await VmProcessorService.GetHostCapabilitiesAsync();
        if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
        return hostCapabilities = result.Data!;
    }

    protected static Brush Brush(string key) => ThemeHelper.ResourceBrush(key);

    protected static ToggleSwitch Switch() => new()
    {
        OnContent = string.Empty,
        OffContent = string.Empty,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 0,
        IsEnabled = false
    };

    protected static Grid Row(string title, string? description, FrameworkElement editor)
    {
        var row = new Grid { Margin = new Thickness(24, 16, 24, 16), ColumnSpacing = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = SettingLabel(title, description, editor);
        row.Children.Add(label);
        Grid.SetColumn(editor, 1);
        editor.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(editor);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor, title);
        return row;
    }

    protected static Grid SettingLabel(string title, string? description, FrameworkElement? editor = null)
        => SettingLabel(title, description, out _, editor);

    private protected static Grid SettingLabel(string title, string? description, out SettingInfoButton info, FrameworkElement? editor = null)
    {
        var label = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 4 };
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        info = new SettingInfoButton(title, description);
        var infoBtn = info;
        Grid.SetColumn(infoBtn, 1);
        label.Children.Add(infoBtn);
        if (editor is SettingFieldEditor field)
            field.AvailabilityChanged += reason =>
            {
                label.Opacity = field.IsFieldEnabled ? 1.0 : 0.45;
                infoBtn.SetDescription(description, reason);
            };
        return label;
    }

    private protected static Grid FieldRow(string title, string? description, SettingFieldEditor field, SettingEditorKind kind)
    {
        field.UseAvailableWidth();
        var row = new Grid { RowSpacing = 8, ColumnSpacing = 12, MinHeight = 44 };
        if (kind == SettingEditorKind.Toggle)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(field, 1);
        }
        else
        {
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(field, 1);
        }
        row.Children.Add(SettingLabel(title, description, field));
        row.Children.Add(field);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, title);
        return row;
    }

    internal static void StyleCardBorder(Grid row, double minHeight, double rowSpacing = 0, Thickness? padding = null)
    {
        row.Background = Brush("CardBackgroundFillColorDefaultBrush");
        row.BorderBrush = Brush("CardStrokeColorDefaultBrush");
        row.BorderThickness = new Thickness(1);
        row.CornerRadius = new CornerRadius(8);
        row.Padding = padding ?? new Thickness(17);
        row.MinHeight = minHeight;
        row.RowSpacing = rowSpacing;
    }

    internal static Grid EnergyFieldRow(string title, string? help, SettingFieldEditor field)
    {
        var row = new Grid();
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = SettingLabel(title, help, field);
        label.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(label);
        if (field.HeaderValueControl != null)
        {
            Grid.SetColumn(field.HeaderValueControl, 1);
            header.Children.Add(field.HeaderValueControl);
            void SyncHeaderValueOpacity() => field.HeaderValueControl.Opacity = field.IsFieldEnabled ? 1.0 : 0.45;
            SyncHeaderValueOpacity();
            field.AvailabilityChanged += _ => SyncHeaderValueOpacity();
        }
        Grid.SetRow(header, 0);
        Grid.SetRow(field, 1);
        row.Children.Add(header);
        row.Children.Add(field);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, title);
        return row;
    }

    protected static Border FieldCard(string title, IEnumerable<Grid> editors, UIElement? footer = null)
    {
        var content = new StackPanel { Spacing = 12 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(content, title);
        var grid = new SettingsFieldPanel();
        foreach (var editor in editors) grid.Children.Add(editor);
        content.Children.Add(grid);
        if (footer != null) content.Children.Add(footer);
        return new Border
        {
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16), Child = content
        };
    }

    protected static Border Card(params UIElement[] rows)
    {
        var content = new StackPanel();
        foreach (var row in rows)
        {
            if (content.Children.Count > 0)
                content.Children.Add(new Border { Height = 1, Margin = new Thickness(24, 0, 24, 0), Background = Brush("CardStrokeColorDefaultBrush") });
            content.Children.Add(row);
        }
        return new Border
        {
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 24), Child = content
        };
    }

    private protected static Border UniformCard(
        string title,
        string? description,
        FrameworkElement editor,
        out Grid label,
        out SettingInfoButton info,
        bool span2 = false)
    {
        var row = new Grid { ColumnSpacing = 20 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label = SettingLabel(title, description, out info);
        row.Children.Add(label);
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        if (editor is Control control)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title);

        var card = new Border
        {
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 14, 20, 14),
            MinHeight = 68,
            Child = row
        };
        SettingsFieldPanel.SetSpan2(card, span2);
        return card;
    }

    private protected static void ApplyCardState(
        Grid label,
        SettingInfoButton info,
        Control control,
        string? description,
        bool available,
        bool editable,
        string? unavailableReason,
        string? lockedReason = null)
    {
        bool enabled = available && editable;
        control.IsEnabled = enabled;
        label.Opacity = enabled ? 1.0 : 0.55;
        string? disabledReason = !available
            ? (unavailableReason ?? L10n.Get("SettingsView_UnsupportedByHost"))
            : !editable
                ? lockedReason
                : null;
        info.SetDescription(description, disabledReason);
    }

    protected static void AddSection(StackPanel panel, string title, Border card)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(card);
    }
}
