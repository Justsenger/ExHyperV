using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ExHyperV.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

public sealed partial class ConsoleFanoutTests
{
    private const string BrowserPage = """
        <!doctype html><meta charset="utf-8"><canvas></canvas><script type="module">
        import { parseConsoleBinaryPacket, createConsoleRenderer } from '/fixture/wire.js';
        const who = location.pathname.split('/').pop();
        const report = (data) => fetch('/fixture/result/' + who, {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(data)});
        try {
          const canvas = document.querySelector('canvas');
          const idrs = new Set();
          const renderer = createConsoleRenderer(canvas, {
            onCodecError: () => report({error:'WebCodecs'}),
            onDisplayed: sequence => {
              const rgba = canvas.getContext('2d').getImageData(0,0,1,1).data;
              ws.send(JSON.stringify({type:'displayed', sequence}));
              report({sequence, idr:idrs.has(sequence), width:canvas.width, luma:rgba[0]});
            }
          });
          const ws = new WebSocket('ws://' + location.host + 'MEDIA_URL');
          ws.binaryType = 'arraybuffer';
          ws.onopen = () => ws.send(JSON.stringify({type:'captureFrame'}));
          ws.onmessage = e => {
            if (typeof e.data === 'string') return;
            const packet = parseConsoleBinaryPacket(e.data, 2);
            if (!packet) { report({error:'packet'}); return; }
            if (packet.kind === 'h264' && packet.isKeyFrame) idrs.add(packet.sequence);
            if (packet.kind === 'h264') renderer.enqueueVideo(packet);
          };
          ws.onerror = () => report({error:'socket'});
        } catch (e) { report({error:e.name}); }
        </script>
        """;

    [TestMethod]
    [TestCategory("Browser")]
    public async Task IndependentChromiumProfilesDecodeSharedIdrAndSurvivePeerRevocation()
    {
        string? browser = Environment.GetEnvironmentVariable("EXHYPERV_TEST_BROWSER");
        string? rendezvous = Environment.GetEnvironmentVariable("EXHYPERV_TEST_BROWSER_RENDEZVOUS");
        if (string.IsNullOrWhiteSpace(browser) && string.IsNullOrWhiteSpace(rendezvous))
            Assert.Inconclusive("设置 EXHYPERV_TEST_BROWSER 或浏览器测试协调文件后执行实机验收。");
        var fake = new FakeConsole();
        long sequence = 1;
        var pixels = Enumerable.Repeat((byte)160, 128 * 128 * 4).ToArray();
        fake.Capture = () => fake.DesktopFrame(128, 128, 512, pixels, Interlocked.Read(ref sequence), sequence - 1, new(0, 0, 128, 128), true);
        Func<int, Task<string>> issue = null!;
        Channel<JsonElement>[] results = [Channel.CreateUnbounded<JsonElement>(), Channel.CreateUnbounded<JsonElement>()];
        await using var host = await SocketHost.Start(["--read-only", "--toolsets", "console"], configure: s => s.AddSingleton<IConsoleSessionFactory>(fake), root: Root, map: app =>
        {
            app.MapGet("/fixture/view/{id:int}", async (int id) => Results.Content(BrowserPage.Replace("MEDIA_URL",
                $"/ws/v2/console/{vm}?backend=RdpBasic&frameEncoding=h264&ticket={await issue(id)}"), "text/html")).AllowAnonymous();
            app.MapGet("/fixture/wire.js", () => Results.File(Path.Combine(AppContext.BaseDirectory, "console-wire.js"), "text/javascript")).AllowAnonymous();
            app.MapPost("/fixture/result/{id:int}", (int id, JsonElement report) => { results[id].Writer.TryWrite(report); return Results.NoContent(); }).AllowAnonymous();
        });
        var a = await Login(host); var b = await Login(host);
        issue = id => Ticket(host, id == 0 ? a : b, "RdpBasic", "Watch");
        var profiles = new List<(Process Process, string Directory)>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(rendezvous == null ? 45 : 180));
        async Task<JsonElement> Frame(int id, long expected)
        {
            while (true)
            {
                var msg = await results[id].Reader.ReadAsync(deadline.Token);
                Assert.IsFalse(msg.TryGetProperty("error", out _), msg.ToString());
                if (msg.GetProperty("sequence").GetInt64() == expected) return msg;
            }
        }
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var url = new Uri(host.Http.BaseAddress!, "/fixture/view/" + i).ToString();
                if (rendezvous != null) await File.WriteAllTextAsync(rendezvous, url);
                else
                {
                    string profile = TestStorage.CreatePath();
                    var start = new ProcessStartInfo(browser!) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (string argument in new[] { "--headless", "--remote-debugging-port=0", "--no-first-run", "--disable-background-networking",
                        "--disable-background-timer-throttling", "--user-data-dir=" + profile, url })
                        start.ArgumentList.Add(argument);
                    profiles.Add((Process.Start(start)!, profile));
                }
                var rendering = Frame(i, sequence);
                if (rendezvous == null && await Task.WhenAny(rendering, profiles[i].Process.WaitForExitAsync(deadline.Token)) != rendering)
                    Assert.Fail($"浏览器在呈现前退出，退出码 {profiles[i].Process.ExitCode}。");
                var displayed = await rendering;
                Assert.IsTrue(displayed.GetProperty("idr").GetBoolean());
                Assert.AreEqual(128, displayed.GetProperty("width").GetInt32());
                Assert.IsGreaterThan(32, displayed.GetProperty("luma").GetInt32());
            }
            Assert.AreEqual(1, fake.Created);
            Assert.AreEqual(1, fake.FrameListeners);
            var auth = host.App.Services.GetRequiredService<Authentication>();
            var removed = auth.Subscribers(b.SessionId).Single();
            Assert.IsTrue(auth.Revoke(b.SessionId));
            long count = removed.DeliveredFrames;
            Interlocked.Increment(ref sequence);
            using (var frame = fake.Capture()) fake.Publish(frame);
            await Frame(0, sequence);
            await Task.Delay(ConsoleSocket.IdleLosslessCompensationMs + 50);
            Assert.AreEqual(count, removed.DeliveredFrames);
            Assert.IsTrue(auth.Revoke(a.SessionId));
            Assert.AreEqual(0, fake.LiveFrames);
        }
        finally
        {
            if (rendezvous != null) File.Delete(rendezvous);
            foreach (var (process, directory) in profiles)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
                TestStorage.Delete(directory);
            }
        }
    }
}
