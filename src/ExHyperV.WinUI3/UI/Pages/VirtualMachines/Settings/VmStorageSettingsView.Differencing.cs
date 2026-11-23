using System.IO;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmStorageSettingsView
{
    private Border ChainSummary(VmDiskChainInspection chain, FrameworkElement? action = null)
    {
        bool ignored = chain.Nodes.Any(n => n.IdMismatchIgnored);
        var (title, severity) = chain.Status switch
        {
            VmDiskChainStatus.Complete when ignored => ("已允许忽略 ID 强制连接", InfoBarSeverity.Warning),
            VmDiskChainStatus.Complete => ("已连接到父盘", InfoBarSeverity.Success),
            VmDiskChainStatus.IdMismatch => ("父盘 ID 不一致", InfoBarSeverity.Warning),
            VmDiskChainStatus.FormatMismatch => ("父盘格式与当前磁盘不一致", InfoBarSeverity.Error),
            VmDiskChainStatus.HostMounted => ("父盘或链上磁盘已在宿主挂载", InfoBarSeverity.Warning),
            VmDiskChainStatus.Locked => ("父盘文件正被占用", InfoBarSeverity.Warning),
            VmDiskChainStatus.Missing => ("上游父盘缺失", InfoBarSeverity.Error),
            VmDiskChainStatus.Cycle => ("磁盘父链存在循环引用", InfoBarSeverity.Error),
            _ => (string.IsNullOrWhiteSpace(chain.Message) ? "无法验证磁盘链" : Clean(chain.Message), InfoBarSeverity.Error)
        };

        string NodeName(VmDiskChainNode node)
        {
            string name = Path.GetFileName(node.Path);
            if (string.IsNullOrEmpty(name)) name = node.Path;
            return chain.Status == VmDiskChainStatus.Missing && node.Depth > 0 && node.Format == null
                ? $"{name} (缺失)"
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
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
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
                d.ParentError = "输入 .vhd 或 .vhdx 文件路径";
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
                feedback.Content = Banner(d.InspectionError == null ? "正在核验父链…" : "未取得父链，请重新检测", InfoBarSeverity.Informational);
            else
            {
                FrameworkElement? fix = null;
                bool canEditParent = CanWrite && d.Access!.CanEditParentPath;
                if (d.IgnoreId)
                    fix = InlineCommand("取消强制", () => { d.IgnoreId = false; InspectParent(); }, canEditParent);
                else if (d.ParentChain is { Status: VmDiskChainStatus.IdMismatch, Nodes.Count: 2 })
                    fix = InlineCommand("强制连接", () => { d.IgnoreId = true; InspectParent(); }, canEditParent,
                        "若确认父盘内容完全一致，可跳过 ID 校验直接连接");
                feedback.Content = ChainSummary(d.ParentChain, fix);
            }
        }
        var row = PathInput(d.Parent, "输入父盘路径 (.vhd / .vhdx)", () => OpenMedia(false), path =>
        {
            d.Parent = path;
            d.IgnoreId = false;
            InspectParent();
        }, out _);
        row.IsEnabled = CanWrite && d.Access!.CanEditParentPath;
        rows.Add(FieldRowCard(
            "父盘路径",
            "重新定位差异磁盘父盘。容量与扇区格式由父盘继承，如需调整容量请先导出完整磁盘或合并到父盘。",
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
            ? "将整条父盘链与当前增量合成为新的独立虚拟磁盘文件，不改动现有差异链与父盘。"
            : "当前差异链未验证完整，暂不支持导出完整磁盘。";
        rows.Add(SettingRow(
            "导出完整磁盘",
            exportDesc,
            Command("导出", () => { d.ExportOpen = !d.ExportOpen; d.MergeOpen = false; Render(); }, enabled: canExport),
            canExport,
            canExport ? null : !complete ? exportDesc : disabledReason));
        if (d.ExportOpen && canExport) rows.Add(ExportRow(d));

        bool canMerge = CanWrite && complete && d.Access!.CanMerge;
        string mergeDesc = complete
            ? "将增量写入所选父盘并清理已并入差异盘，同时自动切换当前磁盘挂载路径。"
            : "当前差异链未验证完整，暂不支持合并操作。";
        rows.Add(SettingRow(
            "合并到父盘",
            mergeDesc,
            Command("合并", () => { d.MergeOpen = !d.MergeOpen; d.ExportOpen = false; Render(); }, enabled: canMerge),
            canMerge,
            canMerge ? null : !complete ? mergeDesc : disabledReason));
        if (d.MergeOpen && canMerge) rows.Add(MergeRow(d, d.SavedChain!));
    }

    private Border ExportRow(DetailDraft d)
    {
        var status = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var start = Command("开始导出", () =>
        {
            string destination = d.ExportPath;
            _ = RunAsync(() => RunStorageTaskAsync("导出完整磁盘", ["验证源磁盘链", "导出独立动态磁盘", "核验导出文件"],
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
            () => StoragePickers.SaveAsync("Export.vhdx", "虚拟磁盘", ".vhdx", ".vhd"), Check, out _);
        var actions = Actions(Command("取消", () =>
        {
            d.ExportOpen = false;
            d.ExportProbe.Cancel();
            Render();
        }), start);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        var content = new StackPanel
        {
            Spacing = 8,
            Children = { Text("导出目标路径", 13.5), path, status, actions }
        };
        if (d.ExportPath.Length > 0) Check(d.ExportPath);
        return CardRow(content, background: ThemeBrush(this, "SubtleFillColorTertiaryBrush"));
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
            string badgeLabel = node.Depth == 0 ? "当前" : isLast && chain.Nodes.Count > 2 ? "根父盘" : $"深度 {node.Depth}";
            string? roleText = isTarget ? "写入目标" : isPassThrough ? "并入" : null;
            string fileName = Path.GetFileName(node.Path);
            if (string.IsNullOrEmpty(fileName)) fileName = node.Path;

            var rail = new Grid { Width = 16 };
            rail.Children.Add(new Border
            {
                Width = 2, HorizontalAlignment = HorizontalAlignment.Center,
                Background = ThemeBrush(this, node.Depth <= clampedDepth ? "VmAccentBrush" : "DividerStrokeColorDefaultBrush"),
                Margin = new(0, node.Depth == 0 ? 18 : 0, 0, isLast ? 18 : 0)
            });
            rail.Children.Add(new Border
            {
                Height = isTarget ? 10 : 7, Width = isTarget ? 10 : 7, CornerRadius = new(5),
                Background = ThemeBrush(this, node.Depth <= clampedDepth ? "VmAccentBrush" : "TextFillColorTertiaryBrush"),
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
                    Foreground = ThemeBrush(this, isTarget ? "VmAccentBrush" : "TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });

            FrameworkElement card;
            if (node.Depth == 0)
            {
                card = new Border
                {
                    Padding = new(12, 8, 12, 8), Margin = new(0, 2, 0, 2),
                    CornerRadius = new(6), BorderThickness = new(1),
                    BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"),
                    Background = ThemeBrush(this, "CardBackgroundFillColorDefaultBrush"),
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
                    Background = isTarget ? ThemeBrush(this, "CardBackgroundFillColorDefaultBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderBrush = ThemeBrush(this, isTarget ? "VmAccentBrush" : "CardStrokeColorDefaultBrush"),
                    Content = line
                };
                AutomationProperties.SetName(button, $"{badgeLabel} {fileName} {roleText}".Trim());
                button.Click += (_, _) => { d.MergeDepth = node.Depth; Render(); };
                card = button;
            }
            ToolTipService.SetToolTip(card, node.Path);
            nodes.Children.Add(WithLeading(rail, card));
        }

        var confirm = DangerButton("确认合并", () => _ = RunAsync(() => RunStorageTaskAsync("合并到父盘",
            ["验证父链和目标祖先", "合并差异磁盘", "切换并核验挂接", "清理已并入的文件"],
            p => VmStorageService.MergeDifferencingDiskAsync(VmId, d.Item, destination, p), d.Item.DeviceToken)), CanWrite && d.Access!.CanMerge);
        var cancel = InlineCommand("取消", () => { d.MergeOpen = false; Render(); });
        confirm.Height = 24;
        confirm.MinHeight = 24;
        confirm.Padding = new(10, 0, 10, 0);
        confirm.FontSize = 12;
        var bannerActions = Actions(6, cancel, confirm);
        var warning = Banner($"将改写「{destinationName}」并删除原差异盘，完成后自动切换磁盘路径",
            InfoBarSeverity.Warning, bannerActions);
        return CardRow(Stack(nodes, warning), background: ThemeBrush(this, "SubtleFillColorTertiaryBrush"));
    }
}
