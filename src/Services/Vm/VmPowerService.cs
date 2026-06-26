using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public static class VmPowerService
    {
        // RequestStateChange 状态码（来自 VMComputerSystemState 枚举，ILspy 反编译确认）
        // 2  = Running（启动）
        // 3  = PowerOff（强制关机）
        // 4  = Stopping（软关机，需要 Integration Services）
        // 6  = Saved（保存状态）
        // 9  = Paused（挂起）
        // 10 = Starting（从 Off/Saved 启动，对应 Reboot 场景）
        // 11 = Reset（硬重置）

        // 回传引擎真实成败：RequestStateChange 经 WmiApi.InvokeAsync 会等异步 Job 完成并带回其 ErrorDescription。
        // 返回 ApiResponse 而非 void：启动失败(配置错误/资源不足/GPU 分区不可用等)不再静默，调用方据此弹错。
        public static async Task<ApiResponse> ExecuteControlActionAsync(string vmName, string action)
        {
            string wql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";

            switch (action)
            {
                case "Start":
                    return await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)2);

                case "TurnOff":
                    return await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)3);

                case "Stop":
                    // 先尝试软关机（4），失败再强制关机（3）
                    var stopResult = await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)4);
                    return stopResult.Success ? stopResult
                        : await WmiApi.InvokeAsync(wql, "RequestStateChange", p => p["RequestedState"] = (ushort)3);

                case "Restart":
                    // 先尝试软重启（10），失败再硬重置（11）
                    var restartResult = await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)10);
                    return restartResult.Success ? restartResult
                        : await WmiApi.InvokeAsync(wql, "RequestStateChange", p => p["RequestedState"] = (ushort)11);

                case "Save":
                    return await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)6);

                case "Suspend":
                    return await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)9);

                default:
                    return ApiResponse.Ok();
            }
        }
    }
}