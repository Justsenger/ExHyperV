using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddFrameCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("ConsoleFrame.构造()", [c =>
        {
            using var frame = c.Call(() => new ConsoleFrame(2, 2, 12, new byte[24], 5));
            c.Equal(2, frame.Width, "宽度"); c.Equal(2, frame.Height, "高度");
            c.Equal(12, frame.Stride, "含行尾填充的步长"); c.Equal(24, frame.ByteLength, "缓冲长度");
            c.Equal(4L, frame.BaseSequence, "前序帧号"); c.Require(frame.HasFullDamage, "公开构造标记整帧变化");
            foreach (var row in new[] { (0, 2, 8), (2, 0, 8), (2, 2, 7), (2, 2, 4), (2, int.MaxValue, 8) })
                c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => new ConsoleFrame(row.Item1, row.Item2, row.Item3, new byte[16], 1)), "非法尺寸或步长 " + row);
            c.Throws<ArgumentException>(() => c.Call(() => new ConsoleFrame(2, 2, 8, new byte[15], 1)), "短缓冲");
            c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => new ConsoleFrame(2, 2, 8, new byte[16], 0)), "零帧号");
        }]);
        add("ConsoleFrame.CopyPixels()", [c =>
        {
            byte[] pattern = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
            using var frame = new ConsoleFrame(2, 2, 12, pattern, 1);
            var copy = c.Call(frame.CopyPixels);
            c.Sequence(pattern, copy, "逐字节复制整帧及行尾填充");
            copy[0] = 255;
            c.Equal((byte)0, frame.Pixels[0], "修改副本保留原帧");
            c.Sequence(pattern, c.Call(frame.CopyPixels), "再次复制返回原内容");
        }, c =>
        {
            var frame = new ConsoleFrame(1, 1, 4, new byte[4], 1);
            frame.Dispose();
            c.Throws<ObjectDisposedException>(() => c.Call(frame.CopyPixels), "释放后的帧拒绝复制");
        }]);
        add("ConsoleFrame.Retain()", [c =>
        {
            byte[] pattern = [1, 2, 3, 255];
            var owner = new CountingFrameOwner();
            using var original = new ConsoleFrame(1, 1, 4, pattern, 8, 7, new(0, 0, 1, 1), true, owner);
            using var retained = c.Call(original.Retain);
            original.Dispose();
            c.Sequence(pattern, retained.Pixels.ToArray(), "原租约释放后保留帧仍有效");
            c.Equal(8L, retained.Sequence, "保留帧号");
            c.Equal(0, owner.ReleaseCount, "尚有租约时保持资源");
            retained.Dispose();
            c.Equal(1, owner.ReleaseCount, "末份租约释放后归还资源一次");
            c.Throws<ObjectDisposedException>(() => c.Call(original.Retain), "已释放租约拒绝再次保留");
        }]);
        add("ConsoleFrame.Dispose()", [c =>
        {
            var owner = new CountingFrameOwner();
            using var frame = new ConsoleFrame(1, 1, 4, new byte[] { 1, 2, 3, 255 }, 1, 0, new(0, 0, 1, 1), true, owner);
            using var a = frame.Retain(); using var b = frame.Retain();
            c.Call(frame.Dispose); c.Call(frame.Dispose);
            c.Sequence(new byte[] { 1, 2, 3, 255 }, a.Pixels.ToArray(), "重复释放原租约后其他租约仍有效");
            c.Call(a.Dispose); c.Call(a.Dispose);
            c.Equal(0, owner.ReleaseCount, "其他租约存活时保持资源");
            c.Sequence(new byte[] { 1, 2, 3, 255 }, b.Pixels.ToArray(), "逐个释放相互独立");
            c.Call(b.Dispose);
            c.Call(b.Dispose);
            c.Equal(1, owner.ReleaseCount, "底层资源恰好归还一次");
            c.Throws<ObjectDisposedException>(() => b.CopyPixels(), "末份租约已释放");
        }]);
        add("ConsoleFrameRegion.Union()", [c =>
        {
            var a = new ConsoleFrameRegion(2, 3, 4, 5);
            foreach (var (other, expected) in new[] {
                (new ConsoleFrameRegion(0, 0, 0, 0), a),
                (new ConsoleFrameRegion(3, 4, 1, 1), a),
                (new ConsoleFrameRegion(4, 5, 4, 5), new ConsoleFrameRegion(2, 3, 6, 7)),
                (new ConsoleFrameRegion(20, 30, 2, 3), new ConsoleFrameRegion(2, 3, 20, 30)) })
            {
                c.Equal(expected, c.Call(() => a.Union(other)), "合并 " + other);
                c.Equal(expected, c.Call(() => other.Union(a)), "交换合并次序");
            }
            c.Throws<OverflowException>(() => c.Call(() => a.Union(new(int.MaxValue, 1, 1, 1))), "坐标溢出");
        }]);
        add("ConsoleAudioPacket.构造()", [c =>
        {
            var format = new ConsoleAudioFormat(48000, 2, ConsoleAudioSampleFormat.Signed16LittleEndian);
            byte[] samples = [1, 2, 3, 4];
            var packet = c.Call(() => new ConsoleAudioPacket(format, samples, 5, TimeSpan.FromMilliseconds(10)));
            samples[0] = 255;
            c.Sequence(new byte[] { 1, 2, 3, 4 }, packet.Samples.ToArray(), "采样独立复制");
            c.Equal(format, packet.Format, "音频格式"); c.Equal(5L, packet.Sequence, "包序号");
            c.Equal(TimeSpan.FromMilliseconds(10), packet.Timestamp, "时间戳");
            foreach (var invalid in new[] { format with { SampleRate = 0 }, format with { Channels = 0 }, format with { Channels = int.MaxValue }, format with { SampleFormat = (ConsoleAudioSampleFormat)99 } })
                c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => new ConsoleAudioPacket(invalid, samples, 0, TimeSpan.Zero)), "非法音频格式");
            c.Throws<ArgumentException>(() => c.Call(() => new ConsoleAudioPacket(format, new byte[3], 0, TimeSpan.Zero)), "截断采样帧");
            c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => new ConsoleAudioPacket(format, samples, -1, TimeSpan.Zero)), "负序号");
            c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => new ConsoleAudioPacket(format, samples, 0, TimeSpan.FromTicks(-1))), "负时间");
        }]);
        add("ConsoleConnectionOptions 默认值", [c =>
        {
            var options = c.Call(() => new ConsoleConnectionOptions());
            c.Equal(Guid.Empty, options.VmId, "待填写目标"); c.Equal(1024, options.Width, "默认宽度");
            c.Equal(768, options.Height, "默认高度"); c.Equal(100u, options.DesktopScale, "默认缩放");
            c.Equal(TimeSpan.FromSeconds(2), options.ConnectTimeout, "默认超时");
        }]);
        add("ConsoleService.GetBackends()", [c =>
        {
            var backends = c.Call(ConsoleService.GetBackends);
            c.Sequence(new[] { "RdpBasic", "RdpEnhanced", "Nimue" }, backends.Select(b => b.BackendId), "固定后端目录");
            c.Sequence(new[] { true, true, false }, backends.Select(b => b.IsImplemented), "实现状态");
            c.Equal(ConsoleCapabilities.Video | ConsoleCapabilities.Input, backends[0].Capabilities, "基本会话能力");
            c.Equal(ConsoleCapabilities.Video | ConsoleCapabilities.Input | ConsoleCapabilities.Clipboard | ConsoleCapabilities.DisplayResize, backends[1].Capabilities, "增强会话能力");
            c.Require(backends is IList<ConsoleBackendInfo> { IsReadOnly: true }, "目录为只读集合");
            c.Throws<NotSupportedException>(() => ((IList<ConsoleBackendInfo>)backends).Clear(), "目录拒绝清空");
        }]);
        add("ConsoleService.CreateConnection()", [c =>
        {
            var options = new ConsoleConnectionOptions { VmId = Guid.NewGuid() };
            foreach (var backend in new[] { ConsoleService.RdpBasicBackendId, ConsoleService.RdpEnhancedBackendId })
            {
                var connection = c.Call(() => ConsoleService.CreateConnection(options, backend));
                c.Require(connection is not null, backend + " 创建尚未启动的连接");
                c.Equal(backend, connection!.BackendId, "后端标识");
                c.Equal(ConsoleConnectionState.Disconnected, connection.State, "初始状态");
                c.Require(connection.Video is not null && connection.Input is not null, "提供画面与输入能力");
                c.Equal(backend == ConsoleService.RdpEnhancedBackendId, connection.Clipboard is not null, "剪贴板能力与后端匹配");
                c.Require(connection.AudioInput is null && connection.AudioOutput is null, "音频保持待接入状态");
                connection!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            var defaultConnection = c.Call(() => ConsoleService.CreateConnection(options));
            c.Equal(ConsoleService.RdpBasicBackendId, defaultConnection.BackendId, "默认使用基本会话");
            defaultConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }, c =>
        {
            var options = new ConsoleConnectionOptions { VmId = Guid.NewGuid() };
            c.Throws<ArgumentNullException>(() => c.Call(() => ConsoleService.CreateConnection(null!)), "空选项");
            c.Throws<ArgumentNullException>(() => c.Call(() => ConsoleService.CreateConnection(options, null!)), "空后端");
            c.Throws<ArgumentException>(() => c.Call(() => ConsoleService.CreateConnection(options, "unknown")), "未知后端");
            c.Throws<NotSupportedException>(() => c.Call(() => ConsoleService.CreateConnection(options, ConsoleService.NimueBackendId)), "Nimue 返回明确错误");
            c.Throws<ArgumentException>(() => c.Call(() => ConsoleService.CreateConnection(new())), "缺少虚拟机身份");
        }]);
        byte[] rgb565 = [0, 248, 224, 7, 31, 0, 255, 255];
        add("VmScreenshotImage.FromRgb565()", [c =>
        {
            var source = (byte[])rgb565.Clone();
            var image = c.Call(() => VmScreenshotImage.FromRgb565(source, 2, 2));
            source[0] = 255;
            c.Equal(2, image.Width, "宽度"); c.Equal(2, image.Height, "高度"); c.Equal(4, image.Stride, "RGB565 步长");
            c.Equal(false, image.IsEncoded, "保存原始像素"); c.Sequence(rgb565, image.CopyBytes(), "红绿蓝白原始像素及输入隔离");
        }, c =>
        {
            c.Throws<ArgumentException>(() => c.Call(() => VmScreenshotImage.FromRgb565(new byte[7], 2, 2)), "短缓冲");
            c.Throws<ArgumentException>(() => c.Call(() => VmScreenshotImage.FromRgb565([], 0, 2)), "零尺寸");
            c.Throws<OverflowException>(() => c.Call(() => VmScreenshotImage.FromRgb565([], int.MaxValue, 2)), "尺寸溢出");
        }]);
        add("VmScreenshotImage.CopyBytes()", [c =>
        {
            var image = VmScreenshotImage.FromRgb565(rgb565, 2, 2);
            var first = c.Call(image.CopyBytes); first[0] = 255;
            c.Sequence(rgb565, c.Call(image.CopyBytes), "返回数组独立于原图像");
        }]);
    }

    private sealed class CountingFrameOwner : IDisposable
    {
        public int ReleaseCount { get; private set; }
        public void Dispose() => ReleaseCount++;
    }
}
