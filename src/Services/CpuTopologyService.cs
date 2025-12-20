using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExHyperV.Services
{
    public static class CpuTopologyService
    {
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
                    var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(currentPtr);

                    if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                    {
                        var coreIdsInGroup = new List<int>();
                        for (int i = 0; i < info.Processor.GroupCount; i++)
                        {
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