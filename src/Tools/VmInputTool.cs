using System.Management;
using System.Diagnostics;

namespace ExHyperV.Tools;

public static class VmInputTool
{
    /// <summary>
    /// 发送真正的硬件级 Ctrl+Alt+Del
    /// </summary>
    public static async Task<bool> SendCtrlAltDelAsync(string vmId)
    {
        string filter = $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId}'";
        var result = await WmiTools.ExecuteMethodAsync(filter, "TypeCtrlAltDel");
        return result.Success;
    }

    /// <summary>
    /// 发送单个按键（扫描码）
    /// </summary>
    public static async Task<bool> SendKeyAsync(string vmId, int scanCode)
    {
        string filter = $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId}'";
        var args = new Dictionary<string, object> { { "keyCode", (uint)scanCode } };
        var result = await WmiTools.ExecuteMethodAsync(filter, "TypeKey", args);
        return result.Success;
    }

    /// <summary>
    /// 在虚拟机中输入一段文本 (支持自动处理 Shift)
    /// </summary>
    public static async Task SendTextAsync(string vmId, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 为了效率，这里不直接调用 WmiTools.ExecuteMethodAsync（因为那个每发一个字符都会重新查询一遍 WMI 对象）
        // 我们在这里手动实现一个高效的批量发送逻辑
        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(WmiTools.HyperVScope,
                    $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId}'");
                using var collection = searcher.Get();
                using var keyboard = collection.Cast<ManagementObject>().FirstOrDefault();

                if (keyboard == null) return;

                foreach (char c in text)
                {
                    if (_scanCodeMap.TryGetValue(c, out var info))
                    {
                        // 1. 如果需要 Shift (如大写字母或符号)
                        if (info.Shift)
                            keyboard.InvokeMethod("PressKey", new object[] { (uint)0x2A }); // 左 Shift (42)

                        // 2. 打字 (按下+弹起)
                        keyboard.InvokeMethod("TypeKey", new object[] { (uint)info.Code });

                        // 3. 释放 Shift
                        if (info.Shift)
                            keyboard.InvokeMethod("ReleaseKey", new object[] { (uint)0x2A });

                        // 给予微小的硬件响应延迟，防止输入太快虚拟机漏字
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"文本输入异常: {ex.Message}");
            }
        });
    }

    // 定义内部映射结构
    private struct KeyInfo { public int Code; public bool Shift; }
    private static readonly Dictionary<char, KeyInfo> _scanCodeMap = CreateScanCodeMap();

    private static Dictionary<char, KeyInfo> CreateScanCodeMap()
    {
        var map = new Dictionary<char, KeyInfo>();

        // 基础数字
        string nums = "1234567890";
        int[] numCodes = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        for (int i = 0; i < nums.Length; i++) map[nums[i]] = new KeyInfo { Code = numCodes[i], Shift = false };

        // 数字键上的符号
        string numSymbols = "!@#$%^&*()";
        for (int i = 0; i < numSymbols.Length; i++) map[numSymbols[i]] = new KeyInfo { Code = numCodes[i], Shift = true };

        // 字母 (a-z)
        string alpha = "qwertyuiopasdfghjklzxcvbnm";
        int[] alphaCodes = { 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 30, 31, 32, 33, 34, 35, 36, 37, 38, 44, 45, 46, 47, 48, 49, 50 };
        for (int i = 0; i < alpha.Length; i++)
        {
            map[alpha[i]] = new KeyInfo { Code = alphaCodes[i], Shift = false };
            map[char.ToUpper(alpha[i])] = new KeyInfo { Code = alphaCodes[i], Shift = true };
        }

        // 其他常用符号
        map[' '] = new KeyInfo { Code = 57, Shift = false };
        map['\r'] = new KeyInfo { Code = 28, Shift = false }; // 回车
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