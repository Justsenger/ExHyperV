using ExHyperV.Tools;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Management;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ExHyperV.Services;

internal static class WindowsRootAffinityPlatform
{
    private const int InsufficientBuffer = 122;
    private const uint StillActive = 259;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint SetInformation = 0x0200;
    private const uint TokenQuery = 0x0008;
    private const uint RelationGroup = 4;

    internal static ApiResponse<RootProcessorTopology> GetTopology()
    {
        if (IntPtr.Size != 8)
            return ApiResponse<RootProcessorTopology>.Fail("Root 亲和性需要 64 位进程，避免 WOW64 处理器掩码折叠。", kind: ApiErrorKind.Unsupported);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint length = 0;
                bool queried = Native.GetLogicalProcessorInformationEx(RelationGroup, IntPtr.Zero, ref length);
                int error = Marshal.GetLastWin32Error();
                if (!queried && error != InsufficientBuffer)
                    return Fail<RootProcessorTopology>(Win32Failure("读取处理器组拓扑失败", error));
                if (length is < 32 or > 1048576)
                    return ApiResponse<RootProcessorTopology>.Fail("系统返回的处理器拓扑长度无效。", kind: ApiErrorKind.InvalidState);
                IntPtr buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    uint allocated = length;
                    if (!Native.GetLogicalProcessorInformationEx(RelationGroup, buffer, ref length))
                    {
                        error = Marshal.GetLastWin32Error();
                        if (error == InsufficientBuffer) continue;
                        return Fail<RootProcessorTopology>(Win32Failure("读取处理器组拓扑失败", error));
                    }
                    if (length > allocated)
                        return ApiResponse<RootProcessorTopology>.Fail("系统返回的处理器拓扑超出缓冲区。", kind: ApiErrorKind.InvalidState);
                    byte[] bytes = new byte[length];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    return ParseTopology(bytes, IntPtr.Size);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return ApiResponse<RootProcessorTopology>.Fail("处理器拓扑持续变化，请重新读取。", kind: ApiErrorKind.Conflict);
        }
        catch (Exception error) { return Fail<RootProcessorTopology>(ExceptionFailure(error)); }
    }

    internal static ApiResponse<RootProcessorTopology> ParseTopology(ReadOnlySpan<byte> bytes, int pointerSize)
    {
        if (pointerSize != 8)
            return ApiResponse<RootProcessorTopology>.Fail("Root 亲和性需要 64 位处理器掩码。", kind: ApiErrorKind.Unsupported);
        // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX 的头为 8 字节；GROUP_RELATIONSHIP
        // 的 GroupInfo 位于其后 24 字节，每个 PROCESSOR_GROUP_INFO 的掩码位于偏移 40。
        if (bytes.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != RelationGroup
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length)
            return ApiResponse<RootProcessorTopology>.Fail("处理器组拓扑头或长度无效。", kind: ApiErrorKind.InvalidState);
        ushort maximumGroups = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        ushort activeGroups = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        if (activeGroups == 0 || maximumGroups < activeGroups || bytes.Length != 32 + activeGroups * 48)
            return ApiResponse<RootProcessorTopology>.Fail("处理器组数量与拓扑记录不一致。", kind: ApiErrorKind.InvalidState);
        if (activeGroups != 1)
            return ApiResponse<RootProcessorTopology>.Fail("Root 亲和性目前仅支持宿主实际只有一个 Windows 处理器组。", kind: ApiErrorKind.Unsupported);
        byte maximumProcessors = bytes[32];
        byte activeProcessors = bytes[33];
        ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(bytes[72..]);
        if (maximumProcessors is 0 or > 64 || activeProcessors == 0 || activeProcessors > maximumProcessors
            || BitOperations.PopCount(mask) != activeProcessors)
            return ApiResponse<RootProcessorTopology>.Fail("处理器数量与活动掩码不一致。", kind: ApiErrorKind.InvalidState);
        return ApiResponse<RootProcessorTopology>.Ok(new(0, mask,
            Array.AsReadOnly(Enumerable.Range(0, 64).Where(index => (mask & (1UL << index)) != 0).ToArray())));
    }

    internal static ApiResponse<IRootAffinityProcess> OpenProcess(Guid vmId, bool write)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<IRootAffinityProcess>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        SafeProcessHandle? handle = null;
        try
        {
            var found = FindProcess(vmId);
            if (!found.HasData) return Fail<IRootAffinityProcess>(AsFailure(found));
            handle = Native.OpenProcess(QueryLimitedInformation | (write ? SetInformation : 0), false, found.Data.ProcessId);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                return Fail<IRootAffinityProcess>(Win32Failure("打开虚拟机进程失败", error));
            }
            var actual = ReadIdentity(handle);
            if (!actual.HasData) return Fail<IRootAffinityProcess>(AsFailure(actual));
            if (!Matches(found.Data, actual.Data))
                return ApiResponse<IRootAffinityProcess>.Fail("虚拟机进程身份已改变，未使用该 PID。", kind: ApiErrorKind.Conflict);
            var process = new WindowsProcess(vmId, found.Data, actual.Data.CreationTime, handle);
            handle = null; // 所有权移交；此后所有查询和设置均使用此句柄。
            return ApiResponse<IRootAffinityProcess>.Ok(process);
        }
        catch (Exception error) { return Fail<IRootAffinityProcess>(ExceptionFailure(error)); }
        finally { handle?.Dispose(); }
    }

    private sealed record Candidate(int ProcessId, long CreationTime, string OwnerSid);
    private sealed record ProcessIdentity(int ProcessId, long CreationTime, string OwnerSid, bool Exited);

    private static ApiResponse<Candidate> FindProcess(Guid vmId, int? processId = null)
    {
        try
        {
            // Win32_Process uses Handle as its key. Keep it in the projection so WMI
            // supplies the instance path required by GetOwner and GetOwnerSid below.
            string query = "SELECT Handle, ProcessId, Name, CreationDate FROM Win32_Process WHERE Name LIKE 'vmmem%'";
            if (processId is int pid) query += $" AND ProcessId = {pid}";
            using var searcher = new ManagementObjectSearcher(query);
            using var objects = searcher.Get();
            var candidates = new List<Candidate>();
            ApiResponse? unresolved = null;
            foreach (ManagementObject process in objects)
            {
                using (process)
                {
                    using var owner = process.InvokeMethod("GetOwner", null, null);
                    if (owner is null || Convert.ToUInt32(owner["ReturnValue"]) != 0)
                    {
                        unresolved = WmiFailure("无法核对 vmmem 所有者", owner?["ReturnValue"]);
                        continue;
                    }
                    if (!Guid.TryParse(owner["User"]?.ToString(), out var ownerVm) || ownerVm != vmId) continue;
                    using var ownerSid = process.InvokeMethod("GetOwnerSid", null, null);
                    if (ownerSid is null || Convert.ToUInt32(ownerSid["ReturnValue"]) != 0)
                        return Fail<Candidate>(WmiFailure("无法读取虚拟机进程所有者 SID", ownerSid?["ReturnValue"]));
                    string? sid = ownerSid["Sid"]?.ToString();
                    string? creation = process["CreationDate"]?.ToString();
                    if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-5-83-", StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(creation)
                        || process["Name"]?.ToString()?.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase) != true)
                        return ApiResponse<Candidate>.Fail("虚拟机进程缺少可信的所有者或创建时间。", kind: ApiErrorKind.Conflict);
                    long creationTime = ManagementDateTimeConverter.ToDateTime(creation).ToUniversalTime().ToFileTimeUtc();
                    int id = Convert.ToInt32(process["ProcessId"]);
                    if (id <= 0 || creationTime <= 0)
                        return ApiResponse<Candidate>.Fail("虚拟机进程身份无效。", kind: ApiErrorKind.InvalidState);
                    candidates.Add(new(id, creationTime, sid));
                }
            }
            if (candidates.Count > 1)
                return ApiResponse<Candidate>.Fail("同一虚拟机匹配了多个 vmmem 进程。", kind: ApiErrorKind.AmbiguousTarget);
            if (candidates.Count == 1) return ApiResponse<Candidate>.Ok(candidates[0]);
            return unresolved is not null ? Fail<Candidate>(unresolved)
                : ApiResponse<Candidate>.Fail("虚拟机的 vmmem 进程尚不存在或已经退出。", kind: ApiErrorKind.NotFound);
        }
        catch (Exception error) { return Fail<Candidate>(ExceptionFailure(error)); }
    }

    // WMI 的 DMTF 时间只保留微秒，FILETIME 保留 100 ns；仅容许一个微秒内的精度差。
    internal static bool CreationTimesMatch(long wmiTime, long nativeTime)
        => wmiTime > 0 && nativeTime > 0
        && (wmiTime >= nativeTime ? wmiTime - nativeTime : nativeTime - wmiTime) < 10;

    private static bool Matches(Candidate expected, ProcessIdentity actual)
        => !actual.Exited && expected.ProcessId == actual.ProcessId
        && CreationTimesMatch(expected.CreationTime, actual.CreationTime)
        && string.Equals(expected.OwnerSid, actual.OwnerSid, StringComparison.Ordinal);

    private static ApiResponse<ProcessIdentity> ReadIdentity(SafeProcessHandle handle)
    {
        uint pid = Native.GetProcessId(handle);
        if (pid == 0) return Fail<ProcessIdentity>(Win32Failure("读取进程身份失败", Marshal.GetLastWin32Error()));
        if (!Native.GetProcessTimes(handle, out long creation, out _, out _, out _))
            return Fail<ProcessIdentity>(Win32Failure("读取进程创建时间失败", Marshal.GetLastWin32Error()));
        if (!Native.GetExitCodeProcess(handle, out uint exitCode))
            return Fail<ProcessIdentity>(Win32Failure("读取进程运行状态失败", Marshal.GetLastWin32Error()));
        if (!Native.OpenProcessToken(handle, TokenQuery, out var token))
        {
            int error = Marshal.GetLastWin32Error();
            token?.Dispose();
            return Fail<ProcessIdentity>(Win32Failure("读取进程令牌失败", error));
        }
        using (token)
        {
            Native.GetTokenInformation(token, 1, IntPtr.Zero, 0, out uint length);
            int error = Marshal.GetLastWin32Error();
            if (error != InsufficientBuffer)
                return Fail<ProcessIdentity>(Win32Failure("读取进程所有者失败", error));
            if (length < IntPtr.Size + 4 || length > 1048576)
                return ApiResponse<ProcessIdentity>.Fail("进程令牌数据长度无效。", kind: ApiErrorKind.InvalidState);
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!Native.GetTokenInformation(token, 1, buffer, length, out _))
                    return Fail<ProcessIdentity>(Win32Failure("读取进程所有者失败", Marshal.GetLastWin32Error()));
                string sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                return ApiResponse<ProcessIdentity>.Ok(new(checked((int)pid), creation, sid, exitCode != StillActive));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private sealed class WindowsProcess(Guid vmId, Candidate candidate, long creationTime,
        SafeProcessHandle handle) : IRootAffinityProcess
    {
        public int ProcessId => candidate.ProcessId;
        public long CreationTime => creationTime;

        public ApiResponse VerifyIdentity()
        {
            try
            {
                var actual = ReadIdentity(handle);
                if (!actual.HasData) return AsFailure(actual);
                if (!Matches(candidate, actual.Data) || actual.Data.CreationTime != CreationTime)
                    return ApiResponse.Fail("原虚拟机进程已退出或身份发生变化。", kind: ApiErrorKind.Conflict);
                var current = FindProcess(vmId, ProcessId);
                if (!current.HasData)
                    return current.ErrorKind == ApiErrorKind.NotFound
                        ? ApiResponse.Fail("原虚拟机进程已不再属于当前虚拟机。", kind: ApiErrorKind.Conflict)
                        : AsFailure(current);
                return Matches(current.Data, actual.Data) ? ApiResponse.Ok()
                    : ApiResponse.Fail("PID 已被其他进程复用，未继续操作。", kind: ApiErrorKind.Conflict);
            }
            catch (Exception error) { return ExceptionFailure(error); }
        }

        public ApiResponse<RootProcessAffinity> Read()
        {
            try
            {
                if (!Native.GetProcessTimes(handle, out long creation, out _, out _, out _))
                    return Fail<RootProcessAffinity>(Win32Failure("读取进程状态失败", Marshal.GetLastWin32Error()));
                if (!Native.GetExitCodeProcess(handle, out uint exitCode))
                    return Fail<RootProcessAffinity>(Win32Failure("读取进程运行状态失败", Marshal.GetLastWin32Error()));
                if (creation != CreationTime || exitCode != StillActive)
                    return ApiResponse<RootProcessAffinity>.Fail("原虚拟机进程已退出或身份改变。", kind: ApiErrorKind.Conflict);
                if (!Native.GetProcessAffinityMask(handle, out nuint mask, out nuint allowed))
                    return Fail<RootProcessAffinity>(Win32Failure("读取进程亲和性失败", Marshal.GetLastWin32Error()));
                return ApiResponse<RootProcessAffinity>.Ok(new(ProcessId, CreationTime, mask, allowed));
            }
            catch (Exception error) { return Fail<RootProcessAffinity>(ExceptionFailure(error)); }
        }

        public ApiResponse Set(ulong mask)
        {
            try
            {
                return Native.SetProcessAffinityMask(handle, checked((nuint)mask)) ? ApiResponse.Ok()
                    : Win32Failure("设置进程亲和性失败", Marshal.GetLastWin32Error());
            }
            catch (Exception error)
            {
                return ApiResponse.Fail(error.Message, error.HResult, ApiErrorSource.None, error, ApiErrorKind.OutcomeUnknown);
            }
        }

        public void Dispose() => handle.Dispose();
    }

    private static ApiResponse WmiFailure(string message, object? value)
    {
        int code = value is null ? -1 : Convert.ToInt32(value);
        return ApiResponse.Fail(message, code, ApiErrorSource.Wmi,
            kind: code is 2 or 3 ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
    }

    private static ApiResponse Win32Failure(string message, int code)
        => ApiResponse.Fail(message + "：" + new Win32Exception(code).Message, code, ApiErrorSource.Win32,
            new Win32Exception(code), code == 5 ? ApiErrorKind.AccessDenied
                : code == 87 ? ApiErrorKind.InvalidArgument : ApiErrorKind.OperationFailed);

    private static ApiResponse ExceptionFailure(Exception error)
        => ApiResponse.Fail(error.Message, error.HResult,
            error is ManagementException ? ApiErrorSource.Wmi : ApiErrorSource.None, error,
            error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException ? ApiErrorKind.Unsupported
                : error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);

    private static ApiResponse AsFailure<T>(ApiResponse<T> response)
        => ApiResponse.Fail(response.Success ? "查询未返回所需数据。" : response.Error,
            response.Code, response.ErrorSource, response.Exception,
            response.Success ? ApiErrorKind.InvalidState : response.ErrorKind);

    private static ApiResponse<T> Fail<T>(ApiResponse response)
        => ApiResponse<T>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLogicalProcessorInformationEx(uint relationship, IntPtr buffer, ref uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetProcessId(SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
            out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessAffinityMask(SafeProcessHandle process, out nuint mask, out nuint systemMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessAffinityMask(SafeProcessHandle process, nuint mask);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
            IntPtr information, uint length, out uint returnedLength);
    }
}
