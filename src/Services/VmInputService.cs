using System.Diagnostics;
using System.Management;
using ExHyperV.Api;

namespace ExHyperV.Services;

public static class VmInputService
{
    /// <summary>
    /// 发送真正的硬件级 Ctrl+Alt+Del
    /// </summary>
    public static async Task<bool> SendCtrlAltDelAsync(string vmId)
    {
        var result = await WmiApi.InvokeAsync(
            $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId}'",
            "TypeCtrlAltDel",
            scope: WmiScope.HyperV);
        return result.Success;
    }

    /// <summary>
    /// 发送单个按键（扫描码）
    /// </summary>
    public static async Task<bool> SendKeyAsync(string vmId, int scanCode)
    {
        var result = await WmiApi.InvokeAsync(
            $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId}'",
            "TypeKey",
            p => p["keyCode"] = (uint)scanCode,
            WmiScope.HyperV);
        return result.Success;
    }

    /// <summary>
    /// 在虚拟机中输入一段文本 (支持自动处理 Shift)
    /// </summary>
    public static async Task SendTextAsync(string vmId, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(WmiScope.HyperV,
                    $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId}'");
                using var collection = searcher.Get();
                using var keyboard = collection.Cast<ManagementObject>().FirstOrDefault();

                if (keyboard == null) return;

                foreach (char c in text)
                {
                    if (_scanCodeMap.TryGetValue(c, out var info))
                    {
                        if (info.Shift)
                            keyboard.InvokeMethod("PressKey", new object[] { (uint)0x2A });

                        keyboard.InvokeMethod("TypeKey", new object[] { (uint)info.Code });

                        if (info.Shift)
                            keyboard.InvokeMethod("ReleaseKey", new object[] { (uint)0x2A });

                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.VmInput_ErrTextInput, ex.Message));
            }
        });
    }
    private struct KeyInfo { public int Code; public bool Shift; }
    private static readonly Dictionary<char, KeyInfo> _scanCodeMap = CreateScanCodeMap();
    private static Dictionary<char, KeyInfo> CreateScanCodeMap()
    {
        var map = new Dictionary<char, KeyInfo>();

        string nums = "1234567890";
        int[] numCodes = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        for (int i = 0; i < nums.Length; i++) map[nums[i]] = new KeyInfo { Code = numCodes[i], Shift = false };

        string numSymbols = "!@#$%^&*()";
        for (int i = 0; i < numSymbols.Length; i++) map[numSymbols[i]] = new KeyInfo { Code = numCodes[i], Shift = true };

        string alpha = "qwertyuiopasdfghjklzxcvbnm";
        int[] alphaCodes = { 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 30, 31, 32, 33, 34, 35, 36, 37, 38, 44, 45, 46, 47, 48, 49, 50 };
        for (int i = 0; i < alpha.Length; i++)
        {
            map[alpha[i]] = new KeyInfo { Code = alphaCodes[i], Shift = false };
            map[char.ToUpper(alpha[i])] = new KeyInfo { Code = alphaCodes[i], Shift = true };
        }

        map[' '] = new KeyInfo { Code = 57, Shift = false };
        map['\r'] = new KeyInfo { Code = 28, Shift = false };
        map['\n'] = new KeyInfo { Code = 28, Shift = false };
        map['-'] = new KeyInfo { Code = 12, Shift = false }; map['_'] = new KeyInfo { Code = 12, Shift = true };
        map['='] = new KeyInfo { Code = 13, Shift = false }; map['+'] = new KeyInfo { Code = 13, Shift = true };
        map['['] = new KeyInfo { Code = 26, Shift = false }; map['{'] = new KeyInfo { Code = 26, Shift = true };
        map[']'] = new KeyInfo { Code = 27, Shift = false }; map['}'] = new KeyInfo { Code = 27, Shift = true };
        map[';'] = new KeyInfo { Code = 39, Shift = false }; map[':'] = new KeyInfo { Code = 39, Shift = true };
        map['\''] = new KeyInfo { Code = 40, Shift = false }; map['"'] = new KeyInfo { Code = 40, Shift = true };
        map[','] = new KeyInfo { Code = 51, Shift = false }; map['<'] = new KeyInfo { Code = 51, Shift = true };
        map['.'] = new KeyInfo { Code = 52, Shift = false }; map['>'] = new KeyInfo { Code = 52, Shift = true };
        map['/'] = new KeyInfo { Code = 53, Shift = false }; map['?'] = new KeyInfo { Code = 53, Shift = true };
        map['\\'] = new KeyInfo { Code = 43, Shift = false }; map['|'] = new KeyInfo { Code = 43, Shift = true };
        map['`'] = new KeyInfo { Code = 41, Shift = false }; map['~'] = new KeyInfo { Code = 41, Shift = true };

        return map;
    }
}