using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#if EXHYPERV_SERVER
using ExHyperV.Server.Hosting;
#else
using ExHyperV.App.UI.Shared.ConsoleHost;
#endif

namespace ExHyperV.ConsoleIpc;

internal sealed class ConsoleIpcGpuDelivery(ulong id, Func<ulong, ulong> cpu, Action<ulong> close) : IConsoleGpuDelivery
{
    private sealed record Slot(long Adapter, int Width, int Height, SafeFileHandle Handle);
    private readonly Dictionary<int, Slot> slots = [];
    private readonly object gate = new();
    private readonly object switchGate = new();
    private ulong epoch;
    private long sequence;
    private (int Slot, ulong Read, ulong Return)? ready;
    private bool disposed, switched;
    public event Action? Available;

    internal void Attach(ulong next, int index, long adapter, int width, int height, SafeFileHandle handle)
    {
        lock (gate)
        {
            if (disposed || switched || next < epoch) return;
            if (next == 0 || index is < 0 or >= 64 || width is <= 0 or > 8192 || height is <= 0 or > 8192)
                throw new IpcFailure(IpcFailure.Incompatible);
            if (next != epoch) { Clear(); epoch = next; }
            if (slots.ContainsKey(index)) throw new IpcFailure(IpcFailure.Incompatible);
            slots.Add(index, new(adapter, width, height, Duplicate(handle)));
        }
    }

    internal void Ready(ulong epoch, int index, long next, ulong read, ulong release)
    {
        lock (gate)
        {
            if (disposed || switched || epoch != this.epoch || next <= sequence || !slots.ContainsKey(index)) return;
            sequence = next;
            ready = (index, read, release);
        }
        Available?.Invoke();
    }

    public ConsoleGpuFrame? TakeLatest()
    {
        lock (gate)
        {
            if (ready is not { } frame) return null;
            ready = null;
            var slot = slots[frame.Slot];
            return new(slot.Adapter, epoch, frame.Slot, sequence, slot.Width, slot.Height, Duplicate(slot.Handle), frame.Read, frame.Return);
        }
    }

    internal static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        if (!DuplicateHandle(-1, source, -1, out var copy, 0, false, 2)) throw new IOException("EXT-HANDLE");
        return copy;
    }

    public ulong RequestCpu()
    {
        // Coalesce concurrent fallback requests without holding the slot lock across a pipe round trip.
        lock (switchGate) return SwitchToCpu();
    }

    private ulong SwitchToCpu()
    {
        ulong previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (switched) return epoch;
            previous = epoch;
        }
        ulong next = cpu(id);
        lock (gate)
        {
            if (next <= previous) throw new IpcFailure(IpcFailure.Incompatible);
            switched = true;
            Clear();
            return epoch = next;
        }
    }

    private void Clear()
    {
        foreach (var slot in slots.Values) slot.Handle.Dispose();
        slots.Clear();
        ready = null;
        sequence = 0;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Clear();
        }
        close(id);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(nint from, SafeFileHandle source, nint to,
        out SafeFileHandle result, uint access, bool inherit, uint options);
}
