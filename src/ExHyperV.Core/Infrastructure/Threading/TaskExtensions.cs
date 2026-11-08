using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ExHyperV.Tools
{
    /// <summary>为无法等待的任务提供显式观察异常的辅助入口。</summary>
    internal static class TaskExtensions
    {
        /// <summary>观察任务结束并将任务异常写入诊断输出，再调用可选错误回调。</summary>
        /// <remarks>返回 void，调用方不能等待此方法。任务异常被捕获，但 onError 自身的异常会从 async void 逸出；它不取消任务或延长宿主进程生命周期。</remarks>
        /// <param name="task">已经启动且需要观察完成状态的任务。</param>
        /// <param name="onError">可选的错误处理回调，须在内部处理异常；界面更新需调度到界面线程。</param>
        public static async void SafeFireAndForget(this Task task, Action<Exception>? onError = null)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SafeFireAndForget] {ex}");
                onError?.Invoke(ex);
            }
        }
    }
}
