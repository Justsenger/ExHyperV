// 文件路径: src/ViewModels/CoreViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.ObjectModel;

namespace ExHyperV.ViewModels
{
    public partial class CoreViewModel : ObservableObject
    {
        [ObservableProperty] private int _coreId;
        [ObservableProperty] private double _currentUsage;

        public ISeries[] Series { get; }
        private readonly ObservableCollection<double> _usageHistory = new();

        // 这是正确的做法：在 ViewModel 中定义坐标轴
        public Axis[] XAxes { get; set; } = { new Axis { IsVisible = false } };
        public Axis[] YAxes { get; set; } = { new Axis { IsVisible = false, MinLimit = 0, MaxLimit = 100 } };

        public CoreViewModel(int coreId)
        {
            CoreId = coreId;
            Series = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = _usageHistory,
                    GeometrySize = 0,
                    Fill = new SolidColorPaint(SKColors.CornflowerBlue.WithAlpha(90)),
                    Stroke = new SolidColorPaint(SKColors.CornflowerBlue) { StrokeThickness = 1 }
                }
            };
        }

        public void AddDataPoint(double usage)
        {
            CurrentUsage = usage;
            _usageHistory.Add(usage);
            if (_usageHistory.Count > 60) _usageHistory.RemoveAt(0);
        }
    }
}