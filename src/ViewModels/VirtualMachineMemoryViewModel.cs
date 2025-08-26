using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachineMemoryViewModel : ObservableObject
    {
        private readonly VirtualMachineMemoryInfo _originalModel;
        private readonly IMemoryService _memoryService;

        [ObservableProperty] private string _startupMB;
        [ObservableProperty] private string _minimumMB;
        [ObservableProperty] private string _maximumMB;
        [ObservableProperty] private string _buffer;
        [ObservableProperty] private double _priority;
        [ObservableProperty] private bool _dynamicMemoryEnabled;

        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isStartupMBValid;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isMinimumMBValid;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isMaximumMBValid;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isBufferValid;

        [ObservableProperty] private long _assignedMB;
        [ObservableProperty] private long _demandMB;
        [ObservableProperty] private string _state;

        public string Status { get; private set; }

        public VirtualMachineMemoryViewModel(VirtualMachineMemoryInfo model)
        {
            _originalModel = model;
            _memoryService = new MemoryService();

            this.PropertyChanged += OnViewModelPropertyChanged;

            UpdateLiveData(model);
            RevertChanges();
        }

        public string VMName => _originalModel.VMName;
        public bool IsVmRunning => State?.Equals("Running", StringComparison.OrdinalIgnoreCase) ?? false;
        public string VmIconGlyph => "\uE977";
        public bool IsDataValid => IsStartupMBValid && IsMinimumMBValid && IsMaximumMBValid && IsBufferValid;

        public double UsagePercentage => IsVmRunning && AssignedMB > 0 ? Math.Min((double)DemandMB / AssignedMB * 100, 100) : 0;

        public Brush UsageBarBrush
        {
            get
            {
                if (!IsVmRunning) return Brushes.Transparent;
                double percentage = this.UsagePercentage;
                if (percentage >= 90) return (Brush)Application.Current.Resources["RedBrush"];
                if (percentage >= 70) return (Brush)Application.Current.Resources["OrangeBrush"];
                return (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            }
        }

        public string MemoryUsageText => IsVmRunning ? $"{DemandMB} MB (需求) / {AssignedMB} MB (已分配)" : "已关闭";

        public void UpdateLiveData(VirtualMachineMemoryInfo liveData)
        {
            State = liveData.State;
            AssignedMB = liveData.AssignedMB;
            DemandMB = liveData.DemandMB;
            Status = liveData.Status;

            OnPropertyChanged(nameof(IsVmRunning));
            OnPropertyChanged(nameof(VmIconGlyph));
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AssignedMB) || e.PropertyName == nameof(DemandMB) || e.PropertyName == nameof(State))
            {
                OnPropertyChanged(nameof(UsagePercentage));
                OnPropertyChanged(nameof(UsageBarBrush));
                OnPropertyChanged(nameof(MemoryUsageText));
            }
            switch (e.PropertyName)
            {
                case nameof(StartupMB):
                case nameof(MinimumMB):
                case nameof(MaximumMB):
                case nameof(Buffer):
                case nameof(DynamicMemoryEnabled):
                    ValidateAllFields();
                    break;
            }
        }

        private void ValidateAllFields()
        {
            IsStartupMBValid = long.TryParse(StartupMB, out long s) && s > 0;
            IsMinimumMBValid = long.TryParse(MinimumMB, out long m) && m > 0;
            IsMaximumMBValid = long.TryParse(MaximumMB, out long x) && x > 0;
            IsBufferValid = int.TryParse(Buffer, out int b) && b >= 5 && b <= 2000;
            OnPropertyChanged(nameof(IsDataValid));
            SaveChangesCommand.NotifyCanExecuteChanged();
        }

        private bool FinalValidation()
        {
            long.TryParse(StartupMB, out long startup);
            long.TryParse(MinimumMB, out long min);
            long.TryParse(MaximumMB, out long max);
            if (DynamicMemoryEnabled && startup < min) { Utils.Show("启动RAM不能小于最小RAM。"); return false; }
            if (DynamicMemoryEnabled && min > max) { Utils.Show("最小RAM不能大于最大RAM。"); return false; }
            return true;
        }

        [RelayCommand(CanExecute = nameof(IsDataValid))]
        private async Task SaveChangesAsync()
        {
            if (!FinalValidation()) return;
            try
            {
                bool success = await _memoryService.SetVmMemoryAsync(this);
                if (success)
                {
                    long.TryParse(StartupMB, out long newStartup);
                    long.TryParse(MinimumMB, out long newMin);
                    long.TryParse(MaximumMB, out long newMax);
                    int.TryParse(Buffer, out int newBuffer);

                    _originalModel.StartupMB = newStartup;
                    _originalModel.MinimumMB = newMin;
                    _originalModel.MaximumMB = newMax;
                    _originalModel.Buffer = newBuffer;
                    _originalModel.DynamicMemoryEnabled = this.DynamicMemoryEnabled;
                    _originalModel.Priority = (int)this.Priority;
                }
            }
            catch (Exception ex)
            {
                Utils.Show($"错误: {ex.Message}");
            }
        }

        [RelayCommand]
        private void RevertChanges()
        {
            DynamicMemoryEnabled = _originalModel.DynamicMemoryEnabled;
            StartupMB = _originalModel.StartupMB.ToString();
            MinimumMB = _originalModel.MinimumMB.ToString();
            MaximumMB = _originalModel.MaximumMB.ToString();
            Buffer = _originalModel.Buffer.ToString();
            Priority = _originalModel.Priority;
            ValidateAllFields();
        }
    }
}