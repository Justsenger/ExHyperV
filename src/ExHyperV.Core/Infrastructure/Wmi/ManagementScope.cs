using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace System.Management;

internal enum ImpersonationLevel { Default, Anonymous, Identify, Impersonate, Delegate }
internal enum AuthenticationLevel { Unchanged = -1, Default, None, Connect, Call, Packet, PacketIntegrity, PacketPrivacy }
internal class ManagementOptions { internal TimeSpan Timeout { get; set; } = TimeSpan.MaxValue; }
internal sealed class ConnectionOptions : ManagementOptions
{
    internal string? Username { get; set; }
    internal string? Password { get; set; }
    internal AuthenticationLevel Authentication { get; set; } = AuthenticationLevel.Unchanged;
    internal ImpersonationLevel Impersonation { get; set; } = ImpersonationLevel.Impersonate;
    internal bool EnablePrivileges { get; set; }
}
internal sealed class EnumerationOptions : ManagementOptions
{
    internal bool ReturnImmediately { get; set; } = true;
    internal bool Rewindable { get; set; } = true;
}
internal sealed class ObjectGetOptions : ManagementOptions;
internal sealed class InvokeMethodOptions : ManagementOptions;
internal sealed class ObjectQuery(string text) { internal string QueryString { get; } = text; }

internal sealed unsafe class ManagementScope(string path, ConnectionOptions? options = null)
{
    private readonly ConnectionOptions connection = options ?? new();
    private readonly object sync = new();
    private WbemHandle? services;
    private WbemHandle Services
    {
        get
        {
            lock (sync) return services ??= WbemNative.InApartment(() => ConnectServices(path, connection));
        }
    }
    internal ManagementPath Path { get; } = new(path);
    internal bool IsConnected => Volatile.Read(ref services) != null;
    internal void Connect() => _ = Services;

