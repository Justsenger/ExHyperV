using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

internal sealed record ConsoleFrameDamage(long BaseSequence, bool Full, ConsoleFrameRegion[] Dirty, ConsoleFrameMove[] Moves)
{
    internal static ConsoleFrameDamage FullFrame { get; } = new(0, true, [], []);
}

// All frames contain immutable final pixels. A skipped move cannot be replayed against
// those pixels, so use a full upload whenever a batch crosses a move dependency.
internal sealed class ConsoleDamageAccumulator
{
    internal const int RegionLimit = 64;
    private readonly List<ConsoleFrameRegion> dirty = [];
    private ConsoleFrameMove[] moves = [];
    private long baseline, latest;
    private bool pending, full;
    private int width, height;

    internal void Add(ConsoleFrame frame)
    {
        if (!pending) baseline = latest;
        full |= frame.Width != width || frame.Height != height || frame.HasFullDamage || latest == 0 || latest != frame.BaseSequence ||
            frame.Sequence != latest + 1 || moves.Length != 0 || pending && frame.MoveRegions.Count != 0 ||
            dirty.Count + frame.DirtyRegions.Count + frame.MoveRegions.Count > RegionLimit;
        if (!full)
        {
            dirty.AddRange(frame.DirtyRegions);
            moves = frame.MoveRegions.ToArray();
        }
        pending = true;
        latest = frame.Sequence;
        width = frame.Width;
        height = frame.Height;
    }

    internal ConsoleFrameDamage Take()
    {
        var damage = full ? ConsoleFrameDamage.FullFrame : new(baseline, false, dirty.ToArray(), moves);
        full = pending = false;
        dirty.Clear();
        moves = [];
        return damage;
    }
}
