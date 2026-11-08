using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.UI.Pages.Switches
{
    public partial class AddSwitchViewModel : ObservableObject
    {
        private readonly IReadOnlyList<SwitchInfo> _existingSwitches;
        private readonly IEnumerable<SwitchUpstreamAdapter> _allPhysicalAdapters;
        private readonly IEnumerable<SwitchUpstreamAdapter> _bridgeableAdapters;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNetworkAdapterSelectionEnabled))]
        private SwitchMode _selectedSwitchType = SwitchMode.Bridge;

        [ObservableProperty]
        private string _switchName = Properties.Resources.AddSwitch_DefaultName_External;

        [ObservableProperty]
        private SwitchUpstreamAdapter? _selectedNetworkAdapter;

        [ObservableProperty]
        private string? _errorMessage;

        public ObservableCollection<SwitchUpstreamAdapter> AvailableNetworkAdapters { get; } = new();
        public bool IsNetworkAdapterSelectionEnabled => SwitchConfigurationRules.RequiresUpstream(SelectedSwitchType);


        public AddSwitchViewModel(IReadOnlyList<SwitchInfo> existingSwitches, IEnumerable<SwitchUpstreamAdapter> allPhysicalAdapters, IEnumerable<SwitchUpstreamAdapter> bridgeableAdapters)
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
            var source = SwitchConfigurationRules.GetAvailableCreationAdapters(SelectedSwitchType,
                _existingSwitches, _allPhysicalAdapters, _bridgeableAdapters);
            SwitchUpstreamAdapter? keep = SelectedNetworkAdapter;
            AvailableNetworkAdapters.Clear();
            foreach (var adapter in source)
                AvailableNetworkAdapters.Add(adapter);
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
            ErrorMessage = SwitchConfigurationRules.GetCreationError(SwitchName, SelectedSwitchType,
                SelectedNetworkAdapter, _existingSwitches, _allPhysicalAdapters, _bridgeableAdapters);
            return ErrorMessage == null;
        }
    }
}
