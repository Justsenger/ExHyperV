using ExHyperV.Models;

namespace ExHyperV.Services
{
    /// <summary>
    /// 定义了MMIO空间检查的结果类型。
    /// </summary>
    public enum MmioCheckResultType
    {
        /// <summary>
        /// MMIO空间充足，无需操作。
        /// </summary>
        Ok,

        /// <summary>
        /// MMIO空间不足，需要用户确认才能进行更新。
        /// </summary>
        NeedsConfirmation,

        /// <summary>
        /// 在检查过程中发生错误。
        /// </summary>
        Error
    }

    /// <summary>
    /// 定义了与Hyper-V和DDA设备交互的服务接口。
    /// </summary>
    public interface IHyperVService
    {
        /// <summary>
        /// 异步获取所有可分配的DDA设备和虚拟机的名称。
        /// </summary>
        /// <returns>包含设备列表和虚拟机名称列表的元组。</returns>
        Task<(List<DeviceInfo> Devices, List<string> VmNames)> GetDdaInfoAsync();

        /// <summary>
        /// 异步检查操作系统是否为服务器版本。
        /// </summary>
        /// <returns>如果是服务器版本，则为true；否则为false。</returns>
        Task<bool> IsServerOperatingSystemAsync();

        /// <summary>
        /// 异步检查指定虚拟机的MMIO空间。
        /// </summary>
        /// <param name="vmName">要检查的虚拟机名称。</param>
        /// <returns>一个包含检查结果类型和相关消息的元组。</returns>
        Task<(MmioCheckResultType Result, string Message)> CheckMmioSpaceAsync(string vmName);

        /// <summary>
        /// 异步强制更新指定虚拟机的MMIO空间（此操作会关闭虚拟机）。
        /// </summary>
        /// <param name="vmName">要更新的虚拟机名称。</param>
        /// <returns>如果操作成功，则为true；否则为false。</returns>
        Task<bool> UpdateMmioSpaceAsync(string vmName);

        /// <summary>
        /// 异步执行DDA设备分配操作。
        /// </summary>
        /// <param name="targetVmName">目标虚拟机名称或"主机"。</param>
        /// <param name="currentVmName">设备当前分配的虚拟机名称。</param>
        /// <param name="instanceId">设备的实例ID。</param>
        /// <param name="path">设备的位置路径。</param>
        /// <returns>一个元组，包含操作是否成功和失败时的错误信息。</returns>
        Task<(bool Success, string? ErrorMessage)> ExecuteDdaOperationAsync(string targetVmName, string currentVmName, string instanceId, string path);
    }
}