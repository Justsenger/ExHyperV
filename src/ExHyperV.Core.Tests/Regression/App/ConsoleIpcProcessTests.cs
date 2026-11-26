using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using ExHyperV.App.UI.Pages.VirtualMachines.Console;
using ExHyperV.ConsoleIpc;
using ExHyperV.ConsoleIpc.Tests;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class ConsoleIpcProcessTests
{
    internal static async Task<int> RunPeer(Guid id, int parent)
    {
        try
        {
            using var service = new ConsoleIpcTestPeer(true, id, parent) { Gpu = true };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            ulong delivery = await service.Delivery.Task.WaitAsync(deadline.Token);
            using var texture = new ConsoleIpcTestTexture(64, 64);
            await service.AttachGpu(texture.Shared, delivery, texture.Adapter, 64, 64);
            await service.ReadyGpu(delivery, 1);
            await service.CpuRequested.Task.WaitAsync(deadline.Token);
            using var ring = MemoryMappedFile.CreateNew(null, 4096);
            using var view = ring.CreateViewAccessor();
            ConsoleIpcTestPeer.FullFrame(view, 8, 8, 8, 2, 51);
            await service.AttachRing(ring, 4096, 8);
            await service.ReadyCpu(8, 2);
            await service.SessionClosed.WaitAsync(deadline.Token);
            return 0;
        }
        catch (UnauthorizedAccessException) { return 5; }
        catch (Exception) { return 1; }
    }

    private sealed class Endpoint(Guid name, Process child) : ConsoleIpcDiscovery
    {
        internal override ConsoleIpcEndpoint Find() => Validate("ExHyperV.Test." + name.ToString("N"), null, "S-1-5-18", new string('0', 64), true);
        internal override void Verify(SafePipeHandle pipe, ConsoleIpcEndpoint endpoint, long openedAfter)
        {
            if (!GetNamedPipeServerProcessId(pipe, out uint pid) || pid != child.Id || child.HasExited)
                throw new UnauthorizedAccessException();
        }
        [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    }

    [TestMethod]
    public async Task ServiceProcessTransfersTextureAndReadOnlySectionToRenderer()
    {
        Guid id = Guid.NewGuid();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ExHyperV.Core.Tests.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--console-ipc-peer");
        start.ArgumentList.Add(id.ToString("N"));
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = new ConsoleIpcClient(new Endpoint(id, child), 1);
            IpcAccess access;
            while ((access = client.Inspect(id, "desktop", true)).Snapshot.Host.Code != ConsoleExtensionStateCode.Ready)
            {
                if (child.HasExited) Assert.Fail($"IPC service process exited with code {child.ExitCode}");
                await Task.Delay(100, deadline.Token);
            }
            await using var session = client.Session(new() { VmId = id }, access);
            using var delivery = session.OpenDelivery();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cpu = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            delivery.Available += () => ready.TrySetResult();
            session.FrameReceived += f => cpu.TrySetResult(f.CopyPixels());
            session.Start();
            await ready.Task.WaitAsync(deadline.Token);
            using var gpu = delivery.TakeLatest();
            Assert.IsNotNull(gpu);
            using var renderer = new ConsoleSwapChainRenderer(_ => 0, () => (1, 1), (_, _) => { });
            Assert.IsTrue(renderer.TryPresent(gpu, 64, 64));
            byte[] expected = new byte[64 * 64 * 4];
            new Random(35).NextBytes(expected);
            CollectionAssert.AreEqual(expected, DesktopConsoleGraphicsTests.Readback(renderer));
            Assert.AreEqual(8UL, delivery.RequestCpu());
            CollectionAssert.AreEqual(Enumerable.Repeat((byte)51, 256).ToArray(), await cpu.Task.WaitAsync(deadline.Token));
            await session.DisconnectAsync().WaitAsync(deadline.Token);
            await child.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }
}
