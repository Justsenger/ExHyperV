// 文件: HyperVSchedulerService.cs

using System.Diagnostics;
using System.Diagnostics.Eventing.Reader; // 引入用于读取事件日志的命名空间
using System.Linq;

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
        /// <summary>
        /// 根据微软官方文档，通过查询 Windows 事件日志来获取当前正在运行的 Hypervisor 调度器类型。
        /// 这是最可靠和兼容性最好的方法。
        /// </summary>
        /// <returns>返回一个表示调度器类型的枚举值。</returns>
        public static HyperVSchedulerType GetSchedulerType()
        {
            try
            {
                // 构建一个 XPath 查询，用于精确查找我们需要的事件
                // 我们要找的是：提供程序为 "Microsoft-Windows-Hyper-V-Hypervisor"，并且事件ID为 2 的事件
                string query = "*[System[Provider[@Name='Microsoft-Windows-Hyper-V-Hypervisor'] and (EventID=2)]]";

                // 创建事件日志查询对象，指定日志名称为 "System"，并设置查询方向为从后向前（ReverseDirection = true）
                // 这样我们读取到的第一个事件就是最新的那一个
                EventLogQuery eventQuery = new EventLogQuery("System", PathType.LogName, query)
                {
                    ReverseDirection = true
                };

                // 创建事件日志读取器
                using (EventLogReader logReader = new EventLogReader(eventQuery))
                {
                    // 读取最新的一个匹配事件
                    EventRecord record = logReader.ReadEvent();

                    if (record != null)
                    {
                        // 事件的详细数据存储在 Properties 集合中。
                        // 根据文档，调度器类型代码是第一个属性。
                        if (record.Properties.Count > 0)
                        {
                            // 将属性值安全地转换为 ushort 类型
                            ushort schedulerCode = Convert.ToUInt16(record.Properties[0].Value);

                            // 根据文档中的值对应关系，返回枚举
                            // 1 = Classic SMT Disabled, 2 = Classic, 3 = Core, 4 = Root
                            switch (schedulerCode)
                            {
                                case 1:
                                case 2:
                                    return HyperVSchedulerType.Classic;
                                case 3:
                                    return HyperVSchedulerType.Core;
                                case 4:
                                    return HyperVSchedulerType.Root;
                                default:
                                    return HyperVSchedulerType.Unknown;
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                // 如果查询事件日志时发生异常（例如权限问题或日志损坏）
                Debug.WriteLine($"[HyperVSchedulerService] 查询事件日志失败: {ex.Message}");
                return HyperVSchedulerType.Unknown;
            }

            // 如果没有找到事件ID为2的日志，则返回未知
            return HyperVSchedulerType.Unknown;
        }
    }
}