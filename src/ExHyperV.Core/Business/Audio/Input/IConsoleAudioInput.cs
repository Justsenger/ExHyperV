namespace ExHyperV.Services;

/// <summary>音频输入的历史预留接口，未实现，不属于当前 Core 的功能范围。</summary>
/// <remarks>
/// <para>当前内置后端的 AudioInput 为 null。以下成员保留采样格式、就绪通知和提交 PCM 的约定。</para>
/// </remarks>
public interface IConsoleAudioInput
{
    /// <summary>音频发送通道是否已就绪。</summary>
    /// <fielddoc>
    /// <meaning>音频发送通道是否已就绪。</meaning>
    /// <values kind="options">true：就绪；false：未就绪或已断开。</values>
    /// </fielddoc>
    bool IsAvailable { get; }
    /// <summary>发送通道当前接受的采样格式。</summary>
    /// <fielddoc>
    /// <meaning>发送通道当前接受的采样格式。</meaning>
    /// <values kind="format">尚未协商或通道不可用时为 null。</values>
    /// </fielddoc>
    ConsoleAudioFormat? Format { get; }
    /// <summary>发送通道的可用状态或采样格式变化时通知。</summary>
    /// <remarks>参数为变化后的 IsAvailable。即使仍为 true，也应重新读取 Format，确认采样格式是否改变。</remarks>
    event Action<bool>? AvailabilityChanged;
    /// <summary>把一段按当前格式准备好的 PCM 音频交给来宾。</summary>
    /// <param name="format">这批 samples 实际使用的格式，应与当前 Format 相同。</param>
    /// <param name="samples">原始 PCM 字节，包含完整的采样帧；本次调用返回后可复用该内存。</param>
    /// <returns>true 表示数据已加入发送队列；通道不可用、格式不匹配或队列无法接收时为 false。</returns>
    /// <remarks>格式可能在采集期间改变。发送时仍传入这批数据原来的格式；如果被拒绝，重新读取 Format 后为下一批数据调整格式。
    /// 返回 true 不表示来宾已经播放或处理了这段声音。</remarks>
    bool SendAudio(ConsoleAudioFormat format, ReadOnlySpan<byte> samples);
}
