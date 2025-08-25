using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachineMemoryViewModel : ObservableObject
    {
        private readonly VirtualMachineMemoryInfo _originalModel;
        private readonly IMemoryService _memoryService;

        [ObservableProperty]
        private string _startupMB;
        [ObservableProperty]
        private string _minimumMB;
        [ObservableProperty]
        private string _maximumMB;
        [ObservableProperty]
        private double _buffer;
        [ObservableProperty]
        private double _priority;
        [ObservableProperty]
        private bool _dynamicMemoryEnabled;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDataValid))]
        private bool _isStartupMBValid;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDataValid))]
        private bool _isMinimumMBValid;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDataValid))]
        private bool _isMaximumMBValid;

        public VirtualMachineMemoryViewModel(VirtualMachineMemoryInfo model)
        {
            _originalModel = model;
            _memoryService = new MemoryService();
            this.PropertyChanged += OnViewModelPropertyChanged;
            RevertChanges();
        }

        public string VMName => _originalModel.VMName;
        public string State => _originalModel.State;
        public string VmIconGlyph => State.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "\uE768" : "\uE97C";
        public bool IsDataValid => IsStartupMBValid && IsMinimumMBValid && IsMaximumMBValid;

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(StartupMB):
                case nameof(MinimumMB):
                case nameof(MaximumMB):
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
            if (DynamicMemoryEnabled && (Buffer < 5 || Buffer > 2000)) { Utils.Show("内存缓冲区必须在 5% 到 2000% 之间。"); return false; }
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
                    _originalModel.StartupMB = newStartup;
                    _originalModel.MinimumMB = newMin;
                    _originalModel.MaximumMB = newMax;
                    _originalModel.DynamicMemoryEnabled = this.DynamicMemoryEnabled;
                    _originalModel.Buffer = (int)this.Buffer;
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
            Buffer = _originalModel.Buffer;
            Priority = _originalModel.Priority;
            ValidateAllFields();
        }
    }
}