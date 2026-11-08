using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class IsoStreamTests
{
    [TestMethod]
    public void RebuildingShorterImageRemovesOldTail()
    {
        InDirectory(path =>
        {
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0xFF, 24).ToArray());
            byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8];
            Write(new ImageStream(expected), path, 4, 2);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(path));
        });
    }

    [TestMethod]
    public void ShortReadsContinueUntilDeclaredImageLength()
    {
        InDirectory(path =>
        {
            byte[] expected = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
            Write(new ImageStream(expected, 2), path, 4, 3);
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(path));
        });
    }

    [TestMethod]
    public void PrematureEndFailsAndReleasesOutput()
    {
        InDirectory(path =>
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() =>
                Write(new ImageStream([1, 2, 3, 4, 5, 6, 7]), path, 4, 2));
            Assert.IsInstanceOfType<EndOfStreamException>(error.InnerException);
            using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.AreEqual(7L, file.Length);
        });
    }

    private static void Write(IStream stream, string path, int blockSize, int totalBlocks)
        => typeof(IsoBuilderService).GetMethod("WriteIStreamToFile", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [stream, path, blockSize, totalBlocks]);

    private static void InDirectory(Action<string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ExHyperV-IsoStream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "image.iso");
        try { check(path); }
        finally { File.Delete(path); Directory.Delete(root); }
    }

    private sealed class ImageStream(byte[] data, int maxRead = int.MaxValue) : IStream
    {
        private int position;
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int count = Math.Min(Math.Min(cb, maxRead), data.Length - position);
            Array.Copy(data, position, pv, 0, count);
            position += count;
            Marshal.WriteInt32(pcbRead, count);
        }
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition) => throw new NotSupportedException();
        public void SetSize(long libNewSize) => throw new NotSupportedException();
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void Stat(out STATSTG pstatstg, int grfStatFlag) => throw new NotSupportedException();
        public void Clone(out IStream ppstm) => throw new NotSupportedException();
    }
}
