using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services; // 需要这个 using 来引用 CoreType 枚举

namespace ExHyperV.ViewModels.Dialogs
{
    public partial class SelectableCoreViewModel : ObservableObject
    {
        [ObservableProperty]
        private int _coreId;

        [ObservableProperty]
        private bool _isSelected;

        // =======================================================
        // 新增属性: 存储核心类型
        // =======================================================
        [ObservableProperty]
        private CoreType _coreType;
    }
}