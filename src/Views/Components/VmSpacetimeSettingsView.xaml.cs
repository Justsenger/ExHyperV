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
                _selectedNodePos = new Point(0, 0);

                var spacetimeList = vm.SpacetimeNodes?.ToList() ?? new List<SpacetimeNode>();
                if (!spacetimeList.Any()) return;
                var root = spacetimeList.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId));
                if (root == null) root = spacetimeList.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Genesis)
                                       ?? spacetimeList.FirstOrDefault();
                if (root == null) return;
                foreach (var node in spacetimeList)
                {
                    if (string.IsNullOrEmpty(node.ParentId)) continue;
                    if (!_treeMap.ContainsKey(node.ParentId)) _treeMap[node.ParentId] = new List<SpacetimeNode>();
                    _treeMap[node.ParentId].Add(node);
                }
                if (_needsInitialCenter || vm.SelectedSpacetimeNode == null)
                {
                    vm.SelectedSpacetimeNode = spacetimeList.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Current);
                }
                DrawRecursive(root, 120, SpacetimeCanvas.Height / 2, 240, vm.SelectedSpacetimeNode);
                if (_needsInitialCenter)
                {
                    _needsInitialCenter = false;
                    Dispatcher.BeginInvoke(new Action(() => CenterOnSelectedNode()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            finally { _isRendering = false; }
        }
        private void DrawRecursive(SpacetimeNode node, double x, double y, double verticalRange, SpacetimeNode? selected)
        {
            if (selected != null && node.Id == selected.Id) _selectedNodePos = new Point(x, y);
            DrawSpacetimeAnchor(new Point(x, y), node, selected?.Id == node.Id);

            if (_treeMap.TryGetValue(node.Id, out var children))
            {
                var sorted = children.OrderBy(c => c.CreatedDate).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    double offset = (sorted.Count > 1) ? (-verticalRange / 2 + (i * (verticalRange / (sorted.Count - 1)))) : 0;
                    double nextX = x + 240, nextY = y + offset;
                    DrawTimeLine(new Point(x, y), new Point(nextX, nextY));
                    DrawRecursive(sorted[i], nextX, nextY, verticalRange * 0.75, selected);
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