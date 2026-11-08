using System.Text.Json;
using ExHyperV.Mcp.Hosting;
using ModelContextProtocol.Extensions.Tasks;

namespace ExHyperV.Mcp.Tests;

[TestClass]
public sealed class JournalTests
{
    private string directory = null!;
    [TestInitialize] public void Initialize() => directory = TestStorage.CreatePath();
    [TestCleanup] public void Cleanup() => TestStorage.Delete(directory);

    [TestMethod]
    public async Task ConcurrentRetriesExecuteOnceAndArgumentsStayPrivate()
    {
        var journal = new InvocationJournal(directory);
        int calls = 0;
        var args = JsonSerializer.SerializeToElement(new { apiId = "query-target", arguments = new { vmId = "target-vm", password = "private-test-value" } });
        async Task<ModelContextProtocol.Protocol.CallToolResult> Execute()
        { Interlocked.Increment(ref calls); await Task.Delay(30); return ToolReply.Result(new { success = true }); }
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => journal.RunAsync("same-request", "test", args, Execute)));
        Assert.AreEqual(1, calls);
        foreach (var file in Directory.GetFiles(directory)) Assert.IsFalse(File.ReadAllText(file).Contains("private-test-value", StringComparison.Ordinal));
        var row = JsonSerializer.SerializeToElement(journal.List()).GetProperty("entries")[0];
        Assert.AreEqual("query-target", row.GetProperty("ApiId").GetString());
        Assert.AreEqual("target-vm", row.GetProperty("ResourceIds").GetProperty("vmId").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InvocationJournal(directory).RunAsync("same-request", "test", args, Execute));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ReusingKeyWithDifferentArgumentsIsRejected()
    {
        var journal = new InvocationJournal(directory);
        Task<ModelContextProtocol.Protocol.CallToolResult> Execute() => Task.FromResult(ToolReply.Result(new { success = true }));
        await journal.RunAsync("key", "test", JsonSerializer.SerializeToElement(new { value = 1 }), Execute);
        await Assert.ThrowsAsync<ArgumentException>(() => journal.RunAsync("key", "test", JsonSerializer.SerializeToElement(new { value = 2 }), Execute));
    }

    [TestMethod]
    public async Task ProtocolTasksSurviveRestartWithoutReplayingWork()
    {
        string taskId;
        using (var store = new PersistentTaskStore(directory)) taskId = (await store.CreateTaskAsync()).TaskId;
        using var restarted = new PersistentTaskStore(directory);
        var status = await restarted.GetTaskAsync(taskId);
        Assert.AreEqual(McpTaskStatus.Failed, status!.Status);
        StringAssert.Contains(status.Error!.Value.GetProperty("message").GetString()!, "核对");
    }

    [TestMethod]
    public async Task LateCancellationPreservesCompletedResult()
    {
        using var store = new PersistentTaskStore(directory);
        var task = await store.CreateTaskAsync();
        await store.SetCompletedAsync(task.TaskId, JsonSerializer.SerializeToElement(new { done = true }));
        Assert.IsFalse(await store.SetCancelledAsync(task.TaskId));
        Assert.AreEqual(McpTaskStatus.Completed, (await store.GetTaskAsync(task.TaskId))!.Status);
    }

    [TestMethod]
    public async Task ProtocolTaskRecordsCanBeReleasedAfterCompletion()
    {
        using var store = new PersistentTaskStore(directory);
        var task = await store.CreateTaskAsync();
        Assert.Throws<InvalidOperationException>(() => store.Release(task.TaskId));
        await store.SetCompletedAsync(task.TaskId, JsonSerializer.SerializeToElement(new { done = true }));
        store.Release(task.TaskId);
        Assert.IsNull(await store.GetTaskAsync(task.TaskId));
    }

    [TestMethod]
    public async Task BackgroundReceiptTracksCompletionAndKeepsActiveWork()
    {
        var journal = new InvocationJournal(directory);
        var finish = new TaskCompletionSource<ModelContextProtocol.Protocol.CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid operationId = Guid.NewGuid();
        await journal.RunAsync("background", "test", JsonSerializer.SerializeToElement(new { value = 1 }),
            () => Task.FromResult(ToolReply.Result(new { accepted = true, operationId })), _ => finish.Task);
        var row = JsonSerializer.SerializeToElement(journal.List()).GetProperty("entries")[0];
        string id = row.GetProperty("Id").GetString()!;
        Assert.Throws<InvalidOperationException>(() => journal.Forget(id, verified: true));
        finish.SetResult(ToolReply.Result(new { success = true }));
        for (int i = 0; i < 100; i++)
        {
            row = JsonSerializer.SerializeToElement(journal.List()).GetProperty("entries")[0];
            if (row.GetProperty("State").GetString() == "Completed") break;
            await Task.Delay(10);
        }
        Assert.AreEqual("Completed", row.GetProperty("State").GetString());
        journal.Forget(id);
    }
}
