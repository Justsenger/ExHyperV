namespace ExHyperV.Services;

/// <summary>一段音频采样及其格式、包序号和时间戳。</summary>
/// <remarks>构造时复制采样数据，因此可在回调返回或连接关闭后继续使用，不需要 Dispose。
/// 当前此类型是音频接口的数据约定，内置控制台后端尚未输出音频包。</remarks>
/// <example>
/// <para>构造一段 10 毫秒的双声道静音，说明数据长度如何由格式计算。</para>
/// <code>
/// using ExHyperV.Services;
/// public static class AudioPacketExample
/// {
///     public static ConsoleAudioPacket Silence()
///     {
///         var format = new ConsoleAudioFormat(48000, 2,
///             ConsoleAudioSampleFormat.Signed16LittleEndian);
///         byte[] samples = new byte[480 * 2 * 2];
///         return new ConsoleAudioPacket(format, samples, 0, TimeSpan.Zero);
///     }
/// }
/// </code>
/// </example>
internal sealed class ConsoleAudioPacket
{
    private readonly byte[] data;

    /// <summary>复制一段完整的 PCM 采样，创建可独立保留的音频包。</summary>
    /// <param name="format">这段数据的采样率、声道数和数值格式。</param>
    /// <param name="samples">按声道交错排列的原始采样；长度必须为“声道数 × 每个采样字节数”的整数倍，可为空。</param>
    /// <param name="sequence">当前音频流中的包序号，非负；由生产者安排顺序。</param>
    /// <param name="timestamp">当前音频流中的相对时间，非负；更具体的计时起点见 Timestamp。</param>
    /// <remarks>返回后可以复用原采样内存。构造器只检查本包的格式、长度、序号和时间是否合法，不检查与上一包的先后关系。</remarks>
    /// <exception cref="ArgumentNullException">format 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">格式无效、声道数过大，或 sequence、timestamp 为负。</exception>
    /// <exception cref="ArgumentException">数据末尾缺少一个完整采样帧所需的字节。</exception>
    public ConsoleAudioPacket(ConsoleAudioFormat format, ReadOnlySpan<byte> samples, long sequence, TimeSpan timestamp)
    {
        ArgumentNullException.ThrowIfNull(format);
        int bytesPerSample = format.SampleFormat switch
        {
            ConsoleAudioSampleFormat.Signed16LittleEndian => 2,
            ConsoleAudioSampleFormat.Float32LittleEndian => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.Channels > int.MaxValue / bytesPerSample)
            throw new ArgumentOutOfRangeException(nameof(format));
        if (samples.Length % (format.Channels * bytesPerSample) != 0)
            throw new ArgumentException("PCM data must contain complete interleaved sample frames.", nameof(samples));
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (timestamp < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timestamp));
        Format = format;
        Sequence = sequence;
        Timestamp = timestamp;
        data = samples.ToArray();
    }

    /// <summary>解释当前采样数据所使用的音频格式。</summary>
    /// <fielddoc>
    /// <meaning>解释当前采样数据所使用的音频格式。</meaning>
    /// </fielddoc>
    public ConsoleAudioFormat Format { get; }
    /// <summary>音频包独立持有的原始 PCM 采样。</summary>
    /// <fielddoc>
    /// <meaning>音频包独立持有的原始 PCM 采样。</meaning>
    /// <values kind="format">按声道交错排列；字节数为“声道数 × 每采样字节数”的整数倍，可为空。</values>
    /// </fielddoc>
    /// <remarks>构造时已复制，可在回调结束和连接关闭后继续读取。</remarks>
    public ReadOnlySpan<byte> Samples => data;
    /// <summary>当前音频流中的包序号。</summary>
    /// <fielddoc>
    /// <meaning>当前音频流中的包序号。</meaning>
    /// <values kind="format">非负整数，由生产者安排；与视频帧序号分别计数。</values>
    /// </fielddoc>
    public long Sequence { get; }
    /// <summary>当前音频流中的相对时间。</summary>
    /// <fielddoc>
    /// <meaning>当前音频流中的相对时间。</meaning>
    /// <values kind="format">非负时间间隔。</values>
    /// </fielddoc>
    /// <remarks>音频后端待接入，音画同步的时间基准待定义。</remarks>
    public TimeSpan Timestamp { get; }
}
