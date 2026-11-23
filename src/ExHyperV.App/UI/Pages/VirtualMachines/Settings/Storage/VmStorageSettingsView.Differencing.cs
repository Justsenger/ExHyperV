
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using static ExHyperV.App.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Storage;

public sealed partial class VmStorageSettingsView
{
    private Border ChainSummary(VmDiskChainInspection chain, FrameworkElement? action = null)
    {
        bool ignored = chain.Nodes.Any(n => n.IdMismatchIgnored);
        var (title, severity) = chain.Status switch
        {
            VmDiskChainStatus.Complete when ignored => (L10n.Get("Storage_ForceParentEnabled"), InfoBarSeverity.Warning),
            VmDiskChainStatus.Complete => (L10n.Get("Storage_ParentConnected"), InfoBarSeverity.Success),
            VmDiskChainStatus.IdMismatch => (L10n.Get("Storage_ParentIdMismatch"), InfoBarSeverity.Warning),
            VmDiskChainStatus.FormatMismatch => (L10n.Get("Storage_ParentFormatMismatch"), InfoBarSeverity.Error),
            VmDiskChainStatus.HostMounted => (L10n.Get("Storage_ChainMounted"), InfoBarSeverity.Warning),
            VmDiskChainStatus.Locked => (L10n.Get("Storage_ParentBusy"), InfoBarSeverity.Warning),
            VmDiskChainStatus.Missing => (L10n.Get("Storage_AncestorMissing"), InfoBarSeverity.Error),
            VmDiskChainStatus.Cycle => (L10n.Get("Storage_ChainCycle"), InfoBarSeverity.Error),
            _ => (string.IsNullOrWhiteSpace(chain.Message) ? L10n.Get("Storage_ChainUnverified") : Clean(chain.Message), InfoBarSeverity.Error)
        };

        string NodeName(VmDiskChainNode node)
        {
            string name = Path.GetFileName(node.Path);
            if (string.IsNullOrEmpty(name)) name = node.Path;
            return chain.Status == VmDiskChainStatus.Missing && node.Depth > 0 && node.Format == null
                ? L10n.Format("Storage_MissingPath", name)
                : name;
        }

        TextBlock? inlineChain = null;
        if (chain.Nodes.Count >= 2)
        {
            var visible = chain.Nodes.Count > 3
                ? new[] { NodeName(chain.Nodes[0]), NodeName(chain.Nodes[1]), "…", NodeName(chain.Nodes[^1]) }
                : chain.Nodes.Select(NodeName);
            inlineChain = new TextBlock
            {
                Text = string.Join(" → ", visible),
                FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                FontSize = 12,
                Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        string fullChain = chain.Nodes.Count > 0
            ? string.Join(" → ", chain.Nodes.Select(n => n.Path))
            : chain.Message;
        string tip = string.IsNullOrWhiteSpace(chain.Message) || chain.Status == VmDiskChainStatus.Complete
            ? fullChain
            : $"{chain.Message}\n{fullChain}".Trim();

        return Banner(title, severity, action, inlineChain, tip);
    }

    private void AddDifferencingRows(DetailDraft d, List<UIElement> rows, string? disabledReason)
    {
        var feedback = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void InspectParent()
        {
            d.ParentChain = null;
            d.ParentError = null;
            d.ParentProbe.Cancel();
            UpdateApply(d);
            if (!Path.IsPathFullyQualified(d.Parent))
            {
                d.ParentError = L10n.Get("Storage_ParentFilePlaceholder");
                ShowParent();
                return;
            }
            ShowParent();
            var parent = d.Parent;
            bool ignore = d.IgnoreId;
            _ = d.ParentProbe.StartAsync(CancellationToken, async token =>
            {
                var chain = Data(await VmStorageService.InspectDifferencingChainAsync(d.Item.PathOrDiskNumber, parent, ignore).WaitAsync(token));
                token.ThrowIfCancellationRequested();
                d.ParentChain = chain;
                ShowParent();
                UpdateApply(d);
            }, message => { d.ParentError = message; ShowParent(); UpdateApply(d); });
        }
        void ShowParent()
        {
            if (d.ParentError != null) feedback.Content = Banner(d.ParentError, InfoBarSeverity.Error);
            else if (d.ParentChain == null)
                feedback.Content = Banner(d.InspectionError == null ? L10n.Get("Storage_CheckingParentChain") : L10n.Get("Storage_NoChainRetry"), InfoBarSeverity.Informational);
            else
            {
                FrameworkElement? fix = null;
                bool canEditParent = CanWrite && d.Access!.CanEditParentPath;
                if (d.IgnoreId)
                    fix = InlineCommand(L10n.Get("Storage_DisableForce"), () => { d.IgnoreId = false; InspectParent(); }, canEditParent);
                else if (d.ParentChain is { Status: VmDiskChainStatus.IdMismatch, Nodes.Count: 2 })
                    fix = InlineCommand(L10n.Get("Storage_ForceConnect"), () => { d.IgnoreId = true; InspectParent(); }, canEditParent,
                        L10n.Get("Storage_ForceConnectHelp"));
                feedback.Content = ChainSummary(d.ParentChain, fix);
            }
        }
        var row = PathInput(d.Parent, L10n.Get("Storage_ParentPathPlaceholder"), () => OpenMedia(false), path =>
        {
            d.Parent = path;
            d.IgnoreId = false;
            InspectParent();
        }, out _);
        row.IsEnabled = CanWrite && d.Access!.CanEditParentPath;
        rows.Add(FieldRowCard(
            L10n.Get("Storage_ParentPath"),
            L10n.Get("Storage_RelocateParentHelp"),
            null,
            row,
            feedback,
            CanWrite && d.Access!.CanEditParentPath,
            d.Access!.CanEditParentPath ? null : disabledReason));
        if (d.ParentDirty && d.ParentChain == null && d.ParentError == null) InspectParent();
        else ShowParent();

        bool complete = d.SavedChain is { IsComplete: true, Nodes.Count: > 1 };
        bool canExport = CanWrite && complete && d.Access!.CanExport;
        string exportDesc = complete
            ? L10n.Get("Storage_ExportChainHelp")
            : L10n.Get("Storage_UnverifiedExport");
        rows.Add(SettingRow(
            L10n.Get("Storage_ExportStandaloneDisk"),
            exportDesc,
            Command(L10n.Get("Storage_Export"), () => { d.ExportOpen = !d.ExportOpen; d.MergeOpen = false; Render(); }, enabled: canExport),
            canExport,
            canExport ? null : !complete ? exportDesc : disabledReason));
        if (d.ExportOpen && canExport) rows.Add(ExportRow(d));

        bool canMerge = CanWrite && complete && d.Access!.CanMerge;
        string mergeDesc = complete
            ? L10n.Get("Storage_MergeParentHelp")
            : L10n.Get("Storage_UnverifiedMerge");
        rows.Add(SettingRow(
            L10n.Get("Storage_MergeIntoParent"),
            mergeDesc,
            Command(L10n.Get("Cp_MenuMerge"), () => { d.MergeOpen = !d.MergeOpen; d.ExportOpen = false; Render(); }, enabled: canMerge),
            canMerge,
            canMerge ? null : !complete ? mergeDesc : disabledReason));
        if (d.MergeOpen && canMerge) rows.Add(MergeRow(d, d.SavedChain!));
    }

    private Border ExportRow(DetailDraft d)
    {
        var status = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var start = Command(L10n.Get("Storage_StartExport"), () =>
        {
            string destination = d.ExportPath;
            _ = RunAsync(() => RunStorageTaskAsync(L10n.Get("Storage_ExportStandaloneDisk"), [L10n.Get("Storage_VerifySourceChain"), L10n.Get("Storage_ExportDynamicDisk"), L10n.Get("Storage_VerifyExport")],
                p => VmStorageService.ExportDifferencingDiskAsync(VmId, d.Item, destination, p), d.Item.DeviceToken));
        }, accent: true, enabled: false);
        void Check(string path)
        {
            d.ExportPath = path;
            start.IsEnabled = false;
            _ = d.ExportProbe.StartAsync(CancellationToken, async token =>
            {
                var check = await Task.Run(() => CheckMedia(path, dvd: false, create: true), token).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                start.IsEnabled = CanWrite && d.Access!.CanExport && check.Valid;
                status.Content = check.Valid ? null : Banner(check.Message, check.Severity);
            }, error => status.Content = Banner(error, InfoBarSeverity.Error));
        }
        var path = PathInput(d.ExportPath, @"D:\Hyper-V\Virtual Hard Disks\exported_full.vhdx",
            () => StoragePickers.SaveAsync("Export.vhdx", L10n.Get("Dev_VirtualDisk"), ".vhdx", ".vhd"), Check, out _);
        var actions = Actions(Command(L10n.Get("Common_Cancel"), () =>
        {
            d.ExportOpen = false;
            d.ExportProbe.Cancel();
            Render();
        }), start);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        var content = new StackPanel
        {
            Spacing = 8,
            Children = { Text(L10n.Get("Storage_ExportTargetPath"), 13.5), path, status, actions }
        };
        if (d.ExportPath.Length > 0) Check(d.ExportPath);
        return CardRow(content, background: ThemeHelper.ThemeBrush(this, "SubtleFillColorTertiaryBrush"));
    }

    private Border MergeRow(DetailDraft d, VmDiskChainInspection chain)
    {
        var nodes = new StackPanel { Spacing = 0 };
        int clampedDepth = Math.Clamp(d.MergeDepth, 1, Math.Max(1, chain.Nodes.Count - 1));
        d.MergeDepth = clampedDepth;
        string destination = chain.Nodes[clampedDepth].Path;
        string destinationName = Path.GetFileName(destination);

        foreach (var node in chain.Nodes)
        {
            bool isTarget = node.Depth == clampedDepth;
            bool isPassThrough = node.Depth > 0 && node.Depth < clampedDepth;
            bool isLast = node.Depth == chain.Nodes.Count - 1;
            string badgeLabel = node.Depth == 0 ? L10n.Get("Cp_BadgeCurrent") : isLast && chain.Nodes.Count > 2 ? L10n.Get("Storage_RootParent") : L10n.Format("Storage_ChainDepth", node.Depth);
            string? roleText = isTarget ? L10n.Get("Storage_WriteTarget") : isPassThrough ? L10n.Get("Storage_MergeSource") : null;
            string fileName = Path.GetFileName(node.Path);
            if (string.IsNullOrEmpty(fileName)) fileName = node.Path;

            var rail = new Grid { Width = 16 };
            rail.Children.Add(new Border
            {
                Width = 2, HorizontalAlignment = HorizontalAlignment.Center,
                Background = ThemeHelper.ThemeBrush(this, node.Depth <= clampedDepth ? "VmAccentBrush" : "DividerStrokeColorDefaultBrush"),
                Margin = new(0, node.Depth == 0 ? 18 : 0, 0, isLast ? 18 : 0)
            });
            rail.Children.Add(new Border
            {
                Height = isTarget ? 10 : 7, Width = isTarget ? 10 : 7, CornerRadius = new(5),
                Background = ThemeHelper.ThemeBrush(this, node.Depth <= clampedDepth ? "VmAccentBrush" : "TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            });

            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var depthBadge = Badge(this, badgeLabel);
            depthBadge.Height = 20;
            depthBadge.Padding = new(7, 0, 7, 0);
            ((TextBlock)depthBadge.Child).VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(depthBadge);
            left.Children.Add(new TextBlock
            {
                Text = fileName, FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            });

            FrameworkElement line = roleText == null
                ? left
                : Split(left, new TextBlock
                {
                    Text = roleText, FontSize = 12,
                    Foreground = ThemeHelper.ThemeBrush(this, isTarget ? "VmAccentBrush" : "TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });

            FrameworkElement card;
            if (node.Depth == 0)
            {
                card = new Border
                {
                    Padding = new(12, 8, 12, 8), Margin = new(0, 2, 0, 2),
                    CornerRadius = new(6), BorderThickness = new(1),
                    BorderBrush = ThemeHelper.ThemeBrush(this, "CardStrokeColorDefaultBrush"),
                    Background = ThemeHelper.ThemeBrush(this, "CardBackgroundFillColorDefaultBrush"),
                    Child = line
                };
            }
            else
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new(12, 8, 12, 8),
                    Margin = new(0, 2, 0, 2),
                    CornerRadius = new(6),
                    BorderThickness = new(1),
                    Background = isTarget ? ThemeHelper.ThemeBrush(this, "CardBackgroundFillColorDefaultBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderBrush = ThemeHelper.ThemeBrush(this, isTarget ? "VmAccentBrush" : "CardStrokeColorDefaultBrush"),
                    Content = line
                };
                AutomationProperties.SetName(button, $"{badgeLabel} {fileName} {roleText}".Trim());
                button.Click += (_, _) => { d.MergeDepth = node.Depth; Render(); };
                card = button;
            }
            ToolTipService.SetToolTip(card, node.Path);
            nodes.Children.Add(WithLeading(rail, card));
        }

        var confirm = DangerButton(L10n.Get("Storage_ConfirmMerge"), () => _ = RunAsync(() => RunStorageTaskAsync(L10n.Get("Storage_MergeIntoParent"),
            [L10n.Get("Storage_VerifyAncestors"), L10n.Get("Storage_MergeDifferencingDisk"), L10n.Get("Storage_SwitchAndVerify"), L10n.Get("Storage_CleanMergedFiles")],
            p => VmStorageService.MergeDifferencingDiskAsync(VmId, d.Item, destination, p), d.Item.DeviceToken)), CanWrite && d.Access!.CanMerge);
        var cancel = InlineCommand(L10n.Get("Common_Cancel"), () => { d.MergeOpen = false; Render(); });
        confirm.Height = 24;
        confirm.MinHeight = 24;
        confirm.Padding = new(10, 0, 10, 0);
        confirm.FontSize = 12;
        var bannerActions = Actions(6, cancel, confirm);
        var warning = Banner(L10n.Format("Storage_MergeWarning", destinationName),
            InfoBarSeverity.Warning, bannerActions);
        return CardRow(Stack(nodes, warning), background: ThemeHelper.ThemeBrush(this, "SubtleFillColorTertiaryBrush"));
    }
}
