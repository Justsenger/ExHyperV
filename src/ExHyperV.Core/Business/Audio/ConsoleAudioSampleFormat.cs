namespace ExHyperV.Services;

/// <summary>PCM 音频中每个采样数值的编码方式。</summary>
/// <remarks>采样按小端字节序存储，以 ConsoleAudioFormat 指定的声道顺序交错排列。</remarks>
public enum ConsoleAudioSampleFormat
{
    /// <summary>每个采样占 2 字节，小端有符号 16 位整数，数值范围 -32768～32767。</summary>
    Signed16LittleEndian,
    /// <summary>每个采样占 4 字节，小端 IEEE 754 单精度浮点数，通常用 -1～1 表示完整振幅。</summary>
    Float32LittleEndian
}
