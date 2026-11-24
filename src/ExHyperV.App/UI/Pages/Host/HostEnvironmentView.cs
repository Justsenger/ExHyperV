using System.Runtime.InteropServices;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostEnvironmentView : UserControl
{
    private readonly ScrollViewer scroll = new()
    {
        Margin = new Thickness(16, 0, 16, 16),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel stack = new() { Spacing = 18 };
    private readonly StackPanel groupsPanel = new() { Spacing = 18 };
    private readonly ProgressRing busyRing = new() { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };

    private HostInventorySnapshot? currentInventory;
    private HostPage? feedbackHost;
    private bool busy;

    public event Func<Task>? InventoryRefreshRequested;

    public HostEnvironmentView()
    {
        scroll.AttachScrollbarClearance(stack, bottomSpacing: 0);

        busyRing.HorizontalAlignment = HorizontalAlignment.Right;
        stack.Children.Add(busyRing);
        stack.Children.Add(groupsPanel);
        scroll.Content = stack;
        Content = scroll;

        ActualThemeChanged += (_, _) =>
        {
            if (currentInventory != null) ApplyInventory(currentInventory);
        };
    }

    internal void BindFeedbackHost(HostPage host) => feedbackHost = host;

    public void ApplyInventory(HostInventorySnapshot inventory)
    {
        currentInventory = inventory;
        groupsPanel.Children.Clear();

        bool hvReady = inventory.Diagnostics?.IsReady == true;
        string hvSub = hvReady
            ? string.Empty
            : (string.IsNullOrWhiteSpace(inventory.Diagnostics?.StatusText)
                ? L10n.Get("Host_Env_HyperVNotRunning")
                : inventory.Diagnostics!.StatusText);

        var hvBtn = new Button
        {
            Content = L10n.Get(hvReady ? "Host_Env_Disable" : "Host_Env_Enable"),
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(4),
            IsEnabled = !busy
        };
        if (!hvReady)
            hvBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        hvBtn.Click += async (_, _) => await UiEvent.RunAsync(() => ToggleHyperVAsync(!hvReady), message => feedbackHost?.ShowSettingsNotice(message));

        // 1. Basic Requirements
        AddGroup(L10n.Get("Host_Env_Group_Basic"),
            BuildStatusRow(inventory.CpuVirtOk, L10n.Get("Host_Env_CpuVirt")),
            BuildStatusRow(hvReady, L10n.Get("Host_Env_HyperVService"), hvSub, hvBtn));

        // 2. GPU-PV Requirements
        int partCount = inventory.Gpus.Count(g => g.IsPartitionable);
        string gpuSub = L10n.Format("Host_Env_PartitionableGpus", partCount);
        AddGroup(L10n.Get("Host_Env_Group_GpuPv"),
            BuildStatusRow(inventory.GpuPvSupported,
                L10n.Format("Host_Env_OsVersion", string.IsNullOrWhiteSpace(inventory.OsCaption) ? Environment.OSVersion.VersionString : inventory.OsCaption),
                gpuSub));

        // 3. PCIe Passthrough Requirements
        string iommuLabel = inventory.Arch == Architecture.Arm64
            ? L10n.Get("Host_Env_SmmuStatus")
            : L10n.Get("Host_Env_IommuStatus");
        string serverSub = inventory.PendingProductSwitch is { IsConfirmed: true } p
            ? L10n.Format("Host_Env_PendingSwitch", p.TargetType)
            : string.Empty;
        AddGroup(L10n.Get("Host_Env_Group_Pcie"),
            BuildStatusRow(inventory.IommuOk, iommuLabel),
            BuildStatusRow(inventory.IsServerSystem, L10n.Get("Host_Env_ServerOs"), serverSub));
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

    private Border BuildStatusRow(bool? ok, string label, string subText = "", FrameworkElement? action = null)
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color okColor = light ? Color.FromArgb(255, 0x00, 0x8A, 0x17) : Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
        Color failColor = light ? Color.FromArgb(255, 0xC4, 0x2B, 0x1C) : Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

        var icon = new FontIcon
        {
            Glyph = ok == null ? "\uE9CE" : ok.Value ? "\uEC61" : "\uEB90",
            FontSize = 20,
            Foreground = new SolidColorBrush(ok == null ? Colors.Gray : ok.Value ? okColor : failColor),
            VerticalAlignment = VerticalAlignment.Center
        };

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "TextFillColorPrimaryBrush",
                light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White)
        });
        if (!string.IsNullOrWhiteSpace(subText))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = subText,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "TextFillColorSecondaryBrush",
                    light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF))
            });
        }

        var row = new Grid { ColumnSpacing = 12, MinHeight = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textStack, 1);
        row.Children.Add(icon);
        row.Children.Add(textStack);
        if (action != null)
        {
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
        }

        return new Border
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
    }

    private async Task ToggleHyperVAsync(bool enable)
    {
        if (busy || feedbackHost == null) return;
        await feedbackHost.RunHostOperationAsync(() => ApplyHyperVAsync(enable));
    }

    private async Task ApplyHyperVAsync(bool enable)
    {
        SetBusy(true);
        try
        {
            var result = await HyperVHostService.SetHyperVEnabledAsync(enable);
            if (!result.Success)
            {
                feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveError(result), InfoBarSeverity.Error);
            }
            else if (result.RequiresRestart)
            {
                feedbackHost?.ShowSettingsNotice(L10n.Get("Host_Env_ToggleSuccessRestart"), InfoBarSeverity.Warning);
            }
            else
            {
                feedbackHost?.ShowSuccessToast(L10n.Get("Host_Env_ToggleSuccess"));
            }

            if (InventoryRefreshRequested != null)
                await InventoryRefreshRequested();
        }
        catch (Exception ex)
        {
            feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        busyRing.IsActive = value;
        busyRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && currentInventory != null)
            ApplyInventory(currentInventory);
    }
}
