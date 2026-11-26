using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Server.Hosting;

// Each encoder opens a private delivery. Handles are authenticated by the local connector.
// The provider keeps each slot immutable through the keyed mutex release on the consuming device.
internal interface IConsoleGpuSource
{
    IConsoleGpuDelivery OpenDelivery();
}

internal interface IConsoleGpuDelivery : IDisposable
{
    event Action? Available;
    ConsoleGpuFrame? TakeLatest();
    ulong RequestCpu();
}

internal sealed record ConsoleGpuFrame(long AdapterLuid, ulong Epoch, int Slot, long Sequence, int Width, int Height,
    SafeFileHandle Texture, ulong ReadKey, ulong ReturnKey) : IDisposable
{
    public void Dispose() => Texture.Dispose();
}
