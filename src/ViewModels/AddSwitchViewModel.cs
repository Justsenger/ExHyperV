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
        private string _switchName = ExHyperV.Properties.Resources.AddSwitch_DefaultName_External;

        [ObservableProperty]
        private string? _selectedNetworkAdapter;

        [ObservableProperty]
        private string? _errorMessage;

        public ObservableCollection<string> AvailableNetworkAdapters { get; } = new();
        public bool IsNetworkAdapterSelectionEnabled => _selectedSwitchType == "External" || _selectedSwitchType == "NAT";
        public string ComboBoxPlaceholderText => AvailableNetworkAdapters.Any() ? ExHyperV.Properties.Resources.AddSwitch_Placeholder_SelectAdapter : ExHyperV.Properties.Resources.AddSwitch_Placeholder_NoAdaptersAvailable;

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
                "External" => ExHyperV.Properties.Resources.AddSwitch_DefaultName_External,
                "NAT" => ExHyperV.Properties.Resources.AddSwitch_DefaultName_NAT,
                "Internal" => ExHyperV.Properties.Resources.AddSwitch_DefaultName_Internal,
                _ => ExHyperV.Properties.Resources.AddSwitch_DefaultName_Generic
            };
        }

        public bool Validate()
        {
            ErrorMessage = null;
            if (string.IsNullOrWhiteSpace(SwitchName))
            {
                ErrorMessage = ExHyperV.Properties.Resources.AddSwitch_Validation_NameCannotBeEmpty;
                return false;
            }
            if (_existingSwitches.Any(s => s.SwitchName.Equals(SwitchName, System.StringComparison.OrdinalIgnoreCase)))
            {
                ErrorMessage = string.Format(Properties.Resources.AddSwitch_Validation_NameExists, SwitchName);
                return false;
            }
            if (IsNetworkAdapterSelectionEnabled && !AvailableNetworkAdapters.Any())
            {
                ErrorMessage = ExHyperV.Properties.Resources.AddSwitch_Validation_NoAdaptersForExternalOrNat;
                return false;
            }
            if (IsNetworkAdapterSelectionEnabled && string.IsNullOrEmpty(SelectedNetworkAdapter))
            {
                ErrorMessage = ExHyperV.Properties.Resources.AddSwitch_Validation_AdapterRequiredForExternalOrNat;
                return false;
            }
            if (_selectedSwitchType == "NAT")
            {
                if (_existingSwitches.Any(s => !s.IsDefaultSwitch && s.SelectedNetworkMode == "NAT"))
                {
                    ErrorMessage = ExHyperV.Properties.Resources.AddSwitch_Validation_OnlyOneNatAllowed;
                    return false;
                }
            }
            return true;
        }
    }
}