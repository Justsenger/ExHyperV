using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        // ===== 时空管理模块 =====

        [ObservableProperty] private ObservableCollection<SpacetimeNode> _spacetimeNodes = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TeleportCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenWormholeCommand))]
        [NotifyCanExecuteChangedFor(nameof(ParallelSpacetimeCommand))]
        [NotifyCanExecuteChangedFor(nameof(AnnihilateCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConvergenceCommand))]
        [NotifyCanExecuteChangedFor(nameof(CloseWormholeCommand))]
        private SpacetimeNode? _selectedSpacetimeNode;
        [ObservableProperty]
        private SpacetimeMode _selectedSpacetimeMode = SpacetimeMode.Continuous;

        [ObservableProperty]
        private bool _isCheckpointsEnabled = true;

        partial void OnIsCheckpointsEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;

            _ = Task.Run(async () =>
            {
                var result = await VmSpacetimeService.SetCheckpointsEnabledAsync(SelectedVm.Name, value);
                if (!result.Success)
                {
                    // 失败时回滚 UI 状态
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        using (SuppressApply()) IsCheckpointsEnabled = !value;
                        ShowError(result.Message);
                    });
                }
            });
        }



        /// <summary>
        /// 判定逻辑：只有选中的是真实的历史快照节点（非现世指针、非虚拟根节点）时，才允许执行穿梭、删除等操作。
        /// </summary>
        private bool CanOperateHistoricalNode => SelectedSpacetimeNode != null &&
                                                SelectedSpacetimeNode.NodeType == SpacetimeNodeType.Snapshot;
        // 开虫洞:该快照尚无虫洞;关虫洞:该快照已有虫洞——按当前状态精确门控(避免点错按钮再弹错)
        private bool CanOpenWormhole => CanOperateHistoricalNode && !SelectedSpacetimeNode!.IsWormhole;
        private bool CanCloseWormhole => CanOperateHistoricalNode && SelectedSpacetimeNode!.IsWormhole;

        [RelayCommand]
        private async Task CommitSpacetimeRenameAsync(SpacetimeNode node)
        {
            if (node == null || !node.IsEditing) return;
            node.IsEditing = false;
            if (string.IsNullOrWhiteSpace(node.EditedName) || node.EditedName == node.Name) return;

            // 起源和当前节点禁止改名
            if (node.IsLogicalNode) return;

            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.RenameSnapshotAsync(node.Path, node.EditedName);
                if (result.Success)
                {
                    node.Name = node.EditedName;
                    OnPropertyChanged(nameof(SpacetimeNodes));
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_ModifyFail}：{result.Message}");
                }
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }
        [RelayCommand]
        private void CancelSpacetimeRename(SpacetimeNode node)
        {
            if (node != null) node.IsEditing = false;
        }

        [RelayCommand]
        private async Task GoToSpacetimeSettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.SpacetimeSettings;
            IsLoadingSettings = true;
            try
            {
                using (SuppressApply())
                    IsCheckpointsEnabled = await VmSpacetimeService.GetCheckpointsEnabledAsync(SelectedVm.Name);

                var nodes = await VmSpacetimeService.GetSpacetimeNodesAsync(SelectedVm.Name);

                // --- 逻辑优化：处理纯净态（仅有起源和当前时空） ---
                var snapshots = nodes.Where(n => n.NodeType == SpacetimeNodeType.Snapshot).ToList();
                if (!snapshots.Any())
                {
                    var genesis = nodes.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Genesis);
                    var current = nodes.FirstOrDefault(n => n.NodeType == SpacetimeNodeType.Current);
                    if (genesis != null && current != null)
                    {
                        // 1. 将起源的时间（原始 VHDX 时间）赋予当前
                        current.CreatedDate = genesis.CreatedDate;
                        // 2. 将当前设为根节点（去掉父 ID）
                        current.ParentId = null;
                        // 3. 移除起源节点
                        nodes.Remove(genesis);
                    }
                }

                // 更新集合
                SpacetimeNodes = new ObservableCollection<SpacetimeNode>(nodes);

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
        // 捕捉瞬间 (创建快照)
        [RelayCommand]
        private async Task CaptureMomentAsync()
        {
            if (SelectedVm == null) return;

            var currentFrame = SelectedVm.Thumbnail;

            // 用 IsLoadingSettings 开局部遮罩，不锁死左侧列表
            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.CaptureMomentAsync(
                    SelectedVm.Name,
                    SelectedSpacetimeMode
                );

                if (result.Success)
                {
                    // 给 WMI 一点沉降时间，防止立刻刷新读不到新快照
                    await Task.Delay(1000);

                    // 重新获取节点，由于在 finally 之前调用，遮罩会一直持续到节点树重新画好
                    await GoToSpacetimeSettingsAsync();

                    ShowSuccess(Properties.Resources.VmPage_MsgSpacetimeCreated2);
                }
                else
                {
                    ShowError(result.Message);
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
            IsLoadingSettings = true;
            try
            {
                // 穿梭前关闭所有虫洞；关失败则中止——否则带着挂着的临时盘应用快照会状态错乱
                var wormholeNodes = SpacetimeNodes?.Where(n => n.IsWormhole).ToList();
                if (wormholeNodes != null && wormholeNodes.Any())
                {
                    foreach (var wNode in wormholeNodes)
                    {
                        var closeRes = await VmSpacetimeService.CloseWormholeAsync(SelectedVm.Name, wNode);
                        if (!closeRes.Success)
                        {
                            ShowError(closeRes.Message);
                            return;
                        }
                    }
                }

                string targetName = SelectedSpacetimeNode.Name;
                var result = await VmSpacetimeService.TeleportAsync(SelectedSpacetimeNode, SelectedVm.Name);
                if (result.Success)
                {
                    await Task.Delay(500);
                    await GoToSpacetimeSettingsAsync();
                    ShowSuccess(string.Format(Properties.Resources.VmPage_MsgTraveledTo2, targetName));
                }
                else
                {
                    ShowError(result.Message);
                }
            }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand(CanExecute = nameof(CanOperateHistoricalNode))]
        private async Task Annihilate()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;

            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.AnnihilateAsync(SelectedVm.Name, SelectedSpacetimeNode);
                if (result.Success)
                {
                    await Task.Delay(800);
                    await GoToSpacetimeSettingsAsync();
                    ShowSuccess(Properties.Resources.VmPage_MsgSpacetimeAnnihilated2);
                }
                else
                {
                    ShowError(result.Message);
                }
            }
            finally { IsLoadingSettings = false; }
        }

        // 开启虫洞
        [RelayCommand(CanExecute = nameof(CanOpenWormhole))]
        private async Task OpenWormhole()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.OpenWormholeAsync(SelectedVm.Name, SelectedSpacetimeNode);
                if (result.Success)
                {
                    string openedNodeId = SelectedSpacetimeNode.Id;
                    await GoToSpacetimeSettingsAsync();
                    var wormholeNode = SpacetimeNodes.FirstOrDefault(n => n.Id == openedNodeId);
                    if (wormholeNode != null) SelectedSpacetimeNode = wormholeNode;
                    ShowSuccess(string.Format(Properties.Resources.VmPage_MsgConnectedTo2, SelectedSpacetimeNode.Name));
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_ErrOpenFailed4}：{result.Message}");
                }
            }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand(CanExecute = nameof(CanCloseWormhole))]
        private async Task CloseWormhole()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.CloseWormholeAsync(SelectedVm.Name, SelectedSpacetimeNode);
                if (result.Success)
                {
                    await GoToSpacetimeSettingsAsync();
                    ShowSuccess(Properties.Resources.VmPage_MsgTimelineRestored2);
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_ErrCloseFailed2}：{result.Message}");
                }
            }
            finally { IsLoadingSettings = false; }
        }

        // 平行宇宙
        [RelayCommand(CanExecute = nameof(CanOperateHistoricalNode))]
        private void ParallelSpacetime()
        {
            ShowTip($"{Properties.Resources.VmPage_MsgFeatureInDev2}：{Properties.Resources.VmPage_MsgParallelUniverse2}");
        }

        // 时空收束
        [RelayCommand(CanExecute = nameof(CanOperateHistoricalNode))]
        private async Task Convergence()
        {
            if (SelectedSpacetimeNode == null || SelectedVm == null) return;

            // 同样改为局部加载
            IsLoadingSettings = true;
            try
            {
                var result = await VmSpacetimeService.ConvergeAsync(SelectedVm.Name, SelectedSpacetimeNode);
                if (result.Success)
                {
                    await Task.Delay(800);
                    await GoToSpacetimeSettingsAsync();
                    ShowSuccess(Properties.Resources.VmPage_MsgSpacetimeConverged2);
                }
                else
                {
                    ShowError(result.Message);
                }
            }
            finally { IsLoadingSettings = false; }
        }
    }
}
