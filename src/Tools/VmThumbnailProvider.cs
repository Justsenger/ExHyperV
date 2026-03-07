using System.Collections.Concurrent;
using System.Management;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExHyperV.Tools
{
    public static class VmThumbnailProvider
    {
        private const string HyperVScopePath = @"root\virtualization\v2";
        private static ManagementScope? _scope;
        private static ManagementObject? _managementService;

        // 缓存：VM名称 -> Settings对象的WMI路径字符串
        // 这样可以避免每次都执行 "SELECT * FROM..." 和 "GetRelated"
        private static readonly ConcurrentDictionary<string, string> _vmSettingsPathCache = new();

        private static void InitializeWmi()
        {
            if (_scope == null || !_scope.IsConnected)
            {
                _scope = new ManagementScope(HyperVScopePath);
                _scope.Connect();
            }

            if (_managementService == null)
            {
                using var serviceClass = new ManagementClass(_scope, new ManagementPath("Msvm_VirtualSystemManagementService"), null);
                _managementService = serviceClass.GetInstances().Cast<ManagementObject>().FirstOrDefault();
            }
        }

        public static async Task<BitmapSource?> GetThumbnailAsync(string vmName, int desiredWidth, int desiredHeight)
        {
            if (desiredWidth <= 0 || desiredHeight <= 0) return null;

            return await Task.Run(() =>
            {
                try
                {
                    InitializeWmi();
                    if (_scope == null || _managementService == null) return null;

                    string targetPath;

                    // 1. 尝试从缓存获取路径
                    if (!_vmSettingsPathCache.TryGetValue(vmName, out targetPath))
                    {
                        // 2. 缓存未命中：执行昂贵的查询操作
                        var vmQuery = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                        using var vmSearcher = new ManagementObjectSearcher(_scope, new ObjectQuery(vmQuery));
                        using var vm = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();

                        if (vm == null) return null;

                        using var settingsCollection = vm.GetRelated("Msvm_VirtualSystemSettingData");
                        using var settingData = settingsCollection.Cast<ManagementObject>().FirstOrDefault();

                        if (settingData == null) return null;

                        targetPath = settingData.Path.Path;

                        // 存入缓存
                        _vmSettingsPathCache[vmName] = targetPath;
                    }

                    // 3. 直接调用方法 (这是最耗时的一步，无法避免，但前置步骤被优化了)
                    using var inParams = _managementService.GetMethodParameters("GetVirtualSystemThumbnailImage");
                    inParams["TargetSystem"] = targetPath; // 直接传入路径字符串
                    inParams["WidthPixels"] = (ushort)desiredWidth;
                    inParams["HeightPixels"] = (ushort)desiredHeight;

                    using var outParams = _managementService.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);

                    if (outParams == null || (uint)outParams["ReturnValue"] != 0)
                    {
                        // 如果失败（例如路径失效），移除缓存，下次重试查询
                        _vmSettingsPathCache.TryRemove(vmName, out _);
                        return null;
                    }

                    var rawBytes = (byte[])outParams["ImageData"];
                    if (rawBytes == null || rawBytes.Length == 0) return null;

                    return CreateBitmapFromRgb565(rawBytes, desiredWidth, desiredHeight);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Thumbnail Error] {ex.Message}");
                    // 发生异常时清空服务对象以便下次重连，并清除当前VM的缓存
                    _managementService?.Dispose();
                    _managementService = null;
                    _scope = null;
                    _vmSettingsPathCache.TryRemove(vmName, out _);
                    return null;
                }
            });
        }

        private static BitmapSource? CreateBitmapFromRgb565(byte[] data, int width, int height)
        {
            try
            {
                int stride = width * 2;
                if (data.Length < stride * height) return null;

                var bitmap = BitmapSource.Create(
                    width, height,
                    96, 96,
                    PixelFormats.Bgr565,
                    null,
                    data,
                    stride);

                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }
    }
}