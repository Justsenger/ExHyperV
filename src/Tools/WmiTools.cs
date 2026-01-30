using System.Diagnostics;
using System.Management;

namespace ExHyperV.Tools;

public static class WmiTools
{
    // 预设命名空间，默认为hyperv
    public const string HyperVScope = @"\\.\root\virtualization\v2";
    public const string CimV2Scope = @"\\.\root\cimv2";

    public static async Task<List<T>> QueryAsync<T>(string queryStr, Func<ManagementObject, T> mapper, string scope = HyperVScope)
    {
        return await Task.Run(() =>
        {
            var result = new List<T>();
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, queryStr);
                using var collection = searcher.Get();

                foreach (var baseObj in collection)
                {
                    if (baseObj is ManagementObject obj)
                    {
                        try
                        {
                            result.Add(mapper(obj));
                        }
                        finally
                        {
                            obj.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WMI 查询异常 [{scope}]: {ex.Message}");
            }
            return result;
        });
    }

    public static async Task<(bool Success, string Message)> ExecuteMethodAsync(string wqlFilter, string methodName, Dictionary<string, object>? inParameters = null, string scope = HyperVScope)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, wqlFilter);
                using var collection = searcher.Get();
                using var targetObj = collection.Cast<ManagementObject>().FirstOrDefault();

                if (targetObj == null) return (false, "未找到 WMI 目标对象。");

                using var methodParams = targetObj.GetMethodParameters(methodName);
                if (inParameters != null)
                {
                    foreach (var kvp in inParameters) methodParams[kvp.Key] = kvp.Value;
                }

                using var outParams = targetObj.InvokeMethod(methodName, methodParams, null);
                int returnValue = Convert.ToInt32(outParams["ReturnValue"]);

                if (returnValue == 0) return (true, "成功");
                if (returnValue == 4096)
                {
                    string jobPath = (string)outParams["Job"];
                    return WaitForJob(jobPath, scope);
                }

                return (false, $"WMI 错误代码: {returnValue}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    private static (bool Success, string Message) WaitForJob(string jobPath, string scopeStr)
    {
        try
        {
            var scope = new ManagementScope(scopeStr);
            using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);

            while (true)
            {
                job.Get();
                ushort jobState = (ushort)job["JobState"];

                if (jobState == 7) return (true, "成功");
                if (jobState > 7)
                {
                    string err = job["ErrorDescription"]?.ToString();
                    if (string.IsNullOrEmpty(err)) err = job["Description"]?.ToString();
                    return (false, err ?? $"任务执行失败 (状态码: {jobState})");
                }

                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            return (false, $"等待任务时异常: {ex.Message}");
        }
    }
}