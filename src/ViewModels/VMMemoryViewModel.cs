using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VMMemoryViewModel : ObservableObject
    {
        private VirtualMachineMemoryInfo _originalModel;
        private readonly MemoryPageViewModel _parentViewModel;
        private readonly IMemoryService _memoryService;

        [ObservableProperty] private string _startupMB;
        [ObservableProperty] private string _minimumMB;
        [ObservableProperty] private string _maximumMB;
        [ObservableProperty] private string _buffer;
        [ObservableProperty] private int _priority;
        [ObservableProperty] private bool _dynamicMemoryEnabled;

        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isStartupMBValid;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isMinimumMBValid;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isMaximumMBValid;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDataValid))] private bool _isBufferValid;

        [ObservableProperty] private long _assignedMB;
        [ObservableProperty] private long _demandMB;
        [ObservableProperty] private string _state;
        [ObservableProperty] private string _status;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveChangesCommand))]
        [NotifyCanExecuteChangedFor(nameof(RevertChangesCommand))]
        private bool _isSaving;

        public VMMemoryViewModel(VirtualMachineMemoryInfo model, MemoryPageViewModel parent, IMemoryService memoryService)
        {
            _originalModel = model;
            _parentViewModel = parent;
            _memoryService = memoryService;

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

        public string MemoryUsageText
        {
            get
            {
                if (IsVmRunning)
                {
                    return string.Format(
                        ExHyperV.Properties.Resources.VMMemory_UsageFormat,
                        DemandMB,
                        AssignedMB);
                }
                else
                {
                    return ExHyperV.Properties.Resources.VMMemory_Status_Off;
                }
            }
        }

        public void UpdateLiveData(VirtualMachineMemoryInfo liveData)
        {
            if (liveData == null) return;
            State = liveData.State;
            AssignedMB = liveData.AssignedMB;
            DemandMB = liveData.DemandMB;
            Status = liveData.Status;

            OnPropertyChanged(nameof(IsVmRunning));
            OnPropertyChanged(nameof(UsagePercentage));
            OnPropertyChanged(nameof(UsageBarBrush));
            OnPropertyChanged(nameof(MemoryUsageText));
        }

        public void UpdateConfiguration(VirtualMachineMemoryInfo newConfig)
        {
            if (IsDirty()) return;
            _originalModel = newConfig;
            RevertChanges();
        }

        public void MarkAsOff()
        {
            State = "Off";
            AssignedMB = 0;
            DemandMB = 0;
            Status = ExHyperV.Properties.Resources.VMMemory_Status_Off;
            OnPropertyChanged(nameof(IsVmRunning));
            OnPropertyChanged(nameof(MemoryUsageText));
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(StartupMB):
                case nameof(MinimumMB):
                case nameof(MaximumMB):
                case nameof(Buffer):
                case nameof(DynamicMemoryEnabled):
                case nameof(Priority):
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
            RevertChangesCommand.NotifyCanExecuteChanged();
        }

        public bool IsDirty()
        {
            return StartupMB != _originalModel.StartupMB.ToString() ||
                   MinimumMB != _originalModel.MinimumMB.ToString() ||
                   MaximumMB != _originalModel.MaximumMB.ToString() ||
                   Buffer != _originalModel.Buffer.ToString() ||
                   Priority != _originalModel.Priority ||
                   DynamicMemoryEnabled != _originalModel.DynamicMemoryEnabled;
        }

        private bool FinalValidation()
        {
            long.TryParse(StartupMB, out long startup);
            long.TryParse(MinimumMB, out long min);
            long.TryParse(MaximumMB, out long max);
            if (DynamicMemoryEnabled && startup < min)
            {
                _parentViewModel.ShowSnackbar(ExHyperV.Properties.Resources.error, ExHyperV.Properties.Resources.StartupRamLessThanMinRam, ControlAppearance.Caution, SymbolRegular.Warning24);
                return false;
            }
            if (DynamicMemoryEnabled && min > max)
            {
                _parentViewModel.ShowSnackbar(ExHyperV.Properties.Resources.error, ExHyperV.Properties.Resources.MinRamGreaterThanMaxRam, ControlAppearance.Caution, SymbolRegular.Warning24);
                return false;
            }
            return true;
        }

        private bool CanExecuteModifyCommands() => IsDataValid && !IsSaving && IsDirty();

        [RelayCommand(CanExecute = nameof(CanExecuteModifyCommands))]
        private async Task SaveChangesAsync()
        {
            if (!FinalValidation()) return;

            IsSaving = true;
            _parentViewModel.IsLoading = true;

            try
            {
                long.TryParse(StartupMB, out long startup);
                long.TryParse(MinimumMB, out long min);
                long.TryParse(MaximumMB, out long max);
                int.TryParse(Buffer, out int buffer);

                var newInfo = new VirtualMachineMemoryInfo
                {
                    VMName = this.VMName,
                    StartupMB = startup,
                    MinimumMB = min,
                    MaximumMB = max,
                    Buffer = buffer,
                    Priority = this.Priority,
                    DynamicMemoryEnabled = this.DynamicMemoryEnabled
                };

                var result = await _memoryService.SetVmMemoryAsync(newInfo);
                if (result.Success)
                {
                    _originalModel = newInfo;
                    _parentViewModel.ShowSnackbar(
                        ExHyperV.Properties.Resources.success,
                        string.Format("虚拟机 {0} 的设置已成功保存。", VMName),
                        ControlAppearance.Success,
                        SymbolRegular.CheckmarkCircle24);
                    ValidateAllFields();
                }
                else
                {
                    _parentViewModel.ShowSnackbar(ExHyperV.Properties.Resources.error, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                _parentViewModel.ShowSnackbar(
                    ExHyperV.Properties.Resources.error,
                    string.Format(ExHyperV.Properties.Resources.Error_GenericFormat, ex.Message),
                    ControlAppearance.Danger,
                    SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsSaving = false;
                _parentViewModel.IsLoading = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanExecuteModifyCommands))]
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