using System.Runtime.InteropServices;

namespace ExHyperV.Services;

/// <summary>一帧虚拟机桌面画面，包含完整像素、尺寸、序号和本次变化区域。</summary>
/// <remarks>
/// <para>Pixels 是 BGRA32 像素：每个像素依次为蓝、绿、红、Alpha 四个字节，图像从上到下排列。
/// 当前控制台后端的帧已包含远端光标，Alpha 为 255；自行构造的帧保留调用方提供的像素。
/// 按 Stride 计算下一行的位置，按 Width 和 Height 取得桌面尺寸。</para>
/// <para>这个对象也负责保持像素内存有效，称为“帧租约”。FrameReceived 回调中的帧由 Core 临时借出；
/// 只在回调内使用时无需保留。要在回调后使用，请调用 Retain，并在使用完毕后 Dispose；需要独立数组则调用 CopyPixels。</para>
/// <para>局部刷新：Damage 描述相对 BaseSequence 那一帧的变化。
/// 只有显示端当前保存的序号等于 BaseSequence，且尺寸一致时，才能直接只更新 Damage。
/// 跳过了中间帧、尺寸改变或 HasFullDamage 为 true 时，最简单可靠的做法是刷新整帧。Pixels 始终含有完整画面。</para>
/// <para>保留的帧在连接关闭后仍可读取。读取期间必须保持该份租约有效，不要由另一线程同时释放。
/// 长时间积压帧会占用缓冲池，应及时消费并释放。</para>
/// </remarks>
/// <example>
/// <para>将帧交给已有的工作队列：入队前保留，消费后释放。队列应有容量限制，避免显示落后时积压大量帧。</para>
/// <code>
/// using ExHyperV.Services;
/// using System.Threading.Channels;
/// public static class RetainedFrameExample
/// {
///     public static void Enqueue(ChannelWriter&lt;ConsoleFrame&gt; queue, ConsoleFrame borrowed)
///     {
///         var retained = borrowed.Retain();
///         // 使用 Wait 模式的有界队列，TryWrite 失败时由此处释放。
///         if (!queue.TryWrite(retained)) retained.Dispose();
///     }
///     public static async Task ConsumeAsync(Channel&lt;ConsoleFrame&gt; queue, Action&lt;ConsoleFrame&gt; draw,
///         Func&lt;Task&gt; stopProducer, CancellationToken cancellationToken = default)
///     {
///         try
///         {
///             await foreach (var frame in queue.Reader.ReadAllAsync(cancellationToken))
///                 using (frame) draw(frame);
///         }
///         finally
///         {
///             // 先关闭入队入口；晚到的 Enqueue 会自行释放，不能再留下新帧。
///             queue.Writer.TryComplete();
///             try
///             {
///                 // 此回调须停止生产，并等待已经进入的帧回调退出。
///                 await stopProducer();
///             }
///             finally
///             {
///                 while (queue.Reader.TryRead(out var remaining)) remaining.Dispose();
///             }
///         }
///     }
/// }
/// </code>
/// </example>
public sealed class ConsoleFrame : IDisposable
{
    private SharedPixels? pixels;
    /// <summary>完整来宾桌面的宽度。</summary>
    /// <fielddoc>
    /// <meaning>完整来宾桌面的宽度。</meaning>
    /// <values kind="format">正整数，单位为物理像素。</values>
    /// </fielddoc>
    public int Width { get; }
    /// <summary>完整来宾桌面的高度。</summary>
    /// <fielddoc>
    /// <meaning>完整来宾桌面的高度。</meaning>
    /// <values kind="format">正整数，单位为物理像素。</values>
    /// </fielddoc>
    public int Height { get; }
    /// <summary>像素缓冲区每行占用的字节数。</summary>
    /// <fielddoc>
    /// <meaning>像素缓冲区每行占用的字节数。</meaning>
    /// <values kind="format">4 的倍数，不小于 Width × 4；连接生成的帧为 Width × 4，自行构造时可包含行尾填充。</values>
    /// </fielddoc>
    public int Stride { get; }
    /// <summary>整帧像素缓冲区的长度。</summary>
    /// <fielddoc>
    /// <meaning>整帧像素缓冲区的长度。</meaning>
    /// <values kind="format">字节数，等于 Stride × Height。</values>
    /// </fielddoc>
    public int ByteLength { get; }
    /// <summary>本连接生成画面时递增的帧序号。</summary>
    /// <fielddoc>
    /// <meaning>本连接生成画面时递增的帧序号。</meaning>
    /// <values kind="format">正整数；重复获取同一画面时序号不变。</values>
    /// </fielddoc>
    public long Sequence { get; }
    /// <summary>Damage 所依据的前一帧序号。</summary>
    /// <fielddoc>
    /// <meaning>Damage 所依据的前一帧序号。</meaning>
    /// <values kind="format">非负整数。例如 BaseSequence 为 41、Sequence 为 42，Damage 描述第 41 帧到第 42 帧的变化。</values>
    /// </fielddoc>
    /// <remarks>显示端缺少该帧或尺寸不一致时，应合并中间变化或刷新完整画面。</remarks>
    public long BaseSequence { get; }
    /// <summary>相对 BaseSequence 发生变化的桌面区域。</summary>
    /// <fielddoc>
    /// <meaning>相对 BaseSequence 发生变化的桌面区域。</meaning>
    /// <values kind="format">以桌面左上角为原点的像素矩形。</values>
    /// </fielddoc>
    /// <remarks>用于局部刷新；Pixels 始终保存完整画面。</remarks>
    public ConsoleFrameRegion Damage { get; }
    /// <summary>本次画面是否需要全量刷新。</summary>
    /// <fielddoc>
    /// <meaning>本次画面是否需要全量刷新。</meaning>
    /// <values kind="options">true：刷新整个桌面；false：可根据 BaseSequence 和 Damage 局部刷新。</values>
    /// </fielddoc>
    public bool HasFullDamage { get; }

