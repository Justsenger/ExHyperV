namespace ExHyperV.Services;

/// <summary>来宾声音接收的预留接口；当前内置后端尚未实现。</summary>
/// <remarks>连接的 AudioOutput 当前为 null。接口约定通过 AudioReceived 交付 PCM 音频包，由调用方播放或编码。</remarks>
/// <businessdoc category="音频">
/// <summary>来宾声音接收；当前尚未实现。</summary>
/// <para>接收方向为“来宾 → 调用方”，数据格式为 PCM；播放由调用方负责。RdpBasic 和 RdpEnhanced 尚未接入音频。</para>
/// <para>采样格式和数据包约定见 <see cref="ConsoleAudioFormat"/>、<see cref="ConsoleAudioPacket"/>。</para>
/// </businessdoc>
internal interface IConsoleAudioOutput
{
    /// <summary>音频接收通道是否已就绪。</summary>
    /// <fielddoc>
    /// <meaning>音频接收通道是否已就绪。</meaning>
    /// <values kind="options">true：就绪；false：未就绪或已断开。</values>
    /// </fielddoc>
    bool IsAvailable { get; }
    /// <summary>音频接收通道变为可用或不可用时通知，参数为变化后的 IsAvailable。</summary>
    event Action<bool>? AvailabilityChanged;
    /// <summary>收到一段来宾声音时通知，参数包含采样字节和格式。</summary>
    /// <remarks>回调在后台线程执行，应尽快返回。数据由音频包独立持有，可直接交给后续处理，无需另行复制或保留租约。</remarks>
    event Action<ConsoleAudioPacket>? AudioReceived;
}
