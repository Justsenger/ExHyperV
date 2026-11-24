using System.Runtime.InteropServices.WindowsRuntime;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings;

/// <summary>虚拟机快照拓扑画布、右键菜单操作与全屏切换。</summary>
public sealed partial class VmCheckpointSettingsView : UserControl
{
    private const double CardWidth = 176;
    private const double CardHeight = 110;
    private const double PitchX = 212;
    private const double PitchY = 136;
    private const double MinZoom = 0.45;
    private const double MaxZoom = 1.85;
    private const string MergeMenuIconPathData =
        "M 15.5 2 Q 15.7 2 15.85 2.15 Q 16 2.3 16 2.5 Q 16 3.38 15.74 4.22 Q 15.48 5.06 15.01 5.79 Q 14.54 6.51 13.88 7.08 Q 13.21 7.65 12.4 8 Q 13.21 8.35 13.88 8.92 Q 14.54 9.49 15.01 10.21 Q 15.48 10.94 15.74 11.78 Q 16 12.62 16 13.5 Q 16 13.7 15.85 13.85 Q 15.7 14 15.5 14 Q 15.3 14 15.15 13.85 Q 15 13.7 15 13.5 Q 15 12.15 14.33 10.99 Q 13.99 10.41 13.52 9.95 Q 13.05 9.48 12.5 9.16 Q 11.94 8.84 11.3 8.67 Q 10.67 8.5 10 8.5 H 1.7 L 4.85 11.65 Q 5 11.8 5 12 Q 5 12.2 4.85 12.35 Q 4.7 12.5 4.5 12.5 Q 4.3 12.5 4.15 12.35 L 0.15 8.35 Q 0 8.2 0 8 Q 0 7.8 0.15 7.65 L 4.15 3.65 Q 4.3 3.5 4.5 3.5 Q 4.7 3.5 4.85 3.65 Q 5 3.8 5 4 Q 5 4.2 4.85 4.35 L 1.7 7.5 H 10 Q 10.67 7.5 11.3 7.33 Q 11.94 7.16 12.5 6.84 Q 13.05 6.52 13.52 6.05 Q 13.99 5.59 14.33 5.01 Q 15 3.85 15 2.5 Q 15 2.3 15.15 2.15 Q 15.3 2 15.5 2 Z";

    private sealed record PendingWrite(string Detail, ApiErrorKind Kind, CheckpointNode? Mount);
    private static readonly Dictionary<Guid, PendingWrite> PendingWrites = new();
    private static WriteableBitmap? sharedBrandMatte;

