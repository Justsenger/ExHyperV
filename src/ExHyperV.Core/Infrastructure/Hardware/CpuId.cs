using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

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

    private static uint[] Leaf(uint leaf, uint subleaf = 0)
    {
        if (!Supported) throw new PlatformNotSupportedException("CPUID 只允许在 x64 进程中调用。");
        var value = X86Base.CpuId(unchecked((int)leaf), unchecked((int)subleaf));
        return [unchecked((uint)value.Eax), unchecked((uint)value.Ebx), unchecked((uint)value.Ecx), unchecked((uint)value.Edx)];
    }
}