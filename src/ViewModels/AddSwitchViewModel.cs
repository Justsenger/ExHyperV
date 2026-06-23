using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels
{
    public partial class AddSwitchViewModel : ObservableObject
    {
        private readonly IEnumerable<SwitchViewModel> _existingSwitches;
        private readonly IEnumerable<string> _allPhysicalAdapters;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNetworkAdapterSelectionEnabled))]
        private SwitchMode _selectedSwitchType = SwitchMode.Bridge;

        [ObservableProperty]
        private string _switchName = ExHyperV.Properties.Resources.AddSwitch_DefaultName_External;

        [ObservableProperty]
        private string? _selectedNetworkAdapter;

        [ObservableProperty]
        private string? _errorMessage;

        public ObservableCollection<string> AvailableNetworkAdapters { get; } = new();
        public bool IsNetworkAdapterSelectionEnabled => _selectedSwitchType == SwitchMode.Bridge || _selectedSwitchType == SwitchMode.NAT;


        public AddSwitchViewModel(IEnumerable<SwitchViewModel> existingSwitches, IEnumerable<string> allPhysicalAdapters)
        {
            _existingSwitches = existingSwitches;
            _allPhysicalAdapters = allPhysicalAdapters;

            foreach (var adapter in _allPhysicalAdapters)
            {
                if (!_existingSwitches.Any(s => s.SelectedUpstreamAdapter == adapter))
                {
                    AvailableNetworkAdapters.Add(adapter);
                }
            }
        }

        partial void OnSelectedSwitchTypeChanged(SwitchMode value)
        {
            SwitchName = value switch
            {
                SwitchMode.Bridge => ExHyperV.Properties.Resources.AddSwitch_DefaultName_External,
                SwitchMode.NAT => ExHyperV.Properties.Resources.AddSwitch_DefaultName_NAT,
                SwitchMode.Isolated => ExHyperV.Properties.Resources.AddSwitch_DefaultName_Internal,
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
            if (_selectedSwitchType == SwitchMode.NAT)
            {
                if (_existingSwitches.Any(s => !s.IsDefaultSwitch && s.SelectedNetworkMode == SwitchMode.NAT))
                {
                    ErrorMessage = ExHyperV.Properties.Resources.AddSwitch_Validation_OnlyOneNatAllowed;
                    return false;
                }
            }
            return true;
        }
    }
}