using System.Buffers.Binary;
using System.Drawing;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExHyperV.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class ConsoleSocketTests
{
    private static readonly string Path = "console/" + Guid.NewGuid();
    private static Task<SocketHost> Start(FakeConsole console, params string[] args) =>
        SocketHost.Start(args, services => services.AddSingleton<IConsoleSessionFactory>(console));

    [TestMethod]
    public async Task ConsoleDeniesPolicyAndInvalidParametersBeforeUpgrade()
    {
        var console = new FakeConsole();
        foreach (string[] args in new[] { new[] { "--read-only" }, ["--toolsets", "vm"] })
        {
            await using var restricted = await Start(console, args);
            await restricted.Reject(Path, HttpStatusCode.Forbidden);
        }
        await using var host = await Start(console);
        foreach (var path in new[] { "console/no-guid", "console/" + Guid.Empty, Path + "?backend=Rdp", Path + "?width=199",
            Path + "?width=8192&height=8192", Path + "?height=99999999999999", Path + "?desktopScale=501",
            Path + "?connectTimeoutMs=0", Path + "?connectTimeoutMs=120001", Path + "?frameEncoding=jpeg",
            Path + "?hostCursor=invalid", Path + "?clipboard=invalid", Path + "?audioOutput=invalid", Path + "?audioInput=invalid" })
            await host.Reject(path, HttpStatusCode.BadRequest);
        var slots = host.App.Services.GetRequiredService<ConsoleSlots>();
        for (int i = 0; i < 16; i++) Assert.IsTrue(slots.Available.Wait(0));
        try { await host.Reject(Path, HttpStatusCode.TooManyRequests); }
        finally { slots.Available.Release(16); }
        Assert.AreEqual(0, console.Created);
    }

    [TestMethod]
    public async Task PngFullFrameDamageAndSkippedBaseDecodeToExpectedPixels()
    {
        var console = new FakeConsole();
        await using var host = await Start(console);
        using var ws = await host.Connect(Path);
        await ws.Json("capabilities");
        Assert.AreEqual(1024, console.Options!.Width);
        Assert.AreEqual(TimeSpan.FromSeconds(2), console.Options.ConnectTimeout);
        foreach (var (sequence, expectedFull) in new[] { (1L, true), (2L, false), (5L, true) })
        {
            console.Emit(sequence, sequence == 1);
            var packet = await ws.Binary();
            CollectionAssert.AreEqual(new byte[] { 0x45, 0x58, 1, expectedFull ? (byte)1 : (byte)0, 1, 0 }, packet[..6]);
            Assert.AreEqual(sequence, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(6)));
            Assert.AreEqual(sequence - 1, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(14)));
            var dimensions = Enumerable.Range(0, 6).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(22 + 2 * i))).ToArray();
            CollectionAssert.AreEqual(expectedFull ? new[] { 2, 2, 0, 0, 2, 2 } : [2, 2, 1, 1, 1, 1], dimensions);
            using var stream = new MemoryStream(packet[34..]);
            using var image = new Bitmap(stream);
            Assert.AreEqual(dimensions[4], image.Width);
            Assert.AreEqual(dimensions[5], image.Height);
            Assert.AreEqual(Color.FromArgb(255, 70, 60, 50).ToArgb(), image.GetPixel(image.Width - 1, image.Height - 1).ToArgb());
        }
        await ws.Close();
        Assert.AreEqual(0, console.LiveFrames);
    }

    [TestMethod]
    public async Task BgraAndDeflateCaptureHaveCompactRows()
    {
        foreach (string encoding in new[] { "bgra", "deflate" })
        {
            var console = new FakeConsole();
            await using var host = await Start(console);
            using var ws = await host.Connect(Path + "?frameEncoding=" + encoding);
            await ws.Send(new { type = "captureFrame" });
            var packet = await ws.Binary();
            Assert.AreEqual(100L, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(6)));
            Assert.AreEqual(encoding == "bgra" ? 0 : 2, packet[4]);
            byte[] pixels = packet[34..];
            if (encoding == "deflate")
            {
                using var decoder = new DeflateStream(new MemoryStream(pixels), CompressionMode.Decompress);
                using var raw = new MemoryStream(); await decoder.CopyToAsync(raw); pixels = raw.ToArray();
            }
            CollectionAssert.AreEqual(FakeConsole.Pixels[..8].Concat(FakeConsole.Pixels[12..20]).ToArray(), pixels);
            await ws.Close();
            Assert.AreEqual(0, console.LiveFrames);
        }
    }

    [TestMethod]
    public async Task FrameBurstKeepsOnlyLatestLeaseAndHostStopDrainsConnection()
    {
        var console = new FakeConsole();
        console.OnStart = () =>
        {
            for (int i = 1; i <= 2000; i++) { console.Emit(i, false); Assert.AreEqual(1, console.LiveFrames); }
        };
        await using var host = await Start(console);
        using var ws = await host.Connect(Path + "?frameEncoding=bgra");
        var packet = await ws.Binary();
        Assert.AreEqual(2000L, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(6)));
        Assert.AreEqual(1, packet[3]);
        await host.App.StopAsync(ws.Timeout);
        Assert.AreEqual(0, console.LiveFrames);
        Assert.AreEqual(0, console.FrameListeners);
        Assert.AreEqual(1, console.Disconnections);
        Assert.AreEqual(1, console.Disposals);
        CollectionAssert.Contains(console.Inputs, "release");
    }

    [TestMethod]
    public async Task ConsoleStatesKeyboardPointerAndResizeReachCore()
    {
        var console = new FakeConsole();
        await using var host = await Start(console);
        using var ws = await host.Connect(Path + "?backend=RdpEnhanced&width=1280&height=720&desktopScale=125&connectTimeoutMs=6000");
        Assert.AreEqual("Connecting", (await ws.Json("state")).GetProperty("state").GetString());
        var caps = await ws.Json("capabilities");
        Assert.AreEqual("RdpEnhanced", caps.GetProperty("backendId").GetString());
        foreach (var field in new[] { "supportsResize", "clipboardAvailable", "audioOutputAvailable", "audioInputAvailable" })
            Assert.IsTrue(caps.GetProperty(field).GetBoolean());
        Assert.AreEqual("Connected", (await ws.Json("state")).GetProperty("state").GetString());
        Assert.AreEqual("LoginCompleted", (await ws.Json("state")).GetProperty("state").GetString());
        Assert.AreEqual(125u, console.Options!.DesktopScale);
        foreach (object cmd in new object[] { new { type = "key", scanCode = 34, extended = true, down = true },
            new { type = "keys", events = new[] { new { scanCode = 40, down = false } } }, new { type = "pause" },
            new { type = "syncKeyboard", flags = 3 }, new { type = "releaseAllInputs" },
            new { type = "pointer", x = 4, y = 5, wheel = -120 }, new { type = "click", x = 3, y = 7 },
            new { type = "resize", width = 1200, height = 900, desktopScale = 150 } }) await ws.Send(cmd);
        Assert.AreEqual(123, (await ws.Json("resizeRejected")).GetProperty("errorCode").GetInt32());
        CollectionAssert.AreEqual(new[] { "key 34 True True", "keys 1 40", "pause", "sync 3", "release",
            "pointer 4 5 None False -120", "click 3 7 Left", "resize 1200 900 150" }, console.Inputs);
        console.End();
        var state = await ws.Json("state");
        Assert.AreEqual("Disconnected", state.GetProperty("state").GetString());
        Assert.AreEqual(77, state.GetProperty("errorCode").GetInt32());
        await ws.Close();
    }

    [TestMethod]
    public async Task ClipboardRoundTripsAndRejectsPerFormatAndTotalLimits()
    {
        var console = new FakeConsole();
        await using var host = await Start(console);
        using var ws = await host.Connect(Path);
        byte[] data = [65, 0, 0, 0];
        await ws.Send(new { type = "clipboard", formats = new[] { new { id = 13, data } } });
        var message = await ws.Json("clipboard");
        CollectionAssert.AreEqual(data, message.GetProperty("formats")[0].GetProperty("data").GetBytesFromBase64());
        var original = console.ClipboardData;
        int limit = 16 * 1024 * 1024;
        await ws.Send(new { type = "clipboard", formats = new[] { new { id = 8, data = new byte[limit + 1] } } });
        await ws.Json("error");
        await ws.Send(new { type = "clipboard", formats = new[] { new { id = 8, data = new byte[limit] }, new { id = 13, data = new byte[limit] } } });
        await ws.Json("error");
        Assert.AreSame(original, console.ClipboardData);
        await ws.Close();
    }

    [TestMethod]
    public async Task AudioNegotiationAcceptsOnlyAlignedMatchingInput()
    {
        var console = new FakeConsole();
        await using var host = await Start(console);
        using var ws = await host.Connect(Path);
        var format = (await ws.Json("capabilities")).GetProperty("audioInputFormat");
        Assert.AreEqual(2, format.GetProperty("channels").GetInt32());
        console.Sound();
        byte[] packet = await ws.Binary();
        CollectionAssert.AreEqual(new byte[] { 69, 88, 2, 0, 1, 2 }, packet[..6]);
        Assert.AreEqual(48000, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(6)));
        Assert.AreEqual(123456L, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(10)));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, packet[18..]);
        await ws.Audio(packet); await ws.Json("error");
        packet[2] = 3;
        await ws.Audio(packet[..^1]); await ws.Json("error");
        packet[4] = 2; await ws.Audio(packet); await ws.Json("error");
        Assert.IsNull(console.Microphone);
        packet[4] = 1; await ws.Audio(packet); await ws.Send(new { type = "ping" }); await ws.Json("pong");
        CollectionAssert.AreEqual(packet[18..], console.Microphone!);
        await ws.Close();
        Assert.AreEqual(1, console.Disposals);
    }

    [TestMethod]
    public async Task HostCursorSeparatesPointerPngWithTransparencyAndCoalesces()
    {
        var defaultConsole = new FakeConsole();
        await using (var defaultHost = await Start(defaultConsole))
        {
            using var defaultWs = await defaultHost.Connect(Path);
            await defaultWs.Json("capabilities");
            Assert.IsFalse(defaultConsole.Options!.HostCursor);
            Assert.IsTrue(defaultConsole.Options!.RedirectClipboard);
            Assert.IsTrue(defaultConsole.Options!.RedirectAudioOutput);
            Assert.IsTrue(defaultConsole.Options!.RedirectAudioInput);
            Assert.AreEqual(0, defaultConsole.CursorListeners);
            await defaultWs.Close();
        }

        var disabledConsole = new FakeConsole();
        await using (var disabledHost = await Start(disabledConsole))
        {
            using var disabledWs = await disabledHost.Connect(Path + "?backend=RdpEnhanced&clipboard=false&audioOutput=0&audioInput=False");
            await disabledWs.Json("capabilities");
            Assert.IsFalse(disabledConsole.Options!.RedirectClipboard);
            Assert.IsFalse(disabledConsole.Options!.RedirectAudioOutput);
            Assert.IsFalse(disabledConsole.Options!.RedirectAudioInput);
            await disabledWs.Close();
        }

        var console = new FakeConsole();
        console.OnStart = () =>
        {
            console.PublishCursor(new(1, 1, 0, 0, [1, 2, 3, 255]));
            console.PublishCursor(new(2, 2, 1, 0,
            [
                0, 0, 0, 0, 10, 20, 30, 255,
                40, 50, 60, 128, 70, 80, 90, 255
            ]));
        };
        await using var host = await Start(console);
        using var ws = await host.Connect(Path + "?hostCursor=true");
        await ws.Json("capabilities");
        Assert.IsTrue(console.Options!.HostCursor);
        Assert.AreEqual(1, console.CursorListeners);
        var shown = await ws.Json("cursor");
        Assert.IsTrue(shown.GetProperty("visible").GetBoolean());
        Assert.AreEqual(2, shown.GetProperty("width").GetInt32());
        Assert.AreEqual(2, shown.GetProperty("height").GetInt32());
        Assert.AreEqual(1, shown.GetProperty("hotX").GetInt32());
        Assert.AreEqual(0, shown.GetProperty("hotY").GetInt32());
        using (var stream = new MemoryStream(shown.GetProperty("png").GetBytesFromBase64()))
        using (var image = new Bitmap(stream))
        {
            Assert.AreEqual(0, image.GetPixel(0, 0).A);
            Assert.AreEqual(Color.FromArgb(255, 30, 20, 10).ToArgb(), image.GetPixel(1, 0).ToArgb());
        }

        console.PublishCursor(null);
        var hidden = await ws.Json("cursor");
        Assert.IsFalse(hidden.GetProperty("visible").GetBoolean());
        await ws.Close();
        Assert.AreEqual(0, console.CursorListeners);
    }

    [TestMethod]
    public void ConsoleWireH264PacketFramingMatchesContract()
    {
        byte[] annexB = [0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0xE0, 0x1F, 0x00, 0x00, 0x00, 0x01, 0x65, 0x88];
        using var owned = ConsoleWire.H264(annexB, 42L, 123_456L, 1920, 1080, isKeyFrame: true, isHardware: true, durationUs: 16_667);
        byte[] packet = owned.Data.ToArray();
        Assert.AreEqual(34 + annexB.Length, packet.Length);
        CollectionAssert.AreEqual(new byte[] { 0x45, 0x58, 3, 3, 0, 0 }, packet[..6]);
        Assert.AreEqual(42L, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(6)));
        Assert.AreEqual(123_456L, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(14)));
        Assert.AreEqual((ushort)1920, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(22)));
        Assert.AreEqual((ushort)1080, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(24)));
        Assert.AreEqual(16_667u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(26)));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(30)));
        CollectionAssert.AreEqual(annexB, packet[34..]);
    }

    [TestMethod]
    public async Task ServerInfoAndConsoleCapabilitiesAdvertiseSupportedEncodings()
    {
        var console = new FakeConsole();
        await using var host = await Start(console);
        var info = await host.Http.GetFromJsonAsync<JsonElement>("server/info");
        var infoEncodings = info.GetProperty("supportedConsoleEncodings").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.Contains(infoEncodings, "bgra");
        CollectionAssert.Contains(infoEncodings, "deflate");
        CollectionAssert.Contains(infoEncodings, "png");
        Assert.AreEqual(ConsoleH264Encoder.IsAvailable, infoEncodings.Contains("h264"));
        Assert.AreEqual(ConsoleH264Encoder.HardwareAvailable, info.GetProperty("h264HardwareAvailable").GetBoolean());

        using var ws = await host.Connect(Path + "?frameEncoding=bgra");
        var caps = await ws.Json("capabilities");
        var wsEncodings = caps.GetProperty("supportedEncodings").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.AreEqual(infoEncodings, wsEncodings);
        Assert.AreEqual("bgra", caps.GetProperty("activeEncoding").GetString());
        await ws.Close();
    }

    [TestMethod]
    public async Task ConsoleH264StreamsAnnexBAndCompensatesStationaryFramesWithLosslessBgra()
    {
        Assert.IsNull(ConsoleH264Encoder.TryCreate(32, 24));
        Assert.IsNull(ConsoleH264Encoder.TryCreate(127, 128));

        const int width = 128, height = 128, stride = width * 4;
        byte[] MakeFramePixels(int seed)
        {
            byte[] buf = new byte[stride * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                buf[i] = (byte)((x * 3 + seed) & 0xFF);
                buf[i + 1] = (byte)((y * 5 + seed) & 0xFF);
                buf[i + 2] = (byte)(((x + y) * 2 + seed) & 0xFF);
                buf[i + 3] = 255;
            }
            return buf;
        }

        if (ConsoleH264Encoder.IsAvailable)
        {
            using var directEncoder = ConsoleH264Encoder.TryCreate(width, height);
            Assert.IsNotNull(directEncoder);
            Assert.IsTrue(directEncoder.Encode(MakeFramePixels(1), stride, 0, true, out var idr, out var isKey));
            Assert.IsTrue(isKey);
            Assert.IsGreaterThan(8, idr.Length);
            Assert.IsTrue((idr[0] == 0 && idr[1] == 0 && idr[2] == 1) || (idr[0] == 0 && idr[1] == 0 && idr[2] == 0 && idr[3] == 1));
            Assert.IsTrue(directEncoder.Encode(MakeFramePixels(20), stride, 16_667, false, out var delta, out _));
            Assert.IsGreaterThan(0, delta.Length);
        }

        var console = new FakeConsole();
        await using var host = await Start(console);
        using var ws = await host.Connect(Path + "?frameEncoding=deflate");
        var caps = await ws.Json("capabilities");
        Assert.AreEqual("deflate", caps.GetProperty("activeEncoding").GetString());

        await ws.Send(new { type = "setEncoding", encoding = "jpeg" });
        await ws.Json("error");

        await ws.Send(new { type = "setEncoding", encoding = "h264" });
        var changed = await ws.Json("encodingChanged");
        string expectedActive = ConsoleH264Encoder.IsAvailable ? "h264" : "deflate";
        Assert.AreEqual(expectedActive, changed.GetProperty("encoding").GetString());

        byte[] p1 = MakeFramePixels(10);
        console.Capture = () => console.DesktopFrame(width, height, stride, p1, 1, 0, new(0, 0, width, height), true);
        using (var f1 = console.DesktopFrame(width, height, stride, p1, 1, 0, new(0, 0, width, height), true))
            console.Publish(f1);

        var firstPacket = await ws.Binary();
        if (ConsoleH264Encoder.IsAvailable)
        {
            Assert.AreEqual(3, firstPacket[2]);
            Assert.AreEqual(1, firstPacket[3] & 1);
            Assert.AreEqual(1L, BinaryPrimitives.ReadInt64LittleEndian(firstPacket.AsSpan(6)));

            // Wait for 300ms idle lossless full refresh on initial full frame.
            var idleFull = await ws.Binary();
            CollectionAssert.AreEqual(new byte[] { 0x45, 0x58, 1, 1, 0, 0 }, idleFull[..6]);

            // Now emit a small dirty region update and verify dirty lossless BGRA compensation after 300ms idle.
            byte[] p2 = MakeFramePixels(99);
            var dirty = new ExHyperV.Services.ConsoleFrameRegion(16, 20, 32, 24);
            console.Capture = () => console.DesktopFrame(width, height, stride, p2, 2, 1, dirty, false);
            using (var f2 = console.DesktopFrame(width, height, stride, p2, 2, 1, dirty, false))
                console.Publish(f2);

            var secondH264 = await ws.Binary();
            Assert.AreEqual(3, secondH264[2]);
            Assert.AreEqual(2L, BinaryPrimitives.ReadInt64LittleEndian(secondH264.AsSpan(6)));

            var idleDirty = await ws.Binary();
            CollectionAssert.AreEqual(new byte[] { 0x45, 0x58, 1, 0, 0, 0 }, idleDirty[..6]);
            Assert.AreEqual((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(idleDirty.AsSpan(26)));
            Assert.AreEqual((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(idleDirty.AsSpan(28)));
            Assert.AreEqual((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(idleDirty.AsSpan(30)));
            Assert.AreEqual((ushort)24, BinaryPrimitives.ReadUInt16LittleEndian(idleDirty.AsSpan(32)));
            Assert.AreEqual(34 + 32 * 24 * 4, idleDirty.Length);
        }
        else
        {
            CollectionAssert.AreEqual(new byte[] { 0x45, 0x58, 1, 1, 2, 0 }, firstPacket[..6]);
        }

        await ws.Send(new { type = "setEncoding", encoding = "bgra" });
        var toBgra = await ws.Json("encodingChanged");
        Assert.AreEqual("bgra", toBgra.GetProperty("encoding").GetString());
        await ws.Close();
        Assert.AreEqual(0, console.LiveFrames);
    }
}
