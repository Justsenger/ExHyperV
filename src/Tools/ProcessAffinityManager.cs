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
        /// 使用 LIKE 操作符以兼容进程名是否带有 .exe 后缀的情况。
        /// </summary>
        private static Process FindVmMemoryProcess(Guid vmId)
        {
            string vmIdString = vmId.ToString("D").ToUpper();

            // ===================================================================
            //                      ★★★ 最终修正点 ★★★
            // 将精确匹配 'vmmem.exe' 修改为模式匹配 'vmmem%'，以确保健壮性。
            // ===================================================================
            string wmiQuery = "SELECT ProcessId, Handle FROM Win32_Process WHERE Name LIKE 'vmmem%'";
            try
            {
                using (var searcher = new ManagementObjectSearcher(wmiQuery))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string[] owner = new string[2];
                        mo.InvokeMethod("GetOwner", (object[])owner);
                        string userName = owner[0];

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

            return null;
        }

        /// <summary>
        /// 获取指定虚拟机的 vmmem 进程的当前 CPU 核心相关性。
        /// </summary>
        public static List<int> GetVmProcessAffinity(Guid vmId)
        {
            var coreIds = new List<int>();
            var process = FindVmMemoryProcess(vmId);
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
            var process = FindVmMemoryProcess(vmId);
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
                    else
                    {
                        long allProcessorsMask = (1L << Environment.ProcessorCount) - 1;
                        if (Environment.ProcessorCount == 64)
                        {
                            allProcessorsMask = -1;
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