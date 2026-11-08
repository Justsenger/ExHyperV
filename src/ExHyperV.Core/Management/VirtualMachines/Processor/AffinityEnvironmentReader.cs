using ExHyperV.Tools;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ExHyperV.Services;

internal static class AffinityEnvironmentReader
{
    internal static Task<ApiResponse<AffinityEnvironment>> ReadRootAsync() => Task.Run(() =>
    {
        try
        {
            var host = ReadHostFingerprint();
            string topology = WindowsAffinityTopology.ReadFingerprint();
            return ApiResponse<AffinityEnvironment>.Ok(new(host, AffinityPreference.RootBackend, topology));
        }
        catch (Exception error) { return Failure(error); }
    });

    internal static async Task<ApiResponse<AffinityEnvironment>> ReadHcsAsync()
    {
        try
        {
            string host = await Task.Run(ReadHostFingerprint).ConfigureAwait(false);
            var topology = await HcsApi.GetProcessorTopologyAsync().ConfigureAwait(false);
            if (!topology.HasData)
                return ApiResponse<AffinityEnvironment>.Fail(topology.Error, topology.Code, topology.ErrorSource,
                    topology.Exception, topology.Success ? ApiErrorKind.InvalidState : topology.ErrorKind);
            return ApiResponse<AffinityEnvironment>.Ok(new(host, AffinityPreference.HcsBackend, HcsFingerprint(topology.Data)));
        }
        catch (Exception error) { return Failure(error); }
    }

    internal static string HcsFingerprint(IReadOnlyList<HcsLogicalProcessor> processors)
    {
        if (processors.Count == 0 || processors.Any(p => p is null) || processors.Select(p => p.LpIndex).Distinct().Count() != processors.Count)
            throw new InvalidOperationException("HCS 处理器拓扑为空或编号重复。");
        return Hash("hcs-topology-v1|" + RuntimeInformation.OSArchitecture + "|" + string.Join(";", processors.OrderBy(p => p.LpIndex)
            .Select(p => FormattableString.Invariant($"{p.LpIndex}:{p.PackageId}:{p.CoreId}:{p.NodeNumber}:{p.RootVpIndex}"))));
    }

    private static string ReadHostFingerprint()
    {
        using var query = new ManagementObjectSearcher("root\\cimv2", "SELECT UUID FROM Win32_ComputerSystemProduct");
        using var rows = query.Get();
        var identifiers = new List<Guid>();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                if (!Guid.TryParse(row["UUID"]?.ToString(), out var id) || id == Guid.Empty || id == Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))
                    throw new InvalidOperationException("宿主没有提供有效的 SMBIOS UUID。");
                identifiers.Add(id);
            }
        }
        if (identifiers.Count != 1) throw new InvalidOperationException("无法唯一确认宿主 SMBIOS UUID。");
        return Hash("smbios-host-v1|" + identifiers[0].ToString("D"));
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static ApiResponse<AffinityEnvironment> Failure(Exception error)
        => ApiResponse<AffinityEnvironment>.Fail(error.Message, error.HResult, exception: error, kind: ApiErrorKind.OperationFailed);
}
