using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class ConsoleFrameContractTests
{
    [TestMethod]
    public void MovesReadFromTheSameBaselineBeforeDirtyPixelsOverwriteTheirDestinations()
    {
        byte[] baseline = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        int[] sourcePixels = [0, 1, 0, 7, 2, 3, 0, 7];
        byte[] expected = sourcePixels.SelectMany(x => baseline.Skip(x * 4).Take(4)).ToArray();
        ConsoleFrameMove[] moves = [new(0, 0, new(2, 0, 3, 1)), new(2, 0, new(4, 0, 2, 1))];
        ConsoleFrameRegion[] dirty = [new(3, 0, 1, 1), new(6, 0, 1, 1)];
        using var frame = new ConsoleFrame(8, 1, 32, expected, 2, 1, ConsoleFrameUpdateKind.Delta, dirty, moves);
        Array.Clear(moves);
        Array.Clear(dirty);
        Assert.AreEqual(new ConsoleFrameRegion(2, 0, 5, 1), frame.Damage);
        Assert.IsTrue(frame.HasChanges);
        Assert.IsFalse(frame.HasFullDamage);
        byte[] rebuilt = baseline.ToArray();
        foreach (var move in frame.MoveRegions)
            baseline.AsSpan(move.SourceX * 4, move.Destination.Width * 4).CopyTo(rebuilt.AsSpan(move.Destination.X * 4));
        foreach (var rect in frame.DirtyRegions)
            frame.Pixels.Slice(rect.X * 4, rect.Width * 4).CopyTo(rebuilt.AsSpan(rect.X * 4));
        CollectionAssert.AreEqual(expected, rebuilt);
    }

    [TestMethod]
    public void ZeroUpdateKeepsTheWholeSnapshotAndRejectsInconsistentRegionsOrBaselines()
    {
        byte[] pixels = [1, 2, 3, 255];
        ConsoleFrameRegion screen = new(0, 0, 1, 1);
        ConsoleFrame Make(ConsoleFrameUpdateKind kind, long baseline = 1, ConsoleFrameRegion[]? dirty = null,
            ConsoleFrameMove[]? moves = null) => new(1, 1, 4, pixels, 2, baseline, kind, dirty, moves);
        using var idle = Make(ConsoleFrameUpdateKind.Unchanged);
        Assert.AreEqual(default(ConsoleFrameRegion), idle.Damage);
        Assert.AreEqual(0, idle.DirtyRegions.Count);
        Assert.AreEqual(0, idle.MoveRegions.Count);
        Assert.IsFalse(idle.HasChanges);
        CollectionAssert.AreEqual(pixels, idle.CopyPixels());
        Assert.Throws<ArgumentException>(() => Make(ConsoleFrameUpdateKind.Unchanged, dirty: [screen]));
        Assert.Throws<ArgumentException>(() => Make(ConsoleFrameUpdateKind.Full, moves: [new(0, 0, screen)]));
        Assert.Throws<ArgumentException>(() => Make(ConsoleFrameUpdateKind.Delta));
        Assert.Throws<ArgumentException>(() => Make((ConsoleFrameUpdateKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(ConsoleFrameUpdateKind.Unchanged, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(ConsoleFrameUpdateKind.Full, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(ConsoleFrameUpdateKind.Delta, dirty: [default]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(ConsoleFrameUpdateKind.Delta, moves: [new(int.MaxValue, 0, screen)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(ConsoleFrameUpdateKind.Delta, moves: [new(0, 0, screen with { Y = -1 })]));
        using var moved = Make(ConsoleFrameUpdateKind.Delta, moves: [new(0, 0, screen)]);
        Assert.AreEqual(screen, moved.Damage);
        Assert.IsFalse(moved.HasFullDamage);
    }
}
