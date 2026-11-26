using System.Buffers.Binary;
using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExHyperV.Server.Hosting;
using ExHyperV.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class ConsoleMediaWorkflowTests
{
    private static Task<SocketHost> Start(FakeConsole console) =>
        SocketHost.Start(configure: services => services.AddSingleton<IConsoleSessionFactory>(console));

    private sealed class Desktop(int width, int height)
    {
        internal int Width => width;
        internal int Height => height;
        internal int Stride => width * 4 + 12;
        internal byte[] Pixels { get; } = Enumerable.Repeat((byte)0xcc, (width * 4 + 12) * height).ToArray();
        internal ConsoleFrameRegion Bounds => new(0, 0, width, height);
        internal void Paint(ConsoleFrameRegion region, int seed)
        {
            for (int y = region.Y; y < region.Y + region.Height; y++)
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                int i = y * Stride + x * 4;
                Pixels[i] = (byte)(x * 17 + seed);
                Pixels[i + 1] = (byte)(y * 31 - seed);
                Pixels[i + 2] = (byte)((x / 8 + y / 6) * 29 + seed);
                Pixels[i + 3] = 255;
            }
        }
        internal byte[] Compact() => Enumerable.Range(0, height)
            .SelectMany(y => Pixels.AsSpan(y * Stride, width * 4).ToArray()).ToArray();
    }

    private static byte[] DecodeVideo(byte[] packet, string encoding, int w, int h)
    {
        using var payload = new MemoryStream(packet, 34, packet.Length - 34);
        if (encoding == "bgra") return packet[34..];
        if (encoding == "deflate")
        {
            using var inflated = new MemoryStream();
            using var deflate = new DeflateStream(payload, CompressionMode.Decompress);
            deflate.CopyTo(inflated);
            return inflated.ToArray();
        }
        using var image = new Bitmap(payload);
        Assert.AreEqual(new Size(w, h), image.Size);
        byte[] result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan((y * w + x) * 4), image.GetPixel(x, y).ToArgb());
        return result;
    }

    [TestMethod]
    [DataRow("png")]
    [DataRow("deflate")]
    [DataRow("bgra")]
    public async Task DesktopReconstructsEveryPixelAcrossDamageResizeAndCapture(string encoding)
    {
        var console = new FakeConsole();
        await using var host = await Start(console);
        using var ws = await host.Connect($"console/{Guid.NewGuid()}?frameEncoding={encoding}");
        await VerifyDesktop(ws, console, encoding);
        await ws.Close();
        Assert.AreEqual(0, console.LiveFrames);
    }

    internal static async Task VerifyDesktop(WireClient ws, FakeConsole console, string encoding)
    {
        Assert.AreEqual("Connecting", (await ws.Json("state")).GetProperty("state").GetString());
        Assert.AreEqual("Connected", (await ws.Json("state")).GetProperty("state").GetString());
        Assert.AreEqual("LoginCompleted", (await ws.Json("state")).GetProperty("state").GetString());
        var desktop = new Desktop(32, 24);
        long sequence = 0;
        byte[] canvas = [];
        ConsoleFrame Next(ConsoleFrameRegion damage) => console.DesktopFrame(desktop.Width, desktop.Height, desktop.Stride,
            desktop.Pixels.ToArray(), ++sequence, sequence - 1, damage, false);
        void Update(ConsoleFrameRegion region)
        {
            desktop.Paint(region, (int)sequence + 1);
            using var frame = Next(region);
            console.Publish(frame);
        }
        async Task ReadFrame(ConsoleFrameRegion region, bool full)
        {
            byte[] packet = await ws.Binary();
            byte format = encoding switch { "bgra" => 0, "png" => 1, _ => 2 };
            CollectionAssert.AreEqual(new byte[] { 69, 88, 1, (byte)(full ? 1 : 0), format, 0 }, packet[..6]);
            Assert.AreEqual(sequence, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(6)));
            Assert.AreEqual(sequence - 1, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(14)));
            int[] header = Enumerable.Range(0, 6).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(22 + 2 * i))).ToArray();
            CollectionAssert.AreEqual(new[] { desktop.Width, desktop.Height, region.X, region.Y, region.Width, region.Height }, header);
            byte[] pixels = DecodeVideo(packet, encoding, header[4], header[5]);
            Assert.AreEqual(header[4] * header[5] * 4, pixels.Length);
            if (full) canvas = new byte[header[0] * header[1] * 4];
            for (int row = 0; row < header[5]; row++)
                pixels.AsSpan(row * header[4] * 4, header[4] * 4).CopyTo(canvas.AsSpan(((header[3] + row) * header[0] + header[2]) * 4));
            CollectionAssert.AreEqual(desktop.Compact(), canvas);
        }

        Update(desktop.Bounds);
        await ReadFrame(desktop.Bounds, true);
        foreach (var region in new ConsoleFrameRegion[] { new(0, 2, 9, 7), new(6, 4, 15, 9), new(29, 20, 3, 4) })
        {
            Update(region);
            await ReadFrame(region, false);
        }
        console.OnResize = (width, height) =>
        {
            desktop = new Desktop(width, height);
            desktop.Paint(desktop.Bounds, 5);
            Update(new(1, 0, 5, 2));
        };
        await ws.Send(new { type = "resize", width = 256, height = 200 });
        await ReadFrame(new(0, 0, 256, 200), true);
        // A capture can already see a newer partial Core frame. A recovering
        // browser must be able to rebuild even after dropping its whole canvas.
        console.Capture = () => Next(new(1, 0, 5, 2));
        Array.Clear(canvas);
        await ws.Send(new { type = "captureFrame" });
        await ReadFrame(desktop.Bounds, true);
    }

    private static byte[] Samples(int index, ConsoleAudioFormat format)
    {
        byte[] data = new byte[format.Channels * (format.SampleFormat == ConsoleAudioSampleFormat.Float32LittleEndian ? 4 : 2) * 32];
        for (int i = 0; i < 32 * format.Channels; i++)
        {
            if (format.SampleFormat == ConsoleAudioSampleFormat.Float32LittleEndian)
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), (index + i) / 128f);
            else BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(index * 509 + i));
        }
        return data;
    }

    [TestMethod]
    [DataRow(48000, 2, ConsoleAudioSampleFormat.Signed16LittleEndian)]
    [DataRow(44100, 1, ConsoleAudioSampleFormat.Float32LittleEndian)]
    public async Task AudioBurstDropsOldestAndContinuesStreaming(int rate, int channels, ConsoleAudioSampleFormat kind)
    {
        var console = new FakeConsole();
        var format = new ConsoleAudioFormat(rate, channels, kind);
        void Emit(int i) => console.Publish(new ConsoleAudioPacket(format, Samples(i, format), i, TimeSpan.FromTicks(i * 10000L)));
        // Start runs after binding the Core events and before the send pump starts.
        console.OnStart = () => { for (int i = 0; i < 20; i++) Emit(i); };
        await using var host = await Start(console);
        using var ws = await host.Connect($"console/{Guid.NewGuid()}");
        for (int i = 12; i <= 21; i++)
        {
            if (i >= 20) Emit(i);
            var packet = await ws.Binary();
            CollectionAssert.AreEqual(new byte[] { 69, 88, 2, 0, (byte)(kind == ConsoleAudioSampleFormat.Float32LittleEndian ? 2 : 1), (byte)channels }, packet[..6]);
            Assert.AreEqual(rate, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(6)));
            Assert.AreEqual(i * 10000L, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(10)));
            CollectionAssert.AreEqual(Samples(i, format), packet[18..]);
        }
        await ws.Close();
    }

    [TestMethod]
    [DataRow(8000, 1)]
    [DataRow(48000, 2)]
    [DataRow(192000, 8)]
    public async Task MicrophoneFollowsNegotiatedFormatAndAvailability(int rate, int channels)
    {
        var console = new FakeConsole { Format = new(rate, channels, ConsoleAudioSampleFormat.Signed16LittleEndian) };
        await using var host = await Start(console);
        using var ws = await host.Connect($"console/{Guid.NewGuid()}");
        await VerifyMicrophone(ws, console);
        await ws.Close();
    }

    internal static async Task VerifyMicrophone(WireClient ws, FakeConsole console)
    {
        var format = console.Format;
        var samples = Samples(3, format);
        byte[] packet = new byte[18 + samples.Length];
        new byte[] { 69, 88, 3, 0, 1, (byte)format.Channels }.CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6), format.SampleRate);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(10), 12345);
        samples.CopyTo(packet, 18);
        async Task Accepted()
        {
            await ws.Audio(packet);
            await ws.Send(new { type = "ping" });
            await ws.Json("pong");
            CollectionAssert.AreEqual(packet[18..], console.Microphone!);
        }
        await Accepted();
        async Task Switch(bool available)
        {
            console.SetMicrophoneAvailable(available);
            JsonElement caps;
            do { caps = await ws.Json("capabilities"); } while (caps.GetProperty("audioInputAvailable").GetBoolean() != available);
            var negotiated = caps.GetProperty("audioInputFormat");
            if (available) Assert.AreEqual(format.SampleRate, negotiated.GetProperty("sampleRate").GetInt32());
            else Assert.AreEqual(JsonValueKind.Null, negotiated.ValueKind);
        }
        await Switch(false);
        packet[18]++;
        await ws.Audio(packet);
        await ws.Json("error");
        CollectionAssert.AreEqual(samples, console.Microphone!);
        await Switch(true);
        await Accepted();
    }

    internal static async Task VerifyInputAndClipboard(WireClient ws, FakeConsole console)
    {
        ConsoleKeyEvent[] keys = [new(29, true, true), new(56, false, true), new(83, true, true),
            new(83, true, false), new(56, false, false), new(29, true, false)];
        await ws.Send(new { type = "keys", events = keys.Select(k => new { scanCode = k.ScanCode, extended = k.Extended, down = k.Down }) });
        await ws.Send(new { type = "syncKeyboard", flags = 6 });
        await ws.Send(new { type = "pointer", x = 21, y = 17 });
        await ws.Send(new { type = "click", x = 21, y = 17, button = "Right" });
        await ws.Send(new { type = "pointer", x = 21, y = 17, wheel = -120 });
        // A one-pixel 24-bit DIB, including the padded scan line.
        byte[] dib = new byte[44];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        dib[12] = 1; dib[14] = 24; dib[42] = 255;
        const string prefix = "<html><body><!--StartFragment-->", suffix = "<!--EndFragment--></body></html>";
        const string fragment = "<b>Guest</b>";
        const string template = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        int start = string.Format(template, 0, 0, 0, 0).Length;
        string html = string.Format(template, start, start + prefix.Length + fragment.Length + suffix.Length,
            start + prefix.Length, start + prefix.Length + fragment.Length) + prefix + fragment + suffix;
        ConsoleClipboardFormat[] formats = [new(13, null, Encoding.Unicode.GetBytes("来宾文本\0")), new(8, null, dib),
            new(0xc001, "HTML Format", Encoding.UTF8.GetBytes(html))];
        async Task ReadClipboard()
        {
            var actual = (await ws.Json("clipboard")).GetProperty("formats");
            Assert.AreEqual(3, actual.GetArrayLength());
            for (int i = 0; i < formats.Length; i++)
            {
                Assert.AreEqual(formats[i].Id, actual[i].GetProperty("id").GetUInt32());
                Assert.AreEqual(formats[i].Name, actual[i].GetProperty("name").GetString());
                CollectionAssert.AreEqual(formats[i].Data.ToArray(), actual[i].GetProperty("data").GetBytesFromBase64());
            }
        }
        console.Publish(new ConsoleClipboardData(formats));
        await ReadClipboard();
        await ws.Send(new { type = "clipboard", formats });
        await ReadClipboard();
        Assert.AreEqual(3, console.ClipboardData!.Formats.Count);
        CollectionAssert.AreEqual(keys, console.KeyBatch!);
        foreach (string input in new[] { "sync 6", "pointer 21 17 None False 0", "click 21 17 Right", "pointer 21 17 None False -120" })
            CollectionAssert.Contains(console.Inputs, input);
    }
}
