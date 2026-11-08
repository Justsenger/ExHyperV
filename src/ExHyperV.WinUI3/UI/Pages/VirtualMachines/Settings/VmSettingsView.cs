using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>设置页的加载、错误显示及离开时等待；标题与返回由仪表盘宿主提供。</summary>
public abstract class VmSettingsView : UserControl
{
    private CancellationTokenSource cancellation = new();
    private readonly InfoBar message = new() { IsOpen = false, Visibility = Visibility.Collapsed, IsClosable = true, Margin = new Thickness(24, 0, 24, 12) };
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
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
        // A closed InfoBar must not reserve its bottom margin above the settings.
        message.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
            message.Visibility = message.IsOpen ? Visibility.Visible : Visibility.Collapsed);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(message, 0);
        Grid.SetRow(body, 1);
        layout.Children.Add(message);
        layout.Children.Add(body);
        Content = layout;
        Loaded += async (_, _) =>
        {
            if (loaded || stopped) return;
            loaded = true;
            await RunAsync(LoadAsync);
        };
    }

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
        sectionHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        sectionScroll = new ScrollViewer { Content = sectionHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
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
        if (stopFailures.Count == 0) return;

        // 离页等待期间发生的失败必须先交给用户；下一次离页不重复拦截已显示的结果。
        string failure = string.Join(Environment.NewLine, stopFailures);
        stopFailures.Clear();
        stopped = false;
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
        body.IsEnabled = true;
        ShowError(failure + "\n页面已保留；查看后可以再次返回。");
        throw new InvalidOperationException(failure);
    }

    protected Task RunAsync(Func<Task> operation)
    {
        if (stopped || IsBusy) return Task.CompletedTask;
        running = RunOperationAsync(operation);
        return running;
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        operationBusy = true;
        body.IsEnabled = false;
        BusyChanged?.Invoke(true);
        message.IsOpen = false;
        try { await operation(); }
        catch (OperationCanceledException) when (stopped) { }
        catch (Exception ex) { ShowError(ex.Message); }
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

    protected void ShowError(string? text)
    {
        string detail = string.IsNullOrWhiteSpace(text) ? "未取得有效结果。" : text;
        if (stopped)
        {
            if (!stopFailures.Contains(detail)) stopFailures.Add(detail);
            return;
        }
        message.Severity = InfoBarSeverity.Error;
        message.Title = "操作未完成";
        message.Message = detail;
        message.IsOpen = true;
    }

    protected void ShowInformation(string text)
    {
        message.Severity = InfoBarSeverity.Informational;
        message.Title = string.Empty;
        message.Message = text;
        message.IsOpen = true;
    }

    protected static void RequireSuccess(ApiResponse result)
    {
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }

    protected async Task ReloadAfterWriteAsync(ApiResponse result)
    {
        try { await LoadAsync(); }
        catch (Exception ex) when (!result.Success)
        {
            // 离页可能取消回读，但不能让取消异常覆盖已经取得的写入失败。
            throw new InvalidOperationException(result.Error + "\n配置回读失败：" + ex.Message, ex);
        }
        RequireSuccess(result);
    }

    protected async Task<VmQueryRow> QueryAsync(params VmQueryField[] fields)
    {
        return await Task.Run(async () =>
        {
            await using var query = new VmQueryService();
            var result = await query.QueryAsync(new VmQueryRequest(fields, [VmId]), CancellationToken);
            if (!result.HasData) throw new InvalidOperationException(result.Error);
            return result.Data!.VirtualMachines.FirstOrDefault()
                ?? throw new InvalidOperationException("没有找到此虚拟机。");
        }, CancellationToken);
    }

    protected async Task<(int Generation, VmState State, bool IsRunning)> ReadContextAsync()
    {
        // Settings need configuration state, not an enhanced-console availability probe.
        var result = await VmConfigurationState.ReadAsync(VmId);
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        var state = result.Data!;
        return (state.Generation, state.State, state.IsRunning);
    }

    protected async Task<VmProcessorService.HostCapabilities> ReadHostCapabilitiesAsync()
    {
        // Hardware capabilities only guide this page's editors. Core revalidates writes
        // against the current host; reopening the page also reads a fresh snapshot.
        if (hostCapabilities != null) return hostCapabilities;
        var result = await VmProcessorService.GetHostCapabilitiesAsync();
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        return hostCapabilities = result.Data!;
    }

    protected static Brush Brush(string key) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];

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
    {
        var label = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 4 };
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var info = new SettingInfoButton(title, description);
        Grid.SetColumn(info, 1);
        label.Children.Add(info);
        if (editor is SettingFieldEditor field)
            field.AvailabilityChanged += reason => info.SetDescription(string.Join("\n\n", new[] { description, reason }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()));
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

    protected static void AddSection(StackPanel panel, string title, Border card)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(card);
    }
}
