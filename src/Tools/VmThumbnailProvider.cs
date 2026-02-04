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

        /// <summary>
        /// 异步获取指定尺寸的虚拟机缩略图。
        /// Hyper-V 会自动处理宽高比，通过添加黑边来确保画面完整性，防止裁剪。
        /// </summary>
        /// <param name="vmName">虚拟机名称。</param>
        /// <param name="desiredWidth">期望的缩略图宽度。</param>
        /// <param name="desiredHeight">期望的缩略图高度。</param>
        /// <returns>一个可以跨线程使用的 BitmapSource，如果失败则返回 null。</returns>
        public static async Task<BitmapSource?> GetThumbnailAsync(string vmName, int desiredWidth, int desiredHeight)
        {
            // 确保传入有效的尺寸
            if (desiredWidth <= 0 || desiredHeight <= 0) return null;

            return await Task.Run(() =>
            {
                try
                {
                    InitializeWmi();

                    if (_scope == null || _managementService == null) return null;

                    var vmQuery = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                    using var vmSearcher = new ManagementObjectSearcher(_scope, new ObjectQuery(vmQuery));
                    using var vm = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vm == null) return null;

                    using var settingsCollection = vm.GetRelated("Msvm_VirtualSystemSettingData");
                    using var settingData = settingsCollection.Cast<ManagementObject>().FirstOrDefault();
                    if (settingData == null) return null;

                    // 直接使用传入的宽高参数调用 WMI 方法
                    using var inParams = _managementService.GetMethodParameters("GetVirtualSystemThumbnailImage");
                    inParams["TargetSystem"] = settingData.Path.Path;
                    inParams["WidthPixels"] = (ushort)desiredWidth;
                    inParams["HeightPixels"] = (ushort)desiredHeight;

                    using var outParams = _managementService.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);
                    if (outParams == null) return null;

                    if ((uint)outParams["ReturnValue"] != 0) return null;

                    var rawBytes = (byte[])outParams["ImageData"];
                    if (rawBytes == null || rawBytes.Length == 0) return null;

                    // 使用请求的尺寸创建位图
                    return CreateBitmapFromRgb565(rawBytes, desiredWidth, desiredHeight);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Thumbnail Error] {ex.Message}");
                    _managementService?.Dispose();
                    _managementService = null;
                    _scope = null;
                    return null;
                }
            });
        }

        private static BitmapSource? CreateBitmapFromRgb565(byte[] data, int width, int height)
        {
            try
            {
                int stride = width * 2; // Bgr565 is 2 bytes per pixel
                if (data.Length < stride * height) return null;

                var bitmap = BitmapSource.Create(
                    width, height,
                    96, 96,
                    PixelFormats.Bgr565,
                    null,
                    data,
                    stride);

                bitmap.Freeze(); // Freeze for cross-thread access
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CreateBitmapFromRgb565 Error] {ex.Message}");
                return null;
            }
        }
    }
}