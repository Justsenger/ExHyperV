using System.Buffers;
using System.Security.Cryptography;

namespace ExHyperV.Tools;

// WinZip AES AE-1/AE-2 authentication: PBKDF2-HMAC-SHA1 (1000), followed by
// HMAC-SHA1 over ALL ciphertext, truncated to 10 bytes. These parameters belong
// to the interoperable ZIP format, not to an application password-storage policy.
// https://www.winzip.com/en/support/aes-encryption/
internal static class ZipAesAuthentication
{
    internal static void Verify(Stream source, long payloadStart, long compressedLength,
        int keyBits, string password, CancellationToken token)
    {
        int keyBytes = keyBits / 8, saltBytes = keyBits / 16;
        long dataLength = compressedLength - saltBytes - 2 - 10;
        if (keyBits is not (128 or 256) || dataLength < 0 || payloadStart < saltBytes + 2
            || payloadStart > source.Length - 10 || dataLength > source.Length - payloadStart - 10)
            throw new CryptographicException("ZIP AES 数据范围无效。");
        token.ThrowIfCancellationRequested();
        source.Position = payloadStart - saltBytes - 2;
        Span<byte> header = stackalloc byte[saltBytes + 2];
        source.ReadExactly(header);
        Span<byte> keys = stackalloc byte[keyBytes * 2 + 2];
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(password, header[..saltBytes], keys, 1000, HashAlgorithmName.SHA1);
            if (!CryptographicOperations.FixedTimeEquals(keys[(keyBytes * 2)..], header[saltBytes..]))
                throw new CryptographicException("ZIP AES 密码验证失败。");
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, keys.Slice(keyBytes, keyBytes));
            long remaining = dataLength;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, remaining);
                source.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            Span<byte> expected = stackalloc byte[10];
            Span<byte> actual = stackalloc byte[20];
            source.ReadExactly(expected);
            hash.GetHashAndReset(actual);
            if (!CryptographicOperations.FixedTimeEquals(actual[..10], expected))
                throw new CryptographicException("ZIP AES 完整认证失败。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
