using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ExHyperV.ViewModels
{
    public partial class SwitchViewModel : ObservableObject
    {
        private readonly INetworkService _networkService;
        private readonly List<PhysicalAdapterInfo> _allPhysicalAdapters;
        private readonly ObservableCollection<SwitchViewModel> _allSwitchViewModels;

        // **** 修改点 **** (添加一个专门用于在操作期间锁定UI交互的属性)
        [ObservableProperty] private bool _isLockedForInteraction = false;

        [ObservableProperty] private string _switchName;
        [ObservableProperty] private string _switchId;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected))] private string _selectedNetworkMode;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(IsConnected)), NotifyPropertyChangedFor(nameof(DropDownButtonContent))] private string? _selectedUpstreamAdapter;
        [ObservableProperty] private bool _isHostConnectionAllowed;
        [ObservableProperty] private bool _isUpstreamSelectionEnabled;
        [ObservableProperty] private bool _isHostConnectionToggleEnabled;
        [ObservableProperty] private bool _isDefaultSwitch;
        [ObservableProperty] private ObservableCollection<string> _menuItems = new();
        [ObservableProperty] private ObservableCollection<AdapterInfo> _connectedClients = new();
        [ObservableProperty] private bool _isExpanded = false;

        public bool IsReverting { get; private set; } = false;

        public string StatusText => IsDefaultSwitch ? "默认交换机，无法修改设定" : IsConnected ? $"已连接到: {SelectedUpstreamAdapter}" : "未连接上游网络";
        public bool IsConnected => !string.IsNullOrEmpty(SelectedUpstreamAdapter) && (SelectedNetworkMode == "Bridge" || SelectedNetworkMode == "NAT");
        public string DropDownButtonContent => IsDefaultSwitch ? "自动适应" : SelectedNetworkMode == "Isolated" ? "不可用" : string.IsNullOrEmpty(SelectedUpstreamAdapter) ? "请选择网卡..." : SelectedUpstreamAdapter;
        public string IconGlyph => Utils.GetIconPath("Switch", SwitchName);

        public SwitchViewModel(SwitchInfo switchInfo, INetworkService networkService, List<PhysicalAdapterInfo> allPhysicalAdapters, ObservableCollection<SwitchViewModel> allSwitchViewModels)
        {
            _networkService = networkService;
            _allPhysicalAdapters = allPhysicalAdapters;
            _allSwitchViewModels = allSwitchViewModels;

            _switchName = switchInfo.SwitchName;
            _switchId = switchInfo.Id;
            _isDefaultSwitch = _switchName == "Default Switch";

            _ = RevertTo(switchInfo);

            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SelectedNetworkMode))
                {
                    UpdateUiLogic();
                    OnPropertyChanged(nameof(DropDownButtonContent));
                }
            };
        }

        [RelayCommand]
        private void SetNetworkMode(string? mode)
        {
            if (string.IsNullOrEmpty(mode) || SelectedNetworkMode == mode)
            {
                return;
            }
            SelectedNetworkMode = mode;
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
                SelectedNetworkMode = GetModeFromSwitchType(switchInfo.SwitchType);
                SelectedUpstreamAdapter = switchInfo.NetAdapterInterfaceDescription;
                IsHostConnectionAllowed = bool.TryParse(switchInfo.AllowManagementOS, out var result) && result;
                if (_isDefaultSwitch) { SelectedNetworkMode = "NAT"; }
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
            IsUpstreamSelectionEnabled = (SelectedNetworkMode == "Bridge" || SelectedNetworkMode == "NAT") && !IsDefaultSwitch;
            IsHostConnectionToggleEnabled = SelectedNetworkMode == "Isolated" && !IsDefaultSwitch;
            if (!IsHostConnectionToggleEnabled && !IsDefaultSwitch)
            {
                IsHostConnectionAllowed = true;
            }
        }

        public void UpdateMenuItems()
        {
            var currentSelection = this.SelectedUpstreamAdapter;
            MenuItems.Clear();
            if (_allPhysicalAdapters == null) return;
            var allPhysicalAdapterNames = _allPhysicalAdapters.Select(p => p.InterfaceDescription).ToList();
            foreach (var name in allPhysicalAdapterNames) { MenuItems.Add(name); }
            if (!string.IsNullOrEmpty(currentSelection) && !MenuItems.Contains(currentSelection)) { MenuItems.Add(currentSelection); }
        }

        [RelayCommand]
        private async Task UpdateTopologyAsync()
        {
            if (string.IsNullOrEmpty(SwitchName)) return;
            var clients = await _networkService.GetFullSwitchNetworkStateAsync(SwitchName);
            ConnectedClients.Clear();
            foreach (var client in clients) { ConnectedClients.Add(client); }
        }

        public static string GetModeFromSwitchType(string switchType) => switchType switch
        {
            "External" => "Bridge",
            "NAT" => "NAT",
            _ => "Isolated"
        };
    }
}