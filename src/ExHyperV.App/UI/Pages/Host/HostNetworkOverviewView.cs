using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostNetworkOverviewView : UserControl
{
    private enum EdgeKind { Bridge, Nat, Host, VmLive, VmOff }

    private sealed record NicNodeData(
        string NodeId,
        SwitchUpstreamAdapter Adapter,
        string Glyph,
        string MediaLabel,
        string Ipv4Text,
        string RateText,
        double X,
        double Y,
        double W,
        double H);

    private sealed record SwitchNodeData(
        string NodeId,
        SwitchInfo Switch,
        string ModeLabel,
        Color ModeColor,
        string UpstreamText,
        string MetaText,
        string RateText,
        double X,
        double Y,
        double W,
        double H);

    private sealed record RightPortData(
        string PortId,
        string SwitchName,
        string PortName,
        string RightText,
        string EdgeRateText,
        bool Active,
        double AnchorY);

    private sealed record RightNodeData(
        string NodeId,
        bool IsHost,
        string Name,
        bool Running,
        Color Color,
        double X,
        double Y,
        double W,
        double H,
        IReadOnlyList<RightPortData> Ports);

    private sealed record EdgeData(
        string EdgeId,
        string FromId,
        string ToId,
        double X1,
        double Y1,
        double X2,
        double Y2,
        EdgeKind Kind,
        Color Color,
        string? PillText);

    private sealed class EdgeVisualRef
    {
        public required string EdgeId { get; init; }
        public required string FromId { get; init; }
        public required string ToId { get; init; }
        public required EdgeKind Kind { get; init; }
        public required Color Color { get; init; }
        public required ShapePath Wire { get; init; }
        public required Ellipse DotStart { get; init; }
        public required Ellipse DotEnd { get; init; }
        public Border? PillBorder { get; init; }
        public TextBlock? PillLabel { get; init; }
    }

    private sealed class NodeVisualRef
    {
        public required string NodeId { get; init; }
        public required Color AccentColor { get; init; }
        public required bool DimWhenOff { get; init; }
        public required Border Card { get; init; }
    }

    private const double ColW = 252;
    private const double Col1X = 32;
    private const double Col2X = 440;
    private const double Col3X = 848;
    private const double TopPad = 24;
    private const double BottomPad = 24;
    private const double NicH = 88;
    private const double NicGap = 14;
    private const double SwH = 92;
    private const double SwGap = 18;
    private const double VmGap = 14;
    private const double CanvasW = 1132;
    private const double MinZoom = 0.45;
    private const double MaxZoom = 1.85;

    private readonly Grid stageGrid = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Canvas panCanvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        RenderTransformOrigin = new Point(0, 0)
    };
    private readonly CompositeTransform panTransform = new();
    private readonly Border controlsBar = new()
    {
        Height = 34,
        Padding = new Thickness(3, 0, 3, 0),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 14, 14)
    };
    private readonly Button zoomOutBtn = CreateCtrlButton("\uE738", L10n.Get("Host_Net_ZoomOut"), width: 24, fontSize: 11.5);
    private readonly Button zoomResetBtn = new()
    {
        Height = 28,
        MinWidth = 36,
        Padding = new Thickness(3, 0, 3, 0),
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(0),
        FontSize = 11.5,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button zoomInBtn = CreateCtrlButton("\uE710", L10n.Get("Host_Net_ZoomIn"), width: 24, fontSize: 11.5);
    private readonly Border ctrlSep = new() { Width = 1, Height = 16, Margin = new Thickness(2, 0, 2, 0) };
    private readonly Button fullscreenBtn = CreateCtrlButton("\uE740", L10n.Get("Host_Net_FullscreenEnter"), width: 28, fontSize: 14);
    private readonly FontIcon fullscreenIcon = new()
    {
        FontFamily = AppFonts.SegoeFluent,
        Glyph = "\uE740",
        FontSize = 14
    };

    private HostInventorySnapshot? inventory;
    private HostLiveSample? lastSample;
    private readonly Dictionary<Guid, int> vmColorSlots = new();
    private int nextColorSlot;

    private readonly List<EdgeVisualRef> edgeVisuals = [];
    private readonly List<NodeVisualRef> nodeVisuals = [];
    private readonly Dictionary<string, TextBlock> liveRateLabels = new(StringComparer.Ordinal);
    private readonly List<EdgeData> currentEdges = [];
    private readonly HashSet<string> allNodeIds = new(StringComparer.Ordinal);

    private string? hoveredNodeId;
    private string? hoverSuppressedNodeId;
    private string? selectedNodeId;
    private bool autoFit = true;
    private bool isFullscreen;
    private double canvasH = 420;
    private double contentMinX = Col1X;
    private double contentMaxX = Col3X + ColW;
    private double contentMinY = TopPad;
    private double contentMaxY = 380;
    private double lastStageW;
    private double lastStageH;
    private double zoom = 1.0;
    private double panX;
    private double panY;
    private uint? dragPointerId;
    private Point dragStart;
    private Point panStart;
    private bool dragged;
    private string lastStructKey = string.Empty;

    internal event Action<bool>? FullscreenChanged;

    public HostNetworkOverviewView()
    {
        panCanvas.RenderTransform = panTransform;
        stageGrid.Children.Add(panCanvas);

        fullscreenBtn.Content = fullscreenIcon;
        ToolTipService.SetToolTip(zoomResetBtn, L10n.Get("Host_Net_ZoomReset"));
        AutomationProperties.SetName(zoomResetBtn, L10n.Get("Host_Net_ZoomReset"));
        Typography.SetNumeralAlignment(zoomResetBtn, FontNumeralAlignment.Tabular);

        var ctrlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        ctrlRow.Children.Add(zoomOutBtn);
        ctrlRow.Children.Add(zoomResetBtn);
        ctrlRow.Children.Add(zoomInBtn);
        ctrlRow.Children.Add(ctrlSep);
        ctrlRow.Children.Add(fullscreenBtn);
        controlsBar.Child = ctrlRow;
        stageGrid.Children.Add(controlsBar);

        Content = stageGrid;

        zoomOutBtn.Click += (_, _) => ZoomStep(-0.15);
        zoomInBtn.Click += (_, _) => ZoomStep(0.15);
        zoomResetBtn.Click += (_, _) => { autoFit = true; ApplyAutoFit(); };
        fullscreenBtn.Click += (_, _) => SetFullscreen(!isFullscreen);

        stageGrid.SizeChanged += (_, args) =>
        {
            double newW = args.NewSize.Width;
            double newH = args.NewSize.Height;
            if (newW > 0 && newH > 0)
                stageGrid.Clip = new RectangleGeometry { Rect = new Rect(0, 0, newW, newH) };
            if (autoFit)
            {
                ApplyAutoFit();
            }
            else if (lastStageW > 0 && lastStageH > 0 && newW > 0 && newH > 0)
            {
                panX += (newW - lastStageW) / 2.0;
                panY += (newH - lastStageH) / 2.0;
                SyncViewportTransform();
            }
            lastStageW = newW;
            lastStageH = newH;
        };
        stageGrid.PointerPressed += OnStagePointerPressed;
        stageGrid.PointerMoved += OnStagePointerMoved;
        stageGrid.PointerReleased += OnStagePointerReleased;
        stageGrid.PointerCanceled += OnStagePointerReleased;
        stageGrid.PointerWheelChanged += OnStagePointerWheelChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        ActualThemeChanged += (_, _) => Rebuild(forceStructure: true);

        UpdateControlsTheme();
        UpdateFullscreenVisual();
    }

    public bool IsFullscreen => isFullscreen;

    public void SetFullscreen(bool value)
    {
        if (isFullscreen == value) return;
        isFullscreen = value;
        autoFit = true;
        UpdateFullscreenVisual();
        FullscreenChanged?.Invoke(value);
        ApplyAutoFit();
    }

    public void UpdateTelemetry(HostInventorySnapshot? snapshot, HostLiveSample? sample)
    {
        bool invChanged = !ReferenceEquals(inventory, snapshot);
        inventory = snapshot ?? inventory;
        if (sample != null) lastSample = sample;
        Rebuild(forceStructure: invChanged);
    }

    public void RefreshTheme()
    {
        UpdateControlsTheme();
        Rebuild(forceStructure: true);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && isFullscreen)
        {
            SetFullscreen(false);
            args.Handled = true;
        }
    }

    private void UpdateFullscreenVisual()
    {
        fullscreenIcon.Glyph = isFullscreen ? "\uE73F" : "\uE740";
        string tip = L10n.Get(isFullscreen ? "Host_Net_FullscreenExit" : "Host_Net_FullscreenEnter");
        ToolTipService.SetToolTip(fullscreenBtn, tip);
        AutomationProperties.SetName(fullscreenBtn, tip);
    }

    private void OnStagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(stageGrid);
        if (!pt.Properties.IsLeftButtonPressed) return;
        dragPointerId = pt.PointerId;
        dragStart = pt.Position;
        panStart = new Point(panX, panY);
        dragged = false;
        stageGrid.CapturePointer(e.Pointer);
    }

    private void OnStagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragPointerId is not uint id || e.Pointer.PointerId != id) return;
        Point cur = e.GetCurrentPoint(stageGrid).Position;
        double dx = cur.X - dragStart.X;
        double dy = cur.Y - dragStart.Y;
        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) dragged = true;
        if (!dragged) return;
        autoFit = false;
        panX = panStart.X + dx;
        panY = panStart.Y + dy;
        SyncViewportTransform();
    }

    private void OnStagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragPointerId is not uint id || e.Pointer.PointerId != id) return;
        dragPointerId = null;
        stageGrid.ReleasePointerCapture(e.Pointer);
        if (!dragged && (selectedNodeId != null || hoverSuppressedNodeId != null))
        {
            selectedNodeId = null;
            hoverSuppressedNodeId = null;
            ApplyFocusVisuals();
        }
    }

    private void OnStagePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(stageGrid);
        int delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) return;
        double step = delta > 0 ? 0.1 : -0.1;
        ZoomAt(pt.Position, step);
        e.Handled = true;
    }

    private void ZoomStep(double step) =>
        ZoomAt(new Point(stageGrid.ActualWidth / 2.0, stageGrid.ActualHeight / 2.0), step);

    private void ZoomAt(Point center, double step)
    {
        double nextZoom = Math.Round(Math.Clamp(zoom + step, MinZoom, MaxZoom), 2);
        if (Math.Abs(nextZoom - zoom) < 0.001) return;
        double ratio = nextZoom / zoom;
        autoFit = false;
        panX = center.X - (center.X - panX) * ratio;
        panY = center.Y - (center.Y - panY) * ratio;
        zoom = nextZoom;
        SyncViewportTransform();
    }

    private void ApplyAutoFit()
    {
        double sw = stageGrid.ActualWidth;
        double sh = stageGrid.ActualHeight;
        if (sw <= 60 || sh <= 60) return;
        double boundsW = Math.Max(1, contentMaxX - contentMinX);
        double boundsH = Math.Max(1, contentMaxY - contentMinY);
        double fit = Math.Min((sw - 48) / boundsW, (sh - 48) / boundsH);
        zoom = boundsW <= sw - 48 && boundsH <= sh - 48
            ? 1.0
            : Math.Floor(Math.Clamp(fit, MinZoom, 1.0) * 100.0) / 100.0;
        double centerX = (contentMinX + contentMaxX) / 2.0;
        double centerY = (contentMinY + contentMaxY) / 2.0;
        panX = Math.Round(sw / 2.0 - centerX * zoom);
        panY = Math.Round(sh / 2.0 - centerY * zoom);
        lastStageW = sw;
        lastStageH = sh;
        SyncViewportTransform();
    }

    private void SyncViewportTransform()
    {
        panTransform.ScaleX = zoom;
        panTransform.ScaleY = zoom;
        panTransform.TranslateX = panX;
        panTransform.TranslateY = panY;
        zoomResetBtn.Content = $"{Math.Round(zoom * 100):0}%";
    }

    private Color GetVmColor(Guid vmId, string name)
    {
        if (vmId == Guid.Empty)
        {
            int idx = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(name)) % HostUiHelpers.VmConsumerPalette.Length;
            return HostUiHelpers.GetVmPaletteColor(idx);
        }
        if (!vmColorSlots.TryGetValue(vmId, out int slot))
            vmColorSlots[vmId] = slot = nextColorSlot++;
        return HostUiHelpers.GetVmPaletteColor(slot);
    }

    private void Rebuild(bool forceStructure)
    {
        Color hostColor = HostUiHelpers.GetHostConsumerColor(ActualTheme);
        var inv = inventory;
        var adapters = (inv?.PhysicalAdapters ?? []).Where(a => a.LinkUp).ToList();
        var switches = inv?.Switches ?? [];
        var endpointsBySwitch = inv?.SwitchEndpoints ?? new Dictionary<string, IReadOnlyList<SwitchEndpointInfo>>();
        var vmNetworks = lastSample?.VmNetworks;

        // Gather all VMs connected to switches (combining live VmNetworks + static SwitchEndpoints fallback)
        var vmMap = new Dictionary<string, (Guid Id, string Name, bool Running, List<(string SwitchName, string PortName, string RightText, string EdgeRate, bool Active)> Ports)>(StringComparer.OrdinalIgnoreCase);

        if (vmNetworks != null)
        {
            foreach (var vm in vmNetworks)
            {
                var portList = new List<(string SwitchName, string PortName, string RightText, string EdgeRate, bool Active)>();
                foreach (var a in vm.Adapters)
                {
                    if (!a.IsConnected || string.IsNullOrWhiteSpace(a.SwitchName)) continue;
                    string compactRate = vm.IsRunning && a.SendBytesPerSec.HasValue && a.ReceiveBytesPerSec.HasValue
                        ? HostUiHelpers.FormatCompactNetRate(a.SendBytesPerSec.Value, a.ReceiveBytesPerSec.Value)
                        : string.Empty;
                    string rightText = vm.IsRunning
                        ? (!string.IsNullOrWhiteSpace(a.PrimaryIp) ? a.PrimaryIp! : (string.IsNullOrEmpty(compactRate) ? "---" : compactRate))
                        : L10n.Get("Host_Net_VmStopped");
                    portList.Add((a.SwitchName, a.Name, rightText, compactRate, vm.IsRunning));
                }
                if (portList.Count > 0)
                    vmMap[vm.Name] = (vm.VmId, vm.Name, vm.IsRunning, portList);
            }
        }

        foreach (var sw in switches)
        {
            if (!endpointsBySwitch.TryGetValue(sw.SwitchName, out var eps)) continue;
            foreach (var ep in eps)
            {
                if (ep.IsManagementOs || string.IsNullOrWhiteSpace(ep.Name)) continue;
                if (vmMap.ContainsKey(ep.Name)) continue;
                var list = new List<(string, string, string, string, bool)>
                {
                    (sw.SwitchName, L10n.Get("Host_Net_DefaultAdapter"), string.IsNullOrWhiteSpace(ep.IpAddress) ? "---" : ep.IpAddress, string.Empty, true)
                };
                vmMap[ep.Name] = (Guid.Empty, ep.Name, true, list);
            }
        }

        // Build Host OS Ports
        var hostPorts = switches
            .Where(s => s.AllowManagementOS)
            .Select(s =>
            {
                string ip = string.Empty;
                if (endpointsBySwitch.TryGetValue(s.SwitchName, out var eps))
                    ip = eps.FirstOrDefault(e => e.IsManagementOs && !string.IsNullOrWhiteSpace(e.IpAddress))?.IpAddress ?? string.Empty;
                return (
                    SwitchName: s.SwitchName,
                    PortName: $"vEthernet ({s.SwitchName})",
                    RightText: string.IsNullOrWhiteSpace(ip) ? L10n.Get("Host_Net_L2SharedOnly") : ip);
            })
            .ToList();

        // Compute vertical layout heights
        var rightPre = new List<(string NodeId, bool IsHost, string Name, bool Running, Color Color, double H, List<(string SwitchName, string PortName, string RightText, string EdgeRate, bool Active)> Ports)>
        {
            (
                "host-os",
                true,
                L10n.Get("Host_Net_HostOsNode"),
                true,
                hostColor,
                38 + Math.Max(1, hostPorts.Count) * 30 + 8,
                hostPorts.Select(p => (p.SwitchName, p.PortName, p.RightText, string.Empty, true)).ToList()
            )
        };
        foreach (var kv in vmMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var vm = kv.Value;
            double h = 38 + Math.Max(1, vm.Ports.Count) * 30 + 8;
            rightPre.Add(($"vm:{vm.Name}", false, vm.Name, vm.Running, GetVmColor(vm.Id, vm.Name), h, vm.Ports));
        }

        double col1Total = adapters.Count * NicH + Math.Max(0, adapters.Count - 1) * NicGap;
        double col2Total = switches.Count * SwH + Math.Max(0, switches.Count - 1) * SwGap;
        double col3Total = rightPre.Sum(r => r.H) + Math.Max(0, rightPre.Count - 1) * VmGap;
        double stageSpan = Math.Max(360, Math.Max(col1Total, Math.Max(col2Total, col3Total)));
        canvasH = TopPad + stageSpan + BottomPad;

        // Col 1: Physical NIC nodes
        var nicNodes = new List<NicNodeData>(adapters.Count);
        double curY = TopPad + Math.Round((stageSpan - col1Total) / 2.0);
        foreach (var nic in adapters)
        {
            string ip = inv != null && inv.AdapterAddresses.TryGetValue(nic.ConnectionId, out var addr) && !string.IsNullOrWhiteSpace(addr.IPv4)
                ? addr.IPv4
                : "---";
            string rate = lastSample != null && lastSample.Adapters.TryGetValue(nic.ConnectionId, out var netStat)
                ? HostUiHelpers.FormatCompactNetRate(netStat.BytesSentPerSec, netStat.BytesReceivedPerSec)
                : "---";
            string media = nic.MediaKind switch
            {
                AdapterMediaKind.Wired => L10n.Get("Host_Net_Media_WiredShort"),
                AdapterMediaKind.WiFi => L10n.Get("Host_Nic_Media_WiFi"),
                AdapterMediaKind.Cellular => L10n.Get("Host_Net_Media_CellularShort"),
                AdapterMediaKind.Tunnel => L10n.Get("Host_Net_Media_TunnelShort"),
                _ => L10n.Get("Host_Net_Media_OtherShort")
            };
            nicNodes.Add(new NicNodeData($"nic:{nic.ConnectionId:N}", nic, HostUiHelpers.MediaGlyph(nic.MediaKind), media, ip, rate, Col1X, curY, ColW, NicH));
            curY += NicH + NicGap;
        }

        // Col 2: Virtual Switch nodes
        var switchNodes = new List<SwitchNodeData>(switches.Count);
        curY = TopPad + Math.Round((stageSpan - col2Total) / 2.0);
        foreach (var sw in switches)
        {
            Color modeColor = sw.SwitchType switch
            {
                SwitchMode.Bridge => Color.FromArgb(255, 0x38, 0x98, 0xEC),
                SwitchMode.NAT => Color.FromArgb(255, 0x00, 0xB7, 0xC3),
                _ => Color.FromArgb(255, 0x9D, 0x5C, 0xE6)
            };
            string modeLabel = sw.SwitchType switch
            {
                SwitchMode.Bridge => L10n.Get("Host_Net_ModeBridge"),
                SwitchMode.NAT => L10n.Get(sw.NatEngine == SwitchNatEngine.HcnManaged ? "Host_Net_ModeNatHcn" : "Host_Net_ModeNat"),
                _ => L10n.Get("Host_Net_ModeIsolated")
            };
            string upstreamText = sw.Upstream?.Description is { Length: > 0 } up ? up : L10n.Get("Host_Net_NoUpstream");
            string? hostIp = null;
            if (endpointsBySwitch.TryGetValue(sw.SwitchName, out var eps))
                hostIp = eps.FirstOrDefault(e => e.IsManagementOs && !string.IsNullOrWhiteSpace(e.IpAddress))?.IpAddress;
            string metaText = !string.IsNullOrWhiteSpace(hostIp)
                ? hostIp!
                : (!string.IsNullOrWhiteSpace(sw.SubnetPrefix) ? sw.SubnetPrefix! : L10n.Get("Host_Net_SwitchL2"));
            string rate = lastSample != null && lastSample.Switches.TryGetValue(sw.SwitchName, out var swStat)
                ? HostUiHelpers.FormatCompactNetRate(swStat.BytesSentPerSec, swStat.BytesReceivedPerSec)
                : "---";
            switchNodes.Add(new SwitchNodeData($"sw:{sw.SwitchName}", sw, modeLabel, modeColor, upstreamText, metaText, rate, Col2X, curY, ColW, SwH));
            curY += SwH + SwGap;
        }

        // Col 3: Host + VMs
        var rightNodes = new List<RightNodeData>(rightPre.Count);
        curY = TopPad + Math.Round((stageSpan - col3Total) / 2.0);
        foreach (var entry in rightPre)
        {
            var builtPorts = new List<RightPortData>(entry.Ports.Count);
            for (int pIdx = 0; pIdx < entry.Ports.Count; pIdx++)
            {
                var p = entry.Ports[pIdx];
                double ay = curY + 38 + pIdx * 30 + 15;
                builtPorts.Add(new RightPortData($"port:{entry.NodeId}:{p.SwitchName}:{pIdx}", p.SwitchName, p.PortName, p.RightText, p.EdgeRate, p.Active, ay));
            }
            rightNodes.Add(new RightNodeData(entry.NodeId, entry.IsHost, entry.Name, entry.Running, entry.Color, Col3X, curY, ColW, entry.H, builtPorts));
            curY += entry.H + VmGap;
        }

        // Edges
        var edges = new List<EdgeData>();
        var primaryNic = nicNodes.FirstOrDefault(n => n.Adapter.MediaKind == AdapterMediaKind.Wired) ?? nicNodes.FirstOrDefault();

        foreach (var swNode in switchNodes)
        {
            var sw = swNode.Switch;
            double swLeftY = swNode.Y + Math.Round(swNode.H / 2.0);
            if (sw.SwitchType == SwitchMode.Bridge)
            {
                var matchedNic = nicNodes.FirstOrDefault(n =>
                    sw.Upstream != null && (n.Adapter.ConnectionId == sw.Upstream.ConnectionId ||
                    (!string.IsNullOrWhiteSpace(sw.Upstream.Description) && n.Adapter.Description.Contains(sw.Upstream.Description, StringComparison.OrdinalIgnoreCase))))
                    ?? primaryNic;
                if (matchedNic != null)
                {
                    edges.Add(new EdgeData(
                        $"edge:{matchedNic.NodeId}:{swNode.NodeId}",
                        matchedNic.NodeId,
                        swNode.NodeId,
                        matchedNic.X + matchedNic.W,
                        matchedNic.Y + Math.Round(matchedNic.H / 2.0),
                        swNode.X,
                        swLeftY,
                        EdgeKind.Bridge,
                        swNode.ModeColor,
                        swNode.RateText));
                }
            }
            else if (sw.SwitchType == SwitchMode.NAT)
            {
                var natNics = nicNodes.Where(n => n == primaryNic || n.Adapter.MediaKind is AdapterMediaKind.Tunnel or AdapterMediaKind.Cellular).ToList();
                for (int i = 0; i < natNics.Count; i++)
                {
                    var nicNode = natNics[i];
                    double yOffset = (i - (natNics.Count - 1) / 2.0) * 14.0;
                    string natLabel = nicNode == primaryNic
                        ? L10n.Get("Host_Net_EdgeNatDefault")
                        : nicNode.Adapter.MediaKind == AdapterMediaKind.Tunnel
                            ? L10n.Get("Host_Net_EdgeNatTunnel")
                            : L10n.Get("Host_Net_EdgeNatAlt");
                    edges.Add(new EdgeData(
                        $"edge:{nicNode.NodeId}:{swNode.NodeId}",
                        nicNode.NodeId,
                        swNode.NodeId,
                        nicNode.X + nicNode.W,
                        nicNode.Y + Math.Round(nicNode.H / 2.0),
                        swNode.X,
                        swLeftY + yOffset,
                        EdgeKind.Nat,
                        swNode.ModeColor,
                        natLabel));
                }
            }

            // Switch -> Host / VM Ports
            var targets = new List<(RightNodeData Node, RightPortData Port)>();
            foreach (var rNode in rightNodes)
                foreach (var port in rNode.Ports)
                    if (string.Equals(port.SwitchName, sw.SwitchName, StringComparison.OrdinalIgnoreCase))
                        targets.Add((rNode, port));

            double swRightX = swNode.X + swNode.W;
            double swRightYBase = swNode.Y + Math.Round(swNode.H / 2.0);
            for (int tIdx = 0; tIdx < targets.Count; tIdx++)
            {
                var (rNode, port) = targets[tIdx];
                double spread = targets.Count > 1 ? ((tIdx / (double)(targets.Count - 1)) - 0.5) * 44.0 : 0;
                EdgeKind kind = rNode.IsHost ? EdgeKind.Host : port.Active ? EdgeKind.VmLive : EdgeKind.VmOff;
                Color col = kind == EdgeKind.Host
                    ? hostColor
                    : kind == EdgeKind.VmLive
                        ? swNode.ModeColor
                        : Color.FromArgb(115, 0x94, 0xA3, 0xB8);
                string? label = kind == EdgeKind.Host
                    ? L10n.Get("Host_Net_EdgeHostShare")
                    : kind == EdgeKind.VmLive
                        ? port.EdgeRateText
                        : L10n.Get("Host_Net_VmStopped");
                edges.Add(new EdgeData(
                    $"edge:{swNode.NodeId}:{port.PortId}",
                    swNode.NodeId,
                    rNode.NodeId,
                    swRightX,
                    Math.Round(swRightYBase + spread),
                    rNode.X,
                    port.AnchorY,
                    kind,
                    col,
                    label));
            }
        }

        currentEdges.Clear();
        currentEdges.AddRange(edges);
        allNodeIds.Clear();
        foreach (var n in nicNodes) allNodeIds.Add(n.NodeId);
        foreach (var s in switchNodes) allNodeIds.Add(s.NodeId);
        foreach (var r in rightNodes) allNodeIds.Add(r.NodeId);

        if (selectedNodeId != null && !allNodeIds.Contains(selectedNodeId)) selectedNodeId = null;
        if (hoveredNodeId != null && !allNodeIds.Contains(hoveredNodeId)) hoveredNodeId = null;
        if (hoverSuppressedNodeId != null && !allNodeIds.Contains(hoverSuppressedNodeId)) hoverSuppressedNodeId = null;

        var bounds = nicNodes.Select(n => (n.X, n.Y, n.W, n.H))
            .Concat(switchNodes.Select(s => (s.X, s.Y, s.W, s.H)))
            .Concat(rightNodes.Select(r => (r.X, r.Y, r.W, r.H)))
            .ToList();
        if (bounds.Count > 0)
        {
            contentMinX = bounds.Min(b => b.X);
            contentMaxX = bounds.Max(b => b.X + b.W);
            contentMinY = bounds.Min(b => b.Y);
            contentMaxY = bounds.Max(b => b.Y + b.H);
        }
        else
        {
            contentMinX = Col1X;
            contentMaxX = Col3X + ColW;
            contentMinY = TopPad;
            contentMaxY = canvasH - BottomPad;
        }

        string structKey = string.Join("|",
            nicNodes.Select(n => n.NodeId)
            .Concat(switchNodes.Select(s => $"{s.NodeId}:{s.ModeLabel}"))
            .Concat(rightNodes.Select(r => $"{r.NodeId}:{r.Running}:{string.Join(",", r.Ports.Select(p => p.PortId))}")));

        if (forceStructure || !string.Equals(structKey, lastStructKey, StringComparison.Ordinal))
        {
            RenderTopologyCanvas(nicNodes, switchNodes, rightNodes, edges);
            lastStructKey = structKey;
            if (autoFit) ApplyAutoFit();
        }
        else
        {
            UpdateLiveRates(nicNodes, switchNodes, rightNodes, edges);
        }

        ApplyFocusVisuals();
    }

    private void RenderTopologyCanvas(
        IReadOnlyList<NicNodeData> nicNodes,
        IReadOnlyList<SwitchNodeData> switchNodes,
        IReadOnlyList<RightNodeData> rightNodes,
        IReadOnlyList<EdgeData> edges)
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Brush nodeBg = new SolidColorBrush(light ? Colors.White : ThemeHelper.ThemeColor(ActualTheme, "CardBackgroundFillColorDefaultBrush", Color.FromArgb(0xFF, 0x24, 0x24, 0x24)));
        Brush nodeStroke = ThemeHelper.TryResourceBrush(ActualTheme, "CardStrokeColorDefaultBrush",
            light ? Color.FromArgb(0x18, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Brush subBg = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush",
            light ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        Brush divider = ThemeHelper.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush", Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        Brush pillBg = new SolidColorBrush(light ? Colors.White : Color.FromArgb(255, 0x28, 0x28, 0x28));
        Brush pillStroke = ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush",
            Color.FromArgb(0x28, 0x80, 0x80, 0x80));

        panCanvas.Children.Clear();
        panCanvas.Width = CanvasW;
        panCanvas.Height = canvasH;
        edgeVisuals.Clear();
        nodeVisuals.Clear();
        liveRateLabels.Clear();

        // Edges (Bezier wires + dots + floating pills)
        foreach (var e in edges)
        {
            double dx = Math.Max(48, Math.Abs(e.X2 - e.X1) * 0.48);
            var seg = new BezierSegment
            {
                Point1 = new Point(e.X1 + dx, e.Y1),
                Point2 = new Point(e.X2 - dx, e.Y2),
                Point3 = new Point(e.X2, e.Y2)
            };
            var fig = new PathFigure { StartPoint = new Point(e.X1, e.Y1), IsClosed = false };
            fig.Segments.Add(seg);
            var geom = new PathGeometry();
            geom.Figures.Add(fig);

            var wire = new ShapePath
            {
                Data = geom,
                Stroke = new SolidColorBrush(e.Color),
                StrokeThickness = 1.9,
                Opacity = e.Kind == EdgeKind.VmOff ? 0.36 : 0.72,
                IsHitTestVisible = false
            };
            if (e.Kind is EdgeKind.Nat or EdgeKind.VmOff)
                wire.StrokeDashArray = [5, 4];

            var dotBrush = new SolidColorBrush(e.Color);
            var dot1 = new Ellipse { Width = 7, Height = 7, Fill = dotBrush, IsHitTestVisible = false };
            Canvas.SetLeft(dot1, e.X1 - 3.5);
            Canvas.SetTop(dot1, e.Y1 - 3.5);

            var dot2 = new Ellipse { Width = 7, Height = 7, Fill = dotBrush, IsHitTestVisible = false };
            Canvas.SetLeft(dot2, e.X2 - 3.5);
            Canvas.SetTop(dot2, e.Y2 - 3.5);

            Canvas.SetZIndex(wire, 2);
            Canvas.SetZIndex(dot1, 3);
            Canvas.SetZIndex(dot2, 3);
            panCanvas.Children.Add(wire);
            panCanvas.Children.Add(dot1);
            panCanvas.Children.Add(dot2);

            Border? pillBorder = null;
            TextBlock? pillTextBlock = null;
            if (e.Kind is EdgeKind.Bridge or EdgeKind.VmLive && !string.IsNullOrWhiteSpace(e.PillText))
            {
                double mx = Math.Round((e.X1 + e.X2) / 2.0);
                double my = Math.Round((e.Y1 + e.Y2) / 2.0);
                double pillW = Math.Clamp(Math.Round(e.PillText.Length * 5.6 + 16), 76, 118);

                pillTextBlock = new TextBlock
                {
                    Text = e.PillText,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(secondary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(pillTextBlock, FontNumeralAlignment.Tabular);
                pillBorder = new Border
                {
                    Width = pillW,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = pillBg,
                    BorderBrush = pillStroke,
                    BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                    Child = pillTextBlock
                };
                Canvas.SetLeft(pillBorder, mx - pillW / 2.0);
                Canvas.SetTop(pillBorder, my - 10);
                Canvas.SetZIndex(pillBorder, 8);
                panCanvas.Children.Add(pillBorder);
                liveRateLabels[e.EdgeId] = pillTextBlock;
            }

            edgeVisuals.Add(new EdgeVisualRef
            {
                EdgeId = e.EdgeId,
                FromId = e.FromId,
                ToId = e.ToId,
                Kind = e.Kind,
                Color = e.Color,
                Wire = wire,
                DotStart = dot1,
                DotEnd = dot2,
                PillBorder = pillBorder,
                PillLabel = pillTextBlock
            });
        }

        Color nicAccent = ThemeHelper.ThemeColor(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));

        // NIC cards
        foreach (var n in nicNodes)
        {
            var stack = new Grid { RowSpacing = 3 };
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Grid { ColumnSpacing = 7 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Background = subBg,
                Child = new FontIcon { Glyph = n.Glyph, FontSize = 12, Foreground = new SolidColorBrush(nicAccent) }
            });
            var titleTb = new TextBlock { Text = n.Adapter.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(primary), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Padding = new Thickness(0, 1, 0, 1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titleTb, 1);
            head.Children.Add(titleTb);
            var badge = HostUiHelpers.BuildMiniBadge(ActualTheme, n.MediaLabel);
            Grid.SetColumn(badge, 2);
            head.Children.Add(badge);
            stack.Children.Add(head);

            var subTb = new TextBlock
            {
                Text = n.Adapter.Description,
                FontSize = 11.5,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
                MaxLines = 1,
                Padding = new Thickness(0, 1, 0, 2),
                Foreground = new SolidColorBrush(secondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(subTb, n.Adapter.Description);
            Grid.SetRow(subTb, 1);
            stack.Children.Add(subTb);

            var foot = new Grid { ColumnSpacing = 8 };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ipTb = new TextBlock { Text = n.Ipv4Text, FontSize = 11.5, MaxLines = 1, Padding = new Thickness(0, 0, 0, 1), Foreground = new SolidColorBrush(secondary), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(ipTb, FontNumeralAlignment.Tabular);
            foot.Children.Add(ipTb);
            var rateTb = new TextBlock { Text = n.RateText, FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(primary), VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(rateTb, FontNumeralAlignment.Tabular);
            Grid.SetColumn(rateTb, 1);
            foot.Children.Add(rateTb);
            liveRateLabels[n.NodeId] = rateTb;
            Grid.SetRow(foot, 2);
            stack.Children.Add(foot);

            PlaceTopoNode(n.NodeId, n.X, n.Y, n.W, n.H, nicAccent, dimWhenOff: false, stack, nodeBg, nodeStroke);
        }

        // Switch cards
        foreach (var s in switchNodes)
        {
            var stack = new Grid { RowSpacing = 3 };
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Grid { ColumnSpacing = 7 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Background = subBg,
                Child = new FontIcon { Glyph = "\uE774", FontSize = 12, Foreground = new SolidColorBrush(s.ModeColor) }
            });
            var titleTb = new TextBlock { Text = s.Switch.SwitchName, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(primary), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Padding = new Thickness(0, 1, 0, 1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titleTb, 1);
            head.Children.Add(titleTb);
            var badge = HostUiHelpers.BuildMiniBadge(ActualTheme, s.ModeLabel);
            Grid.SetColumn(badge, 2);
            head.Children.Add(badge);
            stack.Children.Add(head);

            var subTb = new TextBlock
            {
                Text = s.UpstreamText,
                FontSize = 11.5,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
                MaxLines = 1,
                Padding = new Thickness(0, 1, 0, 2),
                Foreground = new SolidColorBrush(secondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(subTb, s.UpstreamText);
            Grid.SetRow(subTb, 1);
            stack.Children.Add(subTb);

            var foot = new Grid { ColumnSpacing = 8 };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var metaTb = new TextBlock { Text = s.MetaText, FontSize = 11.5, MaxLines = 1, Padding = new Thickness(0, 0, 0, 1), Foreground = new SolidColorBrush(secondary), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(metaTb, FontNumeralAlignment.Tabular);
            foot.Children.Add(metaTb);
            var rateTb = new TextBlock { Text = s.RateText, FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(primary), VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(rateTb, FontNumeralAlignment.Tabular);
            Grid.SetColumn(rateTb, 1);
            foot.Children.Add(rateTb);
            liveRateLabels[s.NodeId] = rateTb;
            Grid.SetRow(foot, 2);
            stack.Children.Add(foot);

            PlaceTopoNode(s.NodeId, s.X, s.Y, s.W, s.H, s.ModeColor, dimWhenOff: false, stack, nodeBg, nodeStroke);
        }

        // Right Host & VM cards
        foreach (var r in rightNodes)
        {
            var stack = new StackPanel { Spacing = 6 };
            var head = new Grid { ColumnSpacing = 7 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(r.Color),
                VerticalAlignment = VerticalAlignment.Center
            });
            var titleTb = new TextBlock { Text = r.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(primary), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titleTb, 1);
            head.Children.Add(titleTb);
            string badgeStr = r.IsHost ? L10n.Format("Host_Net_PortsCount", r.Ports.Count) : L10n.Get(r.Running ? "Host_Net_VmRunning" : "Host_Net_VmStopped");
            var badge = HostUiHelpers.BuildMiniBadge(ActualTheme, badgeStr);
            Grid.SetColumn(badge, 2);
            head.Children.Add(badge);
            stack.Children.Add(head);

            var portsStack = new StackPanel { Spacing = 4 };
            if (r.Ports.Count == 0)
            {
                portsStack.Children.Add(new Border
                {
                    Height = 26,
                    Padding = new Thickness(8, 0, 8, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = subBg,
                    Opacity = 0.65,
                    Child = new TextBlock { Text = L10n.Get("Host_Net_NoHostShare"), FontSize = 11, Foreground = new SolidColorBrush(secondary), VerticalAlignment = VerticalAlignment.Center }
                });
            }
            else
            {
                foreach (var p in r.Ports)
                {
                    var pRow = new Grid { ColumnSpacing = 6 };
                    pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    pRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    pRow.Children.Add(new TextBlock
                    {
                        Text = p.PortName,
                        FontSize = 11.5,
                        MaxLines = 1,
                        Padding = new Thickness(0, 1, 0, 1),
                        Foreground = new SolidColorBrush(primary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    var pRate = new TextBlock
                    {
                        Text = p.RightText,
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(secondary),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Typography.SetNumeralAlignment(pRate, FontNumeralAlignment.Tabular);
                    Grid.SetColumn(pRate, 1);
                    pRow.Children.Add(pRate);
                    liveRateLabels[p.PortId] = pRate;

                    portsStack.Children.Add(new Border
                    {
                        Height = 26,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(4),
                        Background = subBg,
                        Opacity = p.Active ? 1.0 : 0.65,
                        Child = pRow
                    });
                }
            }
            stack.Children.Add(portsStack);

            PlaceTopoNode(r.NodeId, r.X, r.Y, r.W, r.H, r.Color, dimWhenOff: !r.Running, stack, nodeBg, nodeStroke);
        }
    }

    private void PlaceTopoNode(
        string nodeId,
        double x,
        double y,
        double w,
        double h,
        Color accent,
        bool dimWhenOff,
        UIElement content,
        Brush nodeBg,
        Brush nodeStroke)
    {
        var card = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = nodeStroke,
            Background = nodeBg,
            Padding = new Thickness(11, 8, 11, 8),
            Opacity = dimWhenOff ? 0.72 : 1.0,
            Child = content
        };

        card.PointerEntered += (_, _) =>
        {
            if (hoverSuppressedNodeId != null && hoverSuppressedNodeId != nodeId)
                hoverSuppressedNodeId = null;
            if (hoveredNodeId == nodeId) return;
            hoveredNodeId = nodeId;
            ApplyFocusVisuals();
        };
        card.PointerExited += (_, _) =>
        {
            if (hoverSuppressedNodeId == nodeId)
                hoverSuppressedNodeId = null;
            if (hoveredNodeId != nodeId) return;
            hoveredNodeId = null;
            ApplyFocusVisuals();
        };
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (selectedNodeId == nodeId)
            {
                selectedNodeId = null;
                hoverSuppressedNodeId = nodeId;
            }
            else
            {
                selectedNodeId = nodeId;
                hoverSuppressedNodeId = null;
            }
            ApplyFocusVisuals();
        };

        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        Canvas.SetZIndex(card, 20);
        panCanvas.Children.Add(card);

        nodeVisuals.Add(new NodeVisualRef
        {
            NodeId = nodeId,
            AccentColor = accent,
            DimWhenOff = dimWhenOff,
            Card = card
        });
    }

    private void UpdateLiveRates(
        IReadOnlyList<NicNodeData> nicNodes,
        IReadOnlyList<SwitchNodeData> switchNodes,
        IReadOnlyList<RightNodeData> rightNodes,
        IReadOnlyList<EdgeData> edges)
    {
        foreach (var n in nicNodes)
            if (liveRateLabels.TryGetValue(n.NodeId, out var tb))
                tb.Text = n.RateText;

        foreach (var s in switchNodes)
            if (liveRateLabels.TryGetValue(s.NodeId, out var tb))
                tb.Text = s.RateText;

        foreach (var r in rightNodes)
            foreach (var p in r.Ports)
                if (liveRateLabels.TryGetValue(p.PortId, out var tb))
                    tb.Text = p.RightText;

        foreach (var e in edges)
            if (!string.IsNullOrWhiteSpace(e.PillText) && liveRateLabels.TryGetValue(e.EdgeId, out var tb))
                tb.Text = e.PillText;
    }

    private (HashSet<string> ActiveNodes, HashSet<string> ActiveEdges)? ComputeFocusSets(string? focusId)
    {
        if (string.IsNullOrEmpty(focusId)) return null;
        var activeNodes = new HashSet<string>(StringComparer.Ordinal) { focusId };
        var activeEdges = new HashSet<string>(StringComparer.Ordinal);

        if (focusId.StartsWith("nic:", StringComparison.Ordinal))
        {
            var adjSwitches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in currentEdges)
            {
                if (e.FromId == focusId)
                {
                    activeEdges.Add(e.EdgeId);
                    activeNodes.Add(e.ToId);
                    adjSwitches.Add(e.ToId);
                }
            }
            foreach (var e in currentEdges)
            {
                if (adjSwitches.Contains(e.FromId))
                {
                    activeEdges.Add(e.EdgeId);
                    activeNodes.Add(e.ToId);
                }
            }
        }
        else if (focusId.StartsWith("sw:", StringComparison.Ordinal))
        {
            foreach (var e in currentEdges)
            {
                if (e.FromId == focusId || e.ToId == focusId)
                {
                    activeEdges.Add(e.EdgeId);
                    activeNodes.Add(e.FromId);
                    activeNodes.Add(e.ToId);
                }
            }
        }
        else
        {
            var adjSwitches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in currentEdges)
            {
                if (e.ToId == focusId)
                {
                    activeEdges.Add(e.EdgeId);
                    activeNodes.Add(e.FromId);
                    adjSwitches.Add(e.FromId);
                }
            }
            foreach (var e in currentEdges)
            {
                if (adjSwitches.Contains(e.ToId))
                {
                    activeEdges.Add(e.EdgeId);
                    activeNodes.Add(e.FromId);
                }
            }
        }

        return (activeNodes, activeEdges);
    }

    private void ApplyFocusVisuals()
    {
        string? activeHover = hoveredNodeId != null && hoveredNodeId != hoverSuppressedNodeId ? hoveredNodeId : null;
        string? focus = selectedNodeId ?? activeHover;
        var focusSets = ComputeFocusSets(focus);
        bool hasFocus = focusSets.HasValue;

        bool light = ActualTheme == ElementTheme.Light;
        Brush defaultNodeStroke = ThemeHelper.TryResourceBrush(ActualTheme, "CardStrokeColorDefaultBrush",
            light ? Color.FromArgb(0x18, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Brush defaultPillStroke = ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush",
            Color.FromArgb(0x28, 0x80, 0x80, 0x80));
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Color accent = ThemeHelper.ThemeColor(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));

        foreach (var node in nodeVisuals)
        {
            bool isSelected = selectedNodeId == node.NodeId;
            bool isFocused = hasFocus && focusSets!.Value.ActiveNodes.Contains(node.NodeId);
            bool isDimmed = hasFocus && !isFocused;

            node.Card.Opacity = isDimmed ? (node.DimWhenOff ? 0.16 : 0.22) : (node.DimWhenOff ? 0.72 : 1.0);
            node.Card.BorderBrush = isSelected || isFocused ? new SolidColorBrush(node.AccentColor) : defaultNodeStroke;
            node.Card.BorderThickness = new Thickness(isSelected ? 1.8 : 1.0);
        }

        foreach (var edge in edgeVisuals)
        {
            bool isFocused = hasFocus && focusSets!.Value.ActiveEdges.Contains(edge.EdgeId);
            bool isDimmed = hasFocus && !isFocused;

            double wireOpacity = isFocused ? 1.0 : isDimmed ? 0.15 : (edge.Kind == EdgeKind.VmOff ? 0.36 : 0.72);
            edge.Wire.Opacity = wireOpacity;
            edge.Wire.StrokeThickness = isFocused ? 2.7 : 1.9;
            edge.DotStart.Opacity = isDimmed ? 0.15 : 1.0;
            edge.DotEnd.Opacity = isDimmed ? 0.15 : 1.0;
            Canvas.SetZIndex(edge.Wire, isFocused ? 12 : 2);
            Canvas.SetZIndex(edge.DotStart, isFocused ? 13 : 3);
            Canvas.SetZIndex(edge.DotEnd, isFocused ? 13 : 3);

            if (edge.PillBorder != null && edge.PillLabel != null)
            {
                edge.PillBorder.Opacity = isDimmed ? 0.15 : 1.0;
                edge.PillBorder.BorderBrush = isFocused ? new SolidColorBrush(accent) : defaultPillStroke;
                edge.PillLabel.Foreground = new SolidColorBrush(isFocused ? primary : secondary);
                edge.PillLabel.FontWeight = isFocused ? FontWeights.SemiBold : FontWeights.Normal;
                Canvas.SetZIndex(edge.PillBorder, isFocused ? 15 : 8);
            }
        }
    }

    private void UpdateControlsTheme()
    {
        bool light = ActualTheme == ElementTheme.Light;
        controlsBar.Background = ThemeHelper.TryResourceBrush(ActualTheme, "ControlFillColorDefaultBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        controlsBar.BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush",
            Color.FromArgb(0x28, 0x80, 0x80, 0x80));
        ctrlSep.Background = ThemeHelper.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x28, 0x80, 0x80, 0x80));
        var fg = ThemeHelper.TryResourceBrush(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        zoomResetBtn.Background = new SolidColorBrush(Colors.Transparent);
        zoomResetBtn.Foreground = fg;
        zoomOutBtn.Foreground = fg;
        zoomInBtn.Foreground = fg;
        fullscreenBtn.Foreground = fg;
    }

    private static Button CreateCtrlButton(string glyph, string tooltip, double width = 26, double fontSize = 12)
    {
        var btn = new Button
        {
            Width = width,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Content = new FontIcon { FontFamily = AppFonts.SegoeFluent, Glyph = glyph, FontSize = fontSize },
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(btn, tooltip);
        AutomationProperties.SetName(btn, tooltip);
        return btn;
    }
}
