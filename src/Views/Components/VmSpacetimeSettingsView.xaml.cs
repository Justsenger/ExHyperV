using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ExHyperV.Models;
using ExHyperV.ViewModels;
using Wpf.Ui.Appearance;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace ExHyperV.Views.Components
{
    public partial class VmSpacetimeSettingsView : UserControl
    {
        private Dictionary<string, List<SpacetimeNode>> _treeMap = new();
        private Dictionary<string, int> _subtreeLeafCount = new(); // 存储每个节点的叶子总数
        private Point _dragStartPos;
        private Point _dragStartOffset;
        private Point _selectedNodePos;
        private Point _currentNodePos;
        private bool _isDragging = false;
        private bool _needsInitialCenter = true;
        private bool _isRendering = false;
        private VirtualMachinesPageViewModel? _boundVm;

        private DispatcherTimer _liveTimer;

        public VmSpacetimeSettingsView()
        {
            InitializeComponent();
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTimer.Tick += (s, e) => {
                if (DataContext is VirtualMachinesPageViewModel vm)
                {
                    var currentNode = vm.SpacetimeNodes?.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Current);
                    if (currentNode != null)
                    {
                        currentNode.CreatedDate = DateTime.Now;
                        if (vm.SelectedSpacetimeNode?.NodeType == SpacetimeNodeType.Current)
                        {
                            SelectedNodeTimeText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                        }
                    }
                }
            };
            _liveTimer.Start();

            this.DataContextChanged += (s, e) => {
                if (_boundVm != null)
                {
                    _boundVm.PropertyChanged -= OnVmPropertyChanged;
                    UnsubscribeNodeEvents(_boundVm.SpacetimeNodes); // ★ 新增
                }

                if (DataContext is VirtualMachinesPageViewModel vm)
                {
                    _boundVm = vm;
                    _boundVm.PropertyChanged += OnVmPropertyChanged;
                    _needsInitialCenter = true;

                    SpacetimeScrollViewer.ScrollToHorizontalOffset(0);
                    SpacetimeScrollViewer.ScrollToVerticalOffset(SpacetimeCanvas.Height / 2 - 200);

                    RenderSpacetimeFlow();
                }
            };

            this.Loaded += (s, e) => {
                if (_needsInitialCenter) RenderSpacetimeFlow();
            };
            ApplicationThemeManager.Changed += (theme, color) =>
            {
                Dispatcher.Invoke(RenderSpacetimeFlow);
            };
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isRendering) return;

            if (e.PropertyName == nameof(VirtualMachinesPageViewModel.SpacetimeNodes))
            {
                Debug.WriteLine($"[DRAG] !!! SpacetimeNodes changed -> RenderSpacetimeFlow (isDragging={_isDragging})");
                UnsubscribeNodeEvents(_boundVm?.SpacetimeNodes); // ★ 先解绑旧的
                SubscribeNodeEvents(_boundVm?.SpacetimeNodes);   // ★ 再绑定新的
                RenderSpacetimeFlow();
            }
            else if (e.PropertyName == nameof(VirtualMachinesPageViewModel.SelectedSpacetimeNode))
            {
                Debug.WriteLine($"[DRAG] !!! SelectedSpacetimeNode changed -> RefreshSelectionStyle (isDragging={_isDragging})");
                if (!_isDragging)
                    RefreshSelectionStyle();
            }
        }

        private void RefreshSelectionStyle()
        {
            if (DataContext is not VirtualMachinesPageViewModel vm) return;
            string? selectedId = vm.SelectedSpacetimeNode?.Id;

            foreach (var child in SpacetimeCanvas.Children)
            {
                if (child is not Grid g || g.Tag is not SpacetimeNode node) continue;

                bool isSelected = node.Id == selectedId;
                bool isCurrent = node.NodeType == SpacetimeNodeType.Current;

                if (g.Children[0] is Border previewBox)
                {
                    Brush currentBrush = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue;
                    previewBox.BorderBrush = isSelected ? currentBrush
                        : isCurrent ? currentBrush
                        : (TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.DimGray);
                    previewBox.BorderThickness = new Thickness(isSelected ? 3 : 1);
                }

                if (g.Children[1] is TextBlock tb)
                {
                    tb.Opacity = (isSelected || isCurrent) ? 1.0 : 0.6;
                }

                Canvas.SetZIndex(g, isSelected ? 100 : isCurrent ? 80 : 50);
            }

            SelectedNodeTimeText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();

            if (vm.SelectedSpacetimeNode != null)
            {
                foreach (var child in SpacetimeCanvas.Children)
                {
                    if (child is Grid g && g.Tag is SpacetimeNode node && node.Id == selectedId)
                    {
                        double left = Canvas.GetLeft(g) + 100;
                        double top = Canvas.GetTop(g) + 80;
                        _selectedNodePos = new Point(left, top);
                        break;
                    }
                }
            }
        }


        private void RenderSpacetimeFlow()
        {
            Debug.WriteLine($"[DRAG] >>> RenderSpacetimeFlow ENTER " +
                $"offsetBefore=({SpacetimeScrollViewer?.HorizontalOffset:F1},{SpacetimeScrollViewer?.VerticalOffset:F1}) " +
                $"isDragging={_isDragging}");

            _contentBounds = Rect.Empty;
            if (DataContext is not VirtualMachinesPageViewModel vm || _isRendering) return;

            try
            {
                _isRendering = true;
                SpacetimeCanvas.Children.Clear();
                _treeMap.Clear();
                _subtreeLeafCount.Clear();
                _selectedNodePos = new Point(0, 0);

                var spacetimeList = vm.SpacetimeNodes?.ToList() ?? new List<SpacetimeNode>();
                if (!spacetimeList.Any()) return;

                var root = spacetimeList.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId))
                           ?? spacetimeList.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Genesis)
                           ?? spacetimeList.FirstOrDefault();

                if (root == null) return;

                foreach (var node in spacetimeList)
                {
                    if (string.IsNullOrEmpty(node.ParentId)) continue;
                    if (!_treeMap.ContainsKey(node.ParentId)) _treeMap[node.ParentId] = new List<SpacetimeNode>();
                    _treeMap[node.ParentId].Add(node);
                }

                int totalLeaves = CalculateLeafCounts(root.Id);

                double rowHeight = 200;
                double requiredHeight = totalLeaves * rowHeight;

                int maxDepth = CalculateMaxDepth(root.Id);
                double requiredWidth = 150 + maxDepth * 280 + 300;

                SpacetimeCanvas.Height = Math.Max(2000, requiredHeight + 400);
                SpacetimeCanvas.Width = Math.Max(3000, requiredWidth);

                double startY = (SpacetimeCanvas.Height - requiredHeight) / 2;
                double endY = startY + requiredHeight;

                DrawRecursiveStep(root, 150, startY, endY, vm.SelectedSpacetimeNode);
                DrawWormholeLines();
                if (_needsInitialCenter)
                {
                    _needsInitialCenter = false;
                    Dispatcher.BeginInvoke(new Action(() => CenterOnSelectedNode()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            finally
            {
                _isRendering = false;
                Debug.WriteLine($"[DRAG] <<< RenderSpacetimeFlow EXIT " +
                    $"offsetAfter=({SpacetimeScrollViewer.HorizontalOffset:F1},{SpacetimeScrollViewer.VerticalOffset:F1})");
            }
        }

        private int CalculateMaxDepth(string nodeId)
        {
            if (!_treeMap.TryGetValue(nodeId, out var children) || children.Count == 0)
                return 0;
            return 1 + children.Max(c => CalculateMaxDepth(c.Id));
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            ExportTopologyAsImage();
        }

        public void ExportTopologyAsImage()
        {
            const double scale = 3.0;
            const double padding = 100;

            if (DataContext is not VirtualMachinesPageViewModel vm) return;
            var spacetimeList = vm.SpacetimeNodes?.ToList();
            if (spacetimeList == null || !spacetimeList.Any()) return;

            var offCanvas = new Canvas
            {
                Width = SpacetimeCanvas.Width * scale,
                Height = SpacetimeCanvas.Height * scale,
                Background = Brushes.Transparent
            };

            offCanvas.Measure(new Size(offCanvas.Width, offCanvas.Height));
            offCanvas.Arrange(new Rect(0, 0, offCanvas.Width, offCanvas.Height));

            DrawOffscreen(offCanvas, spacetimeList, vm.SelectedSpacetimeNode, scale);

            offCanvas.Measure(new Size(offCanvas.Width, offCanvas.Height));
            offCanvas.Arrange(new Rect(0, 0, offCanvas.Width, offCanvas.Height));
            offCanvas.UpdateLayout();

            Rect crop;
            if (_contentBounds == Rect.Empty)
            {
                crop = new Rect(0, 0, offCanvas.Width, offCanvas.Height);
            }
            else
            {
                double cx = Math.Max(0, (_contentBounds.X - padding) * scale);
                double cy = Math.Max(0, (_contentBounds.Y - padding) * scale);
                double cw = Math.Min(offCanvas.Width - cx, (_contentBounds.Width + padding * 2) * scale);
                double ch = Math.Min(offCanvas.Height - cy, (_contentBounds.Height + padding * 2) * scale);
                crop = new Rect(cx, cy, cw, ch);
            }

            var rtb = new RenderTargetBitmap(
                (int)offCanvas.Width, (int)offCanvas.Height,
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(offCanvas);

            var cropped = new CroppedBitmap(rtb, new Int32Rect(
                (int)crop.X, (int)crop.Y,
                (int)Math.Min(crop.Width, offCanvas.Width - crop.X),
                (int)Math.Min(crop.Height, offCanvas.Height - crop.Y)));

            string safeName = vm.SelectedVm?.Name ?? "VM";
            string safeNode = vm.SelectedSpacetimeNode?.Name ?? "Node";
            string safeTime = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();
            safeNode = string.Concat(safeNode.Select(c => invalidChars.Contains(c) ? '_' : c));

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.VmSpacetimeSettings_DlgExportTitle,
                Filter = Properties.Resources.VmSpacetimeSettings_DlgExportFilter,
                FileName = $"{safeName}_{safeNode}_{safeTime}.png"
            };

            if (dialog.ShowDialog() != true) return;

            using var stream = new FileStream(dialog.FileName, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            encoder.Save(stream);
        }

        private void DrawOffscreen(Canvas canvas, List<SpacetimeNode> nodes,
                                   SpacetimeNode? selected, double scale)
        {
            var treeMap = new Dictionary<string, List<SpacetimeNode>>();
            var leafCount = new Dictionary<string, int>();

            var root = nodes.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId))
                       ?? nodes.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Genesis)
                       ?? nodes.FirstOrDefault();
            if (root == null) return;

            foreach (var node in nodes)
            {
                if (string.IsNullOrEmpty(node.ParentId)) continue;
                if (!treeMap.ContainsKey(node.ParentId)) treeMap[node.ParentId] = new();
                treeMap[node.ParentId].Add(node);
            }

            int CalcLeaves(string id)
            {
                if (!treeMap.TryGetValue(id, out var ch) || ch.Count == 0) { leafCount[id] = 1; return 1; }
                int c = ch.Sum(x => CalcLeaves(x.Id));
                leafCount[id] = c;
                return c;
            }
            int total = CalcLeaves(root.Id);

            double rowH = 200 * scale;
            double required = total * rowH;
            double startY = (canvas.Height - required) / 2;

            void DrawStep(SpacetimeNode node, double x, double top, double bottom)
            {
                double midY = (top + bottom) / 2;
                DrawOffscreenAnchor(canvas, new Point(x, midY), node, selected?.Id == node.Id, scale);

                if (!treeMap.TryGetValue(node.Id, out var children)) return;
                var sorted = children.OrderBy(c => c.CreatedDate).ToList();
                double curTop = top;
                double totalLeaves = leafCount[node.Id];

                foreach (var child in sorted)
                {
                    double childLeaves = leafCount[child.Id];
                    double sector = (childLeaves / totalLeaves) * (bottom - top);
                    double nextX = x + 280 * scale;
                    double childMidY = curTop + sector / 2;

                    var line = new Line
                    {
                        X1 = x,
                        Y1 = midY,
                        X2 = nextX,
                        Y2 = childMidY,
                        Stroke = new SolidColorBrush(Color.FromArgb(160, 120, 120, 120)),
                        Opacity = 1.0,
                        StrokeThickness = scale,
                        StrokeDashArray = new DoubleCollection { 4, 3 }
                    };
                    Canvas.SetZIndex(line, 5);
                    canvas.Children.Add(line);

                    DrawStep(child, nextX, curTop, curTop + sector);
                    curTop += sector;
                }
            }

            DrawStep(root, 150 * scale, startY, startY + required);
        }

        private void DrawOffscreenAnchor(Canvas canvas, Point pos, SpacetimeNode data,
                                          bool isSelected, double scale)
        {
            bool isCurrent = data.NodeType == SpacetimeNodeType.Current;

            double cardW = 200 * scale;
            double cardH = 160 * scale;
            double previewW = 140 * scale;
            double previewH = 80 * scale;

            var previewBox = new Border
            {
                Width = previewW,
                Height = previewH,
                Background = Brushes.Black,
                BorderBrush = isSelected
                                ? new SolidColorBrush(Color.FromRgb(0, 120, 215))
                                : isCurrent
                                    ? new SolidColorBrush(Color.FromRgb(0, 120, 215))
                                    : new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(isSelected ? 3 * scale : scale),
                CornerRadius = new CornerRadius(4 * scale),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (data.Thumbnail != null)
                previewBox.Background = new ImageBrush(data.Thumbnail) { Stretch = Stretch.UniformToFill };

            var labelBg = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
                CornerRadius = new CornerRadius(3 * scale),
                Padding = new Thickness(6 * scale, 2 * scale, 6 * scale, 2 * scale),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = cardW - 10 * scale
            };

            var label = new TextBlock
            {
                Text = data.Name,
                FontSize = 12 * scale,
                FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 235)),
                TextAlignment = TextAlignment.Center,
                Opacity = (isSelected || isCurrent) ? 1.0 : 0.85,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            labelBg.Child = label;

            var labelPanel = new StackPanel
            {
                Width = cardW,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8 * scale)
            };
            labelPanel.Children.Add(labelBg);

            var group = new Grid { Width = cardW, Height = cardH };
            group.Children.Add(previewBox);
            group.Children.Add(labelPanel);

            Canvas.SetLeft(group, pos.X - cardW / 2);
            Canvas.SetTop(group, pos.Y - cardH / 2);
            Canvas.SetZIndex(group, isSelected ? 100 : isCurrent ? 80 : 50);
            canvas.Children.Add(group);
        }
        private Rect _contentBounds = Rect.Empty;

        private void UpdateContentBounds(double left, double top, double width, double height)
        {
            var rect = new Rect(left, top, width, height);
            _contentBounds = _contentBounds == Rect.Empty ? rect : Rect.Union(_contentBounds, rect);
        }

        private void DrawRecursiveStep(SpacetimeNode node, double x, double top, double bottom, SpacetimeNode? selected)
        {
            double midY = (top + bottom) / 2;

            if (selected != null && node.Id == selected.Id) _selectedNodePos = new Point(x, midY);
            if (node.NodeType == SpacetimeNodeType.Current) _currentNodePos = new Point(x, midY);
            DrawSpacetimeAnchor(new Point(x, midY), node, selected?.Id == node.Id);

            if (_treeMap.TryGetValue(node.Id, out var children))
            {
                var sorted = children.OrderBy(c => c.CreatedDate).ToList();
                double currentTop = top;
                double totalLeavesInThisBranch = _subtreeLeafCount[node.Id];

                foreach (var child in sorted)
                {
                    double childLeafCount = _subtreeLeafCount[child.Id];
                    double childSectorHeight = (childLeafCount / totalLeavesInThisBranch) * (bottom - top);

                    double nextX = x + 280;
                    double childMidY = currentTop + (childSectorHeight / 2);

                    DrawTimeLine(new Point(x, midY), new Point(nextX, childMidY));

                    DrawRecursiveStep(child, nextX, currentTop, currentTop + childSectorHeight, selected);

                    currentTop += childSectorHeight;
                }
            }
        }

        private int CalculateLeafCounts(string nodeId)
        {
            if (!_treeMap.TryGetValue(nodeId, out var children) || children.Count == 0)
            {
                _subtreeLeafCount[nodeId] = 1;
                return 1;
            }

            int count = 0;
            foreach (var child in children)
            {
                count += CalculateLeafCounts(child.Id);
            }
            _subtreeLeafCount[nodeId] = count;
            return count;
        }


        private void DrawRecursive(SpacetimeNode node, double x, double y, double verticalRange, SpacetimeNode? selected)
        {
            if (selected != null && node.Id == selected.Id) _selectedNodePos = new Point(x, y);
            DrawSpacetimeAnchor(new Point(x, y), node, selected?.Id == node.Id);

            if (_treeMap.TryGetValue(node.Id, out var children))
            {
                var sorted = children.OrderBy(c => c.CreatedDate).ToList();
                int count = sorted.Count;

                for (int i = 0; i < count; i++)
                {
                    double gap = Math.Max(verticalRange, (count - 1) * 180);

                    double offset = (count > 1)
                        ? (-gap / 2 + (i * (gap / (count - 1))))
                        : 0;

                    double nextX = x + 260;
                    double nextY = y + offset;

                    DrawTimeLine(new Point(x, y), new Point(nextX, nextY));

                    DrawRecursive(sorted[i], nextX, nextY, gap * 0.8, selected);
                }
            }
        }
        private void DrawSpacetimeAnchor(Point pos, SpacetimeNode data, bool isSelected)
        {
            bool isCurrent = data.NodeType == SpacetimeNodeType.Current;
            var anchorGroup = new Grid { Width = 200, Height = 160, Cursor = Cursors.Hand, Tag = data, Background = null };

            // 改用 MouseLeftButtonUp 触发选中，且不 Handled，事件正常冒泡
            anchorGroup.MouseLeftButtonUp += (s, e) => {
                Debug.WriteLine($"[DRAG] *** Node MouseUp isDragging={_isDragging} " +
                    $"node={(((Grid)s).Tag as SpacetimeNode)?.Name}");
                // 拖动后的 MouseUp 不能触发选中，否则拖完之后会意外切换选中节点
                if (!_isDragging && DataContext is VirtualMachinesPageViewModel vm)
                {
                    vm.SelectedSpacetimeNode = (SpacetimeNode)((Grid)s).Tag;
                }
                // 不要 e.Handled = true
            };

            Brush currentBrush = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue;
            Brush wormholeBrush = new SolidColorBrush(Color.FromRgb(255, 215, 0));
            Brush statusBrush = data.IsWormhole ? wormholeBrush
                              : isSelected ? currentBrush
                              : isCurrent ? currentBrush
                              : (TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.DimGray);
            var previewBox = new Border { Width = 140, Height = 80, Background = Brushes.Black, BorderBrush = statusBrush, BorderThickness = new Thickness((isSelected || data.IsWormhole) ? 3 : 1), CornerRadius = new CornerRadius(4), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };


            if (data.Thumbnail != null) previewBox.Background = new ImageBrush(data.Thumbnail) { Stretch = Stretch.UniformToFill };
            anchorGroup.Children.Add(previewBox);

            anchorGroup.Children.Add(new TextBlock { Text = data.Name, FontSize = 12, FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal, Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"), Width = 180, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 105, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = (isSelected || isCurrent) ? 1.0 : 0.6 });

            Canvas.SetLeft(anchorGroup, pos.X - 100);
            Canvas.SetTop(anchorGroup, pos.Y - 80);
            UpdateContentBounds(pos.X - 100, pos.Y - 80, 200, 160);
            Canvas.SetZIndex(anchorGroup, isSelected ? 100 : (isCurrent ? 80 : 50));
            SpacetimeCanvas.Children.Add(anchorGroup);
        }

        private void DrawTimeLine(Point from, Point to)
        {
            var line = new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Gray,
                Opacity = 0.4,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            Canvas.SetZIndex(line, 5);
            SpacetimeCanvas.Children.Add(line);
        }
        private void DrawWormholeLines()
        {
            if (DataContext is not VirtualMachinesPageViewModel vm) return;
            var wormholeNodes = vm.SpacetimeNodes?
                .Where(n => n.IsWormhole && n.NodeType == SpacetimeNodeType.Snapshot)
                .ToList();
            if (wormholeNodes == null || !wormholeNodes.Any()) return;

            foreach (var wNode in wormholeNodes)
            {
                Point? wPos = null;
                foreach (var child in SpacetimeCanvas.Children)
                {
                    if (child is Grid g && g.Tag is SpacetimeNode n && n.Id == wNode.Id)
                    {
                        wPos = new Point(Canvas.GetLeft(g) + 100, Canvas.GetTop(g) + 80);
                        break;
                    }
                }
                if (wPos == null) continue;

                // 底层：黑色粗实线（作为间隔色背景）
                var lineBlack = new Line
                {
                    X1 = wPos.Value.X,
                    Y1 = wPos.Value.Y,
                    X2 = _currentNodePos.X,
                    Y2 = _currentNodePos.Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    StrokeThickness = 6,
                    Opacity = 0.9,
                };
                Canvas.SetZIndex(lineBlack, 9);
                SpacetimeCanvas.Children.Add(lineBlack);

                // 上层：黄色虚线，与黑色底层叠加形成警戒线
                var lineYellow = new Line
                {
                    X1 = wPos.Value.X,
                    Y1 = wPos.Value.Y,
                    X2 = _currentNodePos.X,
                    Y2 = _currentNodePos.Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(255, 210, 0)),
                    StrokeThickness = 6,
                    Opacity = 0.95,
                    StrokeDashArray = new DoubleCollection { 6, 6 },
                };
                Canvas.SetZIndex(lineYellow, 10);
                SpacetimeCanvas.Children.Add(lineYellow);
            }
        }
        private void CenterOnSelectedNode()
        {
            Debug.WriteLine($"[DRAG] !!! CenterOnSelectedNode isDragging={_isDragging} " +
                $"selectedPos=({_selectedNodePos.X:F1},{_selectedNodePos.Y:F1}) " +
                $"oldOffset=({SpacetimeScrollViewer.HorizontalOffset:F1},{SpacetimeScrollViewer.VerticalOffset:F1})");

            double targetX = _selectedNodePos.X > 0 ? _selectedNodePos.X : 120;
            double targetY = _selectedNodePos.Y > 0 ? _selectedNodePos.Y : SpacetimeCanvas.Height / 2;
            SpacetimeScrollViewer.ScrollToHorizontalOffset(targetX - (SpacetimeScrollViewer.ActualWidth / 2));
            SpacetimeScrollViewer.ScrollToVerticalOffset(targetY - (SpacetimeScrollViewer.ActualHeight / 2));
        }
        private Point _lastMousePos;
        private bool _hasLastPos = false;  // 唯一的状态：上一帧位置是否有效

        private void CanvasContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _lastMousePos = e.GetPosition(this);
            _hasLastPos = true;
            _isDragging = false;
            Debug.WriteLine($"[DRAG] === MouseDown === pos=({_lastMousePos.X:F1},{_lastMousePos.Y:F1})");
        }

        private void CanvasContainer_MouseMove(object sender, MouseEventArgs e)
        {
            // 任何按键都没按下 → 重置基准，下次按下才重新开始
            if (e.LeftButton != MouseButtonState.Pressed &&
                e.MiddleButton != MouseButtonState.Pressed &&
                e.RightButton != MouseButtonState.Pressed)
            {
                _hasLastPos = false;
                _isDragging = false;
                return;
            }

            Point currentPos = e.GetPosition(this);

            // 关键守卫：如果没有有效的上一帧位置（说明 MouseDown 没触发就来了 Move），
            // 当前帧不动作，只记录位置，等下一帧用真实位移
            if (!_hasLastPos)
            {
                _lastMousePos = currentPos;
                _hasLastPos = true;
                return;
            }

            double frameDeltaX = _lastMousePos.X - currentPos.X;
            double frameDeltaY = _lastMousePos.Y - currentPos.Y;

            // 单帧位移过大（超过 50 像素）= 异常，直接丢弃这一帧
            // 正常人手单帧不可能移动这么多，这种数据一定是事件错乱导致
            if (Math.Abs(frameDeltaX) > 50 || Math.Abs(frameDeltaY) > 50)
            {
                Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeSettings_LogAbnormalDelta, frameDeltaX, frameDeltaY));
                _lastMousePos = currentPos;
                return;
            }

            if (!_isDragging)
            {
                if (Math.Abs(frameDeltaX) < 2 && Math.Abs(frameDeltaY) < 2)
                    return;  // 抖动忽略，不更新基准

                _isDragging = true;
                CanvasContainer.CaptureMouse();
                CanvasContainer.Cursor = Cursors.SizeAll;
                SpacetimeCanvas.IsHitTestVisible = false;
                Debug.WriteLine($"[DRAG] === DragStart === pos=({currentPos.X:F1},{currentPos.Y:F1}) delta=({frameDeltaX:F1},{frameDeltaY:F1})");
            }

            SpacetimeScrollViewer.ScrollToHorizontalOffset(
                SpacetimeScrollViewer.HorizontalOffset + frameDeltaX);
            SpacetimeScrollViewer.ScrollToVerticalOffset(
                SpacetimeScrollViewer.VerticalOffset + frameDeltaY);

            _lastMousePos = currentPos;
            e.Handled = true;
        }

        private void CanvasContainer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            bool wasDragging = _isDragging;
            _isDragging = false;
            _hasLastPos = false;   // ← 关键：松开就清掉，下次必须重新 MouseDown 才能拖

            if (CanvasContainer.IsMouseCaptured)
            {
                CanvasContainer.ReleaseMouseCapture();
                CanvasContainer.Cursor = Cursors.Arrow;
            }

            SpacetimeCanvas.IsHitTestVisible = true;
            Debug.WriteLine($"[DRAG] === MouseUp === wasDragging={wasDragging}");

            if (wasDragging) e.Handled = true;
        }

        private void CanvasContainer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (CanvasContainer.IsMouseCaptured)
            {
                _isDragging = false;
                _hasLastPos = false;
                CanvasContainer.ReleaseMouseCapture();
                SpacetimeCanvas.IsHitTestVisible = true;
                CanvasContainer.Cursor = Cursors.Arrow;
            }
        }
        private void SubscribeNodeEvents(IEnumerable<SpacetimeNode>? nodes)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
                node.PropertyChanged += OnNodePropertyChanged;
        }

        private void UnsubscribeNodeEvents(IEnumerable<SpacetimeNode>? nodes)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
                node.PropertyChanged -= OnNodePropertyChanged;
        }

        private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SpacetimeNode.IsWormhole)) return;

            Dispatcher.Invoke(() =>
            {
                // 只清掉虫洞线（ZIndex 9 和 10），不重绘整个画布
                var toRemove = SpacetimeCanvas.Children
                    .OfType<Line>()
                    .Where(l => Canvas.GetZIndex(l) == 9 || Canvas.GetZIndex(l) == 10)
                    .ToList();
                foreach (var l in toRemove)
                    SpacetimeCanvas.Children.Remove(l);

                // 重画虫洞线 + 刷新节点边框颜色
                DrawWormholeLines();
                RefreshSelectionStyle();
            });
        }
    }
}