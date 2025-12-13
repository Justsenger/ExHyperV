using ExHyperV.Tools; // 引入包含 Utils 类的命名空间
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public enum HyperVSchedulerType
    {
        Classic,
        Core,
        Root,
        Unknown
    }

    public static class HyperVSchedulerService
    {
        // GetSchedulerType() 方法保持不变，因为它已经很完美了
        public static HyperVSchedulerType GetSchedulerType()
        {
            try
            {
                string query = "*[System[Provider[@Name='Microsoft-Windows-Hyper-V-Hypervisor'] and (EventID=2)]]";
                EventLogQuery eventQuery = new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true };

                using (EventLogReader logReader = new EventLogReader(eventQuery))
                {
                    EventRecord record = logReader.ReadEvent();
                    if (record != null && record.Properties.Count > 0)
                    {
                        ushort schedulerCode = System.Convert.ToUInt16(record.Properties[0].Value);
                        return schedulerCode switch
                        {
                            1 => HyperVSchedulerType.Classic,
                            2 => HyperVSchedulerType.Classic,
                            3 => HyperVSchedulerType.Core,
                            4 => HyperVSchedulerType.Root,
                            _ => HyperVSchedulerType.Unknown,
                        };
                    }
                }
            }
            catch (System.Exception ex) { Debug.WriteLine($"[HyperVSchedulerService] 查询事件日志失败: {ex.Message}"); }
            return HyperVSchedulerType.Unknown;
        }

        /// <summary>
        /// 使用 Utils.RunScriptSTA 异步、无窗口地设置新的 Hypervisor 调度器类型。
        /// </summary>
        /// <returns>一个表示操作是否成功启动的 Task<bool>。</returns>
        public static async Task<bool> SetSchedulerTypeAsync(HyperVSchedulerType type)
        {
            string typeString = type.ToString();

            // 构建一个 PowerShell 脚本来调用 bcdedit.exe
            // 使用 Start-Process -Verb RunAs 来请求管理员权限，并且 -WindowStyle Hidden 来隐藏窗口
            string script = $"Start-Process -FilePath 'bcdedit.exe' -ArgumentList '/set hypervisorschedulertype {typeString}' -Verb RunAs -WindowStyle Hidden -Wait";

            try
            {
                // 调用您 Utils 类中的方法
                await Utils.RunScriptSTA(script);
                return true;
            }
            catch (System.Exception ex)
            {
                // 如果用户在UAC弹窗中点击了“否”，或者发生其他错误，这里会捕获到异常
                Debug.WriteLine($"[HyperVSchedulerService] 使用 Utils.RunScriptSTA 执行 bcdedit 失败: {ex.Message}");
                return false;
            }
        }
    }
}