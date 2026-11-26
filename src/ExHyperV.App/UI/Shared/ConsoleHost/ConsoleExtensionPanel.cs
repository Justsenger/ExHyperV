using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.App.UI.Shared.ConsoleHost;

internal sealed partial class ConsoleExtensionPanel : StackPanel
{
    private readonly Guid vmId;
    private readonly string? vmName;
    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    internal ConsoleExtensionPanel(Guid vmId, string? vmName)
    {
        this.vmId = vmId;
        this.vmName = vmName;
        Spacing = 10;
        pollTimer.Tick += (_, _) => { if (IsLoaded) Refresh(); };
        Loaded += (_, _) =>
        {
            ConsoleExtensionCatalog.Local.Changed += Changed;
            pollTimer.Start();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            pollTimer.Stop();
            ConsoleExtensionCatalog.Local.Changed -= Changed;
        };
    }

    private void Changed(Guid id)
    {
        if (id == Guid.Empty || id == vmId) DispatcherQueue.TryEnqueue(() => { if (IsLoaded) Refresh(); });
    }

    internal static string Stage(ConsoleExtensionStage stage) => stage.Code == ConsoleExtensionStateCode.Unknown
        ? L10n.Format("Extension_Unknown", stage.RawCode) : L10n.Get("Extension_" + stage.Code);

    private void Refresh()
    {
        var catalog = ConsoleExtensionCatalog.Local;
        var s = catalog.Inspect(vmId);
        Children.Clear();
        Row("Extension_Host", catalog.Registered ? Stage(s.Host) : L10n.Get("Extension_Offline"));
        Row("Extension_Authorization", Stage(s.Authorization));
        Row("Extension_Guest", catalog.Registered && vmName != null ? $"{vmName} / {Stage(s.Guest)}" : Stage(s.Guest));
        Row("Extension_Session", Stage(s.Transport));
        if (s.TransportKind is ConsoleTransportKind.CpuShared or ConsoleTransportKind.GpuShared)
            Row("Extension_Path", s.TransportKind.ToString());
        string? diagnostic = ConsoleExtensionCatalog.Diagnostic(s.DiagnosticId);
        Row("Extension_Diagnostic", diagnostic ?? "—", diagnostic);
        if (!string.IsNullOrWhiteSpace(s.Reason))
            Children.Add(new TextBlock { Text = s.Reason, TextWrapping = TextWrapping.Wrap });
    }

    private void Row(string key, string value, string? copy = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = L10n.Get(key), VerticalAlignment = VerticalAlignment.Center });
        FrameworkElement readout;
        if (copy == null) readout = new TextBlock { Text = value, IsTextSelectionEnabled = true };
        else
        {
            var button = new Button { Content = value };
            ToolTipService.SetToolTip(button, L10n.Get("Common_CopyLabel"));
            button.Click += async (_, _) => await UiEvent.RunAsync(() =>
            {
                var data = new DataPackage();
                data.SetText(copy);
                Clipboard.SetContent(data);
                return Task.CompletedTask;
            });
            readout = button;
        }
        Grid.SetColumn(readout, 1);
        grid.Children.Add(readout);
        Children.Add(grid);
    }
}
