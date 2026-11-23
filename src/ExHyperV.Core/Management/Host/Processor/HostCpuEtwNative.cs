using ExHyperV.Tools;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ExHyperV.Models;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

internal sealed record LogicalProcessorTopologyItem(
    int LogicalProcessorIndex,
    int ProcessorGroup,
    int GroupProcessorIndex,
    CpuCoreType CoreType);

internal sealed record ProcessSnapshotItem(
    int ProcessId,
    int ParentProcessId,
    string ImageName);

internal sealed record ProcessThreadSnapshot(
    IReadOnlyDictionary<int, ProcessSnapshotItem> Processes,
    IReadOnlyDictionary<int, int> ThreadToProcess);

internal sealed record EtwHarvestWindow(
    long StartQpc,
    long EndQpc,
    long QpcFrequency,
    uint EventsLost,
    uint RealTimeBuffersLost,
    IReadOnlyDictionary<int, long> IdleTicksByLp,
    IReadOnlyDictionary<(int Lp, uint Pid), long> PidTicksByLp,
    IReadOnlyDictionary<(int Lp, uint Tid), long> UnresolvedTidTicksByLp,
    IReadOnlyDictionary<uint, uint> ObservedThreadToPid);

internal interface IKernelThreadTrace : IDisposable
{
    bool ProcessTopologyDirty { get; }
    void ClearProcessTopologyDirty();
    void SeedThreads(IReadOnlyDictionary<int, int> threadToPid);
    EtwHarvestWindow HarvestWindow();
}

internal static unsafe class HostCpuEtwNative
{
    private const int InsufficientBuffer = 122;
    private const uint RelationProcessorCore = 0;
    private const uint RelationGroup = 4;
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint Th32csSnapThread = 0x00000004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceSystemLoggerMode = 0x02000000;
    private const uint EventTraceFlagProcess = 0x00000001;
    private const uint EventTraceFlagThread = 0x00000002;
    private const uint EventTraceFlagCSwitch = 0x00000010;
    private const uint EventTraceControlStop = 1;
    private const uint EventTraceControlFlush = 3;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    internal const uint ProcessTraceModeRawTimestamp = 0x00001000;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const ushort EventHeaderFlagProcessorIndex = 0x0200;

    internal static readonly Guid ProcessProviderGuid = new("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
    internal static readonly Guid ThreadProviderGuid = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");

    private const ulong InvalidTraceHandle = ulong.MaxValue;

    internal static IReadOnlyList<LogicalProcessorTopologyItem> ReadProcessorTopology()
    {
        var groupBytes = QueryLogicalProcessorBuffer(RelationGroup);
        if (groupBytes is not { Length: >= 32 }) return FallbackTopology();

        ushort activeGroups = BinaryPrimitives.ReadUInt16LittleEndian(groupBytes.AsSpan(10));
        ushort maxGroups = BinaryPrimitives.ReadUInt16LittleEndian(groupBytes.AsSpan(8));
        if (activeGroups == 0 || maxGroups < activeGroups || groupBytes.Length < 32 + activeGroups * 48)
            return FallbackTopology();

        var coreTypes = ReadCoreTypesByGroupBit();
        var items = new List<LogicalProcessorTopologyItem>();
        int globalIndex = 0;

        for (int g = 0; g < activeGroups; g++)
        {
            int infoOffset = 32 + g * 48;
            ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(groupBytes.AsSpan(infoOffset + 40));
            for (int bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) == 0) continue;
                var type = coreTypes.TryGetValue((g, bit), out var parsedType) ? parsedType : CpuCoreType.Unknown;
                items.Add(new LogicalProcessorTopologyItem(globalIndex, g, bit, type));
                globalIndex++;
            }
        }

