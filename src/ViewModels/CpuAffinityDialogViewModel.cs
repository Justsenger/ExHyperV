using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace ExHyperV.ViewModels.Dialogs
{
    public partial class CpuAffinityDialogViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _title;

        // 恢复 Columns 和 Rows 属性，用于驱动 UniformGrid
        [ObservableProperty]
        private int _columns;

        [ObservableProperty]
        private int _rows;

        [ObservableProperty]
        private string _statusText;

        [ObservableProperty]
        private string _statusEmoji;

        private readonly int _assignedCoreCount;

        public ObservableCollection<SelectableCoreViewModel> Cores { get; } = new();

        public CpuAffinityDialogViewModel(string vmName, int assignedCoreCount, ObservableCollection<UiCoreModel> hostCores)
        {
            Title = $"为 {vmName} 设置 CPU 绑定";
            _assignedCoreCount = assignedCoreCount;

            // 使用与主界面完全相同的布局计算逻辑
            Columns = CalculateOptimalColumns(hostCores.Count);
            Rows = (hostCores.Count > 0) ? (int)Math.Ceiling((double)hostCores.Count / Columns) : 0;

            foreach (var core in hostCores.OrderBy(c => c.CoreId))
            {
                var selectableCore = new SelectableCoreViewModel { CoreId = core.CoreId, CoreType = core.CoreType, IsSelected = false };
                selectableCore.PropertyChanged += OnCoreSelectionChanged;
                Cores.Add(selectableCore);
            }

            UpdateStatusText();
        }

        private void OnCoreSelectionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectableCoreViewModel.IsSelected))
            {
                UpdateStatusText();
            }
        }

        private void UpdateStatusText()
        {
            int selectedCount = Cores.Count(c => c.IsSelected);
            if (selectedCount == 0)
            {
                StatusEmoji = "🎲";
                StatusText = "将随机分配CPU核心";
            }
            else if (selectedCount < _assignedCoreCount)
            {
                StatusEmoji = "⚠️";
                StatusText = "性能将受限";
            }
            else if (selectedCount > _assignedCoreCount)
            {
                StatusEmoji = "💨";
                StatusText = "会在选定的核心组内随机漂移";
            }
            else
            {
                StatusEmoji = "✅";
                StatusText = "完美！";
            }
        }

        // 从主界面 CpuPageViewModel.cs 复制而来的、完全一致的布局算法
        private int CalculateOptimalColumns(int count)
        {
            if (count <= 1) return 1;
            if (count <= 3) return count;
            if (count == 4) return 2;
            if (count <= 6) return 3;
            if (count == 8) return 4;

            double sqrt = Math.Sqrt(count);
            if (sqrt == (int)sqrt) return (int)sqrt;

            int startingPoint = (int)sqrt;
            for (int i = startingPoint; i >= 2; i--)
            {
                if (count % i == 0) return count / i;
            }
            return (int)Math.Ceiling(sqrt);
        }
    }
}