// In ViewModels/MemoryPageViewModel.cs (还原版)
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace ExHyperV.ViewModels
{
    public partial class MemoryPageViewModel : ObservableObject
    {
        private readonly IMemoryService _memoryService;

        [ObservableProperty]
        private bool _isLoading;

        // 集合类型恢复为 HostMemoryViewModel
        public ObservableCollection<HostMemoryViewModel> HostMemoryModules { get; } = new();

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

                // 移除分组逻辑，直接创建 HostMemoryViewModel 列表
                var newHostMemoryModules = new List<HostMemoryViewModel>();
                foreach (var memModel in hostMemoryModels)
                {
                    newHostMemoryModules.Add(new HostMemoryViewModel(memModel));
                }

                if (System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    UpdateCollections(newHostMemoryModules);
                }
                else
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        UpdateCollections(newHostMemoryModules);
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

        private void UpdateCollections(List<HostMemoryViewModel> newModules)
        {
            HostMemoryModules.Clear();
            foreach (var module in newModules)
            {
                HostMemoryModules.Add(module);
            }
        }
    }
}