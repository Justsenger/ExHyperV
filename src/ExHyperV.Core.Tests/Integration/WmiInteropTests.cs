using System.Management;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("WindowsIntegration")]
public sealed class WmiInteropTests
{
    [TestMethod]
    public void CimArraysPreserveElementWidthAndUnsignedValues()
    {
        AssertRoundTrip(CimType.SInt8, new sbyte[] { -128, 7, -1 });
        AssertRoundTrip(CimType.UInt8, new byte[] { 255, 7, 0 });
        AssertRoundTrip(CimType.UInt16, new ushort[] { 65535, 0 });
        AssertRoundTrip(CimType.UInt32, new uint[] { uint.MaxValue, 0 });
        AssertRoundTrip(CimType.SInt64, new long[] { long.MinValue, 0 });
        AssertRoundTrip(CimType.UInt64, new ulong[] { ulong.MaxValue, 0 });
        AssertRoundTrip(CimType.String, new string[] { "虚拟机", "" });
        AssertRoundTrip(CimType.Boolean, new bool[] { true, false });
    }

    private static void AssertRoundTrip(CimType type, Array input)
    {
        var variant = WbemVariant.From(type, true, input);
        try
        {
            var output = (Array)variant.Read(type)!;
            Assert.AreEqual(input.GetType(), output.GetType());
            CollectionAssert.AreEqual(input, output);
        }
        finally { variant.Clear(); }
    }

    [TestMethod]
    public void NativeParserDistinguishesNamespacesAndQuotedInstanceKeys()
    {
        var scope = new ManagementPath("root/virtualization/v2");
        Assert.AreEqual(@"root\virtualization\v2", scope.NamespacePath);
        Assert.AreEqual("", scope.ClassName);
        var instance = new ManagementPath(@"\\server\root\cimv2:ExHyperV_Test.Name=""key:with/path""");
        Assert.IsTrue(instance.IsInstance);
        Assert.AreEqual("server", instance.Server);
        Assert.AreEqual("ExHyperV_Test", instance.ClassName);
        Assert.AreEqual(@"ExHyperV_Test.Name=""key:with/path""", instance.RelativePath);
    }
}
