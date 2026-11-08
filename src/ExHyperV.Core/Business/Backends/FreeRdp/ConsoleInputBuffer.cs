using System.Buffers.Binary;

namespace ExHyperV.Services;

internal delegate bool ConsoleInputSender(uint kind, ReadOnlySpan<byte> payload);

// Called under the connection gate. Normal input takes the direct path; only a
// rejected release or an explicitly accepted sequence occupies this bounded buffer.
internal sealed class ConsoleInputBuffer(ConsoleInputSender send)
{
    internal const int MaxSequenceLength = 128;
    private readonly Queue<Command> pending = new();
    private readonly HashSet<int> pressed = new();
    private ushort pointerX, pointerY;
    internal int Width, Height;
    internal bool HasPending => pending.Count != 0;

    private readonly record struct Command(uint Kind, ushort Code, bool Extended, bool Down,
        ushort X = 0, ushort Y = 0, short Wheel = 0)
    {
        internal int Identity => Kind == 1 ? Code + (Extended ? 256 : 0)
            : Kind == 2 && Code != 0 ? 512 + Code : 0;
        internal bool IsRelease => Identity != 0 && !Down;
    }

    internal bool Key(ushort code, bool extended, bool down)
        => code is > 0 and <= 255 && Submit(new(1, code, extended, down));

    internal bool Pointer(ushort x, ushort y, ConsolePointerButton button, bool down, short wheel)
    {
        if (button is not (ConsolePointerButton.None or ConsolePointerButton.Left or ConsolePointerButton.Right or ConsolePointerButton.Middle)
            || wheel is < -255 or > 255 || (wheel != 0 && button != ConsolePointerButton.None)
            || (down && button == ConsolePointerButton.None) || Width <= 0 || Height <= 0) return false;
        bool release = button != ConsolePointerButton.None && !down;
        if (!release && (x >= Width || y >= Height)) return false;
        return Submit(new(2, (ushort)button, false, down, x, y, wheel));
    }

    private bool Submit(Command command)
    {
        Flush();
        if (pending.Count == 0 && TrySend(command)) return true;
        if (!command.IsRelease) return false;
        // At most one trailing release per key/button, in addition to a sequence
        // of at most 128 events: callers cannot grow the retry buffer without bound.
        Command? last = null;
        foreach (var item in pending) if (item.Identity == command.Identity) last = item;
        if (last is not { IsRelease: true }) pending.Enqueue(command);
        return true;
    }

    internal bool Keys(IReadOnlyList<ConsoleKeyEvent> events)
    {
        if (events.Count is 0 or > MaxSequenceLength) return false;
        var copy = events.ToArray();
        if (copy.Length is 0 or > MaxSequenceLength || copy.Any(e => e.ScanCode is 0 or > 255)) return false;
        Flush();
        if (HasPending) return false;
        foreach (var e in copy) pending.Enqueue(new(1, e.ScanCode, e.Extended, e.Down));
        Flush();
        return true;
    }

    internal bool Click(ushort x, ushort y, ConsolePointerButton button)
    {
        if (button is not (ConsolePointerButton.Left or ConsolePointerButton.Right or ConsolePointerButton.Middle)
            || x >= Width || y >= Height) return false;
        Flush();
        if (HasPending) return false;
        pending.Enqueue(new(2, (ushort)button, false, true, x, y));
        pending.Enqueue(new(2, (ushort)button, false, false, x, y));
        Flush();
        return true;
    }

    internal void ReleaseAll()
    {
        // Explicit focus loss ends the gesture; discard its not-yet-sent presses.
        pending.Clear();
        foreach (int id in pressed.Order())
            pending.Enqueue(id > 512 ? new(2, (ushort)(id - 512), false, false, pointerX, pointerY)
                : new(1, (ushort)(id & 255), id > 255, false));
        Flush();
    }

    internal void Flush()
    {
        while (pending.TryPeek(out var next) && TrySend(next)) pending.Dequeue();
    }

    internal void Clear() { pending.Clear(); pressed.Clear(); }

    private bool TrySend(Command command)
    {
        Span<byte> data = stackalloc byte[8];
        int length;
        if (command.Kind == 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data, command.Code);
            data[2] = command.Extended ? (byte)1 : (byte)0;
            data[3] = command.Down ? (byte)1 : (byte)0;
            length = 4;
        }
        else
        {
            if (Width <= 0 || Height <= 0) return false;
            // A resize must not prevent releasing a button at the previous position.
            ushort x = (ushort)Math.Min(command.X, Width - 1), y = (ushort)Math.Min(command.Y, Height - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(data, x);
            BinaryPrimitives.WriteUInt16LittleEndian(data[2..], y);
            BinaryPrimitives.WriteUInt16LittleEndian(data[4..], (ushort)(command.Code | (command.Down ? 8 : 0)));
            BinaryPrimitives.WriteInt16LittleEndian(data[6..], command.Wheel);
            length = 8;
        }
        if (!send(command.Kind, data[..length])) return false;
        if (command.Kind == 2) { pointerX = command.X; pointerY = command.Y; }
        if (command.Identity != 0)
        {
            if (command.Down) pressed.Add(command.Identity);
            else pressed.Remove(command.Identity);
        }
        return true;
    }
}
