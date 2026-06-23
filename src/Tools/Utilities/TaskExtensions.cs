using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ExHyperV.Tools
{
    public static class TaskExtensions
    {
        /// <summary>
        /// 即发即忘 + 异常兜底。替代裸 <c>_ = SomeAsync()</c>——那种写法异常无人观测,
        /// 构造里的加载失败会让页面静默空白。本助手把异常记日志(可选回调上抛 UI)。
        /// 本身是 async void,但内部 try/catch 全吞,正是 SafeFireAndForget 的设计。
        /// </summary>
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
