using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using ExHyperV.Models;
using UiTextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.Tools
{
    public class SwitchTopology : Canvas
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(ObservableCollection<AdapterInfo>), typeof(SwitchTopology), new PropertyMetadata(null, OnItemsSourceChanged));
        public ObservableCollection<AdapterInfo> ItemsSource { get => (ObservableCollection<AdapterInfo>)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

        public static readonly DependencyProperty SwitchNameProperty =
            DependencyProperty.Register("SwitchName", typeof(string), typeof(SwitchTopology), new PropertyMetadata(string.Empty, OnPropertiesChanged));
        public string SwitchName { get => (string)GetValue(SwitchNameProperty); set => SetValue(SwitchNameProperty, value); }

        public static readonly DependencyProperty NetworkModeProperty =
            DependencyProperty.Register("NetworkMode", typeof(string), typeof(SwitchTopology), new PropertyMetadata("Isolated", OnPropertiesChanged));
        public string NetworkMode { get => (string)GetValue(NetworkModeProperty); set => SetValue(NetworkModeProperty, value); }

        public static readonly DependencyProperty UpstreamAdapterProperty =
            DependencyProperty.Register("UpstreamAdapter", typeof(string), typeof(SwitchTopology), new PropertyMetadata(string.Empty, OnPropertiesChanged));
        public string UpstreamAdapter { get => (string)GetValue(UpstreamAdapterProperty); set => SetValue(UpstreamAdapterProperty, value); }

        private const double IconSize = 28;
        private const double NodeSpacing = 120;
        private const double LineThickness = 1.5;
        private const double UpstreamY = 20;
        private const double SwitchOffset = 3;
        private double Radius => IconSize / 2;
        private double SwitchY => UpstreamY + 70;

        public SwitchTopology()
        {
            this.IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.IsVisible) Redraw();
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not SwitchTopology canvas) return;
            if (e.OldValue is ObservableCollection<AdapterInfo> o) o.CollectionChanged -= canvas.OnCollectionChanged;
            if (e.NewValue is ObservableCollection<AdapterInfo> n) n.CollectionChanged += canvas.OnCollectionChanged;
            canvas.Redraw();
        }

        private static void OnPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => (d as SwitchTopology)?.Redraw();
        private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Redraw();

        private static string ParseIPv4(string s) =>
            string.IsNullOrEmpty(s) ? "" :
            s.Trim('{', '}').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
             .FirstOrDefault(ip => IPAddress.TryParse(ip, out var p) &&
                                   p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? "";

        private void DrawLine(double x1, double y1, double x2, double y2)
        {
            var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = LineThickness };
            line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
            Children.Add(line);
        }

        private void CreateNode(string type, string name, string ip, string mac, double x, double y, bool wrap = false)
        {
            var icon = Utils.FontIcon1(type, "");
            icon.FontSize = IconSize;
            SetLeft(icon, x - Radius); SetTop(icon, y - Radius);
            Children.Add(icon);

            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Orientation = Orientation.Vertical };
            var nameText = new UiTextBlock { Text = name, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, -2) };
            if (wrap) { nameText.MaxWidth = NodeSpacing - 10; nameText.TextWrapping = TextWrapping.Wrap; }
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
            panel.Loaded += (s, _) => { if (s is StackPanel p) { SetLeft(p, x - p.ActualWidth / 2); SetTop(p, y + Radius + 5); } };
            Children.Add(panel);
        }

        private void DrawRow(List<(string Name, string Ip, string Mac)> items, double busY, double vmY, double cx)
        {
            if (items.Count == 0) return;
            double startX = cx - ((items.Count - 1) * NodeSpacing) / 2.0;
            if (items.Count > 1)
                DrawLine(startX, busY, startX + (items.Count - 1) * NodeSpacing, busY);
            for (int i = 0; i < items.Count; i++)
            {
                double x = startX + i * NodeSpacing;
                DrawLine(x, busY, x, vmY - Radius);
                CreateNode("Net", items[i].Name, items[i].Ip, items[i].Mac, x, vmY, wrap: true);
            }
        }

        private void Redraw()
        {
            if (!this.IsVisible) return;
            Children.Clear();
            if (ItemsSource == null) return;

            bool isDefaultSwitch = SwitchName == "Default Switch";
            bool hasUpstream = (NetworkMode == "Bridge" || NetworkMode == "NAT") &&
                               (!string.IsNullOrEmpty(UpstreamAdapter) || isDefaultSwitch);
            bool isMultiRow = ItemsSource.Count > 6;

            double centerX, totalWidth;
            if (!isMultiRow)
            {
                int count = Math.Max(ItemsSource.Count, 1);
                totalWidth = count * NodeSpacing + 40;
                centerX = totalWidth / 2;
            }
            else
            {
                totalWidth = 2 * 3 * NodeSpacing + 40;
                centerX = totalWidth / 2;
            }

            if (hasUpstream)
            {
                string upstreamName = isDefaultSwitch ? "Internet" : UpstreamAdapter;
                CreateNode("Upstream", upstreamName, "", "", centerX, UpstreamY);
                DrawLine(centerX, UpstreamY + Radius - SwitchOffset, centerX, SwitchY - Radius + SwitchOffset);
            }

            CreateNode("Switch", SwitchName, "", "", centerX, SwitchY);

            var clients = ItemsSource.Select(a => (Name: a.VMName, Ip: ParseIPv4(a.IPAddresses), Mac: a.MacAddress)).ToList();

            if (clients.Count == 0)
            {
                Width = totalWidth + 40; Height = SwitchY + 60; return;
            }

            if (!isMultiRow)
            {
                double busY = SwitchY + 40;
                double vmY = busY + 30;
                DrawLine(centerX, SwitchY + Radius - SwitchOffset, centerX, busY);
                DrawRow(clients, busY, vmY, centerX);
                Width = totalWidth + 40; Height = vmY + 80;
            }
            else
            {
                double row1BusY = SwitchY + 70;
                double row1VmY = row1BusY + 30;
                double trunkStartY = SwitchY + Radius - SwitchOffset;

                var row1Items = clients.Take(6).ToList();
                var remaining = clients.Skip(6).ToList();

                DrawLine(centerX, trunkStartY, centerX, row1BusY);
                DrawRow(row1Items, row1BusY, row1VmY, centerX);

                double lastY = row1VmY;
                double nextBusY = row1VmY + 80;
                int rowStart = 0;
                double trunkY = row1BusY;

                while (rowStart < remaining.Count)
                {
                    var rowItems = remaining.Skip(rowStart).Take(6).ToList();
                    double busY = nextBusY;
                    double vmY = busY + 30;
                    DrawLine(centerX, trunkY, centerX, busY);
                    trunkY = busY;
                    DrawRow(rowItems, busY, vmY, centerX);
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