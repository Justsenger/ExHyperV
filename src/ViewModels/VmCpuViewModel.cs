// 文件路径: src/ViewModels/VmCpuViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using LiveChartsCore; // 新增 using
using LiveChartsCore.SkiaSharpView; // 新增 using
using LiveChartsCore.SkiaSharpView.Painting; // 新增 using
using SkiaSharp; // 新增 using
using System.Collections.ObjectModel;

namespace ExHyperV.ViewModels
{
    public partial class VmCpuViewModel : ObservableObject
    {
        [ObservableProperty] private string _vmName;
        [ObservableProperty] private double _averageUsage;

        // ▼▼▼ 【核心修改】直接定义 Series 属性 ▼▼▼
        public ISeries[] Series { get; private set; }
        private readonly ObservableCollection<double> _usageHistory = new();
        // ▲▲▲ 【核心修改】结束 ▲▲▲

        public VmCpuViewModel(VmCpuUsage model)
        {
            VmName = model.VmName;
            // 初始化 Series
            Series = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = _usageHistory,
                    GeometrySize = 0,
                    Fill = new SolidColorPaint(SKColors.CornflowerBlue.WithAlpha(90)),
                    Stroke = new SolidColorPaint(SKColors.CornflowerBlue) { StrokeThickness = 2 }
                }
            };
            UpdateData(model);
        }

        public void UpdateData(VmCpuUsage data)
        {
            AverageUsage = data.AverageUsage;
            _usageHistory.Add(data.AverageUsage);
            if (_usageHistory.Count > 30) _usageHistory.RemoveAt(0);
        }
    }
}