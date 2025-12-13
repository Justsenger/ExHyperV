// 文件: ProcessAffinityManager.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace ExHyperV.Services
{
    public static class ProcessAffinityManager
    {
        /// <summary>
        /// 根据虚拟机的 GUID 查找其对应的正在运行的 vmwp.exe 工作进程。
        /// </summary>
        private static Process FindVmWorkerProcess(Guid vmId)
        {
            // 在 Root 调度器下，vmwp.exe 进程的用户名就是虚拟机的 GUID
            string vmIdString = vmId.ToString("D").ToUpper();

            // 使用 WMI 查询所有 vmwp.exe 进程，并获取其拥有者的用户名
            string wmiQuery = "SELECT ProcessId, Handle FROM Win32_Process WHERE Name = 'vmwp.exe'";
            using (var searcher = new ManagementObjectSearcher(wmiQuery))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
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
                        catch { /* 进程可能在我们找到它和获取它之间退出了 */ }
                    }
                }
            }
            return null; // 没有找到匹配的进程
        }

        /// <summary>
        /// 获取指定虚拟机的 vmwp.exe 进程的当前 CPU 核心相关性。
        /// </summary>
        public static List<int> GetVmProcessAffinity(Guid vmId)
        {
            var coreIds = new List<int>();
            var process = FindVmWorkerProcess(vmId);
            if (process != null)
            {
                long affinityMask = (long)process.ProcessorAffinity;
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    // 检查掩码的每一位是否为 1
                    if ((affinityMask & (1L << i)) != 0)
                    {
                        coreIds.Add(i);
                    }
                }
            }
            return coreIds;
        }

        /// <summary>
        /// 为指定虚拟机的 vmwp.exe 进程设置新的 CPU 核心相关性。
        /// </summary>
        public static void SetVmProcessAffinity(Guid vmId, List<int> coreIds)
        {
            var process = FindVmWorkerProcess(vmId);
            if (process != null)
            {
                // 根据选择的核心列表，计算出新的 64 位进程相关性掩码
                long newAffinityMask = 0;
                foreach (int coreId in coreIds)
                {
                    newAffinityMask |= (1L << coreId);
                }

                if (newAffinityMask > 0)
                {
                    process.ProcessorAffinity = (IntPtr)newAffinityMask;
                }
                // 如果 newAffinityMask 为 0，则表示不限制，系统会自动分配
            }
        }
    }
}