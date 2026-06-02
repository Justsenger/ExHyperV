using System.Collections.Concurrent;
using System.Management;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExHyperV.Api;

namespace ExHyperV.Services
{
    public static class VmScreenshotService
    {
        private static readonly ConcurrentDictionary<string, string> _vmSettingsPathCache = new();

        public static async Task<BitmapSource?> CaptureAsync(string vmName, int desiredWidth, int desiredHeight)
        {
            if (desiredWidth <= 0 || desiredHeight <= 0) return null;
            return await Task.Run(() =>
            {
                try
                {
                    if (!_vmSettingsPathCache.TryGetValue(vmName, out var targetPath))
                    {
                        var settingsResp = WmiApi.QueryFirstAsync(
                            $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                            vm => {
                                using var related = vm.GetRelated("Msvm_VirtualSystemSettingData");
                                return related.Cast<ManagementObject>().FirstOrDefault()?.Path.Path ?? "";
                            }, WmiScope.HyperV).GetAwaiter().GetResult();
                        if (!settingsResp.HasData || string.IsNullOrEmpty(settingsResp.Data)) return null;
                        targetPath = settingsResp.Data;
                        _vmSettingsPathCache[vmName] = targetPath;
                    }
                    var svc = WmiApi.GetVirtualSystemManagementService();
                    using var inParams = svc.GetMethodParameters("GetVirtualSystemThumbnailImage");
                    inParams["TargetSystem"] = targetPath;
                    inParams["WidthPixels"] = (ushort)desiredWidth;
                    inParams["HeightPixels"] = (ushort)desiredHeight;
                    using var outParams = svc.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);
                    if (outParams == null || (uint)outParams["ReturnValue"] != 0) { _vmSettingsPathCache.TryRemove(vmName, out _); return null; }
                    var rawBytes = (byte[])outParams["ImageData"];
                    if (rawBytes == null || rawBytes.Length == 0) return null;
                    return CreateBitmapFromRgb565(rawBytes, desiredWidth, desiredHeight);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Thumbnail] {ex.Message}");
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
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr565, null, data, stride);
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }
    }
}