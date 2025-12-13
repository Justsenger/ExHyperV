// 文件: CpuTopologyService.cs

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExHyperV.Services
{
    public static class CpuTopologyService
    {
        #region Windows API 结构体和函数定义 (P/Invoke)
        // 这些定义与您在 CpuMonitorService 中使用的基本一致
        private enum LOGICAL_PROCESSOR_RELATIONSHIP { RelationProcessorCore = 0 }

        [StructLayout(LayoutKind.Sequential)]
        private struct GROUP_AFFINITY
        {
            public UIntPtr Mask;
            public ushort Group;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public ushort[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSOR_RELATIONSHIP
        {
            public byte Flags;
            public byte EfficiencyClass;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;
            public ushort GroupCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public GROUP_AFFINITY[] GroupMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
            public uint Size;
            public PROCESSOR_RELATIONSHIP Processor;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType,
            IntPtr Buffer,
            ref uint ReturnedLength);
        #endregion

        /// <summary>
        /// 精确获取CPU“兄弟”核心的映射表，此方法只关心SMT配对，不区分大小核。
        /// </summary>
        /// <returns>一个字典，其中键是逻辑核心ID，值是它对应的另一个超线程核心的ID。</returns>
        public static Dictionary<int, int> GetCpuSiblingMap()
        {
            var siblingMap = new Dictionary<int, int>();
            uint bufferSize = 0;
            GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref bufferSize);
            if (bufferSize == 0) return siblingMap;

            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                if (!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref bufferSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                IntPtr currentPtr = buffer;
                long offset = 0;
                while (offset < bufferSize)
                {
                    // 注意：这里需要使用您在CpuMonitorService中定义的完整结构体
                    var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(currentPtr);

                    if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                    {
                        var coreIdsInGroup = new List<int>();
                        // 遍历这个物理核心上的所有处理器组（通常只有一个）
                        for (int i = 0; i < info.Processor.GroupCount; i++)
                        {
                            // 访问GroupMask数组
                            IntPtr groupMaskPtr = currentPtr + (int)Marshal.OffsetOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>("Processor")
                                                  + (int)Marshal.OffsetOf<PROCESSOR_RELATIONSHIP>("GroupMask")
                                                  + i * Marshal.SizeOf<GROUP_AFFINITY>();

                            var groupAffinity = Marshal.PtrToStructure<GROUP_AFFINITY>(groupMaskPtr);
                            var mask = (ulong)groupAffinity.Mask;

                            for (int bit = 0; bit < 64; bit++)
                            {
                                if ((mask & (1UL << bit)) != 0)
                                {
                                    coreIdsInGroup.Add(bit + groupAffinity.Group * 64);
                                }
                            }
                        }

                        // 如果一个物理核心正好包含2个逻辑核心，它们就是一对SMT“兄弟”
                        if (coreIdsInGroup.Count == 2)
                        {
                            siblingMap[coreIdsInGroup[0]] = coreIdsInGroup[1];
                            siblingMap[coreIdsInGroup[1]] = coreIdsInGroup[0];
                        }
                    }

                    offset += info.Size;
                    currentPtr = IntPtr.Add(currentPtr, (int)info.Size);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
            return siblingMap;
        }
    }
}