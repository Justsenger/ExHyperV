using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ExHyperV.Services;

/// <summary>宿主电源计划的显示名称与稳定 GUID。</summary>
/// <param name="Name">计划显示名称；读取名称失败时可为 GUID 文本。</param>
/// <param name="Id">电源计划 GUID。</param>
public sealed record HostPowerPlan(string Name, Guid Id);

/// <summary>枚举宿主电源计划并设置当前活动计划；设置影响整个宿主。</summary>
public static class HostPowerPlanService
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint AccessScheme = 16;

    /// <summary>枚举系统电源计划及可读取的名称。</summary>
    /// <returns>已枚举到的计划列表；枚举结束或遇到接口错误时停止，可能返回部分列表。</returns>
    public static IReadOnlyList<HostPowerPlan> GetPowerPlans()
    {
        var plans = new List<HostPowerPlan>();

        for (uint index = 0; ; index++)
        {
            uint size = (uint)Marshal.SizeOf<Guid>();
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint result = PowerEnumerate(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    AccessScheme,
                    index,
                    buffer,
                    ref size);

                if (result != ErrorSuccess)
                    break;

                Guid id = Marshal.PtrToStructure<Guid>(buffer);
                plans.Add(new HostPowerPlan(ReadFriendlyName(id), id));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return plans;
    }

    /// <summary>读取宿主当前活动电源计划的 GUID。</summary>
    /// <returns>活动计划的 GUID；未能取得时为 null。</returns>
    public static Guid? GetActivePowerPlanId()
    {
        uint result = PowerGetActiveScheme(IntPtr.Zero, out IntPtr activeScheme);
        if (result != ErrorSuccess || activeScheme == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStructure<Guid>(activeScheme);
        }
        finally
        {
            LocalFree(activeScheme);
        }
    }

    /// <summary>激活指定宿主电源计划。</summary>
    /// <remarks>作用于宿主全局；原生接口失败时抛出 Win32Exception。</remarks>
    /// <param name="id">已安装电源计划的 GUID。</param>
    /// <exception cref="Win32Exception">系统拒绝激活指定计划，错误码保留在 NativeErrorCode 中。</exception>
    public static void SetActivePowerPlan(Guid id)
    {
        uint result = PowerSetActiveScheme(IntPtr.Zero, ref id);
        if (result != ErrorSuccess)
            throw new Win32Exception((int)result);
    }

    private static string ReadFriendlyName(Guid id)
    {
        uint size = 0;
        uint result = PowerReadFriendlyName(
            IntPtr.Zero,
            ref id,
            IntPtr.Zero,
            IntPtr.Zero,
            null,
            ref size);

        if ((result != ErrorSuccess && result != ErrorMoreData) || size == 0)
            return id.ToString();

        byte[] buffer = new byte[size];
        result = PowerReadFriendlyName(
            IntPtr.Zero,
            ref id,
            IntPtr.Zero,
            IntPtr.Zero,
            buffer,
            ref size);

        if (result != ErrorSuccess)
            return id.ToString();

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
