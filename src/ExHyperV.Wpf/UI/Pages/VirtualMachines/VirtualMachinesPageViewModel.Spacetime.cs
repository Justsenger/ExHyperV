using ExHyperV.UI.Pages.VirtualMachines.Imaging;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public partial class VirtualMachinesPageViewModel
    {

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOpenWormhole))]
        [NotifyPropertyChangedFor(nameof(CanCreateMoment))]
        [NotifyCanExecuteChangedFor(nameof(AnnihilateCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConvergenceCommand))]
        private ObservableCollection<SpacetimeNodeViewModel> _spacetimeNodes = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TeleportCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenWormholeCommand))]
        [NotifyCanExecuteChangedFor(nameof(AnnihilateCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConvergenceCommand))]
        [NotifyCanExecuteChangedFor(nameof(CloseWormholeCommand))]
        private SpacetimeNodeViewModel? _selectedSpacetimeNode;
        [ObservableProperty]
        private SpacetimeMode _selectedSpacetimeMode = SpacetimeMode.Continuous;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCreateMoment))]
        private bool _isCheckpointsEnabled = true;

        partial void OnIsCheckpointsEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;
            var vm = SelectedVm;
            _ = Task.Run(async () =>
            {
                var result = await VmSpacetimeService.SetCheckpointsEnabledAsync(vm.Id, value);
                if (!result.Success)
                {
                    WpfApplication.Current.Dispatcher.Invoke(() =>
                    {
                        if (SelectedVm?.Id == vm.Id)
                            using (SuppressApply()) IsCheckpointsEnabled = !value;
                        ShowError(result.Error);
                    });
                }
            });
        }



        /// <summary>
        /// 判定逻辑：只有选中的是真实的历史快照节点（非现世指针、非虚拟根节点）时，才允许执行穿梭、删除等操作。
        /// </summary>
        private bool CanOperateHistoricalNode => VmSpacetimeService.CanOperateHistoricalNode(SelectedSpacetimeNode?.Model);
        // 开虫洞:该快照尚无虫洞;关虫洞:该快照已有虫洞——按当前状态精确门控(避免点错按钮再弹错)
        private bool CanOpenWormhole => CanOperateHistoricalNode && !SelectedSpacetimeNode!.IsWormhole;
        private bool CanCloseWormhole => CanOperateHistoricalNode && SelectedSpacetimeNode!.IsWormhole;

        // 虫洞开启时磁盘配置暂时偏离检查点树，因此禁止修改检查点树。
        public bool HasOpenWormhole => VmSpacetimeService.HasOpenWormhole(SpacetimeNodes.Select(node => node.Model));
        // 创建时空:需检查点开启 且 无虫洞。湮灭/收束:历史快照 且 无虫洞(穿梭例外——它会先自动关所有虫洞)。
        public bool CanCreateMoment => VmSpacetimeService.CanCreateMoment(IsCheckpointsEnabled, SpacetimeNodes.Select(node => node.Model));
        private bool CanMutateHistoricalNode => VmSpacetimeService.CanMutateHistoricalNode(SelectedSpacetimeNode?.Model, SpacetimeNodes.Select(node => node.Model));

        [RelayCommand]
        private async Task CommitSpacetimeRenameAsync(SpacetimeNodeViewModel node)
        {
            if (node == null || !node.IsEditing || SelectedVm == null) return;
            var vm = SelectedVm;
            node.IsEditing = false;
            if (string.IsNullOrWhiteSpace(node.EditedName) || node.EditedName == node.Name) return;

            // 起源和当前节点禁止改名
            if (node.IsLogicalNode) return;

            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.RenameSnapshotAsync(vm.Id, node.Model, node.EditedName);
                if (result.Success)
                {
                    node.Name = node.EditedName;
                    OnPropertyChanged(nameof(SpacetimeNodes));
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_ModifyFail}：{result.Error}");
                }
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }
        [RelayCommand]
        private void CancelSpacetimeRename(SpacetimeNodeViewModel node)
        {
            if (node != null) node.IsEditing = false;
        }

        [RelayCommand]
        private async Task GoToSpacetimeSettingsAsync()
        {
            if (SelectedVm == null) return;
            var vm = SelectedVm;
            CurrentViewType = VmDetailViewType.SpacetimeSettings;
            IsLoadingSettings = true;
            try
            {
                bool enabled = (await VmSpacetimeService.GetCheckpointsEnabledAsync(vm.Id)).RequireData();
                var models = (await VmSpacetimeService.GetSpacetimeNodesAsync(vm.Id, VmImagePresentation.EncodeJpeg)).RequireData();
                if (SelectedVm?.Id != vm.Id || CurrentViewType != VmDetailViewType.SpacetimeSettings) return;
                using (SuppressApply()) IsCheckpointsEnabled = enabled;
                var nodes = models.Select(node => new SpacetimeNodeViewModel(node)).ToList();

                var snapshots = nodes.Where(n => n.NodeType == SpacetimeNodeType.Snapshot).ToList();
                if (!snapshots.Any())
                {
                    var genesis = nodes.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Genesis);
                    var current = nodes.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Current);
                    if (genesis != null && current != null)
                    {
                        current.CreatedDate = genesis.CreatedDate;
                        current.ParentId = null;
                        nodes.Remove(genesis);
                    }
                }

                SpacetimeNodes = new ObservableCollection<SpacetimeNodeViewModel>(nodes);

                // 优先选中“当前”节点（现在它可能是唯一的根，也可能是快照链的末端）
                var currentNode = nodes.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Current);
                if (currentNode != null)
                {
                    SelectedSpacetimeNode = currentNode;
                }
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.VmPage_ErrRetrieveFailed2}：{ex.Message}");
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        private Task RefreshSpacetimeIfCurrentAsync(Guid vmId) =>
            SelectedVm?.Id == vmId && CurrentViewType == VmDetailViewType.SpacetimeSettings
                ? GoToSpacetimeSettingsAsync() : Task.CompletedTask;

        [RelayCommand]
        private async Task CaptureMomentAsync()
        {
            if (SelectedVm == null) return;
            if (HasOpenWormhole) return;

            var vm = SelectedVm;

            // 用 IsLoadingSettings 开局部遮罩，不锁死左侧列表
            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.CaptureMomentAsync(
                    vm.Id,
                    SelectedSpacetimeMode, encodeThumbnail: VmImagePresentation.EncodeJpeg
                );

                if (result.Success)
                {
                    // 给 WMI 一点沉降时间，防止立刻刷新读不到新快照
                    await Task.Delay(1000);

                    await RefreshSpacetimeIfCurrentAsync(vm.Id);

                    ShowSuccess(Properties.Resources.VmPage_MsgSpacetimeCreated2);
                }
                else
                {
                    ShowError(result.Error);
                }
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 穿梭 (应用快照)
        [RelayCommand(CanExecute = nameof(CanOperateHistoricalNode))]
        private async Task Teleport()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;
            var vm = SelectedVm;
            var node = SelectedSpacetimeNode;
            IsLoadingSettings = true;
            try
            {
                string targetName = node.Name;
                var result = await VmSpacetimeService.TeleportAsync(node.Model, vm.Id, SpacetimeNodes.Select(item => item.Model));
                if (result.Success)
                {
                    await Task.Delay(500);
                    await RefreshSpacetimeIfCurrentAsync(vm.Id);
                    ShowSuccess(string.Format(Properties.Resources.VmPage_MsgTraveledTo2, targetName));
                }
                else
                {
                    ShowError(result.Error);
                }
            }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand(CanExecute = nameof(CanMutateHistoricalNode))]
        private async Task Annihilate()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;
            var vm = SelectedVm;
            var node = SelectedSpacetimeNode;

            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.AnnihilateAsync(vm.Id, node.Model);
                if (result.Success)
                {
                    await Task.Delay(800);
                    await RefreshSpacetimeIfCurrentAsync(vm.Id);
                    ShowSuccess(Properties.Resources.VmPage_MsgSpacetimeAnnihilated2);
                }
                else
                {
                    ShowError(result.Error);
                }
            }
            finally { IsLoadingSettings = false; }
        }

        // 开启虫洞
        [RelayCommand(CanExecute = nameof(CanOpenWormhole))]
        private async Task OpenWormhole()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;
            var vm = SelectedVm;
            var node = SelectedSpacetimeNode;
            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.OpenWormholeAsync(vm.Id, node.Model);
                if (result.Success)
                {
                    string openedNodeId = node.Id;
                    await RefreshSpacetimeIfCurrentAsync(vm.Id);
                    var wormholeNode = SpacetimeNodes.FirstOrDefault(n => n.Id == openedNodeId);
                    if (SelectedVm?.Id == vm.Id && wormholeNode != null) SelectedSpacetimeNode = wormholeNode;
                    ShowSuccess(string.Format(Properties.Resources.VmPage_MsgConnectedTo2, node.Name));
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_ErrOpenFailed4}：{result.Error}");
                }
            }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand(CanExecute = nameof(CanCloseWormhole))]
        private async Task CloseWormhole()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;
            var vm = SelectedVm;
            var node = SelectedSpacetimeNode;
            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.CloseWormholeAsync(vm.Id, node.Model);
                if (result.Success)
                {
                    await RefreshSpacetimeIfCurrentAsync(vm.Id);
                    ShowSuccess(Properties.Resources.VmPage_MsgTimelineRestored2);
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_ErrCloseFailed2}：{result.Error}");
                }
            }
            finally { IsLoadingSettings = false; }
        }

        // 时空收束
        [RelayCommand(CanExecute = nameof(CanMutateHistoricalNode))]
        private async Task Convergence()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;
            var vm = SelectedVm;
            var node = SelectedSpacetimeNode;

            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.ConvergeAsync(vm.Id, node.Model);
                if (result.Success)
                {
                    await Task.Delay(800);
                    await RefreshSpacetimeIfCurrentAsync(vm.Id);
                    ShowSuccess(Properties.Resources.VmPage_MsgSpacetimeConverged2);
                }
                else
                {
                    ShowError(result.Error);
                }
            }
            finally { IsLoadingSettings = false; }
        }
    }
}
