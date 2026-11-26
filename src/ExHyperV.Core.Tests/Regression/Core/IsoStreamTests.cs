using System.Reflection;
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
            byte[] original = [42, 24];
            File.WriteAllBytes(path, original);
            var error = Assert.ThrowsExactly<TargetInvocationException>(() =>
                Write(new ImageStream([1, 2, 3, 4, 5, 6, 7]), path, 4, 2));
            Assert.IsInstanceOfType<EndOfStreamException>(error.InnerException);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(path)!));
        });
    }

    private static void Write(ImageStream stream, string path, int blockSize, int totalBlocks)
        => typeof(IsoBuilderService).GetMethod("WriteImageStream", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [(Func<byte[], int, int>)stream.Read, path, blockSize, totalBlocks, null, CancellationToken.None]);

    private static void InDirectory(Action<string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ExHyperV-IsoStream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "image.iso");
        try { check(path); }
        finally { File.Delete(path); Directory.Delete(root); }
    }

    private sealed class ImageStream(byte[] data, int maxRead = int.MaxValue)
    {
        private int position;
        public int Read(byte[] pv, int cb)
        {
            int count = Math.Min(Math.Min(cb, maxRead), data.Length - position);
            Array.Copy(data, position, pv, 0, count);
            position += count;
            return count;
        }
    }
}
