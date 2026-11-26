using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

internal interface IConsoleNativeConnection : IDisposable
{
    uint Run();
    void Stop();
    bool Send(uint kind, ReadOnlySpan<byte> payload);
    // Optional for native substitutes without a display; production FreeRDP acquires its current frame on demand.
    ConsoleFrame? CaptureFrame() => null;
    bool SupportsGpu => false;
    bool OpenGpu(Action available) => false;
    void CloseGpu() { }
    (long AdapterLuid, ulong Epoch, long Sequence, ulong SlotDims, SafeFileHandle Texture, ulong ReadKey, ulong ReturnKey)? TakeGpuLatest() => null;
    ulong RequestGpuCpu() => 0;
}
