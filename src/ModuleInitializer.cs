// ModuleInitializer.cs
using System;
using System.Runtime.CompilerServices;

namespace ExHyperV
{
    internal static class ModuleInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            // 这是整个应用程序中最早可能执行的代码。
            // 在任何类型被访问、任何方法被调用之前，它就会运行。
            Environment.SetEnvironmentVariable("POWERSHELL_TELEMETRY_OPTOUT", "1");
        }
    }
}