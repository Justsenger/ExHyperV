using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class SerialPortTests
{
    [TestMethod]
    public void PipePathNormalizationAndValidationFollowRules()
    {
        Assert.AreEqual("", VmSerialPortService.NormalizePipePath(null));
        Assert.AreEqual("", VmSerialPortService.NormalizePipePath("   "));
        Assert.AreEqual(@"\\.\pipe\com1", VmSerialPortService.NormalizePipePath("  com1  "));
        Assert.AreEqual(@"\\.\pipe\com1", VmSerialPortService.NormalizePipePath(@"\\.\pipe\com1"));
        Assert.AreEqual(@"\\host01\pipe\kd1", VmSerialPortService.NormalizePipePath(@"\\host01\pipe\kd1"));

        Assert.IsNull(VmSerialPortService.GetPipePathError(@"\\.\pipe\exhyperv-com1"));
        Assert.IsNull(VmSerialPortService.GetPipePathError(@"\\host01\pipe\kd_2"));

        foreach (string invalid in new[] { @"\\.\pipe\", @"\\.\pipe\.", @"\\.\pipe\..", @"\\.\pipe\foo\", @"\\.\pipe\a?b", @"\\.\pipe\a/b", @"C:\pipe\test", @"\\.\other\name" })
            Assert.IsNotNull(VmSerialPortService.GetPipePathError(invalid), invalid);
    }

    [TestMethod]
    public void ModificationConditionRequiresPoweredOffState()
    {
        foreach (var state in Enum.GetValues<VmState>())
            Assert.AreEqual(state == VmState.PoweredOff, VmSerialPortService.GetModificationCondition(state).CanModify, state.ToString());
    }

    [TestMethod]
    public async Task WriteMethodsValidateArgumentsBeforeWmiAccess()
    {
        var id = Guid.NewGuid();
        Assert.AreEqual(ApiErrorKind.InvalidArgument, (await VmSerialPortService.SetPipePathAsync(id, 0, @"\\.\pipe\test")).ErrorKind);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, (await VmSerialPortService.SetPipePathAsync(id, 3, @"\\.\pipe\test")).ErrorKind);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, (await VmSerialPortService.SetPipePathAsync(id, 1, @"\\.\pipe\")).ErrorKind);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, (await VmSerialPortService.SetPipePathAsync(id, 1, @"\\.\pipe\a*b")).ErrorKind);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, (await VmSerialPortService.SetDebuggerModeAsync(id, 0, true)).ErrorKind);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, (await VmSerialPortService.SetDebuggerModeAsync(id, 3, false)).ErrorKind);
    }

    [TestMethod]
    public void QueryFieldsCoverAllSerialFields()
    {
        Assert.AreSame(VmSerialPortFields.PortNumber, VmQueryFields.Find("Serial.PortNumber"));
        Assert.AreSame(VmSerialPortFields.Name, VmQueryFields.Find("Serial.Name"));
        Assert.AreSame(VmSerialPortFields.PipePath, VmQueryFields.Find("Serial.PipePath"));
        Assert.AreSame(VmSerialPortFields.DebuggerMode, VmQueryFields.Find("Serial.DebuggerMode"));
        Assert.AreEqual(VmQueryCollection.SerialPorts, VmSerialPortFields.PortNumber.Collection);
        Assert.AreEqual(VmQueryCollection.SerialPorts, VmSerialPortFields.PipePath.Collection);
    }

    [TestMethod]
    public async Task SerialQueryFailureIsSurfacedCleanly()
    {
        using (WmiApi.UseQuerySource((_, _) => new()))
        {
            var missing = await VmSerialPortService.GetSerialPortsAsync(Guid.NewGuid());
            Assert.IsFalse(missing.Success);
            Assert.AreEqual(ApiErrorKind.NotFound, missing.ErrorKind);
        }

        using var fault = WmiApi.UseQuerySource((_, _) =>
            new(new ApiFailure(ApiErrorKind.AccessDenied, 5, ApiErrorSource.Wmi, "serial fault")));

        var response = await VmSerialPortService.GetSerialPortsAsync(Guid.NewGuid());
        Assert.IsFalse(response.Success);
        Assert.AreEqual(ApiErrorKind.AccessDenied, response.ErrorKind);
    }
}
