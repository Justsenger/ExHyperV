using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ExHyperV.ViewModels
{
    public partial class AddSwitchViewModel : ObservableObject
    {
        private readonly IEnumerable<SwitchViewModel> _existingSwitches;
        private readonly IEnumerable<PhysicalAdapterInfo> _allPhysicalAdapters;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNetworkAdapterSelectionEnabled))]
        private string _selectedSwitchType = "External";

        [ObservableProperty]
        private string _switchName = "新外部交换机";

        [ObservableProperty]
        private string? _selectedNetworkAdapter;

        [ObservableProperty]
        private string? _errorMessage;

        public ObservableCollection<string> AvailableNetworkAdapters { get; } = new();
        public bool IsNetworkAdapterSelectionEnabled => _selectedSwitchType == "External" || _selectedSwitchType == "NAT";
        public string ComboBoxPlaceholderText => AvailableNetworkAdapters.Any() ? "请选择网卡..." : "无可用的物理网卡";

        public bool IsComboBoxEnabled => IsNetworkAdapterSelectionEnabled && AvailableNetworkAdapters.Any();


        public AddSwitchViewModel(IEnumerable<SwitchViewModel> existingSwitches, IEnumerable<PhysicalAdapterInfo> allPhysicalAdapters)
        {
            _existingSwitches = existingSwitches;
            _allPhysicalAdapters = allPhysicalAdapters;

            foreach (var adapter in _allPhysicalAdapters)
            {
                if (!_existingSwitches.Any(s => s.SelectedUpstreamAdapter == adapter.InterfaceDescription))
                {
                    AvailableNetworkAdapters.Add(adapter.InterfaceDescription);
                }
            }
            OnPropertyChanged(nameof(ComboBoxPlaceholderText));
            OnPropertyChanged(nameof(IsComboBoxEnabled));
        }

        partial void OnSelectedSwitchTypeChanged(string value)
        {
            SwitchName = value switch
            {
                "External" => "新外部交换机",
                "NAT" => "新NAT交换机",
                "Internal" => "新内部交换机",
                _ => "新虚拟交换机"
            };
        }

        public bool Validate()
        {
            ErrorMessage = null;
            if (string.IsNullOrWhiteSpace(SwitchName))
            {
                ErrorMessage = "交换机名称不能为空。";
                return false;
            }
            if (_existingSwitches.Any(s => s.SwitchName.Equals(SwitchName, System.StringComparison.OrdinalIgnoreCase)))
            {
                ErrorMessage = $"已存在名为 '{SwitchName}' 的交换机。";
                return false;
            }
            if (IsNetworkAdapterSelectionEnabled && !AvailableNetworkAdapters.Any())
            {
                ErrorMessage = "无法创建外部或NAT交换机，因为没有可用的物理网卡。";
                return false;
            }
            if (IsNetworkAdapterSelectionEnabled && string.IsNullOrEmpty(SelectedNetworkAdapter))
            {
                ErrorMessage = "外部或NAT交换机必须选择一个物理网卡。";
                return false;
            }
            if (_selectedSwitchType == "NAT")
            {
                if (_existingSwitches.Any(s => !s.IsDefaultSwitch && s.SelectedNetworkMode == "NAT"))
                {
                    ErrorMessage = "系统只允许存在一个用户创建的NAT网络。";
                    return false;
                }
            }
            return true;
        }
    }
}