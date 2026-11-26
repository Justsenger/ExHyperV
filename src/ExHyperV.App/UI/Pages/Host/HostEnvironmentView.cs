using System.Runtime.InteropServices;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostEnvironmentView : UserControl
{
    private enum RowState { Ok, Warning, Fail, Unknown }
    private enum PendingToggleKind { None, Enable, Disable }
    private enum HyperVActionKind { None, Enable, Disable, StartVmms }

    private sealed record HyperVRow(RowState State, string InfoText, HyperVActionKind Action = HyperVActionKind.None);

    private readonly ScrollViewer scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel stack = new() { Spacing = 18 };
    private readonly StackPanel groupsPanel = new() { Spacing = 18 };
    private readonly ProgressRing busyRing = new() { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };

    private HostInventorySnapshot? currentInventory;
    private HostPage? feedbackHost;
    private PendingToggleKind pendingToggle;
    private string? unsupportedReason;
    private bool busy;

    public event Func<Task>? InventoryRefreshRequested;

    public HostEnvironmentView()
    {
        scroll.AttachScrollbarClearance(stack, bottomSpacing: 0);
        AutomationProperties.SetAutomationId(groupsPanel, "HostEnvGroupsPanel");

        busyRing.HorizontalAlignment = HorizontalAlignment.Right;
        stack.Children.Add(busyRing);
        stack.Children.Add(groupsPanel);
        scroll.Content = stack;
        Content = scroll;

        ActualThemeChanged += (_, _) =>
        {
            if (currentInventory != null) RenderInventory(currentInventory);
        };
    }

    internal void BindFeedbackHost(HostPage host) => feedbackHost = host;

    public void ApplyInventory(HostInventorySnapshot inventory)
    {
        currentInventory = inventory;
        var diag = inventory.Diagnostics;
        if (pendingToggle == PendingToggleKind.Enable && diag?.IsReady == true ||
            pendingToggle == PendingToggleKind.Disable &&
            diag?.Hypervisor is { State: HyperVCheckState.NotReady, DiagnosticKind: HyperVDiagnosticKind.NotRunning })
            pendingToggle = PendingToggleKind.None;
        if (diag?.Hypervisor.State == HyperVCheckState.Passed)
            unsupportedReason = null;

        RenderInventory(inventory);
    }

    private void RenderInventory(HostInventorySnapshot inventory)
    {
        groupsPanel.Children.Clear();
        var hv = ResolveHyperVRow(inventory.Diagnostics);

        AddGroup(L10n.Get("Host_Env_Group_Basic"),
            BuildStatusRow("HostEnvRowCpuVirt", inventory.CpuVirtOk ? RowState.Ok : RowState.Fail, L10n.Get("Host_Env_CpuVirt"),
                L10n.Get(inventory.CpuVirtOk ? "Host_Env_CpuInfo_Ready" : "Host_Env_CpuInfo_Unavailable")),
            BuildStatusRow("HostEnvRowHyperV", hv.State, L10n.Get("Host_Env_HyperVService"), hv.InfoText, BuildHyperVButton(hv)));

        string osCaption = string.IsNullOrWhiteSpace(inventory.OsCaption) ? Environment.OSVersion.VersionString : inventory.OsCaption;
        AddGroup(L10n.Get("Host_Env_Group_GpuPv"),
            BuildStatusRow("HostEnvRowGpuPv", inventory.GpuPvSupported ? RowState.Ok : RowState.Fail, L10n.Format("Host_Env_OsVersion", osCaption),
                L10n.Get(inventory.GpuPvSupported ? "Host_Env_GpuPvInfo_Ready" : "Host_Env_GpuPvInfo_Unavailable")));

        string iommuLabel = inventory.Arch == Architecture.Arm64
            ? L10n.Get("Host_Env_SmmuStatus")
            : L10n.Get("Host_Env_IommuStatus");
        string serverInfo = inventory.PendingProductSwitch is { IsConfirmed: true } pending
            ? L10n.Format("Host_Env_ServerInfo_Pending", pending.TargetType)
            : L10n.Get(inventory.IsServerSystem ? "Host_Env_ServerInfo_Ready" : "Host_Env_ServerInfo_Workstation");
        AddGroup(L10n.Get("Host_Env_Group_Pcie"),
            BuildStatusRow("HostEnvRowIommu", inventory.IommuOk ? RowState.Ok : RowState.Fail, iommuLabel,
                L10n.Get(inventory.IommuOk ? "Host_Env_IommuInfo_Ready" : "Host_Env_IommuInfo_Unavailable")),
            BuildStatusRow("HostEnvRowServerOs", inventory.IsServerSystem ? RowState.Ok : RowState.Fail, L10n.Get("Host_Env_ServerOs"), serverInfo));
    }

    private HyperVRow ResolveHyperVRow(HyperVEnvironmentStatus? diag)
    {
        if (pendingToggle == PendingToggleKind.Enable && diag?.IsReady != true)
            return new(RowState.Warning, L10n.Get("Host_Env_HvInfo_PendingEnable"));
        if (pendingToggle == PendingToggleKind.Disable && diag?.Hypervisor.State == HyperVCheckState.Passed)
            return new(RowState.Warning, L10n.Get("Host_Env_HvInfo_PendingDisable"));
        if (diag == null)
            return new(RowState.Unknown, L10n.Get("Host_Env_HvInfo_Unknown"));
        if (diag.IsReady)
            return new(RowState.Ok, L10n.Get("Host_Env_HvInfo_Ready"), HyperVActionKind.Disable);
        if (diag.Hypervisor.State == HyperVCheckState.NotReady)
            return !string.IsNullOrWhiteSpace(unsupportedReason)
                ? new(RowState.Fail, unsupportedReason)
                : new(RowState.Fail, L10n.Get("Host_Env_HvInfo_Inactive"), HyperVActionKind.Enable);
        if (diag.Hypervisor.State != HyperVCheckState.Passed)
            return new(RowState.Unknown, SanitizeDiagnosticText(diag.Hypervisor.Message));

        if (diag.Vmms.State == HyperVCheckState.NotReady)
        {
            return diag.Vmms.DiagnosticKind switch
            {
                HyperVDiagnosticKind.NotRunning => new(RowState.Fail, L10n.Get("Host_Env_HvInfo_VmmsStopped"), HyperVActionKind.StartVmms),
                HyperVDiagnosticKind.ServiceDisabled => new(RowState.Fail, L10n.Get("Host_Env_HvInfo_VmmsDisabled")),
                HyperVDiagnosticKind.ExecutableMissing => new(RowState.Fail, diag.VmmsService?.Image.ResolvedPath is { Length: > 0 } path
                    ? L10n.Format("Host_Env_HvInfo_VmmsExeMissingPath", path) : L10n.Get("Host_Env_HvInfo_VmmsExeMissing")),
                HyperVDiagnosticKind.NotFound => new(RowState.Fail, L10n.Get("Host_Env_HvInfo_VmmsMissing"), HyperVActionKind.Enable),
                _ => new(RowState.Fail, SanitizeDiagnosticText(diag.Vmms.Message))
            };
        }
        if (diag.Vmms.State != HyperVCheckState.Passed)
            return new(RowState.Unknown, SanitizeDiagnosticText(diag.Vmms.Message));

        return diag.Wmi.DiagnosticKind is HyperVDiagnosticKind.Timeout or HyperVDiagnosticKind.AccessDenied
            ? new(RowState.Unknown, SanitizeDiagnosticText(diag.Wmi.Message))
            : new(RowState.Fail, L10n.Get("Host_Env_HvInfo_WmiError"));
    }

    private static string SanitizeDiagnosticText(string? text) => string.IsNullOrWhiteSpace(text)
        ? L10n.Get("Host_Env_HvInfo_Unknown")
        : text.Trim().Replace('\uFF1B', '\uFF0C').Replace(';', ',').Replace('\u00B7', ',');

    private Button? BuildHyperVButton(HyperVRow hv)
    {
        string? labelKey = hv.Action switch
        {
            HyperVActionKind.Enable => "Host_Env_Enable",
            HyperVActionKind.Disable => "Host_Env_Disable",
            HyperVActionKind.StartVmms => "Host_Env_StartService",
            _ when hv.State == RowState.Warning => "Host_Env_PendingRestart",
            _ => null
        };
        if (labelKey == null) return null;

        var button = new Button
        {
            Content = L10n.Get(labelKey),
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !busy && hv.Action != HyperVActionKind.None
        };
        AutomationProperties.SetAutomationId(button, "HostEnvHyperVActionButton");
        if (hv.Action is HyperVActionKind.Enable or HyperVActionKind.StartVmms)
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (hv.Action != HyperVActionKind.None)
            button.Click += async (_, _) => await UiEvent.RunAsync(
                () => feedbackHost?.RunHostOperationAsync(() => RunHyperVActionAsync(hv.Action)) ?? Task.CompletedTask,
                message => feedbackHost?.ShowSettingsNotice(SanitizeDiagnosticText(message)));
        return button;
    }

    private void AddGroup(string title, params Border[] cards)
    {
        var group = new StackPanel { Spacing = 8 };
        group.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "TextFillColorPrimaryBrush",
                ActualTheme == ElementTheme.Light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White),
            Margin = new Thickness(2, 0, 0, 2)
        });
        foreach (var card in cards)
            group.Children.Add(card);
        groupsPanel.Children.Add(group);
    }

    private Border BuildStatusRow(string automationId, RowState state, string label, string infoText, Button? action = null)
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color okColor = light ? Color.FromArgb(255, 0x00, 0x8A, 0x17) : Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
        Color failColor = light ? Color.FromArgb(255, 0xC4, 0x2B, 0x1C) : Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

        FrameworkElement icon = state == RowState.Warning
            ? FloatingNoticeBanner.CreateCautionCircleIcon(ActualTheme, 20)
            : new FontIcon
            {
                Glyph = state switch { RowState.Ok => "\uEC61", RowState.Fail => "\uEB90", _ => "\uE9CE" },
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 20,
                Foreground = state == RowState.Unknown
                    ? ThemeHelper.TryResourceBrush(ActualTheme, "TextFillColorSecondaryBrush", Colors.Gray)
                    : new SolidColorBrush(state == RowState.Ok ? okColor : failColor),
                VerticalAlignment = VerticalAlignment.Center
            };

        var labelGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 4 };
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelGrid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "TextFillColorPrimaryBrush",
                light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White)
        });
        var infoButton = new SettingInfoButton(label, infoText);
        Grid.SetColumn(infoButton, 1);
        labelGrid.Children.Add(infoButton);

        var row = new Grid { ColumnSpacing = 12, MinHeight = 32 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(labelGrid, 1);
        row.Children.Add(icon);
        row.Children.Add(labelGrid);
        if (action != null)
        {
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
        }

        var card = new Border
        {
            Background = ThemeHelper.TryResourceBrush(ActualTheme, "CardBackgroundFillColorDefaultBrush",
                light ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "CardStrokeColorDefaultBrush",
                Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 13, 18, 13),
            Child = row
        };
        AutomationProperties.SetAutomationId(card, automationId);
        return card;
    }

    private async Task RunHyperVActionAsync(HyperVActionKind action)
    {
        if (busy || action == HyperVActionKind.None) return;
        SetBusy(true);
        try
        {
            feedbackHost?.ClearSettingsNotice();
            bool enable = action != HyperVActionKind.Disable;
            var result = await HyperVHostService.SetHyperVEnabledAsync(enable);
            if (!result.Success)
            {
                string error = SanitizeDiagnosticText(CoreMessageResolver.ResolveError(result));
                if (action == HyperVActionKind.Enable && result.ErrorKind == ApiErrorKind.Unsupported)
                    unsupportedReason = error;
                feedbackHost?.ShowSettingsNotice(error, InfoBarSeverity.Error);
            }
            else if (result.RequiresRestart)
            {
                pendingToggle = enable ? PendingToggleKind.Enable : PendingToggleKind.Disable;
                feedbackHost?.ShowSettingsNotice(L10n.Get(enable ? "Host_Env_Notice_PendingEnable" : "Host_Env_Notice_PendingDisable"), InfoBarSeverity.Warning);
            }
            else
            {
                pendingToggle = PendingToggleKind.None;
                feedbackHost?.ShowSuccessToast(L10n.Get(action == HyperVActionKind.StartVmms ? "Host_Env_Notice_VmmsStarted" : "Host_Env_ToggleSuccess"));
            }
        }
        catch (Exception ex)
        {
            feedbackHost?.ShowSettingsNotice(SanitizeDiagnosticText(CoreMessageResolver.ResolveException(ex)), InfoBarSeverity.Error);
        }
        finally
        {
            try
            {
                if (InventoryRefreshRequested != null)
                    await InventoryRefreshRequested();
            }
            finally { SetBusy(false); }
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        busyRing.IsActive = value;
        busyRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && currentInventory != null)
            RenderInventory(currentInventory);
    }
}
