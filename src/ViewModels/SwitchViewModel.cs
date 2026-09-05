using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class SwitchViewModel : ObservableObject
    {

        private List<SwitchUpstream> _allPhysicalAdapters;
        private List<SwitchUpstream> _bridgeableAdapters;

        // 默认交换机按固定 ID 识别，避免受本地化名称或同名交换机影响。
        private const string DefaultSwitchId = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444";


        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected))] private bool _isLockedForInteraction = false;
        private SwitchInfo _appliedInfo;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected))] private string? _configurationError;

        [ObservableProperty] private string _switchName;
        [ObservableProperty] private string _switchId = string.Empty;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected))] private SwitchMode _selectedNetworkMode;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected)), NotifyPropertyChangedFor(nameof(DropDownButtonContent))] private SwitchUpstream? _selectedUpstreamAdapter;
        [ObservableProperty] private bool _isHostConnectionAllowed;
        [ObservableProperty] private bool _isUpstreamSelectionEnabled;
        [ObservableProperty] private bool _isHostConnectionToggleEnabled;
        [ObservableProperty] private bool _isDefaultSwitch;
        [ObservableProperty] private ObservableCollection<SwitchUpstream> _menuItems = new();
        [ObservableProperty] private ObservableCollection<AdapterInfo> _connectedClients = new();
        [ObservableProperty] private bool _isExpanded = false;

        public bool IsReverting { get; private set; } = false;

        public string StatusText => IsDefaultSwitch ? Properties.Resources.Warning_CannotModifyDefaultSwitch
            : IsLockedForInteraction ? Properties.Resources.Network_Applying
            : ConfigurationError != null ? ConfigurationError
            : _appliedInfo.StateError != null ? Properties.Resources.Network_StateUnknown + " " + _appliedInfo.StateError
            : IsConnected ? string.Format(Properties.Resources.Network_UplinkConfigured, _appliedInfo.Upstream)
            : Properties.Resources.Status_UpstreamNotConnected;
        public bool IsConnected => !IsLockedForInteraction && ConfigurationError == null && _appliedInfo.StateError == null &&
            _appliedInfo.Upstream?.LinkUp == true && _appliedInfo.SwitchType is SwitchMode.Bridge or SwitchMode.NAT;
        public string DropDownButtonContent => IsDefaultSwitch ? Properties.Resources.Auto : SelectedNetworkMode == SwitchMode.Isolated ? Properties.Resources.Status_Unavailable : SelectedUpstreamAdapter == null ? Properties.Resources.Placeholder_SelectNetworkAdapter : SelectedUpstreamAdapter.DisplayName;
        public string IconGlyph => DeviceIcons.GetGlyph("Switch", SwitchName);


        public SwitchViewModel(SwitchInfo switchInfo, List<SwitchUpstream> allPhysicalAdapters, List<SwitchUpstream> bridgeableAdapters)
        {
            _allPhysicalAdapters = allPhysicalAdapters;
            _bridgeableAdapters = bridgeableAdapters;

            _appliedInfo = switchInfo;
            _switchName = switchInfo.SwitchName;
            _switchId = switchInfo.Id;
            IsDefaultSwitch = string.Equals(_switchId?.Trim('{', '}'), DefaultSwitchId, StringComparison.OrdinalIgnoreCase);

            _ = RevertTo(switchInfo);

            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SelectedNetworkMode))
                {
                    UpdateUiLogic();
                    UpdateMenuItems();   // 桥接↔NAT 切换时重建网卡列表(桥接排不可二层桥的蜂窝/WWAN)
                    OnPropertyChanged(nameof(DropDownButtonContent));
                }
            };
        }


        [RelayCommand]
        private void SetNetworkMode(string? mode)
        {
            if (!Enum.TryParse<SwitchMode>(mode, out var parsed) || SelectedNetworkMode == parsed)
            {
                return;
            }
            SelectedNetworkMode = parsed;
        }

        [RelayCommand]
        private void SelectUpstreamAdapter(SwitchUpstream adapterName)
        {
            SelectedUpstreamAdapter = adapterName;
        }

        public async Task RevertTo(SwitchInfo switchInfo)
        {
            IsReverting = true;
            try
            {
                _appliedInfo = switchInfo;
                SelectedNetworkMode = switchInfo.SwitchType;
                SelectedUpstreamAdapter = switchInfo.Upstream;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsConnected));
                IsHostConnectionAllowed = switchInfo.AllowManagementOS;
                if (IsDefaultSwitch) { SelectedNetworkMode = SwitchMode.NAT; }
                UpdateUiLogic();
                await UpdateTopologyAsync();
            }
            finally
            {
                IsReverting = false;
            }
        }

        private void UpdateUiLogic()
        {
            IsUpstreamSelectionEnabled = (SelectedNetworkMode == SwitchMode.Bridge || SelectedNetworkMode == SwitchMode.NAT) && !IsDefaultSwitch;
            IsHostConnectionToggleEnabled = SelectedNetworkMode == SwitchMode.Isolated && !IsDefaultSwitch;
            if (!IsHostConnectionToggleEnabled && !IsDefaultSwitch)
            {
                IsHostConnectionAllowed = true;
            }
        }

        public void SetAdapters(List<SwitchUpstream> physical, List<SwitchUpstream> bridgeable)
        {
            _allPhysicalAdapters = physical;
            _bridgeableAdapters = bridgeable;
            UpdateMenuItems();
        }

        public void UpdateMenuItems()
        {
            var currentSelection = this.SelectedUpstreamAdapter;
            MenuItems.Clear();
            // 桥接只列可二层桥的网卡(蜂窝/WWAN 不在 Msvm_ExternalEthernetPort/WiFiPort);NAT 列全部物理网卡
            var source = SelectedNetworkMode == SwitchMode.Bridge ? _bridgeableAdapters : _allPhysicalAdapters;
            if (source == null) return;
            foreach (var name in source) { MenuItems.Add(name); }
            if (currentSelection != null && !MenuItems.Any(a => a.ConnectionId == currentSelection.ConnectionId)) { MenuItems.Add(currentSelection); }
        }

        private async Task UpdateTopologyAsync()
        {
            if (string.IsNullOrEmpty(SwitchName)) return;
            var clients = await HyperVSwitchService.GetFullSwitchNetworkStateAsync(SwitchName);
            ConnectedClients.Clear();
            foreach (var client in clients) { ConnectedClients.Add(client); }
        }
    }
    }
