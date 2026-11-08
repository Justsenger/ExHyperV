using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed record TestRunEnvironment(string WindowsVersion, string HostArchitecture, string ProcessArchitecture,
    string RuntimeVersion, string CoreVersion, string? CoreSha256, string TestVersion)
{
    internal static TestRunEnvironment Capture()
    {
        var core = typeof(VmQueryService).Assembly;
        string Version(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "未知";
        string? hash = null;
        if (!string.IsNullOrEmpty(core.Location))
        {
            using var stream = File.OpenRead(core.Location);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        return new(Environment.OSVersion.VersionString, RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
            Version(core), hash, Version(typeof(TestRunEnvironment).Assembly));
    }
}
