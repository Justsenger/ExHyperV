using System;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

/// <summary>进程内执行原生 CPUID，读虚拟化相关位。仅 x64（ARM 无 CPUID 指令）。</summary>
internal static class CpuId
{
    public static bool Supported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    // CPUID.1:ECX[31]：本 OS 是否运行在 hypervisor 之上（来宾恒 true）。
    public static bool HypervisorPresent() => (Leaf(1)[2] & (1u << 31)) != 0;

    // 硬件虚拟化是否暴露给本 OS：Intel VMX(CPUID.1:ECX[5]) 或 AMD SVM(CPUID.80000001h:ECX[2])。
    public static bool HardwareVirtualizationExposed()
    {
        bool vmx = (Leaf(1)[2] & (1u << 5)) != 0;
        var ext = Leaf(0x80000000);
        bool svm = ext[0] >= 0x80000001 && (Leaf(0x80000001)[2] & (1u << 2)) != 0;
        return vmx || svm;
    }

    // Hyper-V 根/父分区：厂商 "Microsoft Hv" 且 0x40000003:EBX[0]=CreatePartitions。
    // 该权限位只有根分区有，来宾没有；由 hypervisor 按分区类型下发，改 UEFI/VMCX、开嵌套都伪造不了。
    public static bool IsHyperVRootPartition()
    {
        var v = Leaf(0x40000000);
        if (v[1] != 0x7263694D || v[2] != 0x666F736F || v[3] != 0x76482074) return false;   // "Microsoft Hv"
        return (Leaf(0x40000003)[1] & 0x1u) != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint allocType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint freeType);

    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CpuidFn(uint leaf, uint subleaf, IntPtr regs);

    // x64: leaf=RCX, subleaf=RDX, regs=R8 → regs[0..3]=eax,ebx,ecx,edx（cpuid 破坏 rbx，故 push/pop）。
    private static readonly byte[] Thunk =
    {
        0x53,                         // push rbx
        0x89, 0xC8,                   // mov  eax, ecx
        0x89, 0xD1,                   // mov  ecx, edx
        0x0F, 0xA2,                   // cpuid
        0x41, 0x89, 0x00,             // mov  [r8],    eax
        0x41, 0x89, 0x58, 0x04,       // mov  [r8+4],  ebx
        0x41, 0x89, 0x48, 0x08,       // mov  [r8+8],  ecx
        0x41, 0x89, 0x50, 0x0C,       // mov  [r8+12], edx
        0x5B,                         // pop  rbx
        0xC3                          // ret
    };

    private static uint[] Leaf(uint leaf, uint subleaf = 0)
    {
        IntPtr code = VirtualAlloc(IntPtr.Zero, (UIntPtr)Thunk.Length, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (code == IntPtr.Zero) throw new InvalidOperationException("VirtualAlloc failed");
        IntPtr buf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(Thunk, 0, code, Thunk.Length);
            var fn = Marshal.GetDelegateForFunctionPointer<CpuidFn>(code);
            fn(leaf, subleaf, buf);
            return new[]
            {
                unchecked((uint)Marshal.ReadInt32(buf, 0)),
                unchecked((uint)Marshal.ReadInt32(buf, 4)),
                unchecked((uint)Marshal.ReadInt32(buf, 8)),
                unchecked((uint)Marshal.ReadInt32(buf, 12)),
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            VirtualFree(code, UIntPtr.Zero, MEM_RELEASE);
        }
    }
}
