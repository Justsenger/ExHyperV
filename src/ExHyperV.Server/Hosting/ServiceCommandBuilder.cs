using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ExHyperV.Server.Hosting;

internal static class ServiceCommandBuilder
{
    internal static string ValidateName(string name) =>
        Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z", RegexOptions.CultureInvariant)
            ? name : throw new ArgumentException("服务名使用 1 至 80 位 ASCII 字母、数字、点、下划线或连字符，首位使用字母或数字。");

    internal static ProcessStartInfo Build(LaunchOptions options, string executable, string[] args)
    {
        string name = ValidateName(options.ServiceName);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        if (options.Command == Command.Uninstall)
        {
            info.ArgumentList.Add("delete");
            info.ArgumentList.Add(name);
            return info;
        }
        if (options.Command != Command.Install) throw new ArgumentException("需要安装或卸载服务命令。");
        var command = new List<string> { Path.GetFullPath(executable) };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command.Add(typeof(Program).Assembly.Location);
        command.AddRange(["--service", "--service-name", name, "--state-dir", options.StateDirectory]);
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--read-only": case "--read-write": command.Add(args[index]); break;
                case "--url": case "--toolsets": case "--cors-origins":
                    command.Add(args[index]); command.Add(args[++index]); break;
                case "--state-dir": case "--service-name": index++; break;
            }
        }
        foreach (string arg in new[] { "create", name, "binPath=", string.Join(" ", command.Select(Quote)), "start=", "auto" })
            info.ArgumentList.Add(arg);
        return info;
    }

    private static string Quote(string value)
    {
        if (value.Contains('"') || value.Any(char.IsControl)) throw new ArgumentException("服务启动参数含有无效字符。");
        // Windows doubles trailing backslashes inside a quoted argument.
        return "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
    }

    internal static async Task<int> Execute(LaunchOptions options, string[] args)
    {
        using var process = Process.Start(Build(options, Environment.ProcessPath!, args))!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
