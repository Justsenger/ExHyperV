using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Windows.Media;
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
        private readonly SwitchConfig _config;

        public event EventHandler? RequestSave;

        [ObservableProperty] private bool _isLockedForInteraction = false;
        [ObservableProperty] private string _switchName;
        [ObservableProperty] private string _switchId;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(IsConnected)), NotifyPropertyChangedFor(nameof(AreAdvancedFeaturesVisible))] private string _selectedNetworkMode;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(DropDownButtonContent))] private string? _selectedUpstreamAdapter;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(AreAdvancedFeaturesVisible))] private bool _isHostConnectionAllowed;
        [ObservableProperty] private bool _isUpstreamSelectionEnabled;
        [ObservableProperty] private bool _isHostConnectionToggleEnabled;
        [ObservableProperty] private bool _isDefaultSwitch;
        [ObservableProperty] private ObservableCollection<string> _menuItems = new();
        [ObservableProperty] private ObservableCollection<AdapterInfo> _connectedClients = new();
        [ObservableProperty] private bool _isExpanded = false;

        [ObservableProperty] private bool _isNatEnabled;
        [ObservableProperty] private bool _isDhcpEnabled;

        [ObservableProperty] private string _subnetOctet1 = string.Empty;
        [ObservableProperty] private string _subnetOctet2 = string.Empty;
        [ObservableProperty] private string _subnetOctet3 = string.Empty;
        [ObservableProperty] private string _subnetOctet4 = string.Empty;
        [ObservableProperty] private string _subnetCidr = string.Empty;

        [ObservableProperty] private string _subnetFeedbackText = "";
        [ObservableProperty] private Brush _subnetFeedbackBrush = Brushes.Gray;

        public bool IsReverting { get; private set; } = false;
        public bool IsConnected => SelectedNetworkMode == "External";
        public bool AreAdvancedFeaturesVisible => SelectedNetworkMode == "Internal" && IsHostConnectionAllowed && !IsDefaultSwitch;
        public string DropDownButtonContent => IsDefaultSwitch ? ExHyperV.Properties.Resources.Auto : SelectedNetworkMode == "Internal" ? ExHyperV.Properties.Resources.Status_Unavailable : string.IsNullOrEmpty(SelectedUpstreamAdapter) ? ExHyperV.Properties.Resources.Placeholder_SelectNetworkAdapter : SelectedUpstreamAdapter;
        public string IconGlyph => Utils.GetIconPath("Switch", SwitchName);

        public SwitchViewModel(SwitchInfo switchInfo, SwitchConfig config, INetworkService networkService, List<PhysicalAdapterInfo> allPhysicalAdapters, ObservableCollection<SwitchViewModel> allSwitchViewModels)
        {
            _networkService = networkService;
            _allPhysicalAdapters = allPhysicalAdapters;
            _allSwitchViewModels = allSwitchViewModels;
            _config = config;

            _switchName = switchInfo.SwitchName;
            _switchId = switchInfo.Id;
            _isDefaultSwitch = switchInfo.SwitchName == "Default Switch";

            LoadConfig();

            _ = RevertTo(switchInfo);
        }

        partial void OnIsNatEnabledChanged(bool value) => RequestSave?.Invoke(this, EventArgs.Empty);
        partial void OnIsDhcpEnabledChanged(bool value) => RequestSave?.Invoke(this, EventArgs.Empty);
        partial void OnSubnetOctet1Changed(string value) => UpdateSubnetAndRequestSave();
        partial void OnSubnetOctet2Changed(string value) => UpdateSubnetAndRequestSave();
        partial void OnSubnetOctet3Changed(string value) => UpdateSubnetAndRequestSave();
        partial void OnSubnetOctet4Changed(string value) => UpdateSubnetAndRequestSave();
        partial void OnSubnetCidrChanged(string value) => UpdateSubnetAndRequestSave();

        private void UpdateSubnetAndRequestSave()
        {
            bool wasValid = UpdateSubnetFeedback();
            if (wasValid)
            {
                RequestSave?.Invoke(this, EventArgs.Empty);
            }
        }

        partial void OnSelectedNetworkModeChanged(string value)
        {
            UpdateUiLogic();

            if (IsReverting) return;

            if (value == "External" && string.IsNullOrEmpty(SelectedUpstreamAdapter))
            {
                var usedAdapters = _allSwitchViewModels.Where(s => s != this && s.IsConnected && !string.IsNullOrEmpty(s.SelectedUpstreamAdapter)).Select(s => s.SelectedUpstreamAdapter);
                var firstAvailableAdapter = _allPhysicalAdapters.Select(p => p.InterfaceDescription).FirstOrDefault(name => !usedAdapters.Contains(name));
                SelectedUpstreamAdapter = firstAvailableAdapter;
            }
            else if (value == "Internal")
            {
                SelectedUpstreamAdapter = null;
            }
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
                if (_isDefaultSwitch) { SelectedNetworkMode = "Internal"; }

                UpdateUiLogic();
                await UpdateTopologyAsync();
            }
            finally { IsReverting = false; }
        }

        private void UpdateUiLogic()
        {
            IsUpstreamSelectionEnabled = SelectedNetworkMode == "External" && !IsDefaultSwitch;
            IsHostConnectionToggleEnabled = !IsDefaultSwitch;
        }

        public void UpdateMenuItems()
        {
            var currentSelection = this.SelectedUpstreamAdapter;
            MenuItems.Clear();
            if (_allPhysicalAdapters == null) return;
            var allPhysicalAdapterNames = _allPhysicalAdapters.Select(p => p.InterfaceDescription);
            foreach (var name in allPhysicalAdapterNames)
            {
                MenuItems.Add(name);
            }
            if (!string.IsNullOrEmpty(currentSelection) && !MenuItems.Contains(currentSelection))
            {
                MenuItems.Add(currentSelection);
            }
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
            "External" => "External",
            _ => "Internal"
        };

        [RelayCommand] private void ConfigureNat() { /* Placeholder */ }
        [RelayCommand] private void ConfigureDhcp() { /* Placeholder */ }

        private void LoadConfig()
        {
            IsNatEnabled = _config.NatEnabled;
            IsDhcpEnabled = _config.DhcpEnabled;

            if (!string.IsNullOrEmpty(_config.Subnet))
            {
                var parts = _config.Subnet.Split('/');
                if (parts.Length == 2)
                {
                    var ipParts = parts[0].Split('.');
                    if (ipParts.Length == 4)
                    {
                        SubnetOctet1 = ipParts[0];
                        SubnetOctet2 = ipParts[1];
                        SubnetOctet3 = ipParts[2];
                        SubnetOctet4 = ipParts[3];
                    }
                    SubnetCidr = parts[1];
                }
            }
            UpdateSubnetFeedback();
        }

        public void UpdateConfig(SwitchConfig config)
        {
            config.NatEnabled = IsNatEnabled;
            config.DhcpEnabled = IsDhcpEnabled;

            if (UpdateSubnetFeedback())
            {
                config.Subnet = $"{SubnetOctet1}.{SubnetOctet2}.{SubnetOctet3}.{SubnetOctet4}/{SubnetCidr}";
            }
            else
            {
                config.Subnet = string.Empty;
            }
        }

        private bool UpdateSubnetFeedback()
        {
            bool isValid = true;
            if (!byte.TryParse(SubnetOctet1, out byte o1)) isValid = false;
            if (!byte.TryParse(SubnetOctet2, out byte o2)) isValid = false;
            if (!byte.TryParse(SubnetOctet3, out byte o3)) isValid = false;
            if (!byte.TryParse(SubnetOctet4, out byte o4)) isValid = false;
            if (!int.TryParse(SubnetCidr, out int cidr) || cidr < 0 || cidr > 32) isValid = false;

            if (!isValid)
            {
                SubnetFeedbackText = "子网地址不合法";
                SubnetFeedbackBrush = Brushes.Red;
                return false;
            }

            try
            {
                var ipAddressBytes = new byte[] { o1, o2, o3, o4 };
                if (BitConverter.IsLittleEndian) Array.Reverse(ipAddressBytes);
                uint ipAsInt = BitConverter.ToUInt32(ipAddressBytes, 0);
                uint maskAsInt = cidr == 0 ? 0 : 0xFFFFFFFF << (32 - cidr);
                uint networkAddressInt = ipAsInt & maskAsInt;
                uint broadcastAddressInt = networkAddressInt | ~maskAsInt;
                byte[] networkBytes = BitConverter.GetBytes(networkAddressInt);
                byte[] broadcastBytes = BitConverter.GetBytes(broadcastAddressInt);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(networkBytes);
                    Array.Reverse(broadcastBytes);
                }
                var firstIp = new IPAddress(networkBytes);
                var lastIp = new IPAddress(broadcastBytes);
                SubnetFeedbackText = $"{firstIp} - {lastIp}";
                SubnetFeedbackBrush = Brushes.Gray;
                return true;
            }
            catch
            {
                SubnetFeedbackText = "计算错误";
                SubnetFeedbackBrush = Brushes.Red;
                return false;
            }
        }
    }
}