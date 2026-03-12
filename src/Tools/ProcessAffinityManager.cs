using System.Diagnostics;
using System.Management;

namespace ExHyperV.Services
{
    public static class ProcessAffinityManager
    {
        /// <summary>
        /// 根据虚拟机的 GUID，通过查询用户名为该 GUID 的 vmmem 进程来查找其内存进程。
        /// </summary>
        private static Process FindVmMemoryProcess(Guid vmId)
        {
            string vmIdString = vmId.ToString("D").ToUpper();
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
                                Debug.WriteLine(string.Format(Properties.Resources.ProcessAffinityManager_1, mo["ProcessId"], ex.Message));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.ProcessAffinityManager_2, ex.Message));
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
                    Debug.WriteLine(string.Format(Properties.Resources.ProcessAffinityManager_3, process.Id, ex.Message));
                }
            }
            return coreIds;
        }

        /// <summary>
        /// 为指定虚拟机的 vmmem 进程设置新的 CPU 核心相关性。
        /// </summary>
        public static bool SetVmProcessAffinity(Guid vmId, List<int> coreIds)
        {
            var process = FindVmMemoryProcess(vmId);
            if (process == null) return false; // 没找到进程，返回失败

            try
            {
                long newAffinityMask = 0;
                foreach (int coreId in coreIds) newAffinityMask |= (1L << coreId);

                if (coreIds.Any())
                    process.ProcessorAffinity = (IntPtr)newAffinityMask;
                else
                    process.ProcessorAffinity = (IntPtr)((1L << Environment.ProcessorCount) - 1);

                return true; // 设置成功
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.ProcessAffinityManager_4, ex.Message));
                return false;
            }
        }
    }
}