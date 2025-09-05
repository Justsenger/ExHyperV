using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using ExHyperV.Models;
using UiTextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.Tools
{
    public class TopologyCanvas : Canvas
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(ObservableCollection<AdapterInfo>), typeof(TopologyCanvas), new PropertyMetadata(null, OnItemsSourceChanged));
        public ObservableCollection<AdapterInfo> ItemsSource { get => (ObservableCollection<AdapterInfo>)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

        public static readonly DependencyProperty SwitchNameProperty =
            DependencyProperty.Register("SwitchName", typeof(string), typeof(TopologyCanvas), new PropertyMetadata(string.Empty, OnPropertiesChanged));
        public string SwitchName { get => (string)GetValue(SwitchNameProperty); set => SetValue(SwitchNameProperty, value); }

        public static readonly DependencyProperty NetworkModeProperty =
            DependencyProperty.Register("NetworkMode", typeof(string), typeof(TopologyCanvas), new PropertyMetadata("Isolated", OnPropertiesChanged));
        public string NetworkMode { get => (string)GetValue(NetworkModeProperty); set => SetValue(NetworkModeProperty, value); }

        public static readonly DependencyProperty UpstreamAdapterProperty =
            DependencyProperty.Register("UpstreamAdapter", typeof(string), typeof(TopologyCanvas), new PropertyMetadata(string.Empty, OnPropertiesChanged));
        public string UpstreamAdapter { get => (string)GetValue(UpstreamAdapterProperty); set => SetValue(UpstreamAdapterProperty, value); }

        public TopologyCanvas()
        {
            this.IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.IsVisible)
            {
                Redraw();
            }
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TopologyCanvas canvas) return;
            if (e.OldValue is ObservableCollection<AdapterInfo> o) o.CollectionChanged -= canvas.OnCollectionChanged;
            if (e.NewValue is ObservableCollection<AdapterInfo> n) n.CollectionChanged += canvas.OnCollectionChanged;
            canvas.Redraw();
        }

        private static void OnPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => (d as TopologyCanvas)?.Redraw();
        private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            if (!this.IsVisible) return;
            Children.Clear();
            if (ItemsSource == null) return;

            double iconSize = 28;
            double radius = iconSize / 2;
            double verticalSpacing = 70;
            double horizontalVmSpacing = 140;
            double lineThickness = 1.5;
            double upstreamY = 20;
            double switchY = upstreamY + verticalSpacing;

            double vmBusY = switchY + 40; // 从交换机到总线的距离
            double vmY = vmBusY + 30;    // 从总线到客户端图标的距离

            void CreateNode(string type, string name, string ip, string mac, double x, double y, bool wrap = false)
            {
                var icon = Utils.FontIcon1(type, "");
                icon.FontSize = iconSize;
                SetLeft(icon, x - iconSize / 2); SetTop(icon, y - iconSize / 2); Children.Add(icon);
                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Orientation = Orientation.Vertical };
                var nameText = new UiTextBlock { Text = name, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
                if (wrap) { nameText.MaxWidth = horizontalVmSpacing - 10; nameText.TextWrapping = TextWrapping.Wrap; }
                nameText.SetResourceReference(UiTextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                panel.Children.Add(nameText);
                if (!string.IsNullOrEmpty(mac))
                {
                    var macText = new UiTextBlock { Text = mac, FontSize = 10, TextAlignment = TextAlignment.Center };
                    macText.SetResourceReference(UiTextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                    panel.Children.Add(macText);
                }
                if (!string.IsNullOrEmpty(ip))
                {
                    var ipText = new UiTextBlock { Text = ip, FontSize = 11, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, -4, 0, 0) };
                    ipText.SetResourceReference(UiTextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                    panel.Children.Add(ipText);
                }
                panel.Loaded += (s, _) => { if (s is StackPanel p) { SetLeft(p, x - p.ActualWidth / 2); SetTop(p, y + iconSize / 2 + 5); } };
                Children.Add(panel);
            }

            void DrawLine(double x1, double y1, double x2, double y2)
            {
                var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = lineThickness };
                line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush"); Children.Add(line);
            }

            string ParseIPv4(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim('{', '}').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(ip => IPAddress.TryParse(ip, out var p) && p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? "";

            var clients = ItemsSource.Select(a => (Name: a.VMName, Ip: ParseIPv4(a.IPAddresses), Mac: a.MacAddress)).ToList();

            bool isDefaultSwitch = SwitchName == "Default Switch";
            bool hasUpstream = (NetworkMode == "Bridge" || NetworkMode == "NAT") && (!string.IsNullOrEmpty(UpstreamAdapter) || isDefaultSwitch);

            double totalWidth = Math.Max(200, (clients.Count > 0 ? clients.Count : 1) * horizontalVmSpacing);
            double centerX = totalWidth / 2;

            CreateNode("Switch", SwitchName, "", "", centerX, switchY);

            if (hasUpstream)
            {
                string upstreamName = isDefaultSwitch ? "Internet" : UpstreamAdapter;
                CreateNode("Upstream", upstreamName, "", "", centerX, upstreamY);
                DrawLine(centerX, upstreamY + radius, centerX, switchY);
            }

            if (clients.Any())
            {
                double startX = centerX - ((clients.Count - 1) * horizontalVmSpacing) / 2;
                DrawLine(centerX, switchY, centerX, vmBusY);
                if (clients.Count > 1) DrawLine(startX, vmBusY, startX + (clients.Count - 1) * horizontalVmSpacing, vmBusY);
                for (int i = 0; i < clients.Count; i++)
                {
                    var c = clients[i];
                    double currentX = startX + i * horizontalVmSpacing;
                    CreateNode("Net", c.Name, c.Ip, c.Mac, currentX, vmY, wrap: true);
                    DrawLine(currentX, vmBusY, currentX, vmY - radius);
                }
            }

            Width = totalWidth + 40;
            Height = vmY + 80;
        }
    }
}