    /// <summary>当前帧的只读像素内存。</summary>
    /// <fielddoc>
    /// <meaning>当前帧的只读像素内存。</meaning>
    /// <values kind="format">从上到下排列的 BGRA32：每像素依次为蓝、绿、红、Alpha 四个字节；行长度为 Stride，完整长度为 ByteLength。连接生成的帧包含远端光标，Alpha 为 255。</values>
    /// </fielddoc>
    /// <remarks>只在当前租约未释放时有效。读取期间保持租约有效，不得同时由另一线程释放。</remarks>
    /// <exception cref="ObjectDisposedException">此租约已经释放。</exception>
    public ReadOnlySpan<byte> Pixels => GetPixels().Span;

    /// <summary>使用调用方提供的像素内存创建租约；任何关联租约存活期间都不得修改该内存。</summary>
    /// <param name="width">正数图像宽度，单位为像素。</param>
    /// <param name="height">正数图像高度，单位为像素。</param>
    /// <param name="stride">每行字节数；必须为 4 的倍数且不小于 width × 4，可包含行尾填充。</param>
    /// <param name="pixels">长度必须等于 stride × height 的完整像素内存，不复制；任何租约存活时都不得修改或释放其底层内存。</param>
    /// <param name="sequence">正数帧序号；此构造将 BaseSequence 设为 sequence - 1 并标记整帧变化。</param>
    /// <remarks>仅验证像素描述与内存长度，不复制或冻结调用方内存。长度运算使用 64 位整数，拒绝无法用托管内存长度表示的帧。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">尺寸、行长度或帧序号无效，或帧字节数超出可表示范围。</exception>
    /// <exception cref="ArgumentException">像素内存长度与图像描述不一致。</exception>
    public ConsoleFrame(int width, int height, int stride, ReadOnlyMemory<byte> pixels, long sequence)
        : this(ValidateManagedPixels(width, height, stride, pixels, sequence), width, height, stride,
            pixels.Length, sequence, sequence - 1, new(0, 0, width, height), true) { }

    private static SharedPixels ValidateManagedPixels(int width, int height, int stride,
        ReadOnlyMemory<byte> pixels, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        if (stride <= 0 || stride % 4 != 0 || stride < (long)width * 4)
            throw new ArgumentOutOfRangeException(nameof(stride), "行长度必须容纳完整 BGRA32 像素并按 4 字节对齐。");
        long byteLength = (long)stride * height;
        if (byteLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(height), "帧字节数超出托管内存长度范围。");
        if (pixels.Length != byteLength)
            throw new ArgumentException("像素内存长度必须等于行长度乘以图像高度。", nameof(pixels));
        return new SharedPixels(pixels, null);
    }