    private static WbemHandle ConnectServices(string ns, ConnectionOptions config)
    {
        using var locator = WbemNative.Create(new("4590f811-1d3a-11d0-891f-00aa004b2e24"), new("dc12a687-737f-11cf-884d-00aa004b2e24"));
        return locator.Use(p =>
        {
            using var privileges = config.EnablePrivileges ? WbemPrivileges.Enable() : null;
            using var scope = new WbemBstr(ns.Replace('/', '\\'));
            using var user = new WbemBstr(config.Username);
            using var password = new WbemBstr(config.Password);
            nint svc = 0;
            var connect = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int, nint, nint, nint*, int>)WbemNative.Slot(p, 3);
            WbemNative.Check(connect(p, scope.Pointer, user.Pointer, password.Pointer, 0, 0x80, 0, 0, &svc));
            return Secure(svc, config);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthIdentity { internal char* User; internal uint UserLength; internal char* Domain; internal uint DomainLength; internal char* Password; internal uint PasswordLength; internal uint Flags; }

    private static WbemHandle Secure(nint owned, ConnectionOptions config)
    {
        var result = new WbemHandle(owned);
        try
        {
            uint level = (uint)config.Authentication;
            if (config.Authentication == AuthenticationLevel.Unchanged)
                WbemNative.Check(CoQueryProxyBlanket(owned, null, null, null, &level, null, null, null));
            string? user = config.Username, domain = null;
            int split = user?.IndexOf('\\') ?? -1;
            if (split >= 0) { domain = user![..split]; user = user[(split + 1)..]; }
            fixed (char* u = user, d = domain, pwd = config.Password)
            {
                AuthIdentity auth = new() { User = u, UserLength = (uint)(user?.Length ?? 0), Domain = d, DomainLength = (uint)(domain?.Length ?? 0),
                    Password = pwd, PasswordLength = (uint)(config.Password?.Length ?? 0), Flags = 2 };
                WbemNative.Check(CoSetProxyBlanket(owned, uint.MaxValue, uint.MaxValue, -1, level,
                    (uint)config.Impersonation, user is null ? null : &auth, user is null ? 0x40u : 0));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private T Call<T>(Func<nint, T> action) => Services.Use(p =>
    {
        using var privileges = connection.EnablePrivileges ? WbemPrivileges.Enable() : null;
        return action(p);
    });

    internal WbemHandle SecureEnumerator(nint owned) => Secure(owned, connection);

    internal WbemHandle GetObject(string path) => Call(p =>
    {
        using var bstr = new WbemBstr(path);
        nint obj = 0;
        WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint, nint*, nint, int>)WbemNative.Slot(p, 6))(p, bstr.Pointer, 0, 0, &obj, 0));
        return new WbemHandle(obj);
    });

    internal WbemHandle Query(string query, EnumerationOptions config) => Call(p =>
    {
        using var language = new WbemBstr("WQL");
        using var statement = new WbemBstr(query);
        nint rows = 0;
        var exec = (delegate* unmanaged[Stdcall]<nint, nint, nint, int, nint, nint*, int>)WbemNative.Slot(p, 20);
        int flags = (config.ReturnImmediately ? 0x10 : 0) | (config.Rewindable ? 0 : 0x20);
        WbemNative.Check(exec(p, language.Pointer, statement.Pointer, flags, 0, &rows));
        return Secure(rows, connection);
    });

    internal WbemHandle? Invoke(string path, string name, WbemHandle? input) => Call(p =>
    {
        WbemHandle? Execute(nint parameters)
        {
            using var target = new WbemBstr(path);
            using var method = new WbemBstr(name);
            nint output = 0;
            var exec = (delegate* unmanaged[Stdcall]<nint, nint, nint, int, nint, nint, nint*, nint, int>)WbemNative.Slot(p, 24);
            WbemNative.Check(exec(p, target.Pointer, method.Pointer, 0, 0, parameters, &output, 0));
            return output == 0 ? null : new WbemHandle(output);
        }
        return input == null ? Execute(0) : input.Use(Execute);
    });

    [DllImport("ole32", ExactSpelling = true)] private static extern int CoSetProxyBlanket(nint proxy, uint authentication, uint authorization, nint principal, uint level, uint impersonation, AuthIdentity* identity, uint capabilities);
    [DllImport("ole32", ExactSpelling = true)] private static extern int CoQueryProxyBlanket(nint proxy, uint* authentication, uint* authorization, nint* principal, uint* level, uint* impersonation, nint* identity, uint* capabilities);
}

// The BCD provider requests all privileges already granted to the caller.
// Adjust an impersonation token for this RPC call only and restore it on exit.
internal sealed unsafe class WbemPrivileges : IDisposable
{
    private readonly SafeAccessTokenHandle original;
    private WbemPrivileges(SafeAccessTokenHandle original) => this.original = original;
    internal static WbemPrivileges Enable()
    {
        const uint query = 8, duplicate = 2, impersonate = 4, adjust = 0x20;
        bool hasThread = OpenThreadToken(GetCurrentThread(), query | duplicate | impersonate, true, out var original);
        SafeAccessTokenHandle? process = null;
        try
        {
            if (!hasThread && (Marshal.GetLastPInvokeError() != 1008 || !OpenProcessToken(GetCurrentProcess(), query | duplicate, out process)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            if (!DuplicateTokenEx(hasThread ? original : process!, query | impersonate | adjust, 0, 2, 2, out var token))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            using (token)
            {
                GetTokenInformation(token, 3, null, 0, out uint count);
                if (count < 4) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                var data = new byte[count];
                fixed (byte* list = data)
                {
                    if (!GetTokenInformation(token, 3, list, count, out _))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                    for (uint i = 0; i < *(uint*)list; i++) *(uint*)(list + 4 + i * 12 + 8) |= 2;
                    if (!AdjustTokenPrivileges(token, false, list, 0, 0, 0) || !SetThreadToken(0, token))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            return new(original);
        }
        catch { original.Dispose(); throw; }
        finally { process?.Dispose(); }
    }
    public void Dispose()
    {
        try
        {
            if (!SetThreadToken(0, original)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { original.Dispose(); }
    }
    [DllImport("kernel32")] private static extern nint GetCurrentThread();
    [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32", SetLastError = true)] private static extern bool OpenThreadToken(nint thread, uint access, bool self, out SafeAccessTokenHandle token);
    [DllImport("advapi32", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle source, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle result);
    [DllImport("advapi32", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, byte* buffer, uint length, out uint needed);
    [DllImport("advapi32", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disable, byte* changes, int size, nint old, nint written);
    [DllImport("advapi32", SetLastError = true)] private static extern bool SetThreadToken(nint thread, SafeAccessTokenHandle token);
}
