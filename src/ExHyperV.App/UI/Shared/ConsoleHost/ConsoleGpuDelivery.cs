using Microsoft.Win32.SafeHandles;

namespace ExHyperV.App.UI.Shared.ConsoleHost;

// Each renderer opens a private delivery. TakeLatest transfers ownership of the frame handle.
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
