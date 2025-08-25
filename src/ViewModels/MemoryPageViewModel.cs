// In ViewModels/MemoryPageViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq; // 确保引用了 LINQ
using System.Threading.Tasks;

namespace ExHyperV.ViewModels
{
    public partial class MemoryPageViewModel : ObservableObject
    {
        private readonly IMemoryService _memoryService;

        [ObservableProperty]
        private bool _isLoading;

        // 将主集合更改为新的分组ViewModel类型
        public ObservableCollection<MemoryBankGroupViewModel> MemoryBankGroups { get; } = new();

        public MemoryPageViewModel()
        {
            _memoryService = new MemoryService();
            _ = LoadDataCommand.ExecuteAsync(null);
        }

        [RelayCommand]
        private async Task LoadDataAsync()
        {
            if (IsLoading) return;
            IsLoading = true;

            try
            {
                var hostMemoryModels = await _memoryService.GetHostMemoryAsync();

                // 1. 先创建所有独立的内存条ViewModel
                var allModules = hostMemoryModels
                    .Select(memModel => new HostMemoryViewModel(memModel))
                    .ToList();

                // 2. 使用 LINQ 按 BankLabel 对它们进行分组，并为每个组创建一个 MemoryBankGroupViewModel
                var groupedData = allModules
                    .GroupBy(module => module.BankLabel)
                    .Select(group => new MemoryBankGroupViewModel(group.Key, group.ToList()))
                    .OrderBy(g => g.BankLabel) // 按Bank标签排序，确保BANK 0在BANK 1之前
                    .ToList();

                if (System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    UpdateCollections(groupedData);
                }
                else
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        UpdateCollections(groupedData);
                    });
                }
            }
            catch (Exception ex)
            {
                Utils.Show(string.Format(Properties.Resources.Error_LoadDataFailed, ex.Message));
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void UpdateCollections(List<MemoryBankGroupViewModel> newGroups)
        {
            MemoryBankGroups.Clear();
            foreach (var group in newGroups)
            {
                MemoryBankGroups.Add(group);
            }
        }
    }
}