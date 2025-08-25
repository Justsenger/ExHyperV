// In ViewModels/MemoryBankGroupViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ExHyperV.ViewModels
{
    public partial class MemoryBankGroupViewModel : ObservableObject
    {
        public string BankLabel { get; }

        // 这个属性将用于在UI上显示组标题，例如 "内存组: BANK 0"
        public string GroupHeader => $"内存组: {BankLabel}";

        // 这个集合将持有所有属于这个组的内存条
        public ObservableCollection<HostMemoryViewModel> MemoryModulesInGroup { get; } = new();

        public MemoryBankGroupViewModel(string bankLabel, List<HostMemoryViewModel> modules)
        {
            BankLabel = bankLabel;
            foreach (var module in modules)
            {
                MemoryModulesInGroup.Add(module);
            }
        }
    }
}