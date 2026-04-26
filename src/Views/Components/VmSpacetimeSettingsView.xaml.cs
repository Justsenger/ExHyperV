using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ExHyperV.Models;
using ExHyperV.ViewModels;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using WPFPoint = System.Windows.Point;
using WPFCursors = System.Windows.Input.Cursors;

namespace ExHyperV.Views.Components
{
    public partial class VmSpacetimeSettingsView : UserControl
    {
        private Dictionary<string, List<SpacetimeNode>> _treeMap = new();
        private Dictionary<string, WPFPoint> _nodePositions = new();
        private WPFPoint _dragStartPos;
        private WPFPoint _dragStartOffset;
        private System.Windows.Threading.DispatcherTimer _liveTimer;
        private SpacetimeNode _liveNodeObject; // 缓存虚拟节点以便更新时间

        public VmSpacetimeSettingsView()
        {
            InitializeComponent();

            // 初始化实时计时器
            _liveTimer = new System.Windows.Threading.DispatcherTimer();
            _liveTimer.Interval = TimeSpan.FromSeconds(1);
            _liveTimer.Tick += (s, e) => {
                // 1. 只有当对象存在且被选中时才操作
                if (_liveNodeObject != null)
                {
                    _liveNodeObject.CreatedDate = DateTime.Now;

                    // 2. 检查当前 UI 选中的是不是“当前时空”
                    if (DataContext is VirtualMachinesPageViewModel vm &&
                        vm.SelectedSpacetimeNode?.Id == "LIVE_POINTER")
                    {
                        // 3. 强制 UI 重新读取 CreatedDate 属性并显示
                        SelectedNodeTimeText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                    }
                }
            };
            _liveTimer.Start();

            this.DataContextChanged += (s, e) => {
                if (DataContext is VirtualMachinesPageViewModel vm)
                {
                    vm.PropertyChanged += (ps, pe) => {
                        if (pe.PropertyName == nameof(VirtualMachinesPageViewModel.SpacetimeNodes) ||
                            pe.PropertyName == nameof(VirtualMachinesPageViewModel.SelectedSpacetimeNode))
                        {
                            RenderSpacetimeFlow();
                        }
                    };
                    RenderSpacetimeFlow();

                    Dispatcher.BeginInvoke(new Action(() => {
                        SpacetimeScrollViewer.ScrollToVerticalOffset(SpacetimeCanvas.Height / 2 - SpacetimeScrollViewer.ActualHeight / 2);
                        SpacetimeScrollViewer.ScrollToHorizontalOffset(50);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            };
        }

        private void RenderSpacetimeFlow()
        {
            if (DataContext is not VirtualMachinesPageViewModel vm) return;

            SpacetimeCanvas.Children.Clear();
            _treeMap.Clear();

            var spacetimeList = vm.SpacetimeNodes?.ToList() ?? new List<SpacetimeNode>();

            // 1. 初始化根节点
            var genesisNode = new SpacetimeNode
            {
                Id = "GENESIS_NODE",
                Name = "主时空",
                VirtualSystemType = "Root",
                CreatedDate = spacetimeList.Any() ? spacetimeList.Min(n => n.CreatedDate).AddHours(-1) : DateTime.Now
            };

            // 2. 构建树
            foreach (var node in spacetimeList)
            {
                string parent = node.ParentId ?? "GENESIS_NODE";
                if (!_treeMap.ContainsKey(parent)) _treeMap[parent] = new List<SpacetimeNode>();
                _treeMap[parent].Add(node);
            }

            // 3. 复用或创建 _liveNodeObject
            var currentRunningBase = spacetimeList.FirstOrDefault(n => n.IsCurrent) ?? genesisNode;

            if (_liveNodeObject == null)
            {
                _liveNodeObject = new SpacetimeNode
                {
                    Id = "LIVE_POINTER",
                    Name = "当前时空"
                };
            }

            _liveNodeObject.ParentId = currentRunningBase.Id;
            if (_liveNodeObject.CreatedDate == default) _liveNodeObject.CreatedDate = DateTime.Now;

            if (!_treeMap.ContainsKey(currentRunningBase.Id)) _treeMap[currentRunningBase.Id] = new List<SpacetimeNode>();
            _treeMap[currentRunningBase.Id].Add(_liveNodeObject);

            // 4. 【关键修复】：确保自动选中当前时空
            // 逻辑：如果之前没选节点，或者之前选的就是旧的实时节点，则强制选中最新的 _liveNodeObject
            if (vm.SelectedSpacetimeNode == null || vm.SelectedSpacetimeNode.Id == "LIVE_POINTER")
            {
                vm.SelectedSpacetimeNode = _liveNodeObject;
            }

            // 绘图时必须基于最新的选中节点，否则蓝色边框不显示
            var finalSelected = vm.SelectedSpacetimeNode;

            // 5. 绘图参数
            double startX = 120;
            double centerY = SpacetimeCanvas.Height / 2;
            double initialVerticalRange = 220;

            // 【关键】：传入 finalSelected 确保绘制选中边框
            DrawRecursive(genesisNode, startX, centerY, initialVerticalRange, finalSelected);
        }
        private void DrawRecursive(SpacetimeNode node, double x, double y, double verticalRange, SpacetimeNode? selected)
        {
            WPFPoint currentPos = new WPFPoint(x, y);
            DrawSpacetimeAnchor(currentPos, node.Name, false, node, selected?.Id == node.Id);

            if (_treeMap.TryGetValue(node.Id, out var children))
            {
                var sortedChildren = children.OrderBy(c => c.CreatedDate).ToList();
                int count = sortedChildren.Count;
                double nextX = x + 180; // 水平间距

                for (int i = 0; i < count; i++)
                {
                    double offset = (count > 1) ? (-verticalRange / 2 + (i * (verticalRange / (count - 1)))) : 0;
                    double nextY = y + offset;

                    DrawTimeLine(currentPos, new WPFPoint(nextX, nextY));
                    DrawRecursive(sortedChildren[i], nextX, nextY, verticalRange * 0.7, selected);
                }
            }
        }

        private void DrawTimeLine(WPFPoint from, WPFPoint to)
        {
            var line = new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = Brushes.White,
                Opacity = 0.1,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            Canvas.SetZIndex(line, 5);
            SpacetimeCanvas.Children.Add(line);
        }
        private void DrawSpacetimeAnchor(WPFPoint pos, string name, bool isCurrent, SpacetimeNode data, bool isSelected)
        {
            bool isLive = data.Id == "LIVE_POINTER";

            var anchorGroup = new Grid
            {
                Width = 200,
                Height = 160,
                Cursor = WPFCursors.Hand,
                Tag = data,
                Background = Brushes.Transparent
            };

            anchorGroup.MouseDown += (s, e) => {
                if (DataContext is VirtualMachinesPageViewModel vm)
                    vm.SelectedSpacetimeNode = (SpacetimeNode)((Grid)s).Tag;
                e.Handled = true;
            };

            // 状态色：选中蓝，当前绿，其余灰
            Brush statusBrush = isSelected ? (Brush)FindResource("SystemAccentColorPrimaryBrush") :
                               (isLive ? Brushes.SpringGreen : Brushes.DimGray);

            // --- 统一标准黑框 ---
            var previewBox = new Border
            {
                Width = 120,
                Height = 68,
                Background = Brushes.Black,
                BorderBrush = statusBrush,
                BorderThickness = new Thickness((isSelected || isLive) ? 2.5 : 0.5),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, Opacity = 0.5, ShadowDepth = 2 }
            };

            // 仅非 LIVE 节点显示截图
            if (!isLive && data.Thumbnail != null)
            {
                previewBox.Background = new ImageBrush(data.Thumbnail) { Stretch = Stretch.UniformToFill };
            }
            anchorGroup.Children.Add(previewBox);

            // --- 统一标签样式 ---
            var label = new TextBlock
            {
                Text = name,
                FontSize = 12,
                FontWeight = isLive ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isLive ? Brushes.SpringGreen : (Brush)FindResource("TextFillColorPrimaryBrush"),
                Width = 180,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 86, 0, 0), // 矩形下边缘外侧
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = (isSelected || isLive) ? 1.0 : 0.6
            };
            anchorGroup.Children.Add(label);

            Canvas.SetLeft(anchorGroup, pos.X - 100);
            Canvas.SetTop(anchorGroup, pos.Y - 80);
            Canvas.SetZIndex(anchorGroup, isSelected ? 100 : (isLive ? 80 : 50));
            SpacetimeCanvas.Children.Add(anchorGroup);
        }
        private void CanvasContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle)
            {
                _dragStartPos = e.GetPosition(this);
                _dragStartOffset = new WPFPoint(SpacetimeScrollViewer.HorizontalOffset, SpacetimeScrollViewer.VerticalOffset);
                CanvasContainer.CaptureMouse();
                if (e.ChangedButton == MouseButton.Middle) CanvasContainer.Cursor = WPFCursors.SizeAll;
            }
        }

        private void CanvasContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (CanvasContainer.IsMouseCaptured)
            {
                WPFPoint currentPos = e.GetPosition(this);
                double dX = _dragStartPos.X - currentPos.X;
                double dY = _dragStartPos.Y - currentPos.Y;
                SpacetimeScrollViewer.ScrollToHorizontalOffset(_dragStartOffset.X + dX);
                SpacetimeScrollViewer.ScrollToVerticalOffset(_dragStartOffset.Y + dY);
            }
        }

        private void CanvasContainer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (CanvasContainer.IsMouseCaptured)
            {
                CanvasContainer.ReleaseMouseCapture();
                CanvasContainer.Cursor = WPFCursors.Arrow;
            }
        }

        private void CanvasContainer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (CanvasContainer.IsMouseCaptured) CanvasContainer.ReleaseMouseCapture();
        }
    }
}