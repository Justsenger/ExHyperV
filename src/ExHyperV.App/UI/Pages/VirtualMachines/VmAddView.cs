using System.Runtime.InteropServices;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace ExHyperV.App.UI.Pages.VirtualMachines;

public sealed partial class VmAddView : UserControl
{
    private const string SystemCatalogUrl = "https://exhyperv.com";

    private enum PresetKind { Windows11, Linux, Custom }

    private readonly UnderlineTabs modeTabs = new();
    private readonly Button closeButton = new()
    {
        Width = 32,
        Height = 32,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
        Content = new FontIcon { Glyph = "\uE711", FontSize = 14 }
    };

    private readonly FloatingNoticeBanner noticeBanner = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Bottom,
        MaxWidth = 760,
        Margin = new Thickness(20, 0, 20, 14),
        Visibility = Visibility.Collapsed
    };

    private readonly ScrollViewer createScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly Grid importPanel = new() { Visibility = Visibility.Collapsed };
    private readonly Border footerBar = new();

    // Presets
    private readonly ToggleButton presetWin11 = CreatePresetChip("Windows 11", L10n.Get("VmAdd_PresetWin11Desc"));
    private readonly ToggleButton presetLinux = CreatePresetChip(L10n.Get("VmAdd_PresetLinuxTitle"), L10n.Get("VmAdd_PresetLinuxDesc"));
    private readonly ToggleButton presetCustom = CreatePresetChip(L10n.Get("VmAdd_PresetCustomTitle"), L10n.Get("VmAdd_PresetCustomDesc"));

    // Basic info
    private readonly TextBox nameBox = new() { PlaceholderText = L10n.Get("VmAdd_NamePlaceholder") };
    private readonly TextBox pathBox = new();
    private readonly Button browsePathButton = new() { Content = L10n.Get("VmAdd_Browse") };

    // Source mode: 0 = ISO, 1 = Existing Disk, 2 = Empty
    private readonly RadioButton modeIsoRadio = new() { Content = L10n.Get("VmAdd_DiskModeIso"), GroupName = "VmAddDiskMode", IsChecked = true };
    private readonly RadioButton modeDiskRadio = new() { Content = L10n.Get("VmAdd_DiskModeExisting"), GroupName = "VmAddDiskMode" };
    private readonly RadioButton modeEmptyRadio = new() { Content = L10n.Get("VmAdd_DiskModeEmpty"), GroupName = "VmAddDiskMode" };

    private readonly StackPanel isoDetailPanel = new() { Spacing = 10 };
    private readonly TextBox isoPathBox = new() { PlaceholderText = L10n.Get("VmAdd_IsoPlaceholder") };
    private readonly Button browseIsoButton = new() { Content = L10n.Get("VmAdd_BrowseIso") };
    private readonly HyperlinkButton catalogLinkButton = new()
    {
        Content = L10n.Get("VmAdd_CatalogLink"),
        Padding = new Thickness(0),
        FontSize = 12.5
    };
    private readonly NumberBox diskSizeBox = new()
    {
        Minimum = 8,
        Maximum = 65536,
        Value = 128,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        Width = 140
    };
    private readonly Button[] diskSizePresets;

    private readonly StackPanel existingDiskPanel = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly TextBox existingDiskBox = new() { PlaceholderText = L10n.Get("VmAdd_ExistingDiskPlaceholder") };
    private readonly Button browseDiskButton = new() { Content = L10n.Get("VmAdd_BrowseDisk") };
    private readonly CheckBox diffDiskCheck = new() { Content = L10n.Get("VmAdd_DiffDisk") };
    private readonly TextBlock batchDiskHint = new()
    {
        Text = L10n.Get("VmAdd_BatchDiskError"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed
    };

    private readonly TextBlock emptyDiskHint = new()
    {
        Text = L10n.Get("VmAdd_EmptyDiskHint"),
        FontSize = 12.5,
        Opacity = 0.72,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed
    };

    // Resources
    private readonly Slider cpuSlider = new() { Minimum = 1, Maximum = Math.Max(1, Environment.ProcessorCount), StepFrequency = 1, Value = Math.Min(4, Math.Max(1, Environment.ProcessorCount)) };
    private readonly TextBlock cpuValueText = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly Slider memorySlider = new() { Minimum = 1, Maximum = 64, StepFrequency = 1, Value = 4 };
    private readonly TextBlock memoryValueText = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };

    // Network
    private readonly ComboBox switchCombo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    // Advanced Expander
    private readonly NumberBox quantityBox = new()
    {
        Minimum = 1,
        Maximum = 16,
        Value = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        Width = 120
    };
    private readonly RadioButton gen2Radio = new() { Content = L10n.Get("VmAdd_Gen2"), GroupName = "VmAddGen", IsChecked = true };
    private readonly RadioButton gen1Radio = new() { Content = L10n.Get("VmAdd_Gen1"), GroupName = "VmAddGen" };
    private readonly TextBlock gen1Hint = new() { FontSize = 12, Opacity = 0.72, Visibility = Visibility.Collapsed };
    private readonly ComboBox versionCombo = new() { Width = 140 };
    private readonly ToggleSwitch secureBootSwitch = new() { IsOn = true, OnContent = L10n.Get("Common_On"), OffContent = L10n.Get("Common_Off") };
    private readonly ToggleSwitch tpmSwitch = new() { IsOn = true, OnContent = L10n.Get("Common_On"), OffContent = L10n.Get("Common_Off") };
    private readonly ToggleSwitch dynamicMemorySwitch = new() { IsOn = true, OnContent = L10n.Get("Common_On"), OffContent = L10n.Get("Common_Off") };
    private readonly ComboBox isolationCombo = new() { Width = 200 };
    private readonly StackPanel igvmRow = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly TextBox igvmPathBox = new() { PlaceholderText = L10n.Get("VmAdd_IgvmPlaceholder") };
    private readonly CheckBox storeDisksWithVmCheck = new() { Content = L10n.Get("VmAdd_StoreDisksWithVm"), IsChecked = true };
    private readonly Grid separateDiskPathRow = new() { ColumnSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBox separateDiskPathBox = new() { PlaceholderText = L10n.Get("VmAdd_SeparateDiskPathPlaceholder") };

    // Footer
    private readonly CheckBox startAfterCreateCheck = new() { Content = L10n.Get("VmAdd_StartAfterCreate"), IsChecked = true };
    private readonly Button cancelButton = new() { Content = L10n.Get("Common_Cancel"), MinWidth = 88 };
    private readonly Button submitButton = new() { Content = L10n.Get("VmAdd_CreateButton"), MinWidth = 128 };
    private readonly StackPanel busyOverlay = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed
    };
    private readonly ProgressRing busyRing = new() { Width = 18, Height = 18, IsActive = false };
    private readonly TextBlock busyText = new() { Text = L10n.Get("VmAdd_CreatingBusy"), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };

    private PresetKind currentPreset = PresetKind.Windows11;
    private bool applyingPreset;
    private bool manualNameEdited;
    private bool manualPathEdited;
    private bool optionsLoaded;
    private bool isBusy;
    private readonly List<(Guid? Id, string Label)> switchItems = [];
    private VmCreationOptions? loadedOptions;

    public event Action? Closed;
    public event Func<Guid?, Task>? Created;

    public VmAddView()
    {
        diskSizePresets = ((int[])[64, 128, 256, 512]).Select(gb =>
        {
            var btn = new Button
            {
                Content = $"{gb} GB",
                Tag = gb,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(4),
                FontSize = 12
            };
            btn.Click += (_, _) => diskSizeBox.Value = gb;
            return btn;
        }).ToArray();

        BuildLayout();
        HookEvents();
        ApplyPreset(PresetKind.Windows11, forceName: true);
    }

    internal async Task ActivateAsync()
    {
        HideNotice();
        modeTabs.Select(0);
        UpdateModeVisibility(0);
        if (!optionsLoaded)
            await LoadOptionsAsync();
    }

    private void BuildLayout()
    {
        var outerFrame = new Border
        {
            Background = ThemeBrush("VmSurfaceBrush"),
            BorderBrush = ThemeBrush("VmStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        modeTabs.SetItems(L10n.Get("VmAdd_TabCreate"), L10n.Get("VmAdd_TabImport"));
        modeTabs.SetIcons(new("\uE710"), new("\uE8B5"));
        modeTabs.Select(0);

        AutomationProperties.SetName(closeButton, L10n.Get("VmAdd_CloseAria"));
        ToolTipService.SetToolTip(closeButton, L10n.Get("VmAdd_CloseTooltip"));

        var headerGrid = new Grid { Margin = new Thickness(16, 12, 16, 8), ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(modeTabs, 0);
        Grid.SetColumn(closeButton, 1);
        headerGrid.Children.Add(modeTabs);
        headerGrid.Children.Add(closeButton);
        Grid.SetRow(headerGrid, 0);
        rootGrid.Children.Add(headerGrid);

        // Create tab body
        var createContent = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(16, 4, 16, 20),
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 1. Presets
        var presetRow = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < 3; i++)
            presetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(presetWin11, 0);
        Grid.SetColumn(presetLinux, 1);
        Grid.SetColumn(presetCustom, 2);
        presetRow.Children.Add(presetWin11);
        presetRow.Children.Add(presetLinux);
        presetRow.Children.Add(presetCustom);
        createContent.Children.Add(presetRow);

        // 2. Basic info card
        var basicCardBody = new StackPanel { Spacing = 12 };
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browsePathButton, 1);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browsePathButton);
        basicCardBody.Children.Add(FieldRow(L10n.Get("VmAdd_FieldVmName"), nameBox));
        basicCardBody.Children.Add(FieldRow(L10n.Get("VmAdd_FieldLocation"), pathRow, L10n.Get("VmAdd_LocationHint")));
        createContent.Children.Add(SectionCard(L10n.Get("VmAdd_SectionBasic"), basicCardBody));

        // 3. Install source card
        var sourceCardBody = new StackPanel { Spacing = 12 };
        var modeSwitchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        modeSwitchRow.Children.Add(modeIsoRadio);
        modeSwitchRow.Children.Add(modeDiskRadio);
        modeSwitchRow.Children.Add(modeEmptyRadio);
        sourceCardBody.Children.Add(modeSwitchRow);

        // ISO detail
        var isoRow = new Grid { ColumnSpacing = 8 };
        isoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        isoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(isoPathBox, 0);
        Grid.SetColumn(browseIsoButton, 1);
        isoRow.Children.Add(isoPathBox);
        isoRow.Children.Add(browseIsoButton);
        isoDetailPanel.Children.Add(isoRow);
        isoDetailPanel.Children.Add(catalogLinkButton);

        var diskSizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        diskSizeRow.Children.Add(diskSizeBox);
        foreach (var btn in diskSizePresets)
            diskSizeRow.Children.Add(btn);
        isoDetailPanel.Children.Add(FieldRow(L10n.Get("VmAdd_FieldDiskSize"), diskSizeRow));
        sourceCardBody.Children.Add(isoDetailPanel);

        // Existing disk detail
        var existingRow = new Grid { ColumnSpacing = 8 };
        existingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        existingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(existingDiskBox, 0);
        Grid.SetColumn(browseDiskButton, 1);
        existingRow.Children.Add(existingDiskBox);
        existingRow.Children.Add(browseDiskButton);
        existingDiskPanel.Children.Add(existingRow);
        existingDiskPanel.Children.Add(diffDiskCheck);
        existingDiskPanel.Children.Add(batchDiskHint);
        sourceCardBody.Children.Add(existingDiskPanel);
        sourceCardBody.Children.Add(emptyDiskHint);
        createContent.Children.Add(SectionCard(L10n.Get("VmAdd_SectionSource"), sourceCardBody));

        // 4. Resources card
        var resCardBody = new StackPanel { Spacing = 12 };
        resCardBody.Children.Add(SliderFieldRow(L10n.Get("VmAdd_FieldCpu"), cpuSlider, cpuValueText));
        resCardBody.Children.Add(SliderFieldRow(L10n.Get("VmAdd_FieldMemory"), memorySlider, memoryValueText));
        createContent.Children.Add(SectionCard(L10n.Get("VmAdd_SectionResources"), resCardBody));

        // 5. Network card
        var netCardBody = new StackPanel { Spacing = 8 };
        netCardBody.Children.Add(FieldRow(L10n.Get("VmAdd_FieldSwitch"), switchCombo, L10n.Get("VmAdd_SwitchHint")));
        createContent.Children.Add(SectionCard(L10n.Get("VmAdd_SectionNetwork"), netCardBody));

        // 6. Advanced expander
        var advBody = new StackPanel { Spacing = 12, Padding = new Thickness(4) };
        advBody.Children.Add(InlineSettingRow(L10n.Get("VmAdd_AdvQuantityTitle"), L10n.Get("VmAdd_AdvQuantityDesc"), quantityBox));

        var genStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        genStack.Children.Add(gen2Radio);
        genStack.Children.Add(gen1Radio);
        var genBlock = new StackPanel { Spacing = 4 };
        genBlock.Children.Add(genStack);
        genBlock.Children.Add(gen1Hint);
        advBody.Children.Add(FieldRow(L10n.Get("VmAdd_AdvGenerationTitle"), genBlock));

        advBody.Children.Add(InlineSettingRow(L10n.Get("VmAdd_AdvVersionTitle"), L10n.Get("VmAdd_AdvVersionDesc"), versionCombo));
        advBody.Children.Add(InlineSettingRow(L10n.Get("VmAdd_AdvSecureBootTitle"), L10n.Get("VmAdd_AdvSecureBootDesc"), secureBootSwitch));
        advBody.Children.Add(InlineSettingRow(L10n.Get("VmAdd_AdvTpmTitle"), L10n.Get("VmAdd_AdvTpmDesc"), tpmSwitch));
        advBody.Children.Add(InlineSettingRow(L10n.Get("VmAdd_AdvDynMemTitle"), L10n.Get("VmAdd_AdvDynMemDesc"), dynamicMemorySwitch));
        advBody.Children.Add(InlineSettingRow(L10n.Get("VmAdd_AdvIsolationTitle"), L10n.Get("VmAdd_AdvIsolationDesc"), isolationCombo));

        var igvmInputRow = new Grid { ColumnSpacing = 8 };
        var browseIgvmButton = new Button { Content = L10n.Get("VmAdd_Browse") };
        browseIgvmButton.Click += async (_, _) =>
        {
            if (await StoragePickers.OpenAsync(".bin", ".img", ".igvm", "*") is { } file)
                igvmPathBox.Text = file;
        };
        igvmInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        igvmInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(igvmPathBox, 0);
        Grid.SetColumn(browseIgvmButton, 1);
        igvmInputRow.Children.Add(igvmPathBox);
        igvmInputRow.Children.Add(browseIgvmButton);
        igvmRow.Children.Add(FieldRow(L10n.Get("VmAdd_AdvIgvmTitle"), igvmInputRow));
        advBody.Children.Add(igvmRow);

        var browseSeparateDiskBtn = new Button { Content = L10n.Get("VmAdd_Browse") };
        browseSeparateDiskBtn.Click += async (_, _) =>
        {
            if (await StoragePickers.FolderAsync() is { } folder)
                separateDiskPathBox.Text = folder;
        };
        separateDiskPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        separateDiskPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(separateDiskPathBox, 0);
        Grid.SetColumn(browseSeparateDiskBtn, 1);
        separateDiskPathRow.Children.Add(separateDiskPathBox);
        separateDiskPathRow.Children.Add(browseSeparateDiskBtn);
        advBody.Children.Add(storeDisksWithVmCheck);
        advBody.Children.Add(separateDiskPathRow);

        var advancedExpander = new Expander
        {
            Header = L10n.Get("VmAdd_AdvancedHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
            Content = advBody
        };
        createContent.Children.Add(advancedExpander);

        createScroll.Content = createContent;
        createScroll.AttachScrollbarClearance(createContent, bottomSpacing: 0);

        // Import tab placeholder (clean structure ready for VmImportService)
        BuildImportPlaceholder();

        var bodyHost = new Grid();
        bodyHost.Children.Add(createScroll);
        bodyHost.Children.Add(importPanel);
        bodyHost.Children.Add(noticeBanner);
        Grid.SetRow(bodyHost, 1);
        rootGrid.Children.Add(bodyHost);

        // Footer
        submitButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        busyOverlay.Children.Add(busyRing);
        busyOverlay.Children.Add(busyText);

        var footerGrid = new Grid { ColumnSpacing = 12 };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionsRow.Children.Add(cancelButton);
        actionsRow.Children.Add(submitButton);

        Grid.SetColumn(startAfterCreateCheck, 0);
        Grid.SetColumn(busyOverlay, 1);
        Grid.SetColumn(actionsRow, 2);
        footerGrid.Children.Add(startAfterCreateCheck);
        footerGrid.Children.Add(busyOverlay);
        footerGrid.Children.Add(actionsRow);

        footerBar.BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush");
        footerBar.BorderThickness = new Thickness(0, 1, 0, 0);
        footerBar.Padding = new Thickness(16, 12, 16, 12);
        footerBar.Child = footerGrid;
        Grid.SetRow(footerBar, 2);
        rootGrid.Children.Add(footerBar);

        outerFrame.Child = rootGrid;
        Content = outerFrame;

        if (!VmCreateService.SupportsGeneration1)
        {
            gen1Radio.IsEnabled = false;
            gen1Hint.Text = L10n.Get("VmAdd_Arm64NoGen1Hint");
            gen1Hint.Visibility = Visibility.Visible;
        }

        UpdateResourceReadouts();
    }

    private void BuildImportPlaceholder()
    {
        var card = new Border
        {
            Background = ThemeBrush("VmCardBrush"),
            BorderBrush = ThemeBrush("VmStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24),
            MaxWidth = 560,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var stack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new FontIcon { Glyph = "\uE8B5", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = L10n.Get("VmAdd_ImportTitle"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = L10n.Get("VmAdd_ImportDesc"),
            FontSize = 13,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var pickFolderBtn = new Button { Content = L10n.Get("VmAdd_ImportPickFolder") };
        var pickZipBtn = new Button { Content = L10n.Get("VmAdd_ImportPickZip") };
        var pendingNotice = L10n.Get("VmAdd_ImportPendingNotice");
        pickFolderBtn.Click += (_, _) => ShowNotice(pendingNotice, InfoBarSeverity.Informational);
        pickZipBtn.Click += (_, _) => ShowNotice(pendingNotice, InfoBarSeverity.Informational);
        actions.Children.Add(pickFolderBtn);
        actions.Children.Add(pickZipBtn);
        stack.Children.Add(actions);

        card.Child = stack;
        importPanel.Children.Add(card);
    }

    private void HookEvents()
    {
        modeTabs.SelectionRequested += index =>
        {
            modeTabs.Select(index);
            UpdateModeVisibility(index);
        };
        closeButton.Click += (_, _) => { if (!isBusy) Closed?.Invoke(); };
        cancelButton.Click += (_, _) => { if (!isBusy) Closed?.Invoke(); };

        presetWin11.Click += (_, _) => ApplyPreset(PresetKind.Windows11);
        presetLinux.Click += (_, _) => ApplyPreset(PresetKind.Linux);
        presetCustom.Click += (_, _) => ApplyPreset(PresetKind.Custom);

        nameBox.TextChanged += (_, _) => { if (!applyingPreset) manualNameEdited = true; };
        pathBox.TextChanged += (_, _) => { if (!applyingPreset) manualPathEdited = true; };

        browsePathButton.Click += async (_, _) =>
        {
            if (await StoragePickers.FolderAsync() is { } folder)
                pathBox.Text = folder;
        };
        browseIsoButton.Click += async (_, _) =>
        {
            if (await StoragePickers.OpenAsync(".iso") is { } iso)
                isoPathBox.Text = iso;
        };
        browseDiskButton.Click += async (_, _) =>
        {
            if (await StoragePickers.OpenAsync(".vhdx", ".vhd", ".avhdx", ".avhd") is { } disk)
                existingDiskBox.Text = disk;
        };
        catalogLinkButton.Click += async (_, _) =>
        {
            if (Uri.TryCreate(SystemCatalogUrl, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
        };

        modeIsoRadio.Checked += (_, _) => UpdateDiskModePanels();
        modeDiskRadio.Checked += (_, _) => UpdateDiskModePanels();
        modeEmptyRadio.Checked += (_, _) => UpdateDiskModePanels();
        diffDiskCheck.Checked += (_, _) => UpdateBatchDiskHint();
        diffDiskCheck.Unchecked += (_, _) => UpdateBatchDiskHint();
        quantityBox.ValueChanged += (_, _) => UpdateBatchDiskHint();

        cpuSlider.ValueChanged += (_, _) => UpdateResourceReadouts();
        memorySlider.ValueChanged += (_, _) => UpdateResourceReadouts();

        gen2Radio.Checked += (_, _) => UpdateGenerationAvailability();
        gen1Radio.Checked += (_, _) => UpdateGenerationAvailability();
        isolationCombo.SelectionChanged += (_, _) => UpdateIsolationFields();
        storeDisksWithVmCheck.Checked += (_, _) => separateDiskPathRow.Visibility = Visibility.Collapsed;
        storeDisksWithVmCheck.Unchecked += (_, _) => separateDiskPathRow.Visibility = Visibility.Visible;

        submitButton.Click += async (_, _) => await CreateAsync();
    }

    private void UpdateModeVisibility(int index)
    {
        bool isCreate = index == 0;
        createScroll.Visibility = isCreate ? Visibility.Visible : Visibility.Collapsed;
        footerBar.Visibility = isCreate ? Visibility.Visible : Visibility.Collapsed;
        importPanel.Visibility = isCreate ? Visibility.Collapsed : Visibility.Visible;
        HideNotice();
    }

    private void ApplyPreset(PresetKind kind, bool forceName = false)
    {
        currentPreset = kind;
        presetWin11.IsChecked = kind == PresetKind.Windows11;
        presetLinux.IsChecked = kind == PresetKind.Linux;
        presetCustom.IsChecked = kind == PresetKind.Custom;
        if (kind == PresetKind.Custom) return;

        applyingPreset = true;
        try
        {
            if (!manualNameEdited || forceName)
                nameBox.Text = kind == PresetKind.Windows11 ? "Windows11" : "Linux";

            gen2Radio.IsChecked = true;
            if (kind == PresetKind.Windows11)
            {
                secureBootSwitch.IsOn = true;
                tpmSwitch.IsOn = true;
                diskSizeBox.Value = 128;
                cpuSlider.Value = Math.Min(4, cpuSlider.Maximum);
                memorySlider.Value = Math.Min(4, memorySlider.Maximum);
            }
            else
            {
                secureBootSwitch.IsOn = false;
                tpmSwitch.IsOn = false;
                diskSizeBox.Value = 64;
                cpuSlider.Value = Math.Min(4, cpuSlider.Maximum);
                memorySlider.Value = Math.Min(4, memorySlider.Maximum);
            }
        }
        finally
        {
            applyingPreset = false;
        }
    }

    private void UpdateDiskModePanels()
    {
        int mode = SelectedDiskMode;
        isoDetailPanel.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        existingDiskPanel.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        emptyDiskHint.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBatchDiskHint();
    }

    private void UpdateBatchDiskHint()
    {
        int qty = (int)Math.Max(1, Math.Round(quantityBox.Value));
        batchDiskHint.Visibility = SelectedDiskMode == 1 && qty > 1 && diffDiskCheck.IsChecked != true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateGenerationAvailability()
    {
        bool gen2 = gen2Radio.IsChecked == true;
        secureBootSwitch.IsEnabled = gen2;
        tpmSwitch.IsEnabled = gen2;
        isolationCombo.IsEnabled = gen2 && loadedOptions?.IsolationSupported == true;
        if (!gen2 && isolationCombo.Items.Count > 0)
            isolationCombo.SelectedIndex = 0;
        UpdateIsolationFields();
    }

    private void UpdateIsolationFields()
    {
        string? selected = isolationCombo.SelectedItem as string;
        igvmRow.Visibility = gen2Radio.IsChecked == true && string.Equals(selected, "OpenHCL", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateResourceReadouts()
    {
        int cores = (int)Math.Round(cpuSlider.Value);
        int maxCores = Math.Max(1, Environment.ProcessorCount);
        cpuValueText.Text = L10n.Format("VmAdd_CpuReadoutFormat", cores, maxCores);
        int memGb = (int)Math.Round(memorySlider.Value);
        memoryValueText.Text = $"{memGb} GB";
    }

    private async Task LoadOptionsAsync()
    {
        if (!manualPathEdited && string.IsNullOrWhiteSpace(pathBox.Text))
            pathBox.Text = VmCreateService.DefaultVmDirectory;

        var mem = HostSystemInfoService.TryReadHostMemoryMiB();
        if (mem is { TotalMiB: > 0 })
        {
            int maxGb = Math.Max(2, (int)(mem.Value.TotalMiB / 1024));
            memorySlider.Maximum = maxGb;
            if (memorySlider.Value > maxGb) memorySlider.Value = maxGb;
            UpdateResourceReadouts();
        }

        var response = await VmCreateService.GetCreationOptionsAsync();
        if (!response.HasData)
        {
            ShowNotice(CoreMessageResolver.ResolveError(response), InfoBarSeverity.Warning);
            return;
        }

        optionsLoaded = true;
        loadedOptions = response.Data!;
        var opts = loadedOptions;

        if (!manualPathEdited && !string.IsNullOrWhiteSpace(opts.DefaultVmPath))
            pathBox.Text = opts.DefaultVmPath;
        if (string.IsNullOrWhiteSpace(separateDiskPathBox.Text))
            separateDiskPathBox.Text = !string.IsNullOrWhiteSpace(opts.DefaultVhdPath) ? opts.DefaultVhdPath : opts.DefaultVmPath;

        versionCombo.Items.Clear();
        foreach (var v in opts.Versions)
            versionCombo.Items.Add(v);
        var defaultStable = opts.Versions.FirstOrDefault(v => double.TryParse(v, out double n) && n < 200);
        versionCombo.SelectedItem = defaultStable ?? opts.Versions.FirstOrDefault();

        isolationCombo.Items.Clear();
        foreach (var iso in opts.IsolationTypes)
            isolationCombo.Items.Add(iso);
        if (isolationCombo.Items.Count == 0) isolationCombo.Items.Add("Disabled");
        isolationCombo.SelectedIndex = 0;
        UpdateGenerationAvailability();

        var noSwitchLabel = L10n.Get("VmAdd_NoSwitch");
        switchItems.Clear();
        switchCombo.Items.Clear();
        switchItems.Add((null, noSwitchLabel));
        switchCombo.Items.Add(noSwitchLabel);
        foreach (var pair in opts.Switches)
        {
            switchItems.Add((pair.Key, pair.Value));
            switchCombo.Items.Add(pair.Value);
        }

        int defaultIdx = switchItems.FindIndex(s => s.Id != null && s.Label.Contains("Default", StringComparison.OrdinalIgnoreCase));
        if (defaultIdx < 0) defaultIdx = switchItems.FindIndex(s => s.Id != null);
        switchCombo.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;
    }

    private int SelectedDiskMode => modeIsoRadio.IsChecked == true ? 0 : modeDiskRadio.IsChecked == true ? 1 : 2;

    private async Task CreateAsync()
    {
        if (isBusy) return;
        HideNotice();

        int quantity = (int)Math.Clamp(Math.Round(quantityBox.Value), 1, 16);
        int diskMode = SelectedDiskMode;
        if (quantity > 1 && diskMode == 1 && diffDiskCheck.IsChecked != true)
        {
            ShowNotice(L10n.Get("VmAdd_BatchDiskError"), InfoBarSeverity.Error);
            return;
        }

        string baseName = string.IsNullOrWhiteSpace(nameBox.Text) ? "NewVM" : nameBox.Text.Trim();
        string vmRoot = string.IsNullOrWhiteSpace(pathBox.Text) ? VmCreateService.DefaultVmDirectory : pathBox.Text.Trim();
        bool gen2 = gen2Radio.IsChecked == true;
        Guid? switchId = switchCombo.SelectedIndex >= 0 && switchCombo.SelectedIndex < switchItems.Count
            ? switchItems[switchCombo.SelectedIndex].Id
            : null;

        VmCreationParameters BuildParameters(string vmName) => new()
        {
            Name = vmName,
            Path = vmRoot,
            StoreNewDisksWithVm = storeDisksWithVmCheck.IsChecked == true,
            Version = versionCombo.SelectedItem as string ?? "8.0",
            Generation = gen2 ? 2 : 1,
            ProcessorCount = (int)Math.Max(1, Math.Round(cpuSlider.Value)),
            MemoryMiB = (long)Math.Max(1, Math.Round(memorySlider.Value)) * 1024L,
            EnableDynamicMemory = dynamicMemorySwitch.IsOn,
            EnableSecureBoot = gen2 && secureBootSwitch.IsOn,
            EnableTpm = gen2 && tpmSwitch.IsOn,
            IsolationType = gen2 ? (isolationCombo.SelectedItem as string ?? "Disabled") : "Disabled",
            OpenHclIgvmPath = igvmPathBox.Text.Trim(),
            DiskMode = diskMode,
            DiskSizeGiB = (long)Math.Max(1, Math.Round(diskSizeBox.Value)),
            VhdPath = diskMode == 1
                ? existingDiskBox.Text.Trim()
                : (storeDisksWithVmCheck.IsChecked == true ? string.Empty : separateDiskPathBox.Text.Trim()),
            CreateDifferencingDisk = diskMode == 1 && diffDiskCheck.IsChecked == true,
            DifferencingDiskRoot = storeDisksWithVmCheck.IsChecked == true ? string.Empty : separateDiskPathBox.Text.Trim(),
            IsoPath = diskMode == 0 ? isoPathBox.Text.Trim() : string.Empty,
            SwitchId = switchId
        };

        var template = BuildParameters(baseName);
        if (VmCreateService.GetCreationError(template) is string validationError)
        {
            ShowNotice(validationError, InfoBarSeverity.Error);
            return;
        }

        SetBusy(true, quantity > 1 ? L10n.Format("VmAdd_CreatingBatchBusy", quantity) : L10n.Get("VmAdd_CreatingBusy"));
        try
        {
            Guid? firstCreatedId = null;
            string? osMetadata = currentPreset switch
            {
                PresetKind.Windows11 => "Windows",
                PresetKind.Linux => "Linux",
                _ => null
            };

            for (int i = 0; i < quantity; i++)
            {
                var p = quantity > 1 ? BuildParameters($"{baseName}-{i + 1:D2}") : template;
                var result = await VmCreateService.CreateVirtualMachineAsync(p);
                if (!result.Success)
                {
                    ShowNotice(result.Error ?? L10n.Get("VmAdd_CreateFailed"), InfoBarSeverity.Error);
                    if (firstCreatedId.HasValue && Created != null)
                        await Created.Invoke(firstCreatedId);
                    return;
                }

                if (result.VmId is { } vmId)
                {
                    if (!firstCreatedId.HasValue) firstCreatedId = vmId;
                    if (osMetadata != null)
                        await VmMetadataService.SetAsync(vmId, VmMetadataService.OsTypeKey, osMetadata);
                    if (startAfterCreateCheck.IsChecked == true)
                        await VmPowerService.ExecuteControlActionAsync(vmId, VmPowerAction.Start);
                }
            }

            if (Created != null)
                await Created.Invoke(firstCreatedId);
        }
        catch (Exception ex)
        {
            ShowNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        isBusy = busy;
        busyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        busyRing.IsActive = busy;
        if (!string.IsNullOrWhiteSpace(status)) busyText.Text = status;
        submitButton.IsEnabled = !busy;
        cancelButton.IsEnabled = !busy;
        closeButton.IsEnabled = !busy;
    }

    private void ShowNotice(string text, InfoBarSeverity severity) => noticeBanner.Show(text, severity);
    private void HideNotice() { if (noticeBanner.IsOpen) noticeBanner.Hide(); }

    private static ToggleButton CreatePresetChip(string title, string subtitle)
    {
        var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        return new ToggleButton
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(6),
            Content = stack
        };
    }

    private static Border SectionCard(string title, UIElement content)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
        body.Children.Add(content);
        return new Border
        {
            Background = ThemeBrush("VmCardBrush"),
            BorderBrush = ThemeBrush("VmStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Child = body
        };
    }

    private static StackPanel FieldRow(string label, UIElement input, string? hint = null)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.85 });
        panel.Children.Add(input);
        if (!string.IsNullOrWhiteSpace(hint))
            panel.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static StackPanel SliderFieldRow(string label, Slider slider, TextBlock readout)
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelTb = new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(readout, 1);
        head.Children.Add(labelTb);
        head.Children.Add(readout);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(head);
        panel.Children.Add(slider);
        return panel;
    }

    private static Grid InlineSettingRow(string title, string subtitle, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        labels.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Opacity = 0.68, TextWrapping = TextWrapping.Wrap });

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(labels, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(labels);
        grid.Children.Add(control);
        return grid;
    }

    private static Brush ThemeBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
