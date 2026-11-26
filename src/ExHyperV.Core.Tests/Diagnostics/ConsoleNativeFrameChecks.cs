using ExHyperV.Services;

internal static class ConsoleNativeFrameChecks
{
    // The packaged bridge produces two disjoint changes, an idle frame and two resizes, then destroys the producer.
    internal static void Run()
    {
        ConsoleFrameRegion[] changes = [new(0, 0, 1, 1), new(3, 3, 1, 1)];
        int count = 0;
        byte[] baseline = [];
        byte[] saved = [];
        ConsoleFrame? retained = null;
        try
        {
            FreeRdpNative.RunFrameFixture(frame =>
            {
                int step = count++;
                var kind = step switch { 1 => ConsoleFrameUpdateKind.Delta, 2 => ConsoleFrameUpdateKind.Unchanged, _ => ConsoleFrameUpdateKind.Full };
                int width = step == 3 ? 2 : 4;
                var desktop = new ConsoleFrameRegion(0, 0, width, 4);
                ConsoleFrameRegion[] expected = step switch { 1 => changes, 2 => [], _ => [desktop] };
                Require(frame.Width == width && frame.Height == 4 && frame.Stride == width * 4
                    && frame.Sequence == step + 1 && frame.BaseSequence == step, "Native fixture dimensions or sequence");
                Require(frame.UpdateKind == kind && frame.HasChanges == (step != 2)
                    && frame.HasFullDamage == (kind == ConsoleFrameUpdateKind.Full)
                    && frame.Damage == (step == 2 ? default : desktop), "Legacy damage envelope or update kind");
                Require(frame.DirtyRegions.SequenceEqual(expected) && frame.MoveRegions.Count == 0, "Native dirty rectangles");
                if (frame.HasFullDamage) baseline = new byte[frame.ByteLength];
                foreach (var rect in frame.DirtyRegions)
                    for (int y = rect.Y; y < rect.Y + rect.Height; y++)
                    {
                        int offset = y * frame.Stride + rect.X * 4;
                        frame.Pixels.Slice(offset, rect.Width * 4).CopyTo(baseline.AsSpan(offset));
                    }
                Require(frame.Pixels.SequenceEqual(baseline), "Dirty pixels must reconstruct the full native snapshot");
                if (step == 1)
                {
                    Require(baseline[0] == 11 && baseline[60] == 29, "Expected both changed pixels");
                    saved = frame.CopyPixels();
                    retained = frame.Retain();
                }
            });
            Require(count == 5 && retained != null && retained.Pixels.SequenceEqual(saved)
                && retained.DirtyRegions.SequenceEqual(changes), "Retained pixels and regions after native shutdown");
        }
        finally { retained?.Dispose(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
