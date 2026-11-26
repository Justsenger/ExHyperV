using System.Text.Json;
using ExHyperV.Server.Api;
using ExHyperV.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class RuntimeTests
{
    private static (ApiObjects Objects, ApiCodec Codec, ApiExecution Execution) Runtime()
    {
        var catalog = new ApiCatalog();
        var objects = new ApiObjects();
        var codec = new ApiCodec(catalog, objects);
        return (objects, codec, new(catalog, objects, codec, NullLogger<ApiExecution>.Instance));
    }

    [TestMethod]
    public async Task ConsoleImagePreservesBgraAlphaAndSkipsRowPadding()
    {
        var catalog = new ApiCatalog();
        var objects = new ApiObjects();
        var codec = new ApiCodec(catalog, objects);
        var policy = new ApiPolicy(catalog, LaunchOptions.Parse(["--read-only"]));
        var gateway = new CoreGateway(catalog, policy, objects, codec, new(catalog, objects, codec, NullLogger<ApiExecution>.Instance));
        byte[] pixels = [0, 0, 255, 255, 0, 255, 0, 128, 99, 99, 99, 99,
                         255, 0, 0, 255, 31, 42, 53, 200, 99, 99, 99, 99];
        using var frame = new ExHyperV.Services.ConsoleFrame(2, 2, 12, pixels, 1);
        Guid handle = objects.Add(frame.Retain());
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        using var image = new MemoryStream();
        context.Response.Body = image;
        await gateway.SendBinary(handle, context);
        Assert.AreEqual("image/png", context.Response.ContentType);
        await objects.ReleaseAsync(handle);
        Assert.AreEqual(0, objects.Count);
        using var reference = new System.Drawing.Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = reference.LockBits(new(0, 0, 2, 2), System.Drawing.Imaging.ImageLockMode.WriteOnly, reference.PixelFormat);
        try
        {
            for (int y = 0; y < 2; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * 12, bits.Scan0 + y * bits.Stride, 8);
        }
        finally { reference.UnlockBits(bits); }
        using var expectedPng = new MemoryStream();
        reference.Save(expectedPng, System.Drawing.Imaging.ImageFormat.Png);
        CollectionAssert.AreEqual(expectedPng.ToArray(), image.ToArray());
        using var stream = new MemoryStream(image.ToArray());
        using var bitmap = new System.Drawing.Bitmap(stream);
        Assert.AreEqual(2, bitmap.Width);
        Assert.AreEqual(2, bitmap.Height);
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            int offset = y * 12 + x * 4;
            var expected = System.Drawing.Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            Assert.AreEqual(expected.ToArgb(), bitmap.GetPixel(x, y).ToArgb());
        }
    }

    [TestMethod]
    public void RepeatedSnapshotsAndDroppedEventsKeepNoHandles()
    {
        var (objects, codec, _) = Runtime();
        using var feed = new ApiFeed(codec);
        for (int i = 0; i < 10000; i++)
        {
            codec.Pack(new { Name = "测试", Value = i });
            feed.Publish("Progress", [new { Value = i }]);
        }
        Assert.AreEqual(0, objects.Count);
        JsonElement result = JsonSerializer.SerializeToElement(feed.Read(128));
        Assert.AreEqual(9872, result.GetProperty("dropped").GetInt32());
        Assert.AreEqual(128, result.GetProperty("events").GetArrayLength());
        Assert.AreEqual(9999, result.GetProperty("events")[127].GetProperty("arguments")[0].GetProperty("Value").GetInt32());
    }

    [TestMethod]
    public async Task BorrowedChildKeepsOwnerAliveAndNeverTakesOwnership()
    {
        var (objects, _, _) = Runtime();
        var parent = new Resource();
        var child = new Resource();
        Guid parentId = objects.Add(parent);
        Guid childId = objects.Add(child, owned: false, parent: parentId);
        using (objects.Use(childId))
            await Assert.ThrowsAsync<InvalidOperationException>(() => objects.ReleaseAsync(parentId));
        await objects.ReleaseAsync(childId);
        Assert.AreEqual(0, child.DisposeCount);
        await objects.ReleaseAsync(parentId);
        Assert.AreEqual(1, parent.DisposeCount);
    }

    [TestMethod]
    public async Task OwnerReleaseInvalidatesBorrowedChildren()
    {
        var (objects, _, _) = Runtime();
        Guid parent = objects.Add(new Resource());
        Guid child = objects.Add(new Resource(), owned: false, parent: parent);
        await objects.ReleaseAsync(parent);
        Assert.AreEqual(0, objects.Count);
        Assert.Throws<ArgumentException>(() => objects.Get(child));
    }

    [TestMethod]
    public async Task FailedCleanupKeepsTheSameHandleForRetry()
    {
        var (objects, _, _) = Runtime();
        var resource = new Resource { Fail = true };
        Guid handle = objects.Add(resource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => objects.ReleaseAsync(handle));
        Assert.AreSame(resource, objects.Get(handle));
        resource.Fail = false;
        await objects.ReleaseAsync(handle);
        Assert.AreEqual(0, objects.Count);
    }

    [TestMethod]
    public async Task ShutdownDeadlinePreservesResourcesStillInUse()
    {
        var (objects, _, execution) = Runtime();
        var resource = new Resource();
        Guid handle = objects.Add(resource);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = execution.Start(async _ =>
        {
            using var lease = objects.Use(handle);
            started.SetResult();
            await finish.Task;
            return null;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await execution.StopAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, resource.DisposeCount);
        finish.SetResult();
        await operation.Task;
        await execution.StopAsync(CancellationToken.None);
        Assert.AreEqual(1, resource.DisposeCount);
    }

    [TestMethod]
    public async Task CancellationHasItsOwnTerminalState()
    {
        var (_, _, execution) = Runtime();
        var operation = execution.Start(async current => { await Task.Delay(Timeout.Infinite, current.Cancellation.Token); return null; });
        operation.Cancellation.Cancel();
        await operation.Task;
        var status = JsonSerializer.SerializeToElement(execution.Status(operation.Id, 1));
        Assert.AreEqual("Cancelled", status.GetProperty("state").GetString());
        execution.Forget(operation.Id);
    }

    [TestMethod]
    public async Task LatestFrameSurvivesCallbackAndReleasesItsLease()
    {
        var (objects, codec, _) = Runtime();
        using var feed = new ApiFeed(codec);
        for (int i = 1; i <= 100; i++)
        {
            using var original = new ExHyperV.Services.ConsoleFrame(1, 1, 4, new byte[] { (byte)i, 0, 0, 255 }, i);
            feed.Publish("FrameReceived", [original]);
        }
        Assert.AreEqual(0, objects.Count);
        var result = JsonSerializer.SerializeToElement(feed.Read(1));
        Assert.AreEqual(99, result.GetProperty("dropped").GetInt32());
        Guid handle = result.GetProperty("events")[0].GetProperty("arguments")[0].GetProperty("$handle").GetGuid();
        var frame = (ExHyperV.Services.ConsoleFrame)objects.Get(handle);
        Assert.AreEqual(100L, frame.Sequence);
        Assert.AreEqual((byte)100, frame.CopyPixels()[0]);
        await objects.ReleaseAsync(handle);
        Assert.Throws<ObjectDisposedException>(() => frame.CopyPixels());
    }

    [TestMethod]
    public async Task CoreUnknownOutcomeRemainsExplicitAtBothLevels()
    {
        var (_, _, execution) = Runtime();
        var operation = execution.Start(_ => Task.FromResult<object?>(ExHyperV.Tools.ApiResponse.Fail("核对资源", kind: ExHyperV.Tools.ApiErrorKind.OutcomeUnknown)));
        var reply = await operation.Task;
        Assert.AreEqual("OutcomeUnknown", reply.ErrorKind);
        Assert.AreEqual("RequiresVerification", JsonSerializer.SerializeToElement(execution.Status(operation.Id, 1)).GetProperty("state").GetString());
        execution.Forget(operation.Id);
    }

    [TestMethod]
    public async Task OperationQueueHasAnAdmissionLimitAndCanBeDrained()
    {
        var (_, _, execution) = Runtime();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = Enumerable.Range(0, 256).Select(_ => execution.Start(async operation => { await stop.Task.WaitAsync(operation.Cancellation.Token); return null; })).ToArray();
        Assert.Throws<InvalidOperationException>(() => execution.Start(_ => Task.FromResult<object?>(null)));
        stop.SetResult();
        await Task.WhenAll(accepted.Select(operation => operation.Task));
        foreach (var operation in accepted) execution.Forget(operation.Id);
        var next = execution.Start(_ => Task.FromResult<object?>(null));
        await next.Task;
        execution.Forget(next.Id);
    }

    [TestMethod]
    public async Task ReflectedCancellationKeepsAReadableFinalState()
    {
        var (_, _, execution) = Runtime();
        var operation = execution.Start(_ => throw new System.Reflection.TargetInvocationException(new OperationCanceledException()));
        var reply = await operation.Task;
        Assert.IsFalse(reply.Success);
        Assert.AreEqual("Cancelled", JsonSerializer.SerializeToElement(execution.Status(operation.Id, 1)).GetProperty("state").GetString());
        execution.Forget(operation.Id);
    }

    [TestMethod]
    public void ObjectAndBinaryAdmissionBudgetsAreEnforced()
    {
        var objects = new ApiObjects();
        objects.Add(new byte[64]);
        Assert.Throws<InvalidOperationException>(() => objects.RequireCapacity(128L * 1024 * 1024));
        for (int index = 1; index < 4096; index++) objects.Add(new object());
        Assert.Throws<InvalidOperationException>(() => objects.RequireCapacity());
    }

    [TestMethod]
    public async Task FrameStaysQueuedUntilItsByteBudgetIsAvailable()
    {
        var (objects, codec, _) = Runtime();
        using var feed = new ApiFeed(codec);
        using (var frame = new ExHyperV.Services.ConsoleFrame(1, 1, 4, new byte[4], 1))
            feed.Publish("FrameReceived", [frame]);
        Guid buffer = objects.Add(new FullBudgetStream());
        var first = JsonSerializer.SerializeToElement(feed.Read(1));
        Assert.IsTrue(first.GetProperty("resourceLimitReached").GetBoolean());
        Assert.AreEqual(1, first.GetProperty("remaining").GetInt32());
        await objects.ReleaseAsync(buffer);
        var packet = JsonSerializer.SerializeToElement(feed.Read(1)).GetProperty("events")[0];
        var id = packet.GetProperty("arguments")[0].GetProperty("$handle").GetGuid();
        var retained = (ExHyperV.Services.ConsoleFrame)objects.Get(id);
        Assert.AreEqual(4, retained.CopyPixels().Length);
        await objects.ReleaseAsync(id);
        Assert.Throws<ObjectDisposedException>(() => retained.CopyPixels());
    }

    [TestMethod]
    public async Task RetainingCollectionsAndSmallBinaryResultsPreservesTheirIdentity()
    {
        var (objects, codec, _) = Runtime();
        byte[] bytes = [1, 2, 3];
        string[] values = ["a", "b"];
        foreach (object item in new object[] { bytes, values })
        {
            var encoded = JsonSerializer.SerializeToElement(codec.Pack(item, retain: true));
            var id = encoded.GetProperty("$handle").GetGuid();
            Assert.AreSame(item, codec.Decode(encoded, item.GetType()));
            await objects.ReleaseAsync(id);
        }
        Assert.AreEqual(0, objects.Count);
    }

    private sealed class FullBudgetStream : MemoryStream
    {
        public override long Length => 128L * 1024 * 1024;
    }

    public sealed class Resource : IDisposable
    {
        public bool Fail { get; set; }
        public int DisposeCount { get; private set; }
        public void Dispose()
        {
            if (Fail) throw new InvalidOperationException("cleanup");
            DisposeCount++;
        }
    }
}
