using System.Collections.Concurrent;
using System.Management;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public static class VmScreenshotService
    {
        private static readonly ConcurrentDictionary<string, string> _vmSettingsPathCache = new();

        /// <summary>
        /// 抓取运行中 VM 的画面缩略图。
        /// desiredWidth/desiredHeight 当作外接边界框：先按 guest 当前分辨率的真实宽高比缩进框内，
        /// 用那个同比尺寸去请求，令接口返回满幅、不烤黑边（接口对非同比请求会用黑色补边）。
        /// 读不到 guest 分辨率时退回按边界框原样请求（即旧行为）。
        /// </summary>
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

                    // 按 guest 当前分辨率的真实比例把请求尺寸缩进边界框，消除接口烤入的黑边
                    var (reqW, reqH) = FitToGuestAspect(targetPath, desiredWidth, desiredHeight);

                    var svc = WmiApi.GetVirtualSystemManagementService();
                    using var inParams = svc.GetMethodParameters("GetVirtualSystemThumbnailImage");
                    inParams["TargetSystem"] = targetPath;
                    inParams["WidthPixels"] = (ushort)reqW;
                    inParams["HeightPixels"] = (ushort)reqH;
                    using var outParams = svc.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);
                    if (outParams == null || (uint)outParams["ReturnValue"] != 0) { _vmSettingsPathCache.TryRemove(vmName, out _); return null; }
                    var rawBytes = (byte[])outParams["ImageData"];
                    if (rawBytes == null || rawBytes.Length == 0) return null;
                    return CreateBitmapFromRgb565(rawBytes, reqW, reqH);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Thumbnail] {ex.Message}");
                    _vmSettingsPathCache.TryRemove(vmName, out _);
                    return null;
                }
            });
        }

        // 从设置路径的 InstanceID("Microsoft:{GUID}") 取 VM GUID → 查 Msvm_VideoHead 拿 guest 当前分辨率，
        // 把真实宽高比缩进 (maxW,maxH) 边界框。任一步读不到就原样返回边界框（退回旧的 320x240 行为）。
        private static (int w, int h) FitToGuestAspect(string settingsPath, int maxW, int maxH)
        {
            try
            {
                int idx = settingsPath.IndexOf("Microsoft:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return (maxW, maxH);
                string after = settingsPath.Substring(idx + "Microsoft:".Length);
                string guid = new string(after.TakeWhile(c => c == '-' || Uri.IsHexDigit(c)).ToArray());
                if (guid.Length < 36) return (maxW, maxH);

                // 多显示头的 guest 会有多个 VideoHead（含 0x0 的失效头）；> 0 过滤掉失效头，取第一个有效头。
                var resp = WmiApi.QueryFirstAsync(
                    $"SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Msvm_VideoHead WHERE SystemName='{guid}' AND CurrentHorizontalResolution > 0 AND CurrentVerticalResolution > 0",
                    o =>
                    {
                        int gw = o["CurrentHorizontalResolution"] is null ? 0 : Convert.ToInt32(o["CurrentHorizontalResolution"]);
                        int gh = o["CurrentVerticalResolution"] is null ? 0 : Convert.ToInt32(o["CurrentVerticalResolution"]);
                        return (gw, gh);
                    }, WmiScope.HyperV).GetAwaiter().GetResult();

                if (!resp.HasData) return (maxW, maxH);
                var (gw2, gh2) = resp.Data;
                if (gw2 <= 0 || gh2 <= 0) return (maxW, maxH);

                double scale = Math.Min((double)maxW / gw2, (double)maxH / gh2);
                int reqW = Math.Max(2, (int)Math.Round(gw2 * scale));
                int reqH = Math.Max(2, (int)Math.Round(gh2 * scale));
                return (reqW, reqH);
            }
            catch { return (maxW, maxH); }
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
