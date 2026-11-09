using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal sealed class SaveVmCredentialsRequest
{
    public string Revision { get; init; } = "";
    public string ResourceRunId { get; init; } = "";
    public string TemplateVmId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
}

internal sealed class VmCredentialStore(string? directory = null)
{
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExHyperV.Core.Tests", "Credentials");
    internal string PathFor(Guid id) => Path.Combine(directory ?? DefaultDirectory, id.ToString("D") + ".clixml");

    internal static Guid Validate(SaveVmCredentialsRequest request, RegistrationSnapshot registration, InspectionReport report)
    {
        if (request.Revision != registration.Revision || request.ResourceRunId != report.RunId || report.Running)
            throw new InvalidOperationException("资源选择已更新，请重新检查后保存登录信息。");
        if (!Guid.TryParse(request.TemplateVmId, out var id) || id == Guid.Empty
            || !Guid.TryParse(registration.Configuration.TemplateVmId, out var selected) || id != selected)
            throw new ArgumentException("请先保存虚拟机模板选择。");
        ValidateInput(request.UserName, request.Password);
        return id;
    }

    internal static void ValidateInput(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 256 || userName.Any(char.IsControl)
            || string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("请填写虚拟机账号和密码。");
    }

    internal async Task SaveAsync(Guid id, string userName, string password)
    {
        ValidateInput(userName, password);
        if (id == Guid.Empty) throw new ArgumentException("虚拟机标识无效。");
        string path = PathFor(id), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false)
        };
        start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        // Secrets travel over stdin, never in command arguments, environment, reports or logs.
        const string script = """
            [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
            $ErrorActionPreference='Stop'
            try {
                $a=ConvertFrom-Json ([Console]::In.ReadToEnd())
                $secret=ConvertTo-SecureString $a.password -AsPlainText -Force
                $credential=[Management.Automation.PSCredential]::new($a.userName,$secret)
                $credential|Export-Clixml -LiteralPath $a.path
                $saved=Import-Clixml -LiteralPath $a.path
                if($saved.UserName -cne $a.userName -or $saved.GetNetworkCredential().Password -cne $a.password){exit 1}
            } catch { exit 1 }
            """;
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("保存登录信息失败。");
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { path = temporary, userName = userName.Trim(), password }).AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new IOException("保存登录信息超时，请重试。");
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
            }
            if (process.ExitCode != 0 || !File.Exists(temporary)) throw new IOException("登录信息加密保存失败。");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
