using ExHyperV.Services;

namespace ExHyperV.Web;

internal sealed record ClientMessage(string Type, long Sequence = 0, int X = 0, int Y = 0,
    int Button = 0, bool Down = false, int Wheel = 0, int ScanCode = 0,
    bool Extended = false, uint Toggles = 0, double Time = 0,
    int Epoch = 0, string? Sdp = null, int Line = 0, string? Candidate = null);

internal sealed class ConsoleInput(IConsoleInput target)
{
    private static readonly IReadOnlyList<ConsoleKeyEvent> CtrlAltDelete = Array.AsReadOnly<ConsoleKeyEvent>(
        [new(0x1d, false, true), new(0x38, false, true), new(0x53, true, true),
         new(0x53, true, false), new(0x38, false, false), new(0x1d, false, false)]);
    private int width, height;
    public void SetSize(int w, int h) { Volatile.Write(ref width, w); Volatile.Write(ref height, h); }

    // Only the receive loop calls Apply/ReleaseAll; dimensions are updated by the frame callback.
    public void Apply(ClientMessage message)
    {
        switch (message.Type)
        {
            case "key":
                if (message.ScanCode is < 1 or > 255) throw new InvalidDataException("Invalid scan code.");
                target.SendKey((ushort)message.ScanCode, message.Extended, message.Down);
                break;
            case "pointer":
                if (message.X is < 0 or > 8191 || message.Y is < 0 or > 8191
                    || message.Button is not (0 or 1 or 2 or 4) || message.Wheel is < -255 or > 255
                    || (message.Button == 0 && message.Down) || (message.Wheel != 0 && message.Button != 0))
                    throw new InvalidDataException("Invalid pointer input.");
                int w = Volatile.Read(ref width), h = Volatile.Read(ref height);
                if (w == 0 || h == 0) return;
                var x = (ushort)Math.Min(message.X, w - 1); var y = (ushort)Math.Min(message.Y, h - 1);
                var button = (ConsolePointerButton)message.Button;
                target.SendPointer(x, y, button, message.Down, (short)message.Wheel);
                break;
            case "sync":
                if (message.Toggles > 15) throw new InvalidDataException("Invalid keyboard state.");
                target.SynchronizeKeyboard((ConsoleKeyboardToggleState)message.Toggles);
                break;
            case "pause": target.SendPause(); break;
            case "cad": target.SendKeys(CtrlAltDelete); break;
            case "release": ReleaseAll(); break;
            default: throw new InvalidDataException("Unknown input message.");
        }
    }

    public void ReleaseAll() => target.ReleaseAllInputs();
}
