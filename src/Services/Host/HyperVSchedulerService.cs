using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace ExHyperV.Services;

public enum HyperVSchedulerType
{
    Classic,
    Core,
    Root,
    Unknown
}

public static class HyperVSchedulerService
{
    public static HyperVSchedulerType GetSchedulerType()
    {
        try
        {
            string query = "*[System[Provider[@Name='Microsoft-Windows-Hyper-V-Hypervisor'] and (EventID=2)]]";
            var eventQuery = new EventLogQuery("System", PathType.LogName, query)
            {
                ReverseDirection = true
            };

            using var logReader = new EventLogReader(eventQuery);
            var record = logReader.ReadEvent();

            if (record != null && record.Properties.Count > 0)
            {
                ushort code = Convert.ToUInt16(record.Properties[0].Value);
                return code switch
                {
                    1 => HyperVSchedulerType.Classic,
                    2 => HyperVSchedulerType.Classic,
                    3 => HyperVSchedulerType.Core,
                    4 => HyperVSchedulerType.Root,
                    _ => HyperVSchedulerType.Unknown,
                };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.HyperVScheduler_LogEventLogQueryFail, ex.Message));
        }

        return HyperVSchedulerType.Unknown;
    }

    public static async Task<bool> SetSchedulerTypeAsync(HyperVSchedulerType type)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = $"/set hypervisorschedulertype {type.ToString().ToLower()}",
                Verb = "runas",          // �Թ���Ա�������
                UseShellExecute = true,             // Verb=runas ����Ϊ true
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start bcdedit.exe");

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.HyperVScheduler_LogBcdeditFail, ex.Message));
            return false;
        }
    }
}