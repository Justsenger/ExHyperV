namespace ExHyperV.Services;

/// <summary>一批音频采样的采样率、声道数和数值格式。</summary>
/// <param name="SampleRate">每个声道每秒的采样数。
/// <fielddoc>
/// <meaning>每个声道每秒的采样数。</meaning>
/// <values kind="format">正整数，单位为 Hz。例如 48000 为 48 kHz。</values>
/// </fielddoc>
/// </param>
/// <param name="Channels">每个采样时刻的声道数量。
/// <fielddoc>
/// <meaning>每个采样时刻的声道数量。</meaning>
/// <values kind="format">正整数。例如 1 为单声道，2 为双声道。</values>
/// </fielddoc>
/// </param>
/// <param name="SampleFormat">PCM 中单个声道采样值的编码格式。
/// <fielddoc>
/// <meaning>PCM 中单个声道采样值的编码格式。</meaning>
/// </fielddoc>
/// </param>
/// <remarks>
/// <para>采样按声道交错排列：双声道依次保存“第 1 次采样的左、右声道，第 2 次采样的左、右声道……”。
/// 同一采样时刻的全部声道组成一个采样帧。</para>
/// <para>例如 48 kHz、双声道、16 位整数，每个采样帧占 4 字节；10 毫秒包含 480 个采样帧，共 1920 字节。</para>
/// <para>此记录只描述格式，构造时不检查数值。ConsoleAudioPacket 会检查格式及数据长度。
/// 当前没有可用的控制台音频后端；多声道的声道顺序仍需在接入后端时确定。</para>
/// </remarks>
public sealed record ConsoleAudioFormat(int SampleRate, int Channels, ConsoleAudioSampleFormat SampleFormat);
