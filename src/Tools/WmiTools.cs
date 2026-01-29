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

    public static async Task<bool> ExecuteMethodAsync(string wqlFilter, string methodName, Dictionary<string, object>? inParameters = null, string scope = HyperVScope)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, wqlFilter);
                using var collection = searcher.Get();
                using var targetObj = collection.Cast<ManagementObject>().FirstOrDefault();

                if (targetObj == null)
                {
                    Debug.WriteLine("WMI 错误: 未找到目标对象。");
                    return false;
                }

                using var methodParams = targetObj.GetMethodParameters(methodName);
                if (inParameters != null)
                {
                    foreach (var kvp in inParameters)
                    {
                        methodParams[kvp.Key] = kvp.Value;
                    }
                }

                using var outParams = targetObj.InvokeMethod(methodName, methodParams, null);

                int returnValue = Convert.ToInt32(outParams["ReturnValue"]);

                if (returnValue == 0) return true;
                if (returnValue == 4096)
                {
                    string jobPath = (string)outParams["Job"];
                    return WaitForJob(jobPath, scope);
                }

                Debug.WriteLine($"WMI 方法 '{methodName}' 执行失败。返回值: {returnValue}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WMI 执行异常: {ex.Message}");
                return false;
            }
        });
    }

    private static bool WaitForJob(string jobPath, string scopeStr)
    {
        try
        {
            var scope = new ManagementScope(scopeStr);
            using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);

            while (true)
            {
                job.Get();
                ushort jobState = (ushort)job["JobState"];

                if (jobState == 7) return true;
                if (jobState > 7 && jobState <= 10)
                {
                    string err = job["ErrorDescription"]?.ToString() ?? "未知错误";
                    Debug.WriteLine($"WMI 任务失败: {err}");
                    return false;
                }

                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WMI 等待任务异常: {ex.Message}");
            return false;
        }
    }
}