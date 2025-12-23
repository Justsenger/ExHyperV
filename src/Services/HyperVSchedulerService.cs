using ExHyperV.Tools;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

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

        public static async Task<bool> SetSchedulerTypeAsync(HyperVSchedulerType type)
        {
            string typeString = type.ToString();
            string script = $"Start-Process -FilePath 'bcdedit.exe' -ArgumentList '/set hypervisorschedulertype {typeString}' -Verb RunAs -WindowStyle Hidden -Wait";

            try
            {
                await Utils.RunScriptSTA(script);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[HyperVSchedulerService] 使用 Utils.RunScriptSTA 执行 bcdedit 失败: {ex.Message}");
                return false;
            }
        }
    }
}