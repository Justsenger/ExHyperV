using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace ExHyperV.ConsoleIpc;

internal sealed record ConsoleIpcEndpoint(string Pipe, SecurityIdentifier Sid, string Thumbprint, bool ProtectedPath);

internal class ConsoleIpcDiscovery
{
    private const string Subkey = @"SOFTWARE\ExHyperV\ConsoleExtensions\External";
    private const string Prefix = @"\\.\pipe\";

    internal static ConsoleIpcEndpoint Validate(string? pipe, string? service, string? sid, string? thumbprint, bool protectedPath)
    {
        if (pipe?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true) pipe = pipe[Prefix.Length..];
        if (pipe is not { Length: >= 1 and <= 240 } || pipe.Contains("..", StringComparison.Ordinal)
            || !pipe.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            || pipe.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
            || thumbprint is not { Length: 64 } || !thumbprint.All(char.IsAsciiHexDigit))
            throw Invalid();
        try
        {
            SecurityIdentifier? identity = null;
            if (sid != null)
            {
                if (!sid.StartsWith("S-1-5-", StringComparison.OrdinalIgnoreCase)) throw Invalid();
                identity = new SecurityIdentifier(sid);
            }
            if (service != null)
            {
                const string domain = @"NT SERVICE\";
                string name = service.StartsWith(domain, StringComparison.OrdinalIgnoreCase) ? service[domain.Length..] : service;
                if (name is not { Length: >= 1 and <= 256 } || name.Contains('\\') || name.Contains('/') || name.Any(char.IsControl))
                    throw Invalid();
                var resolved = (SecurityIdentifier)new NTAccount("NT SERVICE", name).Translate(typeof(SecurityIdentifier));
                if (identity != null && !identity.Equals(resolved)) throw Invalid();
                identity = resolved;
            }
            return new(Prefix + pipe, identity ?? throw Invalid(), thumbprint, protectedPath);
        }
        catch (Exception) { throw Invalid(); }
    }

    internal virtual ConsoleIpcEndpoint? Find()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(Subkey, writable: false);
            if (key == null) return null;
            var names = key.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? Read(string name) => !names.Contains(name) ? null : key.GetValueKind(name) == RegistryValueKind.String
                ? (string?)key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : throw Invalid();
            const string policy = "AllowProtectedSystemPathOnly";
            bool protect = !names.Contains(policy) || (key.GetValueKind(policy) == RegistryValueKind.DWord
                ? key.GetValue(policy) switch { 1 => true, 0 => false, _ => throw Invalid() } : throw Invalid());
            return Validate(Read("PipeName"), Read("ServiceName"), Read("ServiceSid"), Read("SignerThumbprint"), protect);
        }
        catch (Exception) { throw Invalid(); }
    }

    private static UnauthorizedAccessException Invalid() => new("EXT-TRUST-01");

    internal virtual void Verify(SafePipeHandle pipe, ConsoleIpcEndpoint endpoint, long openedAfter) =>
        ConsoleIpcTrust.Verify(pipe, endpoint, openedAfter);

    internal virtual Stream Open()
    {
        var endpoint = Find() ?? throw new IOException("EXT-001");
        long deadline = Environment.TickCount64 + 300;
        while (true)
        {
            try { return Open(endpoint); }
            catch (PipeBusyException) when (Environment.TickCount64 < deadline) { Thread.Sleep(20); }
        }
    }

    private sealed class PipeBusyException() : IOException("EXT-001");

    private Stream Open(ConsoleIpcEndpoint endpoint)
    {
        long before = DateTime.UtcNow.ToFileTimeUtc();
        // FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE.
        var pipe = CreateFileW(endpoint.Pipe, 0x100083, 0, 0, 3, 0x40110000, 0);
        try
        {
            if (pipe.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 231) throw new PipeBusyException();
                if (error == 5) throw new UnauthorizedAccessException("EXT-TRUST-02");
                if (error is 87 or 123 or 206) throw Invalid();
                throw new IOException("EXT-001");
            }
            Verify(pipe, endpoint, before);
            return new NamedPipeClientStream(PipeDirection.InOut, true, pipe);
        }
        catch { pipe.Dispose(); throw; }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
