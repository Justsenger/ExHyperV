// 文件路径: src/ViewModels/HostCpuViewModel.cs
// (这个文件几乎没有变化，只是为了确保代码同步)
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Linq;

namespace ExHyperV.ViewModels
{
    public partial class HostCpuViewModel : ObservableObject
    {
        [ObservableProperty] private string _cpuName = "宿主机 CPU";
        [ObservableProperty] private double _totalUsage;

        public ISeries[] TotalUsageSeries { get; }
        private readonly ObservableCollection<double> _totalUsageHistory = new();
        public ObservableCollection<CoreViewModel> Cores { get; } = new();

        public HostCpuViewModel()
        {
            TotalUsageSeries = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = _totalUsageHistory,
                    GeometrySize = 0,
                    Fill = new SolidColorPaint(SKColors.CornflowerBlue.WithAlpha(90)),
                    Stroke = new SolidColorPaint(SKColors.CornflowerBlue) { StrokeThickness = 2 }
                }
            };
        }

        public void UpdateData(HostCpuUsage data)
        {
            TotalUsage = data.TotalUsage;
            _totalUsageHistory.Add(data.TotalUsage);
            if (_totalUsageHistory.Count > 60) _totalUsageHistory.RemoveAt(0);

            if (Cores.Count == 0 && data.CoreUsages.Any())
            {
                for (int i = 0; i < data.CoreUsages.Count; i++) Cores.Add(new CoreViewModel(i));
            }

            if (Cores.Count == data.CoreUsages.Count)
            {
                for (int i = 0; i < Cores.Count; i++) Cores[i].AddDataPoint(data.CoreUsages[i]);
            }
        }
    }
}