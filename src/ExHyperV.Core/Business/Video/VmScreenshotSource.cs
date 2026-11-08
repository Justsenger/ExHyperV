using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>不建立连接，通过 Hyper-V WMI 获取虚拟机基本控制台的一张截图。</summary>
    /// <remarks>适合列表缩略图，返回 RGB565 图像；不读取增强会话的 RDP 用户桌面。
    /// 对已建立的基本或增强连接截图，应使用该连接的 IConsoleVideoSource.CaptureFrame；持续画面使用 FrameReceived。</remarks>
    public static class VmScreenshotSource
    {
        /// <summary>获取基本控制台截图，并将尺寸限制在指定的宽、高范围内。</summary>
        /// <param name="vmId">本机虚拟机的 GUID。</param>
        /// <param name="desiredWidth">截图允许的最大宽度，1～65535 像素。</param>
        /// <param name="desiredHeight">截图允许的最大高度，1～65535 像素。</param>
        /// <returns>包含实际尺寸和 RGB565 像素的截图。没有可用画面、参数无效或读取失败时返回 null。</returns>
        /// <remarks>
        /// <para>能读取来宾分辨率时，会保持其宽高比。例如 1920 × 1080 的桌面，指定 400 × 300，可得到 400 × 225 的截图。
        /// 有多个显示头时使用首个有效分辨率；无法读取分辨率时按请求的宽高获取。</para>
        /// <para>无法确定当前配置、图像数据不完整或图像过大时返回 null，与没有画面使用同一返回值。</para>
        /// </remarks>
        /// <example>
        /// <para>获取缩略图并转换为 JPEG 字节，随后可写文件或作为网页响应。</para>
        /// <code>
        /// using ExHyperV.Services;
        /// public static class ScreenshotExample
        /// {
        ///     public static async Task&lt;byte[]?&gt; CaptureJpegAsync(Guid vmId)
        ///     {
        ///         var image = await VmScreenshotSource.CaptureAsync(vmId, 400, 300);
        ///         return image?.EncodeJpeg();
        ///     }
        /// }
        /// </code>
        /// </example>
        public static async Task<VmScreenshotImage?> CaptureAsync(Guid vmId, int desiredWidth, int desiredHeight)
        {
            if (vmId == Guid.Empty || !IsValidDimension(desiredWidth) || !IsValidDimension(desiredHeight)) return null;
            return await Task.Run(() =>
            {
                try
                {
                    // 始终按稳定身份读取目标，不因虚拟机重命名或名称复用而改取其他画面。
                    var settingsResp = WmiApi.QueryAsync(
                        $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                        vm => ReadCurrentSettingsPath(vm, vmId), WmiScope.HyperV).GetAwaiter().GetResult();
                    if (!settingsResp.HasData || settingsResp.Data.Count != 1) return null;
                    var targetPath = settingsResp.Data[0];
                    if (string.IsNullOrWhiteSpace(targetPath)) return null;

                    // 按 guest 当前分辨率的真实比例把请求尺寸缩进边界框，消除接口烤入的黑边
                    var (reqW, reqH) = FitToGuestAspect(vmId, desiredWidth, desiredHeight);
                    if (!IsValidDimension(reqW) || !IsValidDimension(reqH)) return null;
                    long byteLength = (long)reqW * reqH * 2;
                    if (byteLength > Array.MaxLength) return null;

                    using var svc = WmiApi.GetVirtualSystemManagementService();
                    using var inParams = svc.GetMethodParameters("GetVirtualSystemThumbnailImage");
                    inParams["TargetSystem"] = targetPath;
                    inParams["WidthPixels"] = (ushort)reqW;
                    inParams["HeightPixels"] = (ushort)reqH;
                    using var outParams = svc.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);
                    if (outParams == null || outParams["ReturnValue"] is not uint result || result != 0) return null;
                    return outParams["ImageData"] is byte[] rawBytes
                        ? VmScreenshotImage.FromHyperVThumbnail(rawBytes, reqW, reqH) : null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Thumbnail] {ex.Message}");
                    return null;
                }
            });
        }

        internal static async Task<VmScreenshotImage?> CaptureAsync(string vmName, int desiredWidth, int desiredHeight)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            return identity.HasData ? await CaptureAsync(identity.Data, desiredWidth, desiredHeight) : null;
        }

        private static bool IsValidDimension(int value) => value is > 0 and <= ushort.MaxValue;

        private static string? ReadCurrentSettingsPath(ManagementObject vm, Guid vmId)
        {
            if (!Guid.TryParse(vm["Name"]?.ToString(), out var actualVmId) || actualVmId != vmId)
                return null;

            using var related = vm.GetRelated(
                "Msvm_VirtualSystemSettingData", "Msvm_SettingsDefineState",
                null, null, "SettingData", "ManagedElement", false, null);
            var configurations = new List<ManagementObject>();
            try
            {
                foreach (ManagementObject configuration in related)
                    configurations.Add(configuration);

                var realized = configurations.Where(item => string.Equals(
                    item["VirtualSystemType"]?.ToString(), "Microsoft:Hyper-V:System:Realized",
                    StringComparison.Ordinal)).ToList();
                // 沿用当前配置读取的兼容约定；不把检查点或计划配置作为后备。
                var candidates = realized.Count > 0 ? realized : configurations.Where(item => string.Equals(
                    item["VirtualSystemType"]?.ToString(), "Microsoft:Hyper-V:System:Definition",
                    StringComparison.Ordinal)).ToList();
                if (candidates.Count != 1) return null;

                var settings = candidates[0];
                if (string.IsNullOrWhiteSpace(settings["InstanceID"]?.ToString()) ||
                    !Guid.TryParse(settings["VirtualSystemIdentifier"]?.ToString(), out var settingsVmId) ||
                    settingsVmId != vmId)
                    return null;

                return settings.Path.Path;
            }
            finally
            {
                foreach (var configuration in configurations)
                    configuration.Dispose();
            }
        }

        // 直接使用请求的 VM GUID；设置 InstanceID 不是显示头的稳定身份。
        // 分辨率不可用时沿用包围框尺寸，不影响缩略图接口本身的读取。
        private static (int w, int h) FitToGuestAspect(Guid vmId, int maxW, int maxH)
        {
            try
            {
                // 多显示头的 guest 会有多个 VideoHead（含 0x0 的失效头）；> 0 过滤掉失效头，取第一个有效头。
                var resp = WmiApi.QueryFirstAsync(
                    $"SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Msvm_VideoHead WHERE SystemName='{vmId:D}' AND CurrentHorizontalResolution > 0 AND CurrentVerticalResolution > 0",
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
                int reqW = Math.Clamp((int)Math.Round(gw2 * scale), 1, maxW);
                int reqH = Math.Clamp((int)Math.Round(gh2 * scale), 1, maxH);
                return (reqW, reqH);
            }
            catch { return (maxW, maxH); }
        }
    }
}
