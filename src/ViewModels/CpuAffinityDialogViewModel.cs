using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace ExHyperV.ViewModels.Dialogs
{
    public partial class CpuAffinityDialogViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _title;

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

        private readonly HyperVSchedulerType _schedulerType;
        private readonly Dictionary<int, int> _cpuSiblingMap;
        private bool _isUpdatingFromLogic = false;
        public string SchedulerTypeInfoText => $"当前调度器模式: {_schedulerType}";

        public CpuAffinityDialogViewModel(
            string vmName,
            int assignedCoreCount,
            ObservableCollection<UiCoreModel> hostCores,
            HyperVSchedulerType schedulerType,
            Dictionary<int, int> cpuSiblingMap)
        {
            Title = $"为 {vmName} 设置 CPU 绑定";
            _assignedCoreCount = assignedCoreCount;
            Columns = CalculateOptimalColumns(hostCores.Count);
            Rows = (hostCores.Count > 0) ? (int)Math.Ceiling((double)hostCores.Count / Columns) : 0;
            _schedulerType = schedulerType;
            _cpuSiblingMap = cpuSiblingMap;

            foreach (var core in hostCores.OrderBy(c => c.CoreId))
            {
                var selectableCore = new SelectableCoreViewModel();
                selectableCore.CoreId = core.CoreId;
                selectableCore.CoreType = core.CoreType;

                selectableCore.PropertyChanged += OnCoreSelectionChanged;
                Cores.Add(selectableCore);
            }

            UpdateStatusText();
        }

        private void OnCoreSelectionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectableCoreViewModel.IsSelected))
            {
                HandleSiblingSelection(sender as SelectableCoreViewModel);
                UpdateStatusText();
            }
        }

        private void HandleSiblingSelection(SelectableCoreViewModel changedCore)
        {
            if (changedCore == null || _isUpdatingFromLogic) return;

            if (_schedulerType == HyperVSchedulerType.Core)
            {
                if (_cpuSiblingMap.TryGetValue(changedCore.CoreId, out int siblingId))
                {
                    var siblingCoreViewModel = Cores.FirstOrDefault(c => c.CoreId == siblingId);
                    if (siblingCoreViewModel != null && siblingCoreViewModel.IsSelected != changedCore.IsSelected)
                    {
                        _isUpdatingFromLogic = true;
                        siblingCoreViewModel.IsSelected = changedCore.IsSelected;
                        _isUpdatingFromLogic = false;
                    }
                }
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