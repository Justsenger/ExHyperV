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
        // ===== 字段 =====

        private readonly List<string> _allPhysicalAdapters;
        private readonly List<string> _bridgeableAdapters;

        // ===== 属性 =====

        [ObservableProperty] private bool _isLockedForInteraction = false;

        [ObservableProperty] private string _switchName;
        [ObservableProperty] private string _switchId;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected))] private SwitchMode _selectedNetworkMode;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected)), NotifyPropertyChangedFor(nameof(DropDownButtonContent))] private string? _selectedUpstreamAdapter;
        [ObservableProperty] private bool _isHostConnectionAllowed;
        [ObservableProperty] private bool _isUpstreamSelectionEnabled;
        [ObservableProperty] private bool _isHostConnectionToggleEnabled;
        [ObservableProperty] private bool _isDefaultSwitch;
        [ObservableProperty] private ObservableCollection<string> _menuItems = new();
        [ObservableProperty] private ObservableCollection<AdapterInfo> _connectedClients = new();
        [ObservableProperty] private bool _isExpanded = false;

        public bool IsReverting { get; private set; } = false;

        public string StatusText => IsDefaultSwitch ? Properties.Resources.Warning_CannotModifyDefaultSwitch : IsConnected ? string.Format(Properties.Resources.Status_ConnectedTo, SelectedUpstreamAdapter) : Properties.Resources.Status_UpstreamNotConnected;
        public bool IsConnected => !string.IsNullOrEmpty(SelectedUpstreamAdapter) && (SelectedNetworkMode == SwitchMode.Bridge || SelectedNetworkMode == SwitchMode.NAT);
        public string DropDownButtonContent => IsDefaultSwitch ? Properties.Resources.Auto : SelectedNetworkMode == SwitchMode.Isolated ? Properties.Resources.Status_Unavailable : string.IsNullOrEmpty(SelectedUpstreamAdapter) ? Properties.Resources.Placeholder_SelectNetworkAdapter : SelectedUpstreamAdapter;
        public string IconGlyph => DeviceIcons.GetGlyph("Switch", SwitchName);

        // ===== 构造 =====

        public SwitchViewModel(SwitchInfo switchInfo, List<string> allPhysicalAdapters, List<string> bridgeableAdapters)
        {
            _allPhysicalAdapters = allPhysicalAdapters;
            _bridgeableAdapters = bridgeableAdapters;

            _switchName = switchInfo.SwitchName;
            _switchId = switchInfo.Id;
            IsDefaultSwitch = _switchName == "Default Switch";

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

        // ===== 命令与逻辑 =====

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
        private void SelectUpstreamAdapter(string adapterName)
        {
            SelectedUpstreamAdapter = adapterName;
        }

        public async Task RevertTo(SwitchInfo switchInfo)
        {
            IsReverting = true;
            try
            {
                SelectedNetworkMode = switchInfo.SwitchType;
                SelectedUpstreamAdapter = switchInfo.NetAdapterInterfaceDescription;
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

        public void UpdateMenuItems()
        {
            var currentSelection = this.SelectedUpstreamAdapter;
            MenuItems.Clear();
            // 桥接只列可二层桥的网卡(蜂窝/WWAN 不在 Msvm_ExternalEthernetPort/WiFiPort);NAT 列全部物理网卡
            var source = SelectedNetworkMode == SwitchMode.Bridge ? _bridgeableAdapters : _allPhysicalAdapters;
            if (source == null) return;
            foreach (var name in source) { MenuItems.Add(name); }
            if (!string.IsNullOrEmpty(currentSelection) && !MenuItems.Contains(currentSelection)) { MenuItems.Add(currentSelection); }
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