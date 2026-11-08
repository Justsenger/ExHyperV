using ExHyperV.UI.Shared.Behaviors;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public partial class VirtualMachinesPageViewModel
    {
        private async Task RefreshBootOrderForSelectedVmAsync(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            try
            {
                var list = (await VmBootService.GetBootOrderAsync(vm.Id)).RequireData();

                WpfApplication.Current.Dispatcher.Invoke(() => {
                    vm.BootOrderItems.Clear();
                    foreach (var item in list)
                    {
                        vm.BootOrderItems.Add(new BootOrderItemViewModel(item));
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
            var vm = SelectedVm;
            CurrentViewType = VmDetailViewType.BootSettings;
            IsLoadingSettings = true;

            try
            {
                var list = (await VmBootService.GetBootOrderAsync(vm.Id)).RequireData();
                if (SelectedVm != vm) return;

                SelectedVm.BootOrderItems.Clear();
                foreach (var item in list) SelectedVm.BootOrderItems.Add(new BootOrderItemViewModel(item));

                if (SelectedVm.BootOrderItems.Count > 0)
                    SelectedVm.BootOrderItems.Last().IsLast = true;
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand]
        private async Task SaveBootOrderAsync()
        {
            if (SelectedVm == null || SelectedVm.BootOrderItems == null) return;

            var vm = SelectedVm;   // await 期间用户可能切走，捕获当前 VM，回滚刷到正确对象
            var result = await VmBootService.SetBootOrderAsync(vm.Id, vm.BootOrderItems.Select(item => item.Model).ToList());
            if (result.Success) return;

            // 保存失败：拖动已把 UI 改成新顺序、后端却没动 → 显示真实原因，再从后端重新拉取回滚 UI，避免列表显示假序
            string reason = string.IsNullOrWhiteSpace(result.Error)
                ? Properties.Resources.Error_Common_SaveFail
                : FriendlyError.CleanLines(result.Error);
            ShowError($"{Properties.Resources.VmBootSettings_TitleBootOrder}：{reason}");
            await RefreshBootOrderForSelectedVmAsync(vm);
        }

        [RelayCommand]
        private void ReorderBootItem(DragMoveArgs args)
        {
            if (SelectedVm == null || args == null) return;

            var list = SelectedVm.BootOrderItems;
            int oldIndex = list.IndexOf(args.Source);
            int newIndex = list.IndexOf(args.Target);

            if (oldIndex == -1 || newIndex == -1) return;

            // 1/3 阈值判定拖放最终落点
            if (newIndex > oldIndex) // 向下拖
            {
                if (args.RelativeY < args.Threshold) return;
            }
            else // 向上拖
            {
                if (args.RelativeY > (args.Threshold * 2)) return; // 对应 targetItem.ActualHeight - threshold
            }

            list.Move(oldIndex, newIndex);

            foreach (var item in list) item.IsLast = false;
            list.Last().IsLast = true;
        }
    }
}
