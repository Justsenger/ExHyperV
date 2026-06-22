using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Interaction;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        // ===== 引导顺序模块 =====
        private async Task RefreshBootOrderForSelectedVmAsync(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            try
            {
                var list = await VmBootService.GetBootOrderAsync(vm.Name);

                Application.Current.Dispatcher.Invoke(() => {
                    vm.BootOrderItems.Clear();
                    foreach (var item in list)
                    {
                        vm.BootOrderItems.Add(item);
                    }
                    if (vm.BootOrderItems.Count > 0)
                    {
                        vm.BootOrderItems.Last().IsLast = true;
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BOOT-REFRESH-ERROR] {ex.Message}");
            }
        }

        // 引导顺序部分
        [RelayCommand]
        private async Task GoToBootSettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.BootSettings;
            IsLoadingSettings = true;

            try
            {
                var list = await VmBootService.GetBootOrderAsync(SelectedVm.Name);

                // 更新 UI 集合
                SelectedVm.BootOrderItems.Clear();
                foreach (var item in list) SelectedVm.BootOrderItems.Add(item);

                // 标记最后一个用于 UI 箭头显示
                if (SelectedVm.BootOrderItems.Count > 0)
                    SelectedVm.BootOrderItems.Last().IsLast = true;
            }
            finally { IsLoadingSettings = false; }
        }

        // 保存逻辑
        [RelayCommand]
        private async Task SaveBootOrderAsync()
        {
            await SilentSaveBootOrderAsync();
        }

        public async Task SilentSaveBootOrderAsync()
        {
            if (SelectedVm == null || SelectedVm.BootOrderItems == null) return;

            try
            {
                var currentOrder = SelectedVm.BootOrderItems.ToList();
                bool success = await VmBootService.SetBootOrderAsync(SelectedVm.Name, currentOrder);
                if (!success)
                    ShowSnackbar(Properties.Resources.VmBootSettings_TitleBootOrder, Properties.Resources.Error_Common_SaveFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmBootSettings_TitleBootOrder, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        [RelayCommand]
        private void ReorderBootItem(DragMoveArgs args)
        {
            if (SelectedVm == null || args == null) return;

            var list = SelectedVm.BootOrderItems;
            int oldIndex = list.IndexOf(args.Source);
            int newIndex = list.IndexOf(args.Target);

            if (oldIndex == -1 || newIndex == -1) return;

            // 之前的 1/3 阈值逻辑迁移到这里进行最终判定
            if (newIndex > oldIndex) // 向下拖
            {
                if (args.RelativeY < args.Threshold) return;
            }
            else // 向上拖
            {
                if (args.RelativeY > (args.Threshold * 2)) return; // 对应 targetItem.ActualHeight - threshold
            }

            // 执行移动
            list.Move(oldIndex, newIndex);

            // 更新 IsLast 标记以维护 UI 箭头显示（如果需要）
            foreach (var item in list) item.IsLast = false;
            list.Last().IsLast = true;
        }
    }
}