    internal ConsoleFrame(int width, int height, int stride, ReadOnlyMemory<byte> pixels, long sequence,
        long baseSequence, ConsoleFrameRegion damage, bool fullDamage, IDisposable? owner = null)
        : this(new SharedPixels(pixels, owner), width, height, stride, pixels.Length, sequence, baseSequence, damage, fullDamage) { }

    internal ConsoleFrame(int width, int height, int stride, nint pixels, int byteLength, SafeHandle owner,
        long sequence, long baseSequence, ConsoleFrameRegion damage, bool fullDamage)
        : this(new SharedPixels(pixels, byteLength, owner), width, height, stride, byteLength, sequence, baseSequence, damage, fullDamage) { }

    private ConsoleFrame(SharedPixels pixels, int width, int height, int stride, int byteLength,
        long sequence, long baseSequence, ConsoleFrameRegion damage, bool fullDamage)
    {
        this.pixels = pixels;
        Width = width; Height = height; Stride = stride; ByteLength = byteLength;
        Sequence = sequence; BaseSequence = baseSequence;
        HasFullDamage = fullDamage;
        Damage = fullDamage ? new(0, 0, width, height) : damage;
    }

    /// <summary>保留这帧画面，供回调结束后继续使用，不复制像素。</summary>
    /// <returns>一份新的帧租约，共享当前像素；调用方用完后必须 Dispose。</returns>
    /// <remarks>每次 Retain 取得一份独立租约，直到该租约释放为止均可读取像素。
    /// 及时释放保留的帧；缓冲池耗尽时会暂停新帧输出。</remarks>
    /// <exception cref="ObjectDisposedException">当前租约已经释放。</exception>
    /// <exception cref="InvalidOperationException">同一缓冲区的租约数量已达整数上限。</exception>
    public ConsoleFrame Retain()
    {
        var shared = GetPixels();
        shared.Retain();
        try { return new(shared, Width, Height, Stride, ByteLength, Sequence, BaseSequence, Damage, HasFullDamage); }
        catch { shared.Release(); throw; }
    }

    /// <summary>复制整帧像素，取得可以独立保存和修改的字节数组。</summary>
    /// <returns>长度为 ByteLength 的新数组，按原来的 Stride 排列；同时保存 Width、Height、Stride 才能解释它。</returns>
    /// <remarks>适合需要长期保留或交给会修改数据的组件。只想延后读取时可使用 Retain，避免这次复制。</remarks>
    /// <exception cref="ObjectDisposedException">当前租约已经释放。</exception>
    public byte[] CopyPixels()
    {
        byte[] copy = Pixels.ToArray();
        GC.KeepAlive(this);
        return copy;
    }

    /// <summary>释放当前这份帧租约；重复释放没有额外效果。</summary>
    /// <remarks>释放后不能再读取此对象的 Pixels、调用 Retain 或 CopyPixels。
    /// 其他通过 Retain 取得的帧仍可使用。不要在读取此租约的同时由另一线程释放它。</remarks>
    public void Dispose() => Interlocked.Exchange(ref pixels, null)?.Release();

    private SharedPixels GetPixels() => Volatile.Read(ref pixels) ?? throw new ObjectDisposedException(nameof(ConsoleFrame));

    private sealed class SharedPixels
    {
        private readonly ReadOnlyMemory<byte> managed;
        private readonly nint address;
        private readonly int length;
        private readonly IDisposable? owner;
        private int references = 1;

        internal SharedPixels(ReadOnlyMemory<byte> managed, IDisposable? owner)
        { this.managed = managed; length = managed.Length; this.owner = owner; }

        internal SharedPixels(nint address, int length, SafeHandle owner)
        { this.address = address; this.length = length; this.owner = owner; }

        // A native SafeHandle also releases the allocation if a consumer forgets Dispose
        // and all of its managed leases become unreachable. Normal display uses prompt Dispose.
        internal unsafe ReadOnlySpan<byte> Span => address != 0 ? new((void*)address, length) : managed.Span;

        internal void Retain()
        {
            int count;
            do
            {
                count = Volatile.Read(ref references);
                ObjectDisposedException.ThrowIf(count == 0, nameof(ConsoleFrame));
                if (count == int.MaxValue) throw new InvalidOperationException("Too many frame leases.");
            } while (Interlocked.CompareExchange(ref references, count + 1, count) != count);
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref references) == 0) owner?.Dispose();
        }
    }
}
