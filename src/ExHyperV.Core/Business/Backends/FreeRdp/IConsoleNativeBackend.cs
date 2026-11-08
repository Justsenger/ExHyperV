namespace ExHyperV.Services;

internal interface IConsoleNativeBackend
{
    // Implementations lend frameCallback a frame, then dispose that source lease in finally.
    // A callback may Retain it independently of this connection's lifetime.
    IConsoleNativeConnection Create(ConsoleConnectionOptions options, Action<uint, byte[], uint, uint> callback, Action<ConsoleFrame> frameCallback);
}
