using ExHyperV.Services;
using ExHyperV.Models;

namespace ExHyperV.UI.Pages.VirtualMachines;

// Only the presentation layer owns updates to collections bound by WPF.
internal static class VmStoragePresentation
{
    public static async Task LoadVmStorageItemsAsync(VmInstanceViewModel vm)
    {
        if (vm == null) return;
        var items = (await VmStorageService.GetVmStorageItemsAsync(vm.Id)).RequireData();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var item in vm.StorageItems) item.Dispose();
            vm.StorageItems.Clear();
            vm.Model.StorageItems.Clear();
            foreach (var item in items)
            {
                vm.StorageItems.Add(new VmStorageItemViewModel(item));
                vm.Model.StorageItems.Add(item);
            }
        });
    }

    public static async Task RefreshVirtualDiskSizesAsync(VmInstanceViewModel vm, CancellationToken cancellationToken = default)
    {
        if (vm == null) return;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var paths = await dispatcher.InvokeAsync(() => vm.StorageItems.Where(i => i.DiskType == "Virtual")
            .Select(i => i.PathOrDiskNumber).Concat(vm.Disks.Where(d => d.DiskType != "Physical").Select(d => d.Path)).ToArray(),
            System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var sizes = await VmStorageService.GetVirtualDiskSizesAsync(paths);
        var observedAt = DateTimeOffset.UtcNow;
        await dispatcher.InvokeAsync(() =>
        {
            foreach (var item in vm.StorageItems.Where(i => i.DiskType == "Virtual"))
                if (sizes.TryGetValue(item.PathOrDiskNumber, out long bytes) && Math.Abs(item.DiskSizeGiB - bytes / 1073741824.0) > 0.001)
                    item.DiskSizeGiB = bytes / 1073741824.0;
            foreach (var disk in vm.Disks.Where(d => d.DiskType != "Physical"))
            {
                if (sizes.TryGetValue(disk.Path, out long bytes))
                {
                    if (disk.CurrentSize != bytes) disk.CurrentSize = bytes;
                    disk.SizeSample = new(PerformanceSampleState.Valid, observedAt);
                }
                else disk.SizeSample = new(PerformanceSampleState.Unavailable, observedAt,
                    "本次未取得虚拟磁盘文件大小。");
            }
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }
}
