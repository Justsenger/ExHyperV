using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ExHyperV.Models;
using ExHyperV.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
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

                // 3. 【核心修复】计算实际需要的垂直空间
                // 每个叶子占 200px 足够了，不再盲目使用 Canvas.Height
                double rowHeight = 200;
                double requiredHeight = totalLeaves * rowHeight;

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
            var anchorGroup = new Grid { Width = 200, Height = 160, Cursor = Cursors.Hand, Tag = data, Background = Brushes.Transparent };

            anchorGroup.MouseDown += (s, e) => {
                if (!_isDragging && e.ChangedButton == MouseButton.Left && DataContext is VirtualMachinesPageViewModel vm)
                {
                    vm.SelectedSpacetimeNode = (SpacetimeNode)((Grid)s).Tag;
                    e.Handled = true;
                }
            };

            Brush statusBrush = isSelected ? (Brush)FindResource("SystemAccentColorPrimaryBrush") : (isCurrent ? Brushes.SpringGreen : Brushes.DimGray);
            var previewBox = new Border { Width = 140, Height = 80, Background = Brushes.Black, BorderBrush = statusBrush, BorderThickness = new Thickness(isSelected ? 3 : 1), CornerRadius = new CornerRadius(4), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            if (data.Thumbnail != null) previewBox.Background = new ImageBrush(data.Thumbnail) { Stretch = Stretch.UniformToFill };
            anchorGroup.Children.Add(previewBox);

            anchorGroup.Children.Add(new TextBlock { Text = data.Name, FontSize = 12, FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal, Foreground = isCurrent ? Brushes.SpringGreen : (Brush)FindResource("TextFillColorPrimaryBrush"), Width = 180, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 105, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = (isSelected || isCurrent) ? 1.0 : 0.6 });

            Canvas.SetLeft(anchorGroup, pos.X - 100);
            Canvas.SetTop(anchorGroup, pos.Y - 80);
            Canvas.SetZIndex(anchorGroup, isSelected ? 100 : (isCurrent ? 80 : 50));
            SpacetimeCanvas.Children.Add(anchorGroup);
        }

        private void DrawTimeLine(Point from, Point to)
        {
            var line = new Line { X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y, Stroke = Brushes.White, Opacity = 0.15, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } };
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
            if (e.LeftButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            {
                Point currentPos = e.GetPosition(this); double deltaX = _dragStartPos.X - currentPos.X; double deltaY = _dragStartPos.Y - currentPos.Y;
                if (!_isDragging && (Math.Abs(deltaX) > 5 || Math.Abs(deltaY) > 5)) { _isDragging = true; CanvasContainer.CaptureMouse(); CanvasContainer.Cursor = Cursors.SizeAll; }
                if (_isDragging) { SpacetimeScrollViewer.ScrollToHorizontalOffset(_dragStartOffset.X + deltaX); SpacetimeScrollViewer.ScrollToVerticalOffset(_dragStartOffset.Y + deltaY); e.Handled = true; }
            }
        }
        private void CanvasContainer_MouseUp(object sender, MouseButtonEventArgs e) { if (CanvasContainer.IsMouseCaptured) { CanvasContainer.ReleaseMouseCapture(); CanvasContainer.Cursor = Cursors.Arrow; if (_isDragging) e.Handled = true; } }
        private void CanvasContainer_MouseLeave(object sender, MouseEventArgs e) { if (CanvasContainer.IsMouseCaptured) CanvasContainer.ReleaseMouseCapture(); }
    }
}