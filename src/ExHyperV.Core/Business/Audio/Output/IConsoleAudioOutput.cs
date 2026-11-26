namespace ExHyperV.Services;

/// <summary>接收来自来宾虚拟机的 PCM 音频输出。</summary>
/// <remarks>RdpBasic 为 null，RdpEnhanced 通过增强会话通道交付来宾声音的 PCM 音频包，播放或混音由调用方负责。</remarks>
/// <businessdoc category="音频">
/// <summary>来宾声音接收。</summary>
/// <para>接收方向为“来宾 → 调用方”，数据格式为 PCM，播放由调用方负责。RdpBasic 不提供音频通道，RdpEnhanced 在来宾启用声音服务后可用。</para>
/// <para>采样格式和数据包约定见 <see cref="ConsoleAudioFormat"/>、<see cref="ConsoleAudioPacket"/>。</para>
/// </businessdoc>
public interface IConsoleAudioOutput
{
    /// <summary>音频接收通道是否已就绪。</summary>
    /// <fielddoc>
    /// <meaning>音频接收通道是否已就绪。</meaning>
    /// <values kind="options">true 表示就绪，false 表示未就绪或已断开。</values>
    /// </fielddoc>
    bool IsAvailable { get; }
    /// <summary>音频接收通道变为可用或不可用时通知，参数为变化后的 IsAvailable。</summary>
    event Action<bool>? AvailabilityChanged;
    /// <summary>收到一段来宾声音时通知，参数包含采样字节和格式。</summary>
    /// <remarks>回调在后台线程执行，应尽快返回。数据由音频包独立持有，可直接交给后续处理，无需另行复制或保留租约。</remarks>
    event Action<ConsoleAudioPacket>? AudioReceived;
}
