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
                if (_boundVm != null) _boundVm.PropertyChanged -= OnVmPropertyChanged;

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

            CanvasContainer.PreviewMouseDown += CanvasContainer_MouseDown;
            CanvasContainer.PreviewMouseMove += CanvasContainer_MouseMove;
            CanvasContainer.PreviewMouseUp += CanvasContainer_MouseUp;
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // 监听 IsLoadingSettings 以便在数据刷新前立刻显示遮罩
            if (e.PropertyName == nameof(VirtualMachinesPageViewModel.IsLoadingSettings))
            {
                // 这里的 Visibility 已经在 XAML 绑定了，但如果需要额外的 UI 逻辑可以在此处理
            }

            if (_isRendering) return;
            if (e.PropertyName == nameof(VirtualMachinesPageViewModel.SpacetimeNodes) ||
                e.PropertyName == nameof(VirtualMachinesPageViewModel.SelectedSpacetimeNode))
            {
                RenderSpacetimeFlow();
            }
        }


        private void RenderSpacetimeFlow()
        {
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

                // 1. 建立树结构
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

                // 2. 预计算每个节点的叶子权重
                int totalLeaves = CalculateLeafCounts(root.Id);

                // 3. 动态扩展画布，防止内容溢出
                double rowHeight = 200;
                double requiredHeight = totalLeaves * rowHeight;

                int maxDepth = CalculateMaxDepth(root.Id);
                double requiredWidth = 150 + maxDepth * 280 + 300;

                SpacetimeCanvas.Height = Math.Max(2000, requiredHeight + 400);
                SpacetimeCanvas.Width = Math.Max(3000, requiredWidth);

                // 计算起始位置使其在 Canvas 中间居中
                double startY = (SpacetimeCanvas.Height - requiredHeight) / 2;
                double endY = startY + requiredHeight;

                // 4. 开始递归绘图
                DrawRecursiveStep(root, 150, startY, endY, vm.SelectedSpacetimeNode);
                if (_needsInitialCenter)
                {
                    _needsInitialCenter = false;
                    Dispatcher.BeginInvoke(new Action(() => CenterOnSelectedNode()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            finally { _isRendering = false; }
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
            const double scale = 3.0;   // 真正的 3x 离屏绘制，非插值放大
            const double padding = 100;

            if (DataContext is not VirtualMachinesPageViewModel vm) return;
            var spacetimeList = vm.SpacetimeNodes?.ToList();
            if (spacetimeList == null || !spacetimeList.Any()) return;

            // ── 1. 在离屏 Canvas 上重绘，完全不碰界面 ──────────────────────────
            var offCanvas = new Canvas
            {
                Width = SpacetimeCanvas.Width * scale,
                Height = SpacetimeCanvas.Height * scale,
                Background = Brushes.Transparent   // 透明背景
            };

            // 强制 Measure/Arrange，否则子元素 ActualWidth 全为 0
            offCanvas.Measure(new Size(offCanvas.Width, offCanvas.Height));
            offCanvas.Arrange(new Rect(0, 0, offCanvas.Width, offCanvas.Height));

            // ── 2. 重新建树（复用已有的 _treeMap / _subtreeLeafCount）──────────
            // 注意：这里不重建树，直接用现有数据重绘到离屏 Canvas
            DrawOffscreen(offCanvas, spacetimeList, vm.SelectedSpacetimeNode, scale);

            // 二次 Measure/Arrange，让新添加的子元素也完成布局
            offCanvas.Measure(new Size(offCanvas.Width, offCanvas.Height));
            offCanvas.Arrange(new Rect(0, 0, offCanvas.Width, offCanvas.Height));
            offCanvas.UpdateLayout();

            // ── 3. 计算裁剪区域（_contentBounds 是 1x 坐标，乘以 scale）────────
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

            // ── 4. 渲染离屏 Canvas → 位图（不影响界面任何元素）─────────────────
            var rtb = new RenderTargetBitmap(
                (int)offCanvas.Width, (int)offCanvas.Height,
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(offCanvas);

            // ── 5. 裁剪 ──────────────────────────────────────────────────────────
            var cropped = new CroppedBitmap(rtb, new Int32Rect(
                (int)crop.X, (int)crop.Y,
                (int)Math.Min(crop.Width, offCanvas.Width - crop.X),
                (int)Math.Min(crop.Height, offCanvas.Height - crop.Y)));

            // ── 6. 保存 ──────────────────────────────────────────────────────────
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出时空拓扑图",
                Filter = "PNG 图片|*.png",
                FileName = $"{vm.SelectedVm?.Name}_{vm.SelectedSpacetimeNode?.Name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"
            };
            if (dialog.ShowDialog() != true) return;

            using var stream = new FileStream(dialog.FileName, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            encoder.Save(stream);
        }

        /// <summary>
        /// 在离屏 Canvas 上以 scale 倍坐标重新绘制整棵树，不触碰任何界面元素。
        /// </summary>
        private void DrawOffscreen(Canvas canvas, List<SpacetimeNode> nodes,
                                   SpacetimeNode? selected, double scale)
        {
            // 重建树结构（离屏专用，不影响 _treeMap）
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

            // 计算叶子数
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

            // 递归绘制
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

                    // 绘制连线
                    var line = new Line
                    {
                        X1 = x,
                        Y1 = midY,
                        X2 = nextX,
                        Y2 = childMidY,
                        Stroke = new SolidColorBrush(Color.FromArgb(160, 120, 120, 120)), // 半透明灰，深浅主题都可见
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

            // 缩略图预览框
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

            // 标签底板
            var labelBg = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
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
                Foreground = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                TextAlignment = TextAlignment.Center,
                Opacity = (isSelected || isCurrent) ? 1.0 : 0.85,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            labelBg.Child = label;

            // 用 StackPanel 替代手动定位
            var labelPanel = new StackPanel
            {
                Width = cardW,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8 * scale)
            };
            labelPanel.Children.Add(labelBg);

            // 组合
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
            // 节点垂直居于它分配到的扇区中心
            double midY = (top + bottom) / 2;

            if (selected != null && node.Id == selected.Id) _selectedNodePos = new Point(x, midY);
            DrawSpacetimeAnchor(new Point(x, midY), node, selected?.Id == node.Id);

            if (_treeMap.TryGetValue(node.Id, out var children))
            {
                // 按创建时间排序
                var sorted = children.OrderBy(c => c.CreatedDate).ToList();
                double currentTop = top;
                double totalLeavesInThisBranch = _subtreeLeafCount[node.Id];

                foreach (var child in sorted)
                {
                    // 获取该子分支占用的叶子比例
                    double childLeafCount = _subtreeLeafCount[child.Id];
                    double childSectorHeight = (childLeafCount / totalLeavesInThisBranch) * (bottom - top);

                    // 水平间距 280 比较美观
                    double nextX = x + 280;
                    double childMidY = currentTop + (childSectorHeight / 2);

                    DrawTimeLine(new Point(x, midY), new Point(nextX, childMidY));

                    // 递归分配子扇区
                    DrawRecursiveStep(child, nextX, currentTop, currentTop + childSectorHeight, selected);

                    currentTop += childSectorHeight;
                }
            }
        }        // 递归计算每个节点控制的叶子节点数量（权重）
        private int CalculateLeafCounts(string nodeId)
        {
            if (!_treeMap.TryGetValue(nodeId, out var children) || children.Count == 0)
            {
                _subtreeLeafCount[nodeId] = 1; // 自己就是叶子
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
                    // 改进：确保即便分叉很多，垂直间距也不低于 180 (节点高160 + 20间隙)
                    double gap = Math.Max(verticalRange, (count - 1) * 180);

                    double offset = (count > 1)
                        ? (-gap / 2 + (i * (gap / (count - 1))))
                        : 0;

                    double nextX = x + 260; // 稍微拉大水平间距（从240改为260）
                    double nextY = y + offset;

                    DrawTimeLine(new Point(x, y), new Point(nextX, nextY));

                    // 衰减系数改为 0.8，防止空间收缩过快
                    DrawRecursive(sorted[i], nextX, nextY, gap * 0.8, selected);
                }
            }
        }
        private void DrawSpacetimeAnchor(Point pos, SpacetimeNode data, bool isSelected)
        {
            bool isCurrent = data.NodeType == SpacetimeNodeType.Current;
            var anchorGroup = new Grid { Width = 200, Height = 160, Cursor = Cursors.Hand, Tag = data, Background = null };

            anchorGroup.MouseDown += (s, e) => {
                if (!_isDragging && e.ChangedButton == MouseButton.Left && DataContext is VirtualMachinesPageViewModel vm)
                {
                    vm.SelectedSpacetimeNode = (SpacetimeNode)((Grid)s).Tag;
                    e.Handled = true;
                }
            };

            Brush currentBrush = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue;
            Brush statusBrush = isSelected ? currentBrush : (isCurrent ? currentBrush : (TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.DimGray));
            var previewBox = new Border { Width = 140, Height = 80, Background = Brushes.Black, BorderBrush = statusBrush, BorderThickness = new Thickness(isSelected ? 3 : 1), CornerRadius = new CornerRadius(4), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

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
                Opacity = 0.4,          // 从 0.25 提高到 0.4，白色主题下也看得清
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            Canvas.SetZIndex(line, 5);
            SpacetimeCanvas.Children.Add(line);
        }

        private void CenterOnSelectedNode()
        {
            double targetX = _selectedNodePos.X > 0 ? _selectedNodePos.X : 120;
            double targetY = _selectedNodePos.Y > 0 ? _selectedNodePos.Y : SpacetimeCanvas.Height / 2;
            SpacetimeScrollViewer.ScrollToHorizontalOffset(targetX - (SpacetimeScrollViewer.ActualWidth / 2));
            SpacetimeScrollViewer.ScrollToVerticalOffset(targetY - (SpacetimeScrollViewer.ActualHeight / 2));
        }

        private void CanvasContainer_MouseDown(object sender, MouseButtonEventArgs e) { _dragStartPos = e.GetPosition(this); _dragStartOffset = new Point(SpacetimeScrollViewer.HorizontalOffset, SpacetimeScrollViewer.VerticalOffset); _isDragging = false; }
        private void CanvasContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed ||
                e.MiddleButton == MouseButtonState.Pressed ||
                e.RightButton == MouseButtonState.Pressed)
            {
                Point currentPos = e.GetPosition(this);
                double deltaX = _dragStartPos.X - currentPos.X;
                double deltaY = _dragStartPos.Y - currentPos.Y;

                if (!_isDragging && (Math.Abs(deltaX) > 5 || Math.Abs(deltaY) > 5))
                {
                    _isDragging = true;
                    CanvasContainer.CaptureMouse();
                    CanvasContainer.Cursor = Cursors.SizeAll;

                    // 拖动开始：关闭画布内所有子元素的命中测试，防止触发节点选中导致重绘
                    SpacetimeCanvas.IsHitTestVisible = false;
                }

                if (_isDragging)
                {
                    SpacetimeScrollViewer.ScrollToHorizontalOffset(_dragStartOffset.X + deltaX);
                    SpacetimeScrollViewer.ScrollToVerticalOffset(_dragStartOffset.Y + deltaY);
                    e.Handled = true;
                }
            }
        }

        private void CanvasContainer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (CanvasContainer.IsMouseCaptured)
            {
                CanvasContainer.ReleaseMouseCapture();
                CanvasContainer.Cursor = Cursors.Arrow;

                // 拖动结束：恢复命中测试
                SpacetimeCanvas.IsHitTestVisible = true;

                if (_isDragging) e.Handled = true;
            }
            _isDragging = false;
        }

        private void CanvasContainer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (CanvasContainer.IsMouseCaptured)
            {
                CanvasContainer.ReleaseMouseCapture();
                // 离开也要恢复
                SpacetimeCanvas.IsHitTestVisible = true;
            }
        }
    }
}