    private readonly Guid vmId;
    private readonly Grid layout = new();
    private readonly Grid viewport = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly RectangleGeometry viewportClip = new();
    private readonly Canvas canvas = new() { Background = new SolidColorBrush(Colors.Transparent), RenderTransformOrigin = new Point(0, 0) };
    private readonly CompositeTransform viewportTransform = new();
    private readonly ContentControl interactiveHost = new() { IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Border disabledBanner = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(14, 0, 0, 14),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 6, 8, 6)
    };
    private readonly Border pendingBanner = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(16, 16, 16, 0),
        MaxWidth = 560,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 12, 16, 12)
    };
    private readonly StackPanel pendingContent = new() { Spacing = 8 };
    private readonly Button fullscreenButton = new()
    {
        Width = 34,
        Height = 34,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(6),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 14, 14)
    };
    private readonly FontIcon fullscreenIcon = new()
    {
        FontFamily = AppFonts.SegoeFluent,
        Glyph = "\uE740",
        FontSize = 15
    };

    private IDashboardFeedbackHost? feedbackHost;
    private FloatingNoticeBanner? standaloneNotice;
    private readonly Dictionary<string, Point> positions = [];
    private readonly Dictionary<string, (Border Card, Button Host, bool IsCurrent, bool IsHistoricalDiskMounted)> cardVisuals = [];
    private List<CheckpointNode> nodes = [];
    private CheckpointNode? selected;
    private Task operation = Task.CompletedTask;
    private Task? stopping;
    private readonly List<string> stopFailures = [];
    private ContentDialog? dialog;
    private Border? renameEditor;
    private bool started, stopped, busy, checkpointsEnabled, dragging, stateAvailable, isFullscreen, hasUserTransform;
    private double zoom = 1.0;
    private Point? pointerPosition;

    internal event Action<bool>? BusyChanged;
    internal event Action<bool>? FullscreenChanged;
    public event EventHandler? Changed;

    public VmCheckpointSettingsView(Guid vmId)
    {
        this.vmId = vmId;

        canvas.RenderTransform = viewportTransform;
        viewport.Clip = viewportClip;
        viewport.Children.Add(canvas);
        viewport.SizeChanged += (_, args) =>
        {
            viewportClip.Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);
            if (stateAvailable && !hasUserTransform) CenterView();
        };
        viewport.PointerPressed += PanPressed;
        viewport.PointerMoved += PanMoved;
        viewport.PointerReleased += PanReleased;
        viewport.PointerCanceled += PanReleased;
        viewport.PointerWheelChanged += ViewportWheelChanged;

        interactiveHost.Content = viewport;
        layout.Children.Add(interactiveHost);

        InitializeDisabledBanner();
        layout.Children.Add(disabledBanner);

        pendingBanner.Background = Brush("CardBackgroundFillColorDefaultBrush");
        pendingBanner.BorderBrush = Brush("CardStrokeColorDefaultBrush");
        pendingBanner.BorderThickness = new Thickness(1);
        pendingBanner.Child = pendingContent;
        layout.Children.Add(pendingBanner);

        fullscreenButton.Content = fullscreenIcon;
        UpdateFullscreenButtonVisual();
        fullscreenButton.Click += (_, _) => SetFullscreen(!isFullscreen);
        layout.Children.Add(fullscreenButton);

        Content = layout;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { if (!started) { started = true; operation = RunAsync(() => LoadAsync()); } };
    }

    internal void BindFeedbackHost(IDashboardFeedbackHost host) => feedbackHost = host;

    public Task StopAsync()
    {
        if (stopping is { IsCompleted: false }) return stopping;
        if (stopped) return Task.CompletedTask;
        return stopping = StopCoreAsync();
    }

    private void InitializeDisabledBanner()
    {
        disabledBanner.Background = Brush("CardBackgroundFillColorDefaultBrush");
        disabledBanner.BorderBrush = Brush("CardStrokeColorDefaultBrush");
        disabledBanner.BorderThickness = new Thickness(1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock
        {
            Text = L10n.Get("Cp_DisabledBanner"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        var enableBtn = new Button
        {
            Content = L10n.Get("Cp_EnableAction"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12
        };
        enableBtn.Click += (_, _) =>
        {
            if (busy || stopped || !stateAvailable) return;
            operation = RunAsync(() => MutateAsync(() => VmCheckpointService.SetCheckpointsEnabledAsync(vmId, true), selected?.Id));
        };
        row.Children.Add(enableBtn);
        disabledBanner.Child = row;
    }

    private async Task StopCoreAsync()
    {
        stopFailures.Clear();
        stopped = true;
        SetFullscreen(false);
        CloseRenameEditor();
        dialog?.Hide();
        await operation;
        if (stopFailures.Count == 0) return;

        string failure = string.Join(Environment.NewLine, stopFailures);
        stopFailures.Clear();
        stopped = false;
        IsEnabled = true;
        if (stateAvailable) { DrawTree(); RefreshOverlays(); }
        ShowError(failure + L10n.Get("SettingsView_PageRetained"));
        throw new InvalidOperationException(failure);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && isFullscreen && dialog == null && renameEditor == null)
        {
            SetFullscreen(false);
            args.Handled = true;
        }
    }

    private void SetFullscreen(bool value)
    {
        if (isFullscreen == value) return;
        isFullscreen = value;
        UpdateFullscreenButtonVisual();
        FullscreenChanged?.Invoke(value);
    }

    private void UpdateFullscreenButtonVisual()
    {
        fullscreenIcon.Glyph = isFullscreen ? "\uE73F" : "\uE740";
        string tip = L10n.Get(isFullscreen ? "Cp_FullscreenExit" : "Cp_FullscreenEnter");
        ToolTipService.SetToolTip(fullscreenButton, tip);
        AutomationProperties.SetName(fullscreenButton, tip);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || stopped) return;
        busy = true;
        IsEnabled = false;
        BusyChanged?.Invoke(true);
        standaloneNotice?.Hide();
        feedbackHost?.ClearSettingsNotice();
        try { await action(); }
        catch (Exception ex) { ShowError(CoreMessageResolver.ResolveException(ex)); }
        finally { busy = false; IsEnabled = !stopped; BusyChanged?.Invoke(false); }
    }

    private async Task LoadAsync(string? selectedId = null)
    {
        stateAvailable = false;
        interactiveHost.IsEnabled = false;
        var enabled = await VmCheckpointService.GetCheckpointsEnabledAsync(vmId);
        if (!enabled.Success || !enabled.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(enabled));
        var response = await VmCheckpointService.GetCheckpointNodesAsync(vmId);
        if (!response.Success || !response.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(response));
        if (stopped) return;

        checkpointsEnabled = enabled.Data;
        nodes = response.Data!;
        if (!nodes.Any(node => node.NodeType == CheckpointNodeType.Snapshot))
        {
            var origin = nodes.FirstOrDefault(node => node.NodeType == CheckpointNodeType.Origin);
            var current = nodes.FirstOrDefault(node => node.NodeType == CheckpointNodeType.Current);
            if (origin != null && current != null)
            {
                current.CreatedDate = origin.CreatedDate;
                current.ParentId = null;
                nodes.Remove(origin);
            }
        }

        selected = nodes.FirstOrDefault(node => node.Id == selectedId)
            ?? nodes.FirstOrDefault(node => node.NodeType == CheckpointNodeType.Current)
            ?? nodes.FirstOrDefault();

        stateAvailable = true;
        DrawTree();
        RefreshOverlays();
        interactiveHost.IsEnabled = true;
        if (!hasUserTransform) DispatcherQueue.TryEnqueue(CenterView);
    }

    private async Task MutateAsync(Func<Task<ApiResponse>> mutation, string? selectedId = null, int settleMilliseconds = 0, CheckpointNode? mount = null)
    {
        if (PendingWrites.ContainsKey(vmId) || stopped) return;
        var result = await mutation();
        if (result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired)
        {
            var detail = string.Join(Environment.NewLine, new[]
            {
                L10n.Get("Cp_Unresolved"),
                result.Error,
                string.Join(Environment.NewLine, result.Steps.Select(step => $"{step.Name} ({step.State}) {step.ResourceId}"))
            });
            PendingWrites[vmId] = new(detail, result.ErrorKind, mount);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (!result.Success)
        {
            if (!stopped) { DrawTree(); RefreshOverlays(); }
            ShowError(CoreMessageResolver.ResolveError(result));
            return;
        }
        if (settleMilliseconds > 0) await Task.Delay(settleMilliseconds);
        if (!stopped) await LoadAsync(selectedId);
    }

    private void RefreshOverlays()
    {
        if (PendingWrites.TryGetValue(vmId, out var pending))
        {
            disabledBanner.Visibility = Visibility.Collapsed;
            pendingContent.Children.Clear();
            pendingContent.Children.Add(new TextBlock { Text = pending.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (pending is { Kind: ApiErrorKind.CleanupRequired, Mount: { IsHistoricalDiskMounted: true } mount })
            {
                var cleanup = new Button { Content = L10n.Get("Cp_CleanupMount") };
                cleanup.Click += (_, _) => operation = RunAsync(() => RetryMountCleanupAsync(pending, mount));
                btnRow.Children.Add(cleanup);
            }
            var recheck = new Button { Content = L10n.Get("Cp_Recheck") };
            recheck.Click += (_, _) => operation = RunAsync(() => VerifyPendingAsync(pending));
            btnRow.Children.Add(recheck);
            pendingContent.Children.Add(btnRow);
            pendingBanner.Visibility = Visibility.Visible;
            return;
        }

        pendingBanner.Visibility = Visibility.Collapsed;
        disabledBanner.Visibility = stateAvailable && !checkpointsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RetryMountCleanupAsync(PendingWrite pending, CheckpointNode mount)
    {
        if (PendingWrites.GetValueOrDefault(vmId) != pending) return;
        var result = await VmCheckpointService.DismountHistoricalDiskAsync(vmId, mount);
        if (!result.Success)
        {
            PendingWrites[vmId] = pending with
            {
                Kind = result.ErrorKind,
                Detail = L10n.Get("Cp_Unresolved") + "\n" + CoreMessageResolver.ResolveError(result)
            };
            if (!stopped) RefreshOverlays();
            return;
        }
        PendingWrites.Remove(vmId);
        Changed?.Invoke(this, EventArgs.Empty);
        if (!stopped) await LoadAsync(mount.Id);
    }

    private async Task VerifyPendingAsync(PendingWrite pending)
    {
        if (!await ConfirmAsync(L10n.Get("Cp_Recheck"), pending.Detail, L10n.Get("Cp_Verified"))) return;
        await LoadAsync(selected?.Id);
        if (stopped || !stateAvailable || PendingWrites.GetValueOrDefault(vmId) != pending) return;
        PendingWrites.Remove(vmId);
        RefreshOverlays();
        DrawTree();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string accept)
    {
        if (stopped || XamlRoot == null) return false;
        dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            PrimaryButtonText = accept,
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
            },
            CloseButtonText = L10n.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && !stopped; }
        finally { dialog = null; }
    }

    private static List<CheckpointNode> Descendants(IEnumerable<CheckpointNode> tree, string root)
    {
        var children = tree.Where(n => n.ParentId != null).ToLookup(n => n.ParentId!);
        var visited = new HashSet<string> { root };
        var queue = new Queue<string>();
        queue.Enqueue(root);
        var result = new List<CheckpointNode>();
        while (queue.TryDequeue(out var parent))
            foreach (var child in children[parent])
                if (visited.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                    if (!child.IsLogicalNode) result.Add(child);
                }
        return result;
    }

    private async Task ConfirmHistoricalChangeAsync(CheckpointNode node, string action)
    {
        if (PendingWrites.ContainsKey(vmId)) return;
        await LoadAsync(node.Id);
        if (stopped || selected?.Id != node.Id || PendingWrites.ContainsKey(vmId)) return;
        node = selected;
        if (action == "Cp_Merge" && !VmCheckpointService.CanMergeHistoricalNode(node, nodes)) return;
        if (action == "Cp_Delete" && !VmCheckpointService.CanMutateHistoricalNode(node, nodes)) return;

        var affected = Descendants(nodes, node.Id);
        if (action == "Cp_Delete")
        {
            var names = string.Join(L10n.Get("Common_ListSeparator"), affected.Take(4).Select(n => n.Name));
            if (affected.Count > 4) names += " ...";
            string body = affected.Count > 0
                ? L10n.Format("Cp_DeleteBranchBody", node.Name, affected.Count, names)
                : L10n.Format("Cp_DeleteLeafBody", node.Name);
            if (!await ConfirmAsync(L10n.Get(action), body, L10n.Get("Cp_MenuDelete"))) return;
        }

        if (action is "Cp_Delete" or "Cp_Merge")
        {
            await LoadAsync(node.Id);
            if (stopped) return;
            if (selected?.Id != node.Id || !affected.Select(n => n.Id).ToHashSet().SetEquals(Descendants(nodes, node.Id).Select(n => n.Id)))
            {
                ShowError(L10n.Get("Cp_Changed"));
                return;
            }
            node = selected;
            if (action == "Cp_Merge" && !VmCheckpointService.CanMergeHistoricalNode(node, nodes))
            {
                ShowError(L10n.Get("Cp_Changed"));
                return;
            }
        }

        string? nextSelectedId = action == "Cp_Merge" ? node.Id : null;
        await MutateAsync(() => action switch
        {
            "Cp_Delete" => VmCheckpointService.DeleteSnapshotAsync(vmId, node),
            "Cp_Merge" => VmCheckpointService.MergeSnapshotAsync(vmId, node),
            _ => VmCheckpointService.ApplySnapshotAsync(node, vmId, nodes)
        }, nextSelectedId);
    }

    private void ShowRenameEditor(CheckpointNode node)
    {
        if (busy || stopped || PendingWrites.ContainsKey(vmId) || !cardVisuals.TryGetValue(node.Id, out var card)) return;
        var input = new TextBox { Text = node.Name, Width = 168 };
        AutomationProperties.SetName(input, L10n.Get("Cp_RenameLabel"));
        var save = new Button { Content = L10n.Get("Cp_SaveAction") };
        var cancel = new Button { Content = L10n.Get("Common_Cancel") };
        CloseRenameEditor();
        var editor = renameEditor = new Border
        {
            Padding = new Thickness(6), CornerRadius = new CornerRadius(6),
            Background = Brush("LayerFillColorDefaultBrush"),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { input, save, cancel } }
        };
        void Commit()
        {
            string name = input.Text.Trim();
            if (name.Length == 0) return;
            CloseRenameEditor();
            if (!stopped && !busy && !PendingWrites.ContainsKey(vmId) && name != node.Name)
                operation = RunAsync(() => MutateAsync(() => VmCheckpointService.RenameSnapshotAsync(vmId, node, name), node.Id));
        }
        save.Click += (_, _) => Commit();
        cancel.Click += (_, _) => CloseRenameEditor();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; CloseRenameEditor(); }
        };
        Canvas.SetLeft(editor, Canvas.GetLeft(card.Host));
        Canvas.SetTop(editor, Canvas.GetTop(card.Host) + CardHeight + 4);
        Canvas.SetZIndex(editor, 200);
        canvas.Children.Add(editor);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (renameEditor == editor) { input.Focus(FocusState.Programmatic); input.SelectAll(); }
        });
    }

    private void CloseRenameEditor()
    {
        if (renameEditor == null) return;
        canvas.Children.Remove(renameEditor);
        renameEditor = null;
    }

    private void DrawTree()
    {
        CloseRenameEditor();
        canvas.Children.Clear();
        positions.Clear();
        cardVisuals.Clear();

        var children = nodes.Where(n => n.ParentId != null)
            .GroupBy(n => n.ParentId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.CreatedDate).ToList());
        var root = nodes.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId)) ?? nodes.FirstOrDefault();
        if (root == null) return;

        var byId = nodes.ToDictionary(n => n.Id, n => n);
        var currentNode = nodes.FirstOrDefault(n => n.NodeType == CheckpointNodeType.Current);
        var activeChainIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = currentNode;
        for (int guard = 0; cursor != null && guard < 256; guard++)
        {
            if (!activeChainIds.Add(cursor.Id)) break;
            cursor = cursor.ParentId != null && byId.TryGetValue(cursor.ParentId, out var p) ? p : null;
        }

        var leaves = new Dictionary<string, int>();
        var visiting = new HashSet<string>();
        int Count(CheckpointNode node)
        {
            if (!visiting.Add(node.Id)) throw new InvalidOperationException(L10n.Get("Cp_CyclicTree"));
            var count = children.TryGetValue(node.Id, out var list) && list.Count > 0 ? list.Sum(Count) : 1;
            visiting.Remove(node.Id);
            return leaves[node.Id] = count;
        }
        var total = Count(root);
        int Depth(CheckpointNode node) => children.TryGetValue(node.Id, out var list) && list.Count > 0 ? 1 + list.Max(Depth) : 0;

        double spanY = total * PitchY;
        canvas.Width = Math.Max(1200, Depth(root) * PitchX + 360);
        canvas.Height = Math.Max(680, spanY + 240);

        double startX = 140;
        double topY = (canvas.Height - spanY) / 2;
        Draw(root, startX, topY, topY + spanY);

        if (positions.TryGetValue(CheckpointNode.CurrentId, out var currentPos))
            foreach (var node in nodes.Where(n => n.IsHistoricalDiskMounted))
                if (positions.TryGetValue(node.Id, out var point)) AddBezierEdge(point, currentPos, isActive: false, isHistoricalDiskMounted: true);

        void Draw(CheckpointNode node, double x, double start, double end)
        {
            var point = new Point(x, (start + end) / 2);
            positions[node.Id] = point;
            AddNodeCard(node, point);
            if (!children.TryGetValue(node.Id, out var list)) return;
            var offset = start;
            foreach (var child in list)
            {
                var sector = leaves[child.Id] * (end - start) / leaves[node.Id];
                var childPoint = new Point(x + PitchX, offset + sector / 2);
                bool activeEdge = activeChainIds.Contains(node.Id) && activeChainIds.Contains(child.Id);
                AddBezierEdge(point, childPoint, isActive: activeEdge, isHistoricalDiskMounted: false);
                Draw(child, childPoint.X, offset, offset + sector);
                offset += sector;
            }
        }
    }

    private void AddNodeCard(CheckpointNode node, Point point)
    {
        bool isSelected = selected?.Id == node.Id;
        bool isCurrent = node.NodeType == CheckpointNodeType.Current;

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var artLayer = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 20, 26)) };
        artLayer.Children.Add(new Image
        {
            Source = GetBrandMatteBitmap(),
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false
        });
        artLayer.Children.Add(new Image
        {
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")) { DecodePixelWidth = 64 }
        });

        if (isCurrent)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 156, 84)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(8, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = L10n.Get("Cp_BadgeCurrent"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White)
                }
            };
            artLayer.Children.Add(badge);
        }

        cardGrid.Children.Add(artLayer);

        var infoPanel = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        infoPanel.Children.Add(new TextBlock
        {
            Text = node.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap
        });
        infoPanel.Children.Add(new TextBlock
        {
            Text = node.CreatedDate.ToString("MM-dd HH:mm"),
            FontSize = 10.5,
            Opacity = 0.6
        });

        var infoBar = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            Child = infoPanel
        };
        Grid.SetRow(infoBar, 1);
        cardGrid.Children.Add(infoBar);

        var cardBorder = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(8),
            BorderBrush = ResolveCardBorderBrush(isSelected, isCurrent, node.IsHistoricalDiskMounted),
            BorderThickness = new Thickness(isCurrent || node.IsHistoricalDiskMounted ? 2 : isSelected ? 1.5 : 1),
            Child = cardGrid
        };

        var button = new Button
        {
            Content = cardBorder,
            Width = CardWidth,
            Height = CardHeight,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetName(button, node.Name);
        if (node.Name.Length > 18) ToolTipService.SetToolTip(button, node.Name);

        button.Click += (_, _) =>
        {
            if (dragging || busy || stopped) return;
            SelectNodeInPlace(node);
        };

        if (isCurrent || VmCheckpointService.CanOperateHistoricalNode(node))
        {
            button.ContextFlyout = BuildNodeContextMenu(node);
        }

        Canvas.SetLeft(button, point.X - CardWidth / 2);
        Canvas.SetTop(button, point.Y - CardHeight / 2);
        Canvas.SetZIndex(button, isSelected ? 100 : isCurrent ? 80 : 50);
        canvas.Children.Add(button);
        cardVisuals[node.Id] = (cardBorder, button, isCurrent, node.IsHistoricalDiskMounted);
    }

    private void SelectNodeInPlace(CheckpointNode node)
    {
        if (selected?.Id == node.Id) return;
        CloseRenameEditor();
        selected = node;
        foreach (var entry in cardVisuals)
        {
            bool isSel = string.Equals(entry.Key, selected.Id, StringComparison.OrdinalIgnoreCase);
            var (card, host, isCur, isMounted) = entry.Value;
            card.BorderBrush = ResolveCardBorderBrush(isSel, isCur, isMounted);
            card.BorderThickness = new Thickness(isCur || isMounted ? 2 : isSel ? 1.5 : 1);
            Canvas.SetZIndex(host, isSel ? 100 : isCur ? 80 : 50);
        }
    }

    private static Brush ResolveCardBorderBrush(bool isSelected, bool isCurrent, bool isHistoricalDiskMounted)
    {
        if (isHistoricalDiskMounted) return new SolidColorBrush(Colors.Gold);
        if (isCurrent) return Brush("VmAccentBrush");
        if (isSelected) return Brush("TextFillColorSecondaryBrush");
        return Brush("CardStrokeColorDefaultBrush");
    }

    private MenuFlyout BuildNodeContextMenu(CheckpointNode node)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            if (!busy && !stopped) SelectNodeInPlace(node);
        };

        bool blockedByPending = PendingWrites.ContainsKey(vmId);

        if (node.NodeType == CheckpointNodeType.Current)
        {
            var createItem = new MenuFlyoutItem
            {
                Text = L10n.Get("Cp_MenuCreate"),
                Icon = new FontIcon { FontFamily = AppFonts.SegoeFluent, Glyph = "\uE710" },
                IsEnabled = !blockedByPending && VmCheckpointService.CanCreateSnapshot(checkpointsEnabled, nodes)
            };
            createItem.Click += (_, _) => operation = RunAsync(() =>
                MutateAsync(() => VmCheckpointService.CreateSnapshotAsync(vmId), settleMilliseconds: 1000));
            menu.Items.Add(createItem);
            return menu;
        }

        bool canMutate = !blockedByPending && VmCheckpointService.CanMutateHistoricalNode(node, nodes);
        bool canMerge = !blockedByPending && VmCheckpointService.CanMergeHistoricalNode(node, nodes);

        var applyItem = new MenuFlyoutItem
        {
            Text = L10n.Get("Cp_MenuApply"),
            Icon = new FontIcon { FontFamily = AppFonts.SegoeFluent, Glyph = "\uE81C" },
            IsEnabled = !blockedByPending
        };
        applyItem.Click += (_, _) => { selected = node; operation = RunAsync(() => ConfirmHistoricalChangeAsync(node, "Cp_Restore")); };
        menu.Items.Add(applyItem);

        var renameItem = new MenuFlyoutItem
        {
            Text = L10n.Get("Cp_MenuRename"),
            Icon = new FontIcon { FontFamily = AppFonts.SegoeFluent, Glyph = "\uE70F" },
            IsEnabled = !blockedByPending
        };
        renameItem.Click += (_, _) => { selected = node; ShowRenameEditor(node); };
        menu.Items.Add(renameItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var mergeItem = new MenuFlyoutItem
        {
            Text = L10n.Get("Cp_MenuMerge"),
            Icon = CreateMergeMenuIcon(),
            IsEnabled = canMerge
        };
        mergeItem.Click += (_, _) => { selected = node; operation = RunAsync(() => ConfirmHistoricalChangeAsync(node, "Cp_Merge")); };
        menu.Items.Add(mergeItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = L10n.Get("Cp_MenuDelete"),
            Icon = new FontIcon { FontFamily = AppFonts.SegoeFluent, Glyph = "\uE74D" },
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 83, 73)),
            IsEnabled = canMutate
        };
        deleteItem.Click += (_, _) => { selected = node; operation = RunAsync(() => ConfirmHistoricalChangeAsync(node, "Cp_Delete")); };
        menu.Items.Add(deleteItem);

        return menu;
    }

    private static IconElement CreateMergeMenuIcon()
        => (PathIcon)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<PathIcon xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Width=\"16\" Height=\"16\" Data=\"{MergeMenuIconPathData}\" />");

    private void AddBezierEdge(Point from, Point to, bool isActive, bool isHistoricalDiskMounted)
    {
        double startX = isHistoricalDiskMounted ? from.X : from.X + CardWidth / 2;
        double startY = from.Y;
        double endX = isHistoricalDiskMounted ? to.X : to.X - CardWidth / 2;
        double endY = to.Y;
        double dx = Math.Max(40, Math.Abs(endX - startX));

        var segment = new BezierSegment
        {
            Point1 = new Point(startX + dx * 0.45, startY),
            Point2 = new Point(endX - dx * 0.45, endY),
            Point3 = new Point(endX, endY)
        };
        var figure = new PathFigure { StartPoint = new Point(startX, startY), IsClosed = false, IsFilled = false };
        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = isHistoricalDiskMounted ? new SolidColorBrush(Colors.Gold) : isActive ? Brush("VmAccentBrush") : Brush("TextFillColorTertiaryBrush"),
            StrokeThickness = isHistoricalDiskMounted ? 3 : isActive ? 2 : 1.5,
            Opacity = isHistoricalDiskMounted ? 0.95 : isActive ? 1.0 : 0.7,
            IsHitTestVisible = false
        };
        if (!isActive || isHistoricalDiskMounted)
            path.StrokeDashArray = new DoubleCollection { 4, 3 };

        Canvas.SetZIndex(path, isHistoricalDiskMounted ? 10 : isActive ? 6 : 5);
        canvas.Children.Add(path);
    }

    private void CenterView()
    {
        if (stopped || positions.Count == 0 || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;

        double minX = positions.Values.Min(p => p.X) - CardWidth / 2;
        double maxX = positions.Values.Max(p => p.X) + CardWidth / 2;
        double minY = positions.Values.Min(p => p.Y) - CardHeight / 2;
        double maxY = positions.Values.Max(p => p.Y) + CardHeight / 2;

        double boundsWidth = Math.Max(1, maxX - minX);
        double boundsHeight = Math.Max(1, maxY - minY);
        double fitZoom = Math.Min((viewport.ActualWidth - 48) / boundsWidth, (viewport.ActualHeight - 48) / boundsHeight);
        double targetZoom = boundsWidth <= viewport.ActualWidth - 48 && boundsHeight <= viewport.ActualHeight - 48
            ? 1.0
            : Math.Round(Math.Clamp(fitZoom, 0.72, 1.0), 2);

        double centerX = (minX + maxX) / 2;
        double centerY = (minY + maxY) / 2;
        double focusX = boundsWidth * targetZoom <= viewport.ActualWidth - 32 || selected == null || !positions.TryGetValue(selected.Id, out var selPos)
            ? centerX
            : selPos.X;

        zoom = targetZoom;
        viewportTransform.ScaleX = targetZoom;
        viewportTransform.ScaleY = targetZoom;
        viewportTransform.TranslateX = viewport.ActualWidth / 2 - focusX * targetZoom;
        viewportTransform.TranslateY = viewport.ActualHeight / 2 - centerY * targetZoom;
    }

    private void PanPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(viewport).Properties.IsLeftButtonPressed) return;
        pointerPosition = args.GetCurrentPoint(viewport).Position;
        dragging = false;
    }

    private void PanMoved(object sender, PointerRoutedEventArgs args)
    {
        if (pointerPosition is not { } previous || !args.GetCurrentPoint(viewport).Properties.IsLeftButtonPressed) return;
        var current = args.GetCurrentPoint(viewport).Position;
        double dx = current.X - previous.X;
        double dy = current.Y - previous.Y;
        if (!dragging && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        dragging = true;
        hasUserTransform = true;
        viewport.CapturePointer(args.Pointer);
        viewportTransform.TranslateX += dx;
        viewportTransform.TranslateY += dy;
        pointerPosition = current;
        args.Handled = true;
    }

    private void PanReleased(object sender, PointerRoutedEventArgs args)
    {
        pointerPosition = null;
        viewport.ReleasePointerCapture(args.Pointer);
        if (dragging) { args.Handled = true; dragging = false; }
    }

    private void ViewportWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(viewport);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        double step = delta > 0 ? 0.1 : -0.1;
        double nextZoom = Math.Round(Math.Clamp(zoom + step, MinZoom, MaxZoom), 2);
        if (Math.Abs(nextZoom - zoom) < 0.001) return;

        double ratio = nextZoom / zoom;
        double mx = point.Position.X;
        double my = point.Position.Y;
        viewportTransform.TranslateX = mx - (mx - viewportTransform.TranslateX) * ratio;
        viewportTransform.TranslateY = my - (my - viewportTransform.TranslateY) * ratio;
        viewportTransform.ScaleX = nextZoom;
        viewportTransform.ScaleY = nextZoom;
        zoom = nextZoom;
        hasUserTransform = true;
        args.Handled = true;
    }

    private static WriteableBitmap GetBrandMatteBitmap()
    {
        if (sharedBrandMatte != null) return sharedBrandMatte;

        const int w = 352;
        const int h = 136;
        var bitmap = new WriteableBitmap(w, h);
        byte[] bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double dx = (x / (double)w - 0.5) / 0.58;
                double dy = (y / (double)h - 0.5) / 0.72;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double halo = Math.Pow(Math.Max(0.0, 1.0 - Math.Min(1.0, dist)), 1.6);

                byte r = (byte)Math.Round(17 + halo * 9);
                byte g = (byte)Math.Round(20 + halo * 22);
                byte b = (byte)Math.Round(26 + halo * 44);

                if (x % 14 == 0 && y % 14 == 0)
                {
                    r = (byte)Math.Min(255, r + 14);
                    g = (byte)Math.Min(255, g + 16);
                    b = (byte)Math.Min(255, b + 20);
                }

                int idx = (y * w + x) * 4;
                bgra[idx] = b;
                bgra[idx + 1] = g;
                bgra[idx + 2] = r;
                bgra[idx + 3] = 255;
            }
        }
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(bgra, 0, bgra.Length);
        bitmap.Invalidate();
        sharedBrandMatte = bitmap;
        return bitmap;
    }

    private void ShowError(string error)
    {
        string detail = string.IsNullOrWhiteSpace(error) ? L10n.Get("Cp_ReadWriteFailed") : error;
        if (stopped)
        {
            if (!stopFailures.Contains(detail)) stopFailures.Add(detail);
            return;
        }
        Button? retryButton = null;
        if (!stateAvailable)
        {
            retryButton = new Button { Content = L10n.Get("Common_ReadAgain"), Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
            retryButton.Click += (_, _) => operation = RunAsync(() => LoadAsync(selected?.Id));
        }
        if (feedbackHost != null)
        {
            feedbackHost.ShowSettingsNotice(detail, InfoBarSeverity.Error, retryButton);
            return;
        }
        standaloneNotice ??= new FloatingNoticeBanner
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(8, 0, 8, 8)
        };
        if (!layout.Children.Contains(standaloneNotice)) layout.Children.Add(standaloneNotice);
        standaloneNotice.Show(detail, InfoBarSeverity.Error, retryButton);
    }

    private static Brush Brush(string key) => ThemeHelper.ResourceBrush(key);
}
