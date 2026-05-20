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
            if (this.IsVisible) Redraw();
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
            double nodeSpacing = 120;
            double lineThick = 1.5;
            double upstreamY = 20;
            double switchY = upstreamY + 70;
            // Switch/Upstream 图标视觉修正（字体图标渲染区域比几何边界小）
            double switchOffset = 3;

            string ParseIPv4(string s) => string.IsNullOrEmpty(s) ? "" :
                s.Trim('{', '}').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                 .FirstOrDefault(ip => IPAddress.TryParse(ip, out var p) &&
                                       p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? "";

            void DrawLine(double x1, double y1, double x2, double y2)
            {
                var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = lineThick };
                line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
                Children.Add(line);
            }

            void CreateNode(string type, string name, string ip, string mac, double x, double y, bool wrap = false)
            {
                var icon = Utils.FontIcon1(type, "");
                icon.FontSize = iconSize;
                SetLeft(icon, x - radius); SetTop(icon, y - radius);
                Children.Add(icon);

                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Orientation = Orientation.Vertical };
                var nameText = new UiTextBlock { Text = name, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, -2) };
                if (wrap) { nameText.MaxWidth = nodeSpacing - 10; nameText.TextWrapping = TextWrapping.Wrap; }
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
                panel.Loaded += (s, _) => { if (s is StackPanel p) { SetLeft(p, x - p.ActualWidth / 2); SetTop(p, y + radius + 5); } };
                Children.Add(panel);
            }

            // 画一行节点：总线 + 节点垂线 + 节点
            // avoidCenter=true 时奇数行偏移半格，确保没有节点落在主干线（centerX）上
            void DrawRow(List<(string Name, string Ip, string Mac)> items, double busY, double vmY, double cx, bool avoidCenter)
            {
                if (items.Count == 0) return;
                double startX = cx - ((items.Count - 1) * nodeSpacing) / 2.0;
                // 奇数时中间节点落在 cx，偶数时左右对称，总线都经过 cx
                // 不再做任何偏移

                if (items.Count > 1)
                    DrawLine(startX, busY, startX + (items.Count - 1) * nodeSpacing, busY);

                for (int i = 0; i < items.Count; i++)
                {
                    double x = startX + i * nodeSpacing;
                    DrawLine(x, busY, x, vmY - radius);
                    CreateNode("Net", items[i].Name, items[i].Ip, items[i].Mac, x, vmY, wrap: true);
                }
            }
            var clients = ItemsSource.Select(a => (Name: a.VMName, Ip: ParseIPv4(a.IPAddresses), Mac: a.MacAddress)).ToList();

            bool isDefaultSwitch = SwitchName == "Default Switch";
            bool hasUpstream = (NetworkMode == "Bridge" || NetworkMode == "NAT") &&
                               (!string.IsNullOrEmpty(UpstreamAdapter) || isDefaultSwitch);

            bool isMultiRow = clients.Count > 6;

            double centerX, totalWidth;
            if (!isMultiRow)
            {
                int count = Math.Max(clients.Count, 1);
                totalWidth = count * nodeSpacing + 40;
                centerX = totalWidth / 2;
            }
            else
            {
                double gap = 60;
                totalWidth =  2 * 3 * nodeSpacing + 40;
                centerX = totalWidth / 2;
            }

            // 上游节点
            if (hasUpstream)
            {
                string upstreamName = isDefaultSwitch ? "Internet" : UpstreamAdapter;
                CreateNode("Upstream", upstreamName, "", "", centerX, upstreamY);
                DrawLine(centerX, upstreamY + radius - switchOffset, centerX, switchY - radius + switchOffset);
            }

            // Switch 节点
            CreateNode("Switch", SwitchName, "", "", centerX, switchY);

            if (clients.Count == 0)
            {
                Width = totalWidth + 40; Height = switchY + 60; return;
            }

            if (!isMultiRow)
            {
                // 单行居中
                double busY = switchY + 40;
                double vmY = busY + 30;
                DrawLine(centerX, switchY + radius - switchOffset, centerX, busY);
                DrawRow(clients, busY, vmY, centerX, avoidCenter: false);
                Width = totalWidth + 40; Height = vmY + 80;
            }
            else
            {
                // 多行：第一行左三右三，主干穿中
                double gap = 80;
                double halfGap = gap / 2.0;

                double row1BusY = switchY + 70;
                double row1VmY = row1BusY + 30;
                double trunkStartY = switchY + radius - switchOffset;

                var row1Items = clients.Take(6).ToList();
                var remaining = clients.Skip(6).ToList();

                // 主干线：Switch 到第一行总线
                DrawLine(centerX, trunkStartY, centerX, row1BusY);
                DrawRow(row1Items, row1BusY, row1VmY, centerX, avoidCenter: true);

                double lastY = row1VmY;
                double nextBusY = row1VmY + 80;
                int rowStart = 0;
                double trunkY = row1BusY; // 主干线当前终点，沿总线向下延伸

                while (rowStart < remaining.Count)
                {
                    var rowItems = remaining.Skip(rowStart).Take(6).ToList();
                    double busY = nextBusY;
                    double vmY = busY + 30;

                    // 主干线始终走 centerX
                    DrawLine(centerX, trunkY, centerX, busY);
                    trunkY = busY;

                    DrawRow(rowItems, busY, vmY, centerX, avoidCenter: true);

                    lastY = vmY;
                    nextBusY = vmY + 80;
                    rowStart += 6;
                }

                Width = totalWidth + 40;
                Height = lastY + 80;
            }
        }
    }
}