        return items.Count > 0 ? items : FallbackTopology();
    }

    private static IReadOnlyList<LogicalProcessorTopologyItem> FallbackTopology()
    {
        int count = Math.Max(1, Environment.ProcessorCount);
        var items = new LogicalProcessorTopologyItem[count];
        for (int i = 0; i < count; i++)
            items[i] = new LogicalProcessorTopologyItem(i, i / 64, i % 64, CpuCoreType.Unknown);
        return items;
    }

    internal static Dictionary<(int Group, int Bit), CpuCoreType> ReadCoreTypesByGroupBit()
    {
        var map = new Dictionary<(int Group, int Bit), CpuCoreType>();
        var coreBytes = QueryLogicalProcessorBuffer(RelationProcessorCore);
        if (coreBytes is not { Length: >= 32 }) return map;

        var raw = new List<(int Group, int Bit, byte EfficiencyClass)>();
        byte minClass = byte.MaxValue;
        byte maxClass = byte.MinValue;
        int offset = 0;

        while (offset + 32 <= coreBytes.Length)
        {
            uint rel = BinaryPrimitives.ReadUInt32LittleEndian(coreBytes.AsSpan(offset));
            int size = BinaryPrimitives.ReadInt32LittleEndian(coreBytes.AsSpan(offset + 4));
            if (size < 32 || offset + size > coreBytes.Length) break;
            if (rel == RelationProcessorCore)
            {
                byte efficiency = coreBytes[offset + 9];
                if (efficiency > maxClass) maxClass = efficiency;
                if (efficiency < minClass) minClass = efficiency;
                int groupCount = BinaryPrimitives.ReadUInt16LittleEndian(coreBytes.AsSpan(offset + 30));
                for (int g = 0; g < groupCount; g++)
                {
                    int entryOffset = offset + 32 + g * 16;
                    if (entryOffset + 16 > offset + size) break;
                    ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(coreBytes.AsSpan(entryOffset));
                    int group = BinaryPrimitives.ReadUInt16LittleEndian(coreBytes.AsSpan(entryOffset + 8));
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0)
                            raw.Add((group, bit, efficiency));
                    }
                }
            }
            offset += size;
        }

        if (maxClass <= minClass) return map;
        foreach (var entry in raw)
            map[(entry.Group, entry.Bit)] = entry.EfficiencyClass == maxClass ? CpuCoreType.Performance : CpuCoreType.Efficient;
        return map;
    }

    private static byte[]? QueryLogicalProcessorBuffer(uint relationship)
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint size = 0;
                bool ok = Native.GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref size);
                int err = Marshal.GetLastWin32Error();
                if (!ok && err != InsufficientBuffer) return null;
                if (size is < 32 or > 16 * 1024 * 1024) return null;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!Native.GetLogicalProcessorInformationEx(relationship, buffer, ref size))
                    {
                        if (Marshal.GetLastWin32Error() == InsufficientBuffer) continue;
                        return null;
                    }
                    byte[] copy = new byte[size];
                    Marshal.Copy(buffer, copy, 0, (int)size);
                    return copy;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (Exception diagnosticError) { CoreDiagnostics.Report("EtwPropertyReadFailed", error: diagnosticError); }
        return null;
    }

    internal static ProcessThreadSnapshot SnapshotProcessesAndThreads()
    {
        var processes = new Dictionary<int, ProcessSnapshotItem>();
        var threads = new Dictionary<int, int>();

        IntPtr snap = Native.CreateToolhelp32Snapshot(Th32csSnapProcess | Th32csSnapThread, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            return new ProcessThreadSnapshot(processes, threads);

        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Native.Process32FirstW(snap, ref pe))
            {
                do
                {
                    int pid = checked((int)pe.th32ProcessID);
                    int ppid = checked((int)pe.th32ParentProcessID);
                    processes[pid] = new ProcessSnapshotItem(pid, ppid, pe.szExeFile ?? string.Empty);
                }
                while (Native.Process32NextW(snap, ref pe));
            }

            var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (Native.Thread32First(snap, ref te))
            {
                do
                {
                    threads[checked((int)te.th32ThreadID)] = checked((int)te.th32OwnerProcessID);
                }
                while (Native.Thread32Next(snap, ref te));
            }
        }
        finally
        {
            Native.CloseHandle(snap);
        }

        return new ProcessThreadSnapshot(processes, threads);
    }

    internal static Guid? TryReadVirtualMachineTokenOwner(int processId)
    {
        if (processId <= 4) return null;
        try
        {
            using var process = Native.OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid) return null;
            if (!Native.OpenProcessToken(process, TokenQuery, out var token))
            {
                token?.Dispose();
                return null;
            }
            using (token)
            {
                Native.GetTokenInformation(token, 1, IntPtr.Zero, 0, out uint length);
                if (Marshal.GetLastWin32Error() != InsufficientBuffer || length < (uint)IntPtr.Size + 8 || length > 1048576)
                    return null;
                IntPtr buf = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!Native.GetTokenInformation(token, 1, buf, length, out _)) return null;
                    IntPtr sidPtr = Marshal.ReadIntPtr(buf);
                    if (sidPtr == IntPtr.Zero) return null;
                    var sid = new SecurityIdentifier(sidPtr);
                    if (!sid.Value.StartsWith("S-1-5-83-", StringComparison.Ordinal)) return null;
                    var account = sid.Translate(typeof(NTAccount)).Value;
                    int slash = account.IndexOf('\\');
                    string name = slash >= 0 ? account[(slash + 1)..] : account;
                    return Guid.TryParse(name, out var vmId) ? vmId : null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { return null; }
    }

    internal static bool TryStartKernelThreadTrace(
        int processorCount,
        out IKernelThreadTrace? trace,
        out int win32ErrorCode,
        out string? errorMessage)
    {
        try
        {
            var created = RealTimeKernelThreadTrace.Start(processorCount);
            trace = created;
            win32ErrorCode = 0;
            errorMessage = null;
            return true;
        }
        catch (Win32Exception ex)
        {
            trace = null;
            win32ErrorCode = ex.NativeErrorCode;
            errorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            trace = null;
            win32ErrorCode = ex.HResult;
            errorMessage = ex.Message;
            return false;
        }
    }

    internal static long QueryQpc()
    {
        Native.QueryPerformanceCounter(out long qpc);
        return qpc;
    }

    internal static long QueryQpcFrequency()
    {
        Native.QueryPerformanceFrequency(out long freq);
        return Math.Max(1L, freq);
    }

    internal sealed class RealTimeKernelThreadTrace : IKernelThreadTrace
    {
        private readonly string _sessionName;
        private readonly int _lpCount;
        private readonly long _qpcFrequency;
        private readonly object _syncRoot = new();

        private ulong _sessionHandle;
        private ulong _traceHandle = InvalidTraceHandle;
        private IntPtr _propertiesPtr;
        private int _propertiesSize;
        private IntPtr _loggerNamePtr;
        private Native.EventRecordCallback _eventCallback;
        private Native.BufferCallback _bufferCallback;
        private Thread? _consumerThread;

        private readonly uint[] _currentTid;
        private readonly long[] _lastSwitchQpc;
        private readonly bool[] _lpInitialized;
        private long _windowStartQpc;

        private readonly long[] _idleTicks;
        private Dictionary<(int Lp, uint Pid), long> _pidTicks = new();
        private Dictionary<(int Lp, uint Tid), long> _unresolvedTidTicks = new();
        private readonly Dictionary<uint, uint> _tidToPid = new(8192);
        private readonly HashSet<uint> _idleTids = new() { 0 };

        private int _processTopologyDirty = 1;
        private int _buffersProcessed;
        private volatile bool _stopping;
        private volatile string? _consumerFailure;
        private uint _previousEventsLost, _previousBuffersLost;

        private RealTimeKernelThreadTrace(string sessionName, int lpCount, long qpcFrequency)
        {
            _sessionName = sessionName;
            _lpCount = Math.Max(1, lpCount);
            _qpcFrequency = Math.Max(1L, qpcFrequency);
            _currentTid = new uint[_lpCount];
            _lastSwitchQpc = new long[_lpCount];
            _lpInitialized = new bool[_lpCount];
            _idleTicks = new long[_lpCount];
            _eventCallback = OnEventRecord;
            _bufferCallback = OnBuffer;
        }

        public bool ProcessTopologyDirty => Volatile.Read(ref _processTopologyDirty) != 0;

        public void ClearProcessTopologyDirty() => Interlocked.Exchange(ref _processTopologyDirty, 0);

        public void SeedThreads(IReadOnlyDictionary<int, int> threadToPid)
        {
            if (threadToPid == null || threadToPid.Count == 0) return;
            lock (_syncRoot)
            {
                foreach (var kv in threadToPid)
                {
                    if (kv.Key <= 0 || kv.Value < 0) continue;
                    uint tid = checked((uint)kv.Key);
                    uint pid = checked((uint)kv.Value);
                    _tidToPid.TryAdd(tid, pid);
                    if (pid == 0) _idleTids.Add(tid);
                }
            }
        }

        public static RealTimeKernelThreadTrace Start(int processorCount)
        {
            int lpCount = Math.Max(1, processorCount);
            string sessionName = $"ExHyperV.CpuTrace.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var instance = new RealTimeKernelThreadTrace(sessionName, lpCount, QueryQpcFrequency());
            try
            {
                instance.Initialize();
                return instance;
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        private void Initialize()
        {
            int structSize = sizeof(EVENT_TRACE_PROPERTIES);
            int nameBytes = (_sessionName.Length + 2) * sizeof(char);
            _propertiesSize = structSize + nameBytes;
            _propertiesPtr = Marshal.AllocHGlobal(_propertiesSize);
            Unsafe.InitBlockUnaligned((void*)_propertiesPtr, 0, (uint)_propertiesSize);

            var props = (EVENT_TRACE_PROPERTIES*)_propertiesPtr;
            props->Wnode.BufferSize = (uint)_propertiesSize;
            props->Wnode.Flags = WnodeFlagTracedGuid;
            props->Wnode.ClientContext = 1; // QueryPerformanceCounter
            props->BufferSize = 64;
            props->MinimumBuffers = (uint)Math.Clamp(_lpCount * 2, 8, 256);
            props->MaximumBuffers = (uint)Math.Clamp(_lpCount * 8, 32, 1024);
            props->LogFileMode = EventTraceRealTimeMode | EventTraceSystemLoggerMode;
            props->FlushTimer = 1;
            props->EnableFlags = EventTraceFlagProcess | EventTraceFlagThread | EventTraceFlagCSwitch;
            props->LoggerNameOffset = (uint)structSize;
            fixed (char* namePtr = _sessionName)
            {
                Buffer.MemoryCopy(namePtr, (byte*)_propertiesPtr + structSize, nameBytes, _sessionName.Length * sizeof(char));
            }

            uint rc = Native.StartTraceW(out _sessionHandle, _sessionName, _propertiesPtr);
            if (rc != 0) throw new Win32Exception(checked((int)rc));

            _loggerNamePtr = Marshal.StringToHGlobalUni(_sessionName);
            EVENT_TRACE_LOGFILEW logfile = default;
            logfile.LoggerName = _loggerNamePtr;
            logfile.ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp;
            logfile.BufferCallback = Marshal.GetFunctionPointerForDelegate(_bufferCallback);
            logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_eventCallback);

            _traceHandle = Native.OpenTraceW(ref logfile);
            if (_traceHandle == InvalidTraceHandle)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            long startQpc = QueryQpc();
            lock (_syncRoot)
            {
                _windowStartQpc = startQpc;
                for (int i = 0; i < _lpCount; i++) _lastSwitchQpc[i] = startQpc;
            }

            _consumerThread = new Thread(RunProcessTrace)
            {
                IsBackground = true,
                Name = "ExHyperV-ETW-CpuTrace"
            };
            _consumerThread.Start();
        }

        private void RunProcessTrace()
        {
            try
            {
                ulong handle = _traceHandle;
                if (handle == InvalidTraceHandle) return;
                uint result = Native.ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero);
                if (!_stopping) _consumerFailure = $"ProcessTrace exited ({result}).";
            }
            catch (Exception ex)
            {
                _consumerFailure = ex.Message;
            }
        }

        private uint OnBuffer(EVENT_TRACE_LOGFILEW* logfile) => _stopping ? 0u : 1u;

        private void OnEventRecord(EVENT_RECORD* record)
        {
            if (_stopping || record == null) return;
            Interlocked.Increment(ref _buffersProcessed);

            ref readonly EVENT_HEADER header = ref record->EventHeader;
            if (header.ProviderId == ThreadProviderGuid)
            {
                byte opcode = header.EventDescriptor.Opcode;
                if (opcode == 36 && record->UserDataLength >= 8 && record->UserData != IntPtr.Zero)
                {
                    uint* payload = (uint*)record->UserData;
                    uint newTid = payload[0];
                    uint oldTid = payload[1];
                    int lp = (header.Flags & EventHeaderFlagProcessorIndex) != 0
                        ? record->BufferContext.ProcessorIndex
                        : record->BufferContext.ProcessorNumber;
                    RecordContextSwitch(lp, header.TimeStamp, oldTid, newTid);
                }
                else if (opcode is 1 or 3 && record->UserDataLength >= 8 && record->UserData != IntPtr.Zero)
                {
                    uint* payload = (uint*)record->UserData;
                    uint pid = payload[0];
                    uint tid = payload[1];
                    RecordThreadStart(tid, pid);
                }
                else if (opcode is 2 or 4 && record->UserDataLength >= 8 && record->UserData != IntPtr.Zero)
                {
                    uint* payload = (uint*)record->UserData;
                    uint tid = payload[1];
                    RecordThreadEnd(tid);
                }
            }
            else if (header.ProviderId == ProcessProviderGuid && header.EventDescriptor.Opcode is 1 or 2 or 3 or 4)
            {
                Interlocked.Exchange(ref _processTopologyDirty, 1);
            }
        }

        private void RecordThreadStart(uint tid, uint pid)
        {
            if (tid == 0) return;
            lock (_syncRoot)
            {
                _tidToPid[tid] = pid;
                if (pid == 0) _idleTids.Add(tid);
                else _idleTids.Remove(tid);
            }
        }

        private void RecordThreadEnd(uint tid)
        {
            if (tid == 0) return;
            lock (_syncRoot)
            {
                // Flush any unresolved slices accumulated before ThreadStart was processed.
                if (_tidToPid.TryGetValue(tid, out uint pid))
                {
                    for (int lp = 0; lp < _lpCount; lp++)
                    {
                        var key = (lp, tid);
                        if (_unresolvedTidTicks.Remove(key, out long dt) && dt > 0)
                        {
                            if (pid == 0) _idleTicks[lp] += dt;
                            else
                            {
                                var pidKey = (lp, pid);
                                _pidTicks[pidKey] = _pidTicks.TryGetValue(pidKey, out long cur) ? cur + dt : dt;
                            }
                        }
                    }
                    _tidToPid.Remove(tid);
                }
                _idleTids.Remove(tid);
            }
        }

        private void RecordContextSwitch(int lp, long qpc, uint oldTid, uint newTid)
        {
            if ((uint)lp >= (uint)_lpCount) return;
            lock (_syncRoot)
            {
                long start = Math.Max(_lastSwitchQpc[lp], _windowStartQpc);
                if (qpc > start)
                {
                    long dt = qpc - start;
                    uint switchedOut = _lpInitialized[lp] ? _currentTid[lp] : oldTid;
                    AccumulateSliceLocked(lp, switchedOut, dt);
                    _lastSwitchQpc[lp] = qpc;
                }
                else if (!_lpInitialized[lp])
                {
                    _lastSwitchQpc[lp] = Math.Max(start, qpc);
                }

                _currentTid[lp] = newTid;
                _lpInitialized[lp] = true;
            }
        }

        private void AccumulateSliceLocked(int lp, uint tid, long dt)
        {
            if (dt <= 0) return;
            if (tid == 0 || _idleTids.Contains(tid))
            {
                _idleTicks[lp] += dt;
                return;
            }
            if (_tidToPid.TryGetValue(tid, out uint pid))
            {
                if (pid == 0) _idleTicks[lp] += dt;
                else
                {
                    var key = (lp, pid);
                    _pidTicks[key] = _pidTicks.TryGetValue(key, out long cur) ? cur + dt : dt;
                }
                return;
            }
            var tKey = (lp, tid);
            _unresolvedTidTicks[tKey] = _unresolvedTidTicks.TryGetValue(tKey, out long uCur) ? uCur + dt : dt;
        }

        public EtwHarvestWindow HarvestWindow()
        {
            if (_consumerFailure is { } failure) throw new IOException(failure);
            int beforeFlush = Volatile.Read(ref _buffersProcessed);
            uint eventsLost = 0;
            uint rtBuffersLost = 0;

            if (_sessionHandle != 0 && _propertiesPtr != IntPtr.Zero)
            {
                uint flushRc = Native.ControlTraceW(_sessionHandle, null, _propertiesPtr, EventTraceControlFlush);
                if (flushRc != 0) throw new Win32Exception((int)flushRc);
                var props = (EVENT_TRACE_PROPERTIES*)_propertiesPtr;
                eventsLost = unchecked(props->EventsLost - _previousEventsLost);
                rtBuffersLost = unchecked(props->RealTimeBuffersLost - _previousBuffersLost);
                _previousEventsLost = props->EventsLost;
                _previousBuffersLost = props->RealTimeBuffersLost;
            }

            // Wait briefly for the ProcessTrace consumer thread to drain flushed kernel buffers.
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(2);
                int current = Volatile.Read(ref _buffersProcessed);
                if (current == beforeFlush && i > 0) break;
                beforeFlush = current;
            }

            long harvestQpc = QueryQpc();
            lock (_syncRoot)
            {
                long startQpc = _windowStartQpc;
                long endQpc = Math.Max(startQpc, harvestQpc);

                for (int lp = 0; lp < _lpCount; lp++)
                {
                    long lastQpc = Math.Max(_lastSwitchQpc[lp], startQpc);
                    if (_lpInitialized[lp] && endQpc > lastQpc)
                    {
                        AccumulateSliceLocked(lp, _currentTid[lp], endQpc - lastQpc);
                    }
                    _lastSwitchQpc[lp] = endQpc;
                }

                var idleCopy = new Dictionary<int, long>(_lpCount);
                for (int lp = 0; lp < _lpCount; lp++)
                {
                    if (_idleTicks[lp] > 0) idleCopy[lp] = _idleTicks[lp];
                    _idleTicks[lp] = 0;
                }

                var pidCopy = _pidTicks;
                _pidTicks = new Dictionary<(int Lp, uint Pid), long>(pidCopy.Count);

                var unresolvedCopy = _unresolvedTidTicks;
                _unresolvedTidTicks = new Dictionary<(int Lp, uint Tid), long>(unresolvedCopy.Count);

                var tidMapCopy = new Dictionary<uint, uint>(_tidToPid);
                _windowStartQpc = endQpc;

                return new EtwHarvestWindow(
                    startQpc,
                    endQpc,
                    _qpcFrequency,
                    eventsLost,
                    rtBuffersLost,
                    idleCopy,
                    pidCopy,
                    unresolvedCopy,
                    tidMapCopy);
            }
        }

        public void Dispose()
        {
            if (_stopping) return;
            _stopping = true;

            uint stopResult = 0, closeResult = 0;
            ulong session = Interlocked.Exchange(ref _sessionHandle, 0);
            if (session != 0 && _propertiesPtr != IntPtr.Zero)
            {
                stopResult = Native.ControlTraceW(session, _sessionName, _propertiesPtr, EventTraceControlStop);
            }

            ulong trace = Interlocked.Exchange(ref _traceHandle, InvalidTraceHandle);
            if (trace != InvalidTraceHandle)
            {
                closeResult = Native.CloseTrace(trace);
            }

            if (_consumerThread is { IsAlive: true })
            {
                _consumerThread.Join();
            }

            IntPtr propsPtr = Interlocked.Exchange(ref _propertiesPtr, IntPtr.Zero);
            if (propsPtr != IntPtr.Zero) Marshal.FreeHGlobal(propsPtr);

            IntPtr loggerPtr = Interlocked.Exchange(ref _loggerNamePtr, IntPtr.Zero);
            if (loggerPtr != IntPtr.Zero) Marshal.FreeHGlobal(loggerPtr);
            if (stopResult is not (0 or 4201)) throw new Win32Exception((int)stopResult);
            if (closeResult is not (0 or 7007)) throw new Win32Exception((int)closeResult); // ERROR_CTX_CLOSE_PENDING
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        public fixed char StandardName[32];
        public fixed ushort StandardDate[8];
        public int StandardBias;
        public fixed char DaylightName[32];
        public fixed ushort DaylightDate[8];
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_LOGFILEW
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 4)]
    internal struct ETW_BUFFER_CONTEXT
    {
        [FieldOffset(0)] public byte ProcessorNumber;
        [FieldOffset(1)] public byte Alignment;
        [FieldOffset(0)] public ushort ProcessorIndex;
        [FieldOffset(2)] public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    internal static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void EventRecordCallback(EVENT_RECORD* eventRecord);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate uint BufferCallback(EVENT_TRACE_LOGFILEW* logfile);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint StartTraceW(out ulong sessionHandle, string instanceName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint ControlTraceW(ulong sessionHandle, string? instanceName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern uint ProcessTrace(ulong* handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern uint CloseTrace(ulong traceHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLogicalProcessorInformationEx(uint relationship, IntPtr buffer, ref uint length);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryPerformanceCounter(out long performanceCount);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryPerformanceFrequency(out long frequency);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, uint length, out uint returned);
    }
}
