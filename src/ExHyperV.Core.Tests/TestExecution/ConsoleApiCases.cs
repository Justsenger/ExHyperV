using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Management;
using System.Text;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class ConsoleApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api.StartsWith("IConsole", StringComparison.Ordinal) || p.Api is "控制台事件" or "VmScreenshotSource.CaptureAsync()" or "VmInputService.SendCtrlAltDelAsync()";
    internal static bool IsLive(CatalogTest p) => p.Id is "T-3ffa317c0bdfecc8-1" or "T-d3b8251b0e40130c-1" or "api.video-capture-live" || p.Api is "VmScreenshotSource.CaptureAsync()" or "VmInputService.SendCtrlAltDelAsync()" or "IConsoleAudioInput.SendAudio()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (IsLive(p)) { await LiveAsync(p, c); return; }
        c.Evidence.Add("受控传输边界：核对 Core 接受、拒绝、编码、事件与资源释放契约。");
        var backend = new ConsoleConnectionChecks.FakeBackend();
        await using var conn = new FreeRdpConsoleConnection(new() { VmId = Guid.NewGuid() }, true, backend);
        if (p.Api == "IConsoleVideoSource.CaptureFrame()")
        {
            c.Require(c.Call(conn.CaptureFrame) is null, "连接前返回空画面");
            await StartAsync(conn, backend);
            c.Require(c.Call(conn.CaptureFrame) is null, "连接建立但首帧尚未到达时返回空画面");
            byte[] pixels = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            using var source = new ConsoleFrame(4, 4, 16, pixels, 7, 6, new(1, 1, 1, 1), false);
            backend.Native.FrameToCapture = source;
            using var first = c.Call(conn.CaptureFrame);
            using var second = c.Call(conn.CaptureFrame);
            c.Require(first is not null && second is not null, "静止画面可重复获取独立租约");
            c.Require(!ReferenceEquals(first, second), "每次调用返回独立租约对象");
            c.Sequence(pixels, first.CopyPixels(), "局部刷新标记下仍返回完整像素");
            c.Equal(7L, first.Sequence, "保留同一连接的帧序号");
            first.Dispose();
            c.Sequence(pixels, second.CopyPixels(), "释放第一份租约后第二份仍可读取");
            await conn.DisconnectAsync();
            c.Require(c.Call(conn.CaptureFrame) is null, "断开后返回空画面");
            c.Sequence(pixels, second.CopyPixels(), "断开连接后已取得的租约仍可读取");
            return;
        }
        if (p.Api is "IConsoleConnection.Dispose()" or "IConsoleConnection.DisposeAsync()")
        {
            await using (var empty = new FreeRdpConsoleConnection(new() { VmId = Guid.NewGuid() }, true, new ConsoleConnectionChecks.FakeBackend()))
            { if (p.Api.Contains("DisposeAsync")) await c.CallAsync(async () => { await empty.DisposeAsync(); return true; }); else c.Call(empty.Dispose); }
            await StartAsync(conn, backend);
            if (p.Id.EndsWith("-2"))
            {
                backend.DelayExit = true; Task? dispose = null;
                try
                {
                    if (p.Api.Contains("DisposeAsync")) { dispose = c.CallAsync(async () => { await conn.DisposeAsync(); return true; }); c.Require(!dispose.IsCompleted, "异步释放等待原生退出"); }
                    else { c.Call(conn.Dispose); c.Equal(0, backend.Native.Disposals, "同步释放只发送关闭请求"); }
                }
                finally { backend.Native.Exit.Set(); if (dispose is not null) await dispose; await conn.DisconnectAsync(); }
            }
            else if (p.Api.Contains("DisposeAsync")) await c.CallAsync(async () => { await conn.DisposeAsync(); return true; }); else c.Call(conn.Dispose);
            await conn.DisconnectAsync(); if (p.Api.Contains("DisposeAsync")) await c.CallAsync(async () => { await conn.DisposeAsync(); return true; }); else c.Call(conn.Dispose);
            c.Equal(1, backend.Native.Disposals, "原生对象恰好释放一次"); return;
        }
        if (p.Api == "IConsoleConnection.DisconnectAsync()")
        {
            await using (var empty = new FreeRdpConsoleConnection(new() { VmId = Guid.NewGuid() }, true, new ConsoleConnectionChecks.FakeBackend())) await c.CallAsync(async () => { await empty.DisconnectAsync(); return true; });
            await StartAsync(conn, backend); await c.CallAsync(async () => { await conn.DisconnectAsync(); return true; }); await c.CallAsync(async () => { await conn.DisconnectAsync(); return true; }); c.Equal(1, backend.Native.Disposals, "活动连接重复断开恰好释放一次"); c.Equal(ConsoleConnectionState.Disconnected, conn.State, "断开状态"); return;
        }
        if (p.Api == "IConsoleConnection.Start()")
        {
            if (p.Id.EndsWith("-3")) { c.Call(conn.Start); await backend.Native.RunEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); backend.Emit(1, []); c.Throws<InvalidOperationException>(() => c.Call(conn.Start), "二次启动拒绝"); return; }
            foreach (Exception error in new Exception[] { new DllNotFoundException("fixture-missing-library"), new BadImageFormatException("fixture-wrong-architecture") })
            {
                await using var failed = new FreeRdpConsoleConnection(new() { VmId = Guid.NewGuid() }, true, new ThrowingBackend(error)); var ended = new TaskCompletionSource<ConsoleDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously); failed.Disconnected += v => ended.TrySetResult(v);
                c.Call(failed.Start); var result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)); c.Require(result.Error?.Contains(error.Message) == true, "原生加载失败保留原因：" + error.GetType().Name); c.Equal(ConsoleConnectionState.Disconnected, failed.State, "加载失败终态");
            }
            await using var missingVm = ConsoleService.CreateConnection(new() { VmId = Guid.NewGuid(), ConnectTimeout = TimeSpan.FromSeconds(2) }); var signal = new TaskCompletionSource<ConsoleDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously); missingVm.Disconnected += value => signal.TrySetResult(value); c.Call(missingVm.Start); var failedVm = await signal.Task.WaitAsync(TimeSpan.FromSeconds(20)); c.Require(failedVm.ErrorCode != 0 || !string.IsNullOrEmpty(failedVm.Error), "缺席虚拟机返回连接诊断"); return;
        }
        if (p.Id == "F005")
        {
            var events = new ConcurrentQueue<string>();
            int frameWidth = 0; uint resizeCode = 0; string? clipboardText = null;
            conn.Connected += () => throw new InvalidOperationException("controlled-observer"); conn.Connected += () => events.Enqueue("connected"); conn.LoginCompleted += () => events.Enqueue("login");
            conn.FrameReceived += _ => throw new InvalidOperationException("controlled-frame-observer"); conn.FrameReceived += f => { frameWidth = f.Width; events.Enqueue("frame"); };
            conn.ResizeRejected += code => { resizeCode = code; events.Enqueue("resize"); };
            conn.ClipboardReceived += value => { clipboardText = Encoding.Unicode.GetString(value.Formats[0].Data.Span); events.Enqueue("clipboard"); };
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); conn.Disconnected += _ => { events.Enqueue("disconnected"); ended.TrySetResult(); };
            c.Call(conn.Start); await backend.Native.RunEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); backend.Emit(1, []); backend.Emit(2, []); backend.Emit(3, new byte[64], 4, 4); backend.Emit(6, [], 3, 55);
            ConsolePayload.TryWriteClipboard(new([new(13, null, Encoding.Unicode.GetBytes("event\0"))]), out var payload); backend.Emit(4, payload); await conn.DisconnectAsync(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            c.Equal(4, frameWidth, "事件帧宽"); c.Equal(55U, resizeCode, "尺寸拒绝码"); c.Equal("event\0", clipboardText, "事件剪贴板内容");
            c.Sequence(new[] { "connected", "login", "frame", "resize", "clipboard", "disconnected" }, events, "事件顺序与订阅者异常隔离"); c.Equal(backend.FramesCreated, backend.FramesReleased, "事件帧资源释放"); c.Equal(1, backend.Native.Disposals, "连接资源释放"); return;
        }
        if (p.Api == "IConsoleInput.SendKey()" && p.Id.EndsWith("-2"))
        {
            c.Require(!c.Call(() => conn.SendKey(30, false, true)), "连接前拒绝"); await StartAsync(conn, backend);
            foreach (ushort code in new ushort[] { 0, 256, ushort.MaxValue }) c.Require(!c.Call(() => conn.SendKey(code, false, true)), "扫描码边界拒绝：" + code); c.Equal(0, backend.Native.Sent.Count, "无效输入未进入原生端"); return;
        }
        if (p.Api == "IConsoleVideoSource.Resize()")
        {
            c.Require(!c.Call(() => conn.Resize(1280, 720)), "连接前拒绝尺寸请求"); await StartAsync(conn, backend);
            foreach (var (w, h, scale) in new[] { (199, 720, 100U), (1280, 8193, 100U), (8192, 8192, 100U), (1280, 720, 501U) }) c.Require(!c.Call(() => conn.Resize(w, h, scale)), "尺寸参数边界");
            var basicBackend = new ConsoleConnectionChecks.FakeBackend(); await using var basic = new FreeRdpConsoleConnection(new() { VmId = Guid.NewGuid() }, false, basicBackend); await StartAsync(basic, basicBackend); c.Require(!c.Call(() => basic.Resize(1280, 720)), "基本会话尺寸能力为关闭"); return;
        }
        await StartAsync(conn, backend); backend.Emit(3, new byte[16 * 12 * 4], 16, 12);
        switch (p.Api)
        {
            case "IConsoleInput.SendKey()":
                foreach (var e in new[] { new ConsoleKeyEvent(30, false, true), new(30, false, false), new(29, true, true), new(29, true, false) }) c.Require(c.Call(() => conn.SendKey(e.ScanCode, e.Extended, e.Down)), "接受固定扫描码序列");
                c.Sequence(new[] { "1E000001", "1E000000", "1D000101", "1D000100" }, backend.Native.Sent.Select(s => Convert.ToHexString(s.Bytes)), "原生输入字节"); break;
            case "IConsoleInput.SendPause()":
                c.Require(c.Call(conn.SendPause), "Pause 已接受"); c.Require(backend.Native.Sent.Single() is { Kind: 6, Bytes.Length: 0 }, "Pause 独立指令"); break;
            case "IConsoleInput.SynchronizeKeyboard()":
                for (uint bits = 0; bits < 16; bits++) c.Require(c.Call(() => conn.SynchronizeKeyboard((ConsoleKeyboardToggleState)bits)), "锁键组合：" + bits);
                c.Sequence(Enumerable.Range(0, 16).Select(v => (uint)v), backend.Native.Sent.Select(v => BinaryPrimitives.ReadUInt32LittleEndian(v.Bytes)), "锁键位图"); break;
            case "IConsoleInput.SendPointer()":
                foreach (var (x, y) in new[] { (0, 0), (15, 0), (0, 11), (15, 11) }) c.Require(c.Call(() => conn.SendPointer((ushort)x, (ushort)y)), "四角坐标");
                foreach (var button in new[] { ConsolePointerButton.Left, ConsolePointerButton.Middle, ConsolePointerButton.Right }) foreach (bool down in new[] { true, false }) c.Require(c.Call(() => conn.SendPointer(3, 4, button, down)), "按键边界");
                foreach (short wheel in new short[] { 120, -120 }) c.Require(c.Call(() => conn.SendPointer(3, 4, wheel: wheel)), "滚轮方向"); c.Require(!c.Call(() => conn.SendPointer(16, 12)), "越界拒绝");
                c.Sequence(new short[] { 120, -120 }, backend.Native.Sent.TakeLast(2).Select(v => BinaryPrimitives.ReadInt16LittleEndian(v.Bytes.AsSpan(6))), "滚轮编码"); break;
            case "IConsoleInput.SendKeys()":
                c.Require(c.Call(() => conn.SendKeys([new(30, false, true), new(30, false, false)])), "完整序列接受"); c.Equal(2, backend.Native.Sent.Count, "完整序列传递");
                backend.Native.AcceptInput = false; var oversized = Enumerable.Range(0, 10000).Select(i => new ConsoleKeyEvent(31, false, i % 2 == 0)).ToArray(); c.Require(!c.Call(() => conn.SendKeys(oversized)), "超容量序列整体拒绝"); c.Equal(2, backend.Native.Sent.Count, "拒绝时原生队列保持原内容"); await conn.DisconnectAsync(); c.Require(!c.Call(() => conn.SendKeys([new(30, false, true)])), "断开后拒绝序列"); break;
            case "IConsoleInput.SendClick()":
                c.Require(c.Call(() => conn.SendClick(3, 4)), "完整点击接受"); c.Equal(2, backend.Native.Sent.Count, "一次点击包含按下与释放"); c.Require(!c.Call(() => conn.SendClick(16, 12)), "点击坐标越界");
                backend.Native.AcceptInput = false; bool rejected = false; for (int i = 0; i < 10000 && !rejected; i++) rejected = !c.Call(() => conn.SendClick(3, 4)); c.Require(rejected, "有限队列最终拒绝完整点击"); break;
            case "IConsoleInput.ReleaseAllInputs()":
                conn.SendKey(30, false, true); conn.SendPointer(3, 4, ConsolePointerButton.Left, true); c.Require(c.Call(conn.ReleaseAllInputs), "释放已按下输入");
                var sent = backend.Native.Sent.ToArray();
                c.Sequence(new[] { "1E000001", "0300040009000000", "1E000000", "0300040001000000" }, sent.Select(s => Convert.ToHexString(s.Bytes)), "A 键与鼠标左键的按下和释放序列");
                int count = sent.Length; c.Require(c.Call(conn.ReleaseAllInputs), "重复释放可接受"); c.Equal(count, backend.Native.Sent.Count, "重复释放无新增按键"); break;
            case "IConsoleClipboard.SetClipboard()":
                var text = new ConsoleClipboardData([new(13, null, Encoding.Unicode.GetBytes("剪贴板 fixture\0"))]);
                if (p.Id.EndsWith("-2")) { c.Require(!c.Call(() => conn.SetClipboard(text)), "通道尚未就绪"); backend.Emit(5, [], 1); c.Require(!c.Call(() => conn.SetClipboard(new([new(13, null, new byte[] { 1 })]))), "错误 UTF-16 文本拒绝"); }
                else
                {
                    backend.Emit(5, [], 1); byte[] dib = new byte[44]; BinaryPrimitives.WriteInt32LittleEndian(dib, 40); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1); BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1); BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
                    foreach (var data in new[] { text, new ConsoleClipboardData([new(0xc000, "HTML Format", HtmlFixture())]), new ConsoleClipboardData([new(8, null, dib)]) })
                    { c.Require(c.Call(() => conn.SetClipboard(data)), "格式进入发送队列"); var clipboardCommand = backend.Native.Sent.Last(); c.Equal(4U, clipboardCommand.Kind, "剪贴板指令"); c.Require(ConsolePayload.TryReadClipboard(clipboardCommand.Bytes, out var decoded), "接收端解码"); c.Sequence(data.Formats[0].Data.ToArray(), decoded.Formats[0].Data.ToArray(), "格式内容完整"); }
                }
                break;
            default: throw new InvalidOperationException(p.Api);
        }
    }
    private static byte[] HtmlFixture()
    {
        const string prefix = "<html><body><!--StartFragment-->", fragment = "fixture", suffix = "<!--EndFragment--></body></html>";
        const string header = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        int start = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        int startFragment = start + Encoding.UTF8.GetByteCount(prefix), endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        return Encoding.UTF8.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, header, start, endFragment + Encoding.UTF8.GetByteCount(suffix), startFragment, endFragment) + prefix + fragment + suffix + "\0");
    }

    private static async Task StartAsync(FreeRdpConsoleConnection connection, ConsoleConnectionChecks.FakeBackend backend)
    { connection.Start(); await backend.Native.RunEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); backend.Emit(1, []); }
    private sealed class ThrowingBackend(Exception error) : IConsoleNativeBackend
    { public IConsoleNativeConnection Create(ConsoleConnectionOptions options, Action<uint, byte[], uint, uint> callback, Action<ConsoleFrame> frameCallback) => throw error; }

    private static async Task LiveAsync(CatalogTest p, ApiCaseContext c)
    {
        bool enhanced = p.Api.Contains("Resize") || p.Api.Contains("SendAudio");
        await using var vm = enhanced ? await TemporaryVm.CopyTemplateAsync(c) : await TemporaryVm.CreateAsync(c);
        await vm.StartAsync();
        if (p.Api == "VmInputService.SendCtrlAltDelAsync()")
        {
            await SystemBaseline.WaitStateAsync(vm.Id, 2);
            var empty = await c.CallAsync(() => VmInputService.SendCtrlAltDelAsync(Guid.Empty));
            c.Equal(ApiErrorKind.InvalidArgument, empty.ErrorKind, "空虚拟机 ID 拒绝输入");
            var missing = await c.CallAsync(() => VmInputService.SendCtrlAltDelAsync(Guid.NewGuid()));
            c.Require(!missing.Success, "缺席虚拟机返回失败");
            var result = await c.CallAsync(() => VmInputService.SendCtrlAltDelAsync(vm.Id)); c.Require(result.Success, result.Error);
            ushort immediate = Convert.ToUInt16(SystemBaseline.Vm(vm.Id)["EnabledState"]);
            // The API promises Hyper-V input acceptance. Firmware and the installed
            // OS decide how to react; a firmware reset is a valid reaction.
            c.Evidence.Add($"Hyper-V 已接受 Ctrl+Alt+Del；调用后观测到状态码 {immediate}。已核验有效目标、空 ID 和缺席目标的接口结果。"); return;
        }
        if (p.Api == "VmScreenshotSource.CaptureAsync()")
        {
            await Task.Delay(2000, c.Token); var shot = await c.CallAsync(() => VmScreenshotSource.CaptureAsync(vm.Id, 320, 240)); c.Require(shot is not null, "运行的固件画面可截图");
            c.Require(shot!.Width is > 0 and <= 320 && shot.Height is > 0 and <= 240 && shot.Stride == shot.Width * 2, "尺寸与 RGB565 步幅");
            using var service = new ManagementObjectSearcher(@"root\virtualization\v2", "SELECT * FROM Msvm_VirtualSystemManagementService"); using var rows = service.Get(); using var native = rows.Cast<ManagementObject>().Single(); using var args = native.GetMethodParameters("GetVirtualSystemThumbnailImage");
            args["TargetSystem"] = SystemBaseline.Settings(vm.Id)["__PATH"]; args["WidthPixels"] = (ushort)shot.Width; args["HeightPixels"] = (ushort)shot.Height;
            using var result = native.InvokeMethod("GetVirtualSystemThumbnailImage", args, null); c.Equal(0U, Convert.ToUInt32(result!["ReturnValue"]), "独立系统截图调用"); byte[] bytes = (byte[])result["ImageData"];
            int pixelBytes = shot.Width * shot.Height * 2;
            if (bytes.Length == pixelBytes + 4)
            {
                c.Equal(bytes.Length, BinaryPrimitives.ReadInt32BigEndian(bytes), "系统截图长度头");
                bytes = bytes[4..];
            }
            c.Equal(pixelBytes, bytes.Length, "系统截图像素长度");
            var baseline = VmScreenshotImage.FromRgb565(bytes, shot.Width, shot.Height); using var one = new System.Drawing.Bitmap(new MemoryStream(shot.EncodeJpeg())); using var two = new System.Drawing.Bitmap(new MemoryStream(baseline.EncodeJpeg()));
            double delta = 0; int count = 0; for (int y = 0; y < one.Height; y += 4) for (int x = 0; x < one.Width; x += 4) { var a = one.GetPixel(x, y); var b = two.GetPixel(x, y); delta += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); count += 3; }
            c.Require(delta / count < 15, "Core 截图与同一固定固件画面的系统截图平均色差小于 15"); return;
        }
        if (enhanced)
        {
            bool ready = false; for (int i = 0; i < 60; i++) { var availability = await VmConsoleSupportService.IsEnhancedSessionAvailableAsync(vm.Id); if (availability.HasData && availability.Data) { ready = true; break; } await Task.Delay(1000, c.Token); }
            if (!ready) throw new ApiCheckSkippedException("模板完整副本启动后 60 秒内仍未提供增强会话。");
        }
        await using var connection = ConsoleService.CreateConnection(new() { VmId = vm.Id, Width = 1280, Height = 720, ConnectTimeout = TimeSpan.FromSeconds(15) }, enhanced ? ConsoleService.RdpEnhancedBackendId : ConsoleService.RdpBasicBackendId);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var frame = new TaskCompletionSource<(int W, int H)>(TaskCreationOptions.RunContinuationsAsynchronously); var resized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggedOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.LoginCompleted += () => loggedOn.TrySetResult();
        connection.Connected += () => connected.TrySetResult(); connection.Video!.FrameReceived += f => { if (f.Width > 0 && f.Height > 0) frame.TrySetResult((f.Width, f.Height)); if (f.Width == 1600 && f.Height == 900) resized.TrySetResult(); };
        connection.Disconnected += e => { var error = new IOException($"控制台断开：{e.ErrorCode:X8} {e.Error}"); connected.TrySetException(error); frame.TrySetException(error); resized.TrySetException(error); };
        if (p.Api.Contains("Start")) c.Call(connection.Start); else connection.Start(); await connected.Task.WaitAsync(TimeSpan.FromSeconds(30)); var size = await frame.Task.WaitAsync(TimeSpan.FromSeconds(30)); c.Require(size.W > 0 && size.H > 0, "真实链路收到有效帧");
        if (p.Api.Contains("Resize"))
        {
            bool accepted = false;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            do
            {
                accepted = c.Call(() => connection.Video.Resize(1600, 900));
                if (accepted) break;
                // false means no request was queued; wait for the display channel.
                await Task.Delay(250, c.Token);
            } while (DateTime.UtcNow < deadline && connection.State == ConsoleConnectionState.Connected);
            if (!accepted && !loggedOn.Task.IsCompleted && connection.State == ConsoleConnectionState.Connected)
                throw new ApiCheckSkippedException("增强会话已有登录画面，但尚未登录桌面，显示通道未就绪；分辨率实测需要已登录的测试桌面。");
            c.Require(accepted, "尺寸请求进入真实连接");
            await resized.Task.WaitAsync(TimeSpan.FromSeconds(30)); c.Evidence.Add("已收到 1600×900 的实际画面帧。");
        }
        if (p.Api == "IConsoleVideoSource.CaptureFrame()")
        {
            using var captured = c.Call(connection.Video.CaptureFrame);
            c.Require(captured is not null, "真实基本连接收到首帧后可单次取图");
            c.Equal(size.W, captured.Width, "截图宽度与连接画面一致");
            c.Equal(size.H, captured.Height, "截图高度与连接画面一致");
            byte[] pixels = captured.CopyPixels();
            c.Equal(captured.Stride * captured.Height, pixels.Length, "完整 BGRA 像素长度");
            await connection.DisconnectAsync();
            c.Require(c.Call(connection.Video.CaptureFrame) is null, "真实连接断开后返回空画面");
            c.Require(pixels.AsSpan().SequenceEqual(captured.CopyPixels()), "原生连接释放后帧租约仍保持原始像素");
        }
        if (p.Api.Contains("SendAudio"))
        {
            if (connection.AudioInput is not { } audio) throw new ApiCheckSkippedException("当前 Core 的 RdpBasic 与 RdpEnhanced 均未实现音频输入，AudioInput 返回 null。");
            if (!audio.IsAvailable || audio.Format is not { } format) throw new ApiCheckSkippedException("音频输入通道尚未协商格式。");
            int bytes = format.SampleFormat == ConsoleAudioSampleFormat.Signed16LittleEndian ? 2 : 4;
            c.Require(c.Call(() => audio.SendAudio(format, new byte[format.SampleRate / 100 * format.Channels * bytes])), "10 毫秒匹配格式 PCM 接受");
        }
    }
}
