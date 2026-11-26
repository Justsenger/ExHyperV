using System.Runtime.InteropServices;
using ExHyperV.Services;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.ConsoleIpc;

internal sealed unsafe class ConsoleIpcCpuRing : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        internal long Commit;
        internal ulong Epoch;
        internal long Sequence, Base;
        internal ConsoleFrameUpdateKind Kind;
        internal int Width, Height, Stride, Pixels, Length, DirtyCount, MoveCount, Dirty, Moves;
    }

    private sealed class View : SafeHandleZeroOrMinusOneIsInvalid
    {
        public View() : base(true) { }
        protected override bool ReleaseHandle() => UnmapViewOfFile(handle);
    }

    private readonly View view;
    private readonly int slots, stride;
    internal ulong Epoch { get; }

    internal ConsoleIpcCpuRing(SafeFileHandle section, ulong epoch, int bytes, int slots, int stride)
    {
        if (epoch == 0 || bytes is <= 0 or > 268435456 || slots is <= 0 or > 64 ||
            stride < sizeof(Header) || stride % 8 != 0 || (long)slots * stride != bytes)
            throw new IpcFailure(IpcFailure.Incompatible);
        Epoch = epoch;
        this.slots = slots;
        this.stride = stride;
        view = MapViewOfFile(section, 4, 0, 0, (nuint)bytes);
        if (view.IsInvalid) { view.Dispose(); throw new IpcFailure(IpcFailure.Incompatible); }
    }

    internal ConsoleFrame? Read(ulong epoch, int slot, long sequence)
    {
        if (epoch != Epoch || (uint)slot >= slots || sequence <= 0) return null;
        bool retained = false;
        try
        {
            view.DangerousAddRef(ref retained);
            var start = (byte*)view.DangerousGetHandle() + (long)stride * slot;
            var shared = (Header*)start;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long commit = Volatile.Read(ref shared->Commit);
                if (commit <= 0 || (commit & 1) != 0) continue;
                Header h = *shared;
                if (h.Epoch != epoch || h.Sequence != sequence || h.Width is <= 0 or > 8192 || h.Height is <= 0 or > 8192 ||
                    h.Stride < h.Width * 4 || (h.Stride & 3) != 0 || (long)h.Stride * h.Height != h.Length ||
                    h.Length > 64 << 20 || !Range(h.Pixels, h.Length) ||
                    h.DirtyCount is < 0 or > 4096 || h.MoveCount is < 0 or > 4096 ||
                    !Range(h.Dirty, h.DirtyCount * 16) || !Range(h.Moves, h.MoveCount * 24)) return null;
                var dirty = new ConsoleFrameRegion[h.DirtyCount];
                var moves = new ConsoleFrameMove[h.MoveCount];
                for (int i = 0; i < dirty.Length; i++)
                {
                    dirty[i] = Rect(start + h.Dirty + i * 16);
                    if (!Within(dirty[i], h)) return null;
                }
                for (int i = 0; i < moves.Length; i++)
                {
                    int* ptr = (int*)(start + h.Moves + i * 24);
                    var dest = Rect((byte*)(ptr + 2));
                    var move = new ConsoleFrameMove(ptr[0], ptr[1], dest);
                    if (!Within(dest, h) || !Within(dest with { X = move.SourceX, Y = move.SourceY }, h)) return null;
                    moves[i] = move;
                }
                var pixels = new ReadOnlySpan<byte>(start + h.Pixels, h.Length).ToArray();
                Thread.MemoryBarrier();
                if (Volatile.Read(ref shared->Commit) != commit || Volatile.Read(ref shared->Epoch) != epoch) continue;
                try { return new(h.Width, h.Height, h.Stride, pixels, sequence, h.Base, h.Kind, dirty, moves); }
                catch (ArgumentException) { return null; }
            }
            return null;
        }
        finally { if (retained) view.DangerousRelease(); }
    }

    private bool Range(int offset, int bytes) => bytes == 0 && offset == 0 ||
        offset >= sizeof(Header) && (offset & 3) == 0 && (long)offset + bytes <= stride;

    private static ConsoleFrameRegion Rect(byte* address)
    {
        int* p = (int*)address;
        return new(p[0], p[1], p[2], p[3]);
    }

    private static bool Within(ConsoleFrameRegion r, Header h) => r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0 &&
        (long)r.X + r.Width <= h.Width && (long)r.Y + r.Height <= h.Height;

    public void Dispose() => view.Dispose();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] private static extern View MapViewOfFile(SafeFileHandle section, uint access, uint high, uint low, nuint bytes);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(nint address);
}
