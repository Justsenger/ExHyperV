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
        private readonly INetworkService _networkService;
        private readonly List<PhysicalAdapterInfo> _allPhysicalAdapters;
        private readonly ObservableCollection<SwitchViewModel> _allSwitchViewModels;

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

        public string StatusText => IsDefaultSwitch ? ExHyperV.Properties.Resources.Warning_CannotModifyDefaultSwitch : IsConnected ? string.Format(Properties.Resources.Status_ConnectedTo, SelectedUpstreamAdapter) : ExHyperV.Properties.Resources.Status_UpstreamNotConnected;
        public bool IsConnected => !string.IsNullOrEmpty(SelectedUpstreamAdapter) && (SelectedNetworkMode == "Bridge" || SelectedNetworkMode == "NAT");
        public string DropDownButtonContent => IsDefaultSwitch ? ExHyperV.Properties.Resources.Auto : SelectedNetworkMode == "Isolated" ? ExHyperV.Properties.Resources.Status_Unavailable : string.IsNullOrEmpty(SelectedUpstreamAdapter) ? ExHyperV.Properties.Resources.Placeholder_SelectNetworkAdapter : SelectedUpstreamAdapter;
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