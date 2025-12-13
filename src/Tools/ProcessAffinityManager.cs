// 文件: ProcessAffinityManager.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace ExHyperV.Services
{
    public static class ProcessAffinityManager
    {
        /// <summary>
        /// 根据虚拟机的 GUID，通过查询用户名为该 GUID 的 vmmem 进程来查找其内存进程。
        /// 这是根据实际系统行为确定的最直接、最可靠的方法。
        /// </summary>
        private static Process FindVmMemoryProcess(Guid vmId)
        {
            // 在 Root 调度器下，vmmem 进程的用户名就是虚拟机的 GUID
            string vmIdString = vmId.ToString("D").ToUpper();

            // 使用 WMI 查询所有 vmmem.exe 进程，并获取其 ProcessId 和用于 GetOwner 的 Handle
            string wmiQuery = "SELECT ProcessId, Handle FROM Win32_Process WHERE Name = 'vmmem.exe'";
            try
            {
                using (var searcher = new ManagementObjectSearcher(wmiQuery))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        // GetOwner 方法需要一个 out string[2] 数组来接收用户名和域名
                        string[] owner = new string[2];
                        mo.InvokeMethod("GetOwner", (object[])owner);
                        string userName = owner[0];

                        // 如果进程的用户名与我们的 VM GUID 匹配，就找到了目标进程
                        if (!string.IsNullOrEmpty(userName) && userName.Equals(vmIdString, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                int pid = Convert.ToInt32(mo["ProcessId"]);
                                return Process.GetProcessById(pid);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ProcessAffinityManager] 找到匹配的 vmmem 进程 (PID: {mo["ProcessId"]}) 但获取失败: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessAffinityManager] WMI 查询 vmmem 进程失败: {ex.Message}");
            }

            return null; // 没有找到匹配的 vmmem 进程
        }

        /// <summary>
        /// 获取指定虚拟机的 vmmem 进程的当前 CPU 核心相关性。
        /// </summary>
        public static List<int> GetVmProcessAffinity(Guid vmId)
        {
            var coreIds = new List<int>();
            var process = FindVmMemoryProcess(vmId); // 调用新的查找方法
            if (process != null)
            {
                try
                {
                    long affinityMask = (long)process.ProcessorAffinity;
                    for (int i = 0; i < Environment.ProcessorCount; i++)
                    {
                        if ((affinityMask & (1L << i)) != 0)
                        {
                            coreIds.Add(i);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessAffinityManager] 获取进程 {process.Id} 的相关性失败: {ex.Message}");
                }
            }
            return coreIds;
        }

        /// <summary>
        /// 为指定虚拟机的 vmmem 进程设置新的 CPU 核心相关性。
        /// </summary>
        public static void SetVmProcessAffinity(Guid vmId, List<int> coreIds)
        {
            var process = FindVmMemoryProcess(vmId); // 调用新的查找方法
            if (process != null)
            {
                try
                {
                    long newAffinityMask = 0;
                    foreach (int coreId in coreIds)
                    {
                        newAffinityMask |= (1L << coreId);
                    }

                    if (coreIds.Any())
                    {
                        process.ProcessorAffinity = (IntPtr)newAffinityMask;
                    }
                    else // 如果用户没有选择任何核心，则恢复为允许所有核心
                    {
                        long allProcessorsMask = (1L << Environment.ProcessorCount) - 1;
                        if (Environment.ProcessorCount == 64)
                        {
                            allProcessorsMask = -1; // Special case for 64 processors
                        }
                        process.ProcessorAffinity = (IntPtr)allProcessorsMask;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessAffinityManager] 设置进程 {process.Id} 的相关性失败: {ex.Message}");
                }
            }
        }
    }
}