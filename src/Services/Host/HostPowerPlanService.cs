using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ExHyperV.Services;

public sealed record HostPowerPlan(string Name, Guid Id);

public static class HostPowerPlanService
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint AccessScheme = 16;

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
