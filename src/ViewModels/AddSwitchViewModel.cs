using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels
{
    public partial class AddSwitchViewModel : ObservableObject
    {
        private readonly IEnumerable<SwitchViewModel> _existingSwitches;
        private readonly IEnumerable<string> _allPhysicalAdapters;
        private readonly IEnumerable<string> _bridgeableAdapters;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNetworkAdapterSelectionEnabled))]
        private SwitchMode _selectedSwitchType = SwitchMode.Bridge;

        [ObservableProperty]
        private string _switchName = Properties.Resources.AddSwitch_DefaultName_External;

        [ObservableProperty]
        private string? _selectedNetworkAdapter;

        [ObservableProperty]
        private string? _errorMessage;

        public ObservableCollection<string> AvailableNetworkAdapters { get; } = new();
        public bool IsNetworkAdapterSelectionEnabled => SelectedSwitchType == SwitchMode.Bridge || SelectedSwitchType == SwitchMode.NAT;


        public AddSwitchViewModel(IEnumerable<SwitchViewModel> existingSwitches, IEnumerable<string> allPhysicalAdapters, IEnumerable<string> bridgeableAdapters)
        {
            _existingSwitches = existingSwitches;
            _allPhysicalAdapters = allPhysicalAdapters;
            _bridgeableAdapters = bridgeableAdapters;
            RebuildAvailableAdapters();
        }

        // 按交换机类型分源:外部/桥接只列可桥接网卡(蜂窝/WWAN 不可二层桥);NAT 列全部可上网物理网卡。
        // 同时排除已被现有交换机占用的上游。
        private void RebuildAvailableAdapters()
        {
            var source = SelectedSwitchType == SwitchMode.Bridge ? _bridgeableAdapters : _allPhysicalAdapters;
            string? keep = SelectedNetworkAdapter;
            AvailableNetworkAdapters.Clear();
            foreach (var adapter in source)
            {
                if (!_existingSwitches.Any(s => s.SelectedUpstreamAdapter == adapter))
                    AvailableNetworkAdapters.Add(adapter);
            }
            // 类型切换后,原选中项若已不在新列表里则清空
            SelectedNetworkAdapter = (keep != null && AvailableNetworkAdapters.Contains(keep)) ? keep : null;
        }

        partial void OnSelectedSwitchTypeChanged(SwitchMode value)
        {
            SwitchName = value switch
            {
                SwitchMode.Bridge => Properties.Resources.AddSwitch_DefaultName_External,
                SwitchMode.NAT => Properties.Resources.AddSwitch_DefaultName_NAT,
                SwitchMode.Isolated => Properties.Resources.AddSwitch_DefaultName_Internal,
                _ => Properties.Resources.AddSwitch_DefaultName_Generic
            };
            RebuildAvailableAdapters();   // 桥接↔NAT 切换时重建网卡列表(桥接排蜂窝)
        }

        public bool Validate()
        {
            ErrorMessage = null;
            if (string.IsNullOrWhiteSpace(SwitchName))
            {
                ErrorMessage = Properties.Resources.AddSwitch_Validation_NameCannotBeEmpty;
                return false;
            }
            if (_existingSwitches.Any(s => s.SwitchName.Equals(SwitchName, System.StringComparison.OrdinalIgnoreCase)))
            {
                ErrorMessage = string.Format(Properties.Resources.AddSwitch_Validation_NameExists, SwitchName);
                return false;
            }
            if (IsNetworkAdapterSelectionEnabled && !AvailableNetworkAdapters.Any())
            {
                ErrorMessage = Properties.Resources.AddSwitch_Validation_NoAdaptersForExternalOrNat;
                return false;
            }
            if (IsNetworkAdapterSelectionEnabled && string.IsNullOrEmpty(SelectedNetworkAdapter))
            {
                ErrorMessage = Properties.Resources.AddSwitch_Validation_AdapterRequiredForExternalOrNat;
                return false;
            }
            if (SelectedSwitchType == SwitchMode.NAT)
            {
                if (_existingSwitches.Any(s => !s.IsDefaultSwitch && s.SelectedNetworkMode == SwitchMode.NAT))
                {
                    ErrorMessage = Properties.Resources.AddSwitch_Validation_OnlyOneNatAllowed;
                    return false;
                }
            }
            return true;
        }
    }
}