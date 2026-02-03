using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExHyperV.Tools
{
    public static class VmThumbnailProvider
    {
        private const string HyperVScopePath = @"root\virtualization\v2";

        // 缓存 Scope 和 Service，避免每 2 秒重复查询导致的 CPU 开销
        private static ManagementScope? _scope;
        private static ManagementObject? _managementService;

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

        public static async Task<BitmapSource?> GetThumbnailAsync(string vmName, int? desiredWidth = null, int? desiredHeight = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 1. 初始化或获取缓存的 WMI 连接
                    InitializeWmi();

                    if (_scope == null || _managementService == null) return null;

                    // 2. 快速定位虚拟机
                    var vmQuery = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                    using var vmSearcher = new ManagementObjectSearcher(_scope, new ObjectQuery(vmQuery));
                    using var vmCollection = vmSearcher.Get();
                    using var vm = vmCollection.Cast<ManagementObject>().FirstOrDefault();

                    if (vm == null) return null;

                    // 3. 获取 SettingData (关键参数)
                    using var settingsCollection = vm.GetRelated("Msvm_VirtualSystemSettingData");
                    using var settingData = settingsCollection.Cast<ManagementObject>().FirstOrDefault();

                    if (settingData == null) return null;

                    // 4. 确定分辨率
                    int finalWidth = desiredWidth ?? 160;
                    int finalHeight = desiredHeight ?? 120;

                    // 如果没有指定宽高，才去查询视频头（比较耗时）
                    if (!desiredWidth.HasValue || !desiredHeight.HasValue)
                    {
                        (finalWidth, finalHeight) = GetNativeVmResolution(vm);
                    }

                    // 5. 调用方法
                    using var inParams = _managementService.GetMethodParameters("GetVirtualSystemThumbnailImage");
                    inParams["TargetSystem"] = settingData.Path.Path;
                    inParams["WidthPixels"] = (ushort)finalWidth;
                    inParams["HeightPixels"] = (ushort)finalHeight;

                    using var outParams = _managementService.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);

                    if (outParams == null) return null;

                    uint returnValue = (uint)outParams["ReturnValue"];
                    if (returnValue != 0) return null;

                    var rawBytes = (byte[])outParams["ImageData"];
                    if (rawBytes == null || rawBytes.Length == 0) return null;

                    // 6. 转换图像
                    return CreateBitmapFromRgb565(rawBytes, finalWidth, finalHeight);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Thumbnail Error] {ex.Message}");
                    // 如果发生错误，清空缓存以便下次重连
                    _managementService = null;
                    _scope = null;
                    return null;
                }
            });
        }

        private static (int width, int height) GetNativeVmResolution(ManagementObject vm)
        {
            using var videoCollection = vm.GetRelated("Msvm_VideoHead");
            using var videoHead = videoCollection.Cast<ManagementObject>().FirstOrDefault();
            if (videoHead != null)
            {
                var wObj = (Array)videoHead["CurrentHorizontalResolution"];
                var hObj = (Array)videoHead["CurrentVerticalResolution"];
                int width = (wObj != null && wObj.Length > 0) ? Convert.ToInt32(wObj.GetValue(0)) : 1024;
                int height = (hObj != null && hObj.Length > 0) ? Convert.ToInt32(hObj.GetValue(0)) : 768;
                return (width, height);
            }
            return (1024, 768);
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

                bitmap.Freeze(); // 必须冻结，否则跨线程赋值会报错
                return bitmap;
            }
            catch { return null; }
        }
    }
}