using ExHyperV.ViewModels;

namespace ExHyperV.Services
{
    public interface IVmProcessorService
    {
        /// <summary>
        /// 异步获取指定虚拟机的处理器配置。
        /// </summary>
        /// <param name="vmName">虚拟机的名称。</param>
        /// <returns>包含处理器配置的 ViewModel，如果找不到则返回 null。</returns>
        Task<VMProcessorViewModel?> GetVmProcessorAsync(string vmName);

        /// <summary>
        /// 异步设置指定虚拟机的处理器配置。
        /// </summary>
        /// <param name="vmName">虚拟机的名称。</param>
        /// <param name="processorSettings">要应用的新处理器配置。</param>
        /// <returns>一个元组，指示操作是否成功以及相关的消息。</returns>
        Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VMProcessorViewModel processorSettings);
    }
}