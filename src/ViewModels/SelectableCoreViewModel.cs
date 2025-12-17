using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels.Dialogs
{
    public partial class SelectableCoreViewModel : ObservableObject
    {
        [ObservableProperty]
        private int _coreId;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private CoreType _coreType;
    }
}