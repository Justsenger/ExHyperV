using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using ExHyperV.Services;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace ExHyperV.Tools;

// Keep the native .NET inflater for ordinary ZIPs. SharpZipLib supplies AES support;
// its types never cross the Core public API.
internal sealed class CoreZipArchive : IDisposable
{
    private readonly SharpZipFile index;
    private readonly Stream source;
    private readonly bool leaveOpen;
    private ZipArchive? plainArchive;
    private readonly ImportArchiveContext? context;
    private string? password;
    internal IReadOnlyList<CoreZipEntry> Entries { get; }

    internal static CoreZipArchive OpenRead(string path, ImportArchiveContext context)
        => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), context.Password, context);

    internal CoreZipArchive(Stream source, string? password, ImportArchiveContext? context = null, bool leaveOpen = false)
    {
        this.source = source;
        this.leaveOpen = leaveOpen;
        this.context = context;
        this.password = password;
        try
        {
            CheckAdvertisedEntryCount(source, context?.MaxEntries);
            index = new SharpZipFile(source, leaveOpen: true) { Password = password };
            ImportArchiveContext.Check(index.Count, context?.MaxEntries, nameof(VmImportArchiveOptions.MaxEntries));
            var entries = new List<CoreZipEntry>();
            long total = 0;
            for (int i = 0; i < index.Count; i++)
            {
                ZipEntry entry = index[i];
                if (entry.Size < 0) throw new InvalidDataException("ZIP 条目没有有效大小：" + entry.Name);
                ImportArchiveContext.Check(entry.Size, context?.MaxEntryBytes, nameof(VmImportArchiveOptions.MaxEntryBytes), entry.Name);
                total = checked(total + entry.Size);
                ImportArchiveContext.Check(total, context?.MaxTotalBytes, nameof(VmImportArchiveOptions.MaxTotalBytes));
                if (entry.IsCrypted)
                {
                    if (entry.AESKeySize is not (128 or 256))
                        throw new VmImportArchiveException(VmImportArchiveError.UnsupportedEncryption,
                            "ZIP 条目不是受支持的 AES-128/AES-256 加密：" + entry.Name, entry.Name);
                    if (password is null)
                        throw new VmImportArchiveException(VmImportArchiveError.PasswordRequired,
                            "读取此 ZIP 需要密码。", entry.Name);
                }
                entries.Add(new CoreZipEntry(this, i, entry));
            }
            Entries = entries.AsReadOnly();
        }
        catch
        {
            index?.Close();
            if (!leaveOpen) source.Dispose();
            throw;
        }
    }

    private static void CheckAdvertisedEntryCount(Stream stream, int? maximum)
    {
        if (maximum is null || stream.Length < 22) return;
        long previous = stream.Position;
        try
        {
            // Reject an over-budget central directory before the ZIP library
            // allocates one object per advertised entry. The parsed count is also
            // checked afterward. The ZIP comment can contain signature bytes.
            byte[] tail = new byte[(int)Math.Min(stream.Length, 65535 + 22 + 20)];
            stream.Position = stream.Length - tail.Length;
            stream.ReadExactly(tail);
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054b50
                    || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) != tail.Length - i - 22) continue;
                ulong count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
                if (i >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i - 20)) == 0x07064b50)
                {
                    ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(i - 12));
                    if (stream.Length < 56 || offset > (ulong)(stream.Length - 56))
                        throw new InvalidDataException("ZIP64 目录位置无效。");
                    stream.Position = (long)offset;
                    Span<byte> record = stackalloc byte[56];
                    stream.ReadExactly(record);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50)
                        throw new InvalidDataException("ZIP64 目录记录无效。");
                    count = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
                }
                ImportArchiveContext.Check(count > long.MaxValue ? long.MaxValue : (long)count,
                    maximum, nameof(VmImportArchiveOptions.MaxEntries));
                return;
            }
        }
        finally { stream.Position = previous; }
    }

    private Stream Open(CoreZipEntry entry)
    {
        Stream stream;
        long encryptedPayloadStart = 0;
        try
        {
            if (entry.Encrypted)
            {
                stream = index.GetInputStream(entry.Index);
                // SharpZipLib 1.4.2 has read the salt and password verifier here;
                // the inflater has not read payload yet. Pin this behavior in interop tests.
                encryptedPayloadStart = source.Position;
            }
            else
            {
                plainArchive ??= new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
                ZipArchiveEntry plain = plainArchive.Entries[entry.Index];
                if (plain.FullName != entry.FullName || plain.Length != entry.Length)
                    throw new InvalidDataException("ZIP 目录解析结果不一致。");
                stream = plain.Open();
            }
        }
        catch (Exception ex) when (entry.Encrypted && IsEncryptedDataFailure(ex)) { throw entry.DecryptionFailure(ex); }
        return new CheckedStream(stream, entry, context, token => Authenticate(entry, encryptedPayloadStart, token));
    }

    private static bool IsEncryptedDataFailure(Exception exception)
        => exception is SharpZipBaseException or InvalidDataException or CryptographicException or EndOfStreamException;

    private void Authenticate(CoreZipEntry entry, long payloadStart, CancellationToken token)
    {
        if (!entry.Encrypted) return;
        // A DEFLATE end marker can precede the end of the encrypted payload. In
        // that case the library's inflater may stop before its AES stream verifies
        // the tag. Authenticate the complete ciphertext independently before EOF
        // is reported. Encryption/decryption itself remains in SharpZipLib.
        long previous = source.Position;
        try { ZipAesAuthentication.Verify(source, payloadStart, entry.CompressedLength, entry.KeyBits, password!, token); }
        catch (CryptographicException ex) { throw entry.DecryptionFailure(ex); }
        finally { source.Position = previous; }
    }

    public void Dispose()
    {
        plainArchive?.Dispose();
        index.Password = null;
        password = null;
        index.Close();
        if (!leaveOpen) source.Dispose();
    }

    internal sealed class CoreZipEntry
    {
        private readonly CoreZipArchive owner;
        internal int Index { get; }
        internal string FullName { get; }
        internal string Name => FullName.EndsWith('/') || FullName.EndsWith('\\') ? "" : Path.GetFileName(FullName);
        internal long Length { get; }
        internal bool Encrypted { get; }
        internal long CompressedLength { get; }
        internal int KeyBits { get; }
        internal long? Crc { get; }
        internal CoreZipEntry(CoreZipArchive owner, int index, ZipEntry entry)
        {
            this.owner = owner; Index = index; FullName = entry.Name; Length = entry.Size; Encrypted = entry.IsCrypted;
            CompressedLength = entry.CompressedSize; KeyBits = entry.AESKeySize;
            Crc = entry.Crc;
            if (Encrypted)
            {
                // AE-2 replaces CRC with authentication. AE-1 still carries CRC.
                ReadOnlySpan<byte> extra = entry.ExtraData;
                while (extra.Length >= 4)
                {
                    int tag = BinaryPrimitives.ReadUInt16LittleEndian(extra);
                    int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
                    if (size > extra.Length - 4) throw new InvalidDataException("ZIP 扩展字段损坏。");
                    if (tag == 0x9901)
                    {
                        if (size < 7) throw new InvalidDataException("ZIP AES 扩展字段损坏。");
                        int version = BinaryPrimitives.ReadUInt16LittleEndian(extra[4..]);
                        if (version == 2) Crc = null;
                        else if (version != 1) throw new InvalidDataException("ZIP AES 格式版本不受支持。");
                    }
                    extra = extra[(4 + size)..];
                }
            }
        }
        internal Stream Open() => owner.Open(this);
        internal VmImportArchiveException DecryptionFailure(Exception exception)
            => new(VmImportArchiveError.DecryptionFailed, "ZIP 密码不正确或加密数据损坏：" + FullName,
                FullName, cause: exception);
    }

    private sealed class CheckedStream(Stream input, CoreZipEntry entry, ImportArchiveContext? context,
        Action<CancellationToken> authenticate) : Stream
    {
        private readonly Crc32 crc = new();
        private long position;
        private bool completed;
        private int Check(byte[] buffer, int offset, int count, int requested, CancellationToken token = default)
        {
            if (requested == 0) return count;
            if (count == 0)
            {
                if (position != entry.Length || (entry.Crc is long expected && crc.Value != expected))
                    throw new InvalidDataException("ZIP 条目长度或 CRC 校验失败：" + entry.FullName);
                authenticate(token);
                completed = true;
                return 0;
            }
            position = checked(position + count);
            ImportArchiveContext.Check(position, context?.MaxEntryBytes, nameof(VmImportArchiveOptions.MaxEntryBytes), entry.FullName);
            context?.Consume(count, entry.FullName);
            if (position > entry.Length) throw new InvalidDataException("ZIP 实际解压量超过声明大小：" + entry.FullName);
            if (entry.Crc != null) crc.Update(new ArraySegment<byte>(buffer, offset, count));
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (completed) return 0;
            try { return Check(buffer, offset, input.Read(buffer, offset, count), count); }
            catch (Exception ex) when (entry.Encrypted && IsEncryptedDataFailure(ex)) { throw entry.DecryptionFailure(ex); }
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (completed) return 0;
            try { return Check(buffer, offset, await input.ReadAsync(buffer, offset, count, token).ConfigureAwait(false), count, token); }
            catch (Exception ex) when (entry.Encrypted && IsEncryptedDataFailure(ex)) { throw entry.DecryptionFailure(ex); }
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => entry.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
