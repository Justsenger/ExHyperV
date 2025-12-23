using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ExHyperV.ViewModels.Dialogs
{
    public partial class CpuAffinityDialogViewModel : ObservableObject
    {
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

        public CpuAffinityDialogViewModel(
            string vmName,
            int assignedCoreCount,
            ObservableCollection<UiCoreModel> hostCores,
            HyperVSchedulerType schedulerType,
            Dictionary<int, int> cpuSiblingMap)
        {
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

            string countStr = $"({selectedCount} / {_assignedCoreCount})";

            if (selectedCount == 0)
            {
                StatusEmoji = "🔄";
                StatusText = ExHyperV.Properties.Resources.SystemAutomaticallyScheduled;
            }
            else if (selectedCount < _assignedCoreCount)
            {
                StatusEmoji = "⚠️";
                StatusText = string.Format(ExHyperV.Properties.Resources.PerformanceLimited, countStr);
            }
            else if (selectedCount > _assignedCoreCount)
            {
                StatusEmoji = "💨";
                StatusText = string.Format(ExHyperV.Properties.Resources.RandomlyDriftWithinSelectedCoreGroup, countStr);
            }
            else
            {
                StatusEmoji = "🎯";
                StatusText = string.Format(ExHyperV.Properties.Resources.Perfect, countStr);
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