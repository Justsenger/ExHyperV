using ExHyperV.Api;

namespace ExHyperV.Services
{
    public class VmPowerService
    {
        // RequestStateChange 状态码（来自 VMComputerSystemState 枚举，ILspy 反编译确认）
        // 2  = Running（启动）
        // 3  = PowerOff（强制关机）
        // 4  = Stopping（软关机，需要 Integration Services）
        // 6  = Saved（保存状态）
        // 9  = Paused（挂起）
        // 10 = Starting（从 Off/Saved 启动，对应 Reboot 场景）
        // 11 = Reset（硬重置）

        public async Task ExecuteControlActionAsync(string vmName, string action)
        {
            string wql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";

            switch (action)
            {
                case "Start":
                    await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)2);
                    break;

                case "TurnOff":
                    await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)3);
                    break;

                case "Stop":
                    // 先尝试软关机（4），失败再强制关机（3）
                    var stopResult = await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)4);
                    if (!stopResult.Success)
                        await WmiApi.InvokeAsync(wql, "RequestStateChange",
                            p => p["RequestedState"] = (ushort)3);
                    break;

                case "Restart":
                    // 先尝试软重启（10），失败再硬重置（11）
                    var restartResult = await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)10);
                    if (!restartResult.Success)
                        await WmiApi.InvokeAsync(wql, "RequestStateChange",
                            p => p["RequestedState"] = (ushort)11);
                    break;

                case "Save":
                    await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)6);
                    break;

                case "Suspend":
                    await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)9);
                    break;
            }
        }
    }
}