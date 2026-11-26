#if EXHYPERV_SERVER
using ExHyperV.Server.Hosting;
#else
using ExHyperV.App.UI.Shared.ConsoleHost;
#endif
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.ConsoleIpc;

using NativeTakeDelegate = Func<(long Adapter, ulong Epoch, long Sequence, ulong SlotDims, SafeFileHandle Handle, ulong ReadKey, ulong ReturnKey)?>;
using NativeBinderDelegate = Func<Action, (Func<(long Adapter, ulong Epoch, long Sequence, ulong SlotDims, SafeFileHandle Handle, ulong ReadKey, ulong ReturnKey)?> TakeLatest, Func<ulong> RequestCpu, Action Dispose)>;

internal sealed class ConsoleCoreGpuSource : IConsoleGpuSource
{
    private readonly NativeBinderDelegate binder;

    private ConsoleCoreGpuSource(NativeBinderDelegate binder) => this.binder = binder;

    internal static IConsoleGpuSource? Wrap(object? candidate)
    {
        if (candidate is IConsoleGpuSource direct) return direct;
        if (candidate is IServiceProvider sp &&
            sp.GetService(typeof(NativeBinderDelegate)) is NativeBinderDelegate binder)
            return new ConsoleCoreGpuSource(binder);
        return null;
    }

    public IConsoleGpuDelivery OpenDelivery() => new Delivery(binder);

    private sealed class Delivery : IConsoleGpuDelivery
    {
        private readonly NativeTakeDelegate take;
        private readonly Func<ulong> requestCpu;
        private readonly Action close;
        private int disposed;

        public event Action? Available;

        internal Delivery(NativeBinderDelegate binder)
        {
            (take, requestCpu, close) = binder(() => Available?.Invoke());
        }

        public ConsoleGpuFrame? TakeLatest()
        {
            if (Volatile.Read(ref disposed) != 0) return null;
            if (take() is not { } f) return null;
            int slot = (ushort)(f.SlotDims & 0xFFFFu);
            int width = (int)((f.SlotDims >> 16) & 0xFFFFu);
            int height = (int)((f.SlotDims >> 32) & 0xFFFFu);
            return new(f.Adapter, f.Epoch, slot, f.Sequence, width, height, f.Handle, f.ReadKey, f.ReturnKey);
        }

        public ulong RequestCpu() => requestCpu();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) close();
        }
    }
}
