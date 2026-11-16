using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

internal static class SerialPortRuleChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(VmSerialPortService.NormalizePipePath(null) == "", "Null pipe path normalizes to empty string");
        check(VmSerialPortService.NormalizePipePath("  vm1-com1  ") == @"\\.\pipe\vm1-com1", "Short serial pipe name expands to local pipe prefix");
        check(VmSerialPortService.NormalizePipePath(@"\\srv\pipe\kd") == @"\\srv\pipe\kd", "Full remote named pipe path is preserved");

        check(VmSerialPortService.GetPipePathError(@"\\.\pipe\vm1-com1") is null, "Valid local named pipe passes validation");
        check(VmSerialPortService.GetPipePathError(@"\\.\pipe\") is not null, "Empty pipe leaf name is rejected");
        check(VmSerialPortService.GetPipePathError(@"\\.\pipe\bad?name") is not null, "Invalid wildcard character in pipe name is rejected");

        foreach (var state in Enum.GetValues<VmState>())
            check(VmSerialPortService.GetModificationCondition(state).CanModify == (state == VmState.PoweredOff),
                $"Serial port modification condition matches PoweredOff requirement for {state}");

        check(VmSerialPortFields.PortNumber.Collection == VmQueryCollection.SerialPorts
            && VmSerialPortFields.DebuggerMode.Collection == VmQueryCollection.SerialPorts
            && ReferenceEquals(VmQueryFields.Find("Serial.PipePath"), VmSerialPortFields.PipePath),
            "Serial query fields are registered under VmQueryCollection.SerialPorts");

        var badPort = await VmSerialPortService.SetPipePathAsync(Guid.NewGuid(), 3, @"\\.\pipe\test");
        check(badPort.ErrorKind == ApiErrorKind.InvalidArgument, "SetPipePathAsync rejects out-of-range COM port numbers");

        var badPipe = await VmSerialPortService.SetPipePathAsync(Guid.NewGuid(), 1, @"\\.\pipe\..");
        check(badPipe.ErrorKind == ApiErrorKind.InvalidArgument, "SetPipePathAsync rejects invalid pipe paths");

        var badDbgPort = await VmSerialPortService.SetDebuggerModeAsync(Guid.NewGuid(), 0, true);
        check(badDbgPort.ErrorKind == ApiErrorKind.InvalidArgument, "SetDebuggerModeAsync rejects out-of-range COM port numbers");

        using (WmiApi.UseQuerySource((_, _) => new()))
        {
            var missing = await VmSerialPortService.GetSerialPortsAsync(Guid.NewGuid());
            check(!missing.Success && missing.ErrorKind == ApiErrorKind.NotFound,
                "GetSerialPortsAsync returns NotFound when VM is absent");
        }

        using (WmiApi.UseQuerySource((_, _) => new(new ApiFailure(ApiErrorKind.AccessDenied, 5, ApiErrorSource.Wmi, "simulated serial query error"))))
        {
            var response = await VmSerialPortService.GetSerialPortsAsync(Guid.NewGuid());
            check(!response.Success && response.ErrorKind == ApiErrorKind.AccessDenied,
                "GetSerialPortsAsync surfaces WMI failure without throwing");
        }
    }
}
