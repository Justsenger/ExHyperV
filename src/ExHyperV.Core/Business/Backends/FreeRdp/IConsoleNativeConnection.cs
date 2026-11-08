namespace ExHyperV.Services;

internal interface IConsoleNativeConnection : IDisposable
{
    uint Run();
    void Stop();
    bool Send(uint kind, ReadOnlySpan<byte> payload);
    // Optional for native substitutes without a display; production FreeRDP acquires its current frame on demand.
    ConsoleFrame? CaptureFrame() => null;
}
