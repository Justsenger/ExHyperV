using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmPowerService
{
    internal sealed record WorkerIdentity(int ProcessId, long CreationTime,
        Guid OwnerVmId, string OwnerDomain, string Name);

    internal static bool MatchesWorkerIdentity(Guid vmId, int processId, long creationTime, WorkerIdentity worker)
        => vmId != Guid.Empty && processId > 0 && creationTime > 0
        && worker.ProcessId == processId && worker.OwnerVmId == vmId
        && worker.Name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase)
        && worker.OwnerDomain.Equals("NT VIRTUAL MACHINE", StringComparison.OrdinalIgnoreCase)
        // WMI creation timestamps retain microseconds; a process handle retains 100 ns.
        && worker.CreationTime > 0 && worker.CreationTime / 10 == creationTime / 10;

    internal static Task<ApiResponse<WorkerIdentity>> ReadWorkerIdentityAsync(int processId)
        => WmiApi.QueryFirstAsync(
            $"SELECT Handle, ProcessId, Name, CreationDate FROM Win32_Process WHERE ProcessId = {processId}",
            process =>
            {
                using var owner = process.InvokeMethod("GetOwner", null, null);
                if (owner is null || owner["ReturnValue"] is null || Convert.ToUInt32(owner["ReturnValue"]) != 0)
                    throw new InvalidOperationException("无法读取虚拟机工作进程的所有者。");
                string creation = process["CreationDate"]?.ToString()
                    ?? throw new InvalidOperationException("无法读取虚拟机工作进程的创建时间。");
                return new WorkerIdentity(Convert.ToInt32(process["ProcessId"]),
                    ManagementDateTimeConverter.ToDateTime(creation).ToUniversalTime().ToFileTimeUtc(),
                    Guid.TryParse(owner["User"]?.ToString(), out var vmId) ? vmId : Guid.Empty,
                    owner["Domain"]?.ToString() ?? string.Empty, process["Name"]?.ToString() ?? string.Empty);
            }, WmiScope.CimV2);
}
