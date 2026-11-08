using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services;

// 只补当前系统自己的组件，不修改 SKU，不提供任意包安装入口。
internal static class HyperVFeatureInstaller
{
    internal static Task<ApiResponse> EnableAsync() => EnableAsync(new WindowsBackend());

    internal interface IBackend
    {
        (bool Server, bool Home) ReadEdition();
        Task<ApiResponse<DismFeatureState>> GetFeatureStateAsync(string feature);
        string[] FindPackages();
        Task<ApiResponse> AddPackageAsync(string path);
        Task<DismFeatureOperation> EnableFeatureAsync(string feature);
    }

    internal static async Task<ApiResponse> EnableAsync(IBackend backend)
    {
        var steps = new List<OperationStepResult>();
        bool restart = false;
        bool submitted = false;
        ApiResponse Finish(ApiResponse response) => response.WithRestartRequired(restart).WithSteps(steps);
        try
        {
            var edition = backend.ReadEdition();
            string feature = edition.Server ? "Microsoft-Hyper-V" : "Microsoft-Hyper-V-All";
            var query = await backend.GetFeatureStateAsync(feature).ConfigureAwait(false);
            if (!query.Success) return Finish(QueryFailure(feature, query));
            if (query.IsEmpty)
            {
                // 家庭版兼容入口只使用原已支持的本机客户端包，不转换 SKU。
                if (!edition.Home || edition.Server)
                    return Finish(ApiResponse.Fail("系统未识别 Hyper-V 功能，且当前系统没有受支持的本机组件登记路径。",
                        kind: ApiErrorKind.Unsupported));
                string[] packages = backend.FindPackages();
                if (packages.Length == 0)
                    return Finish(ApiResponse.Fail("缺少可用的 Hyper-V 安装清单：未找到本机客户端基础 MUM。",
                        kind: ApiErrorKind.Unsupported));
                foreach (string package in packages)
                {
                    submitted = true;
                    var added = await backend.AddPackageAsync(package).ConfigureAwait(false);
                    if (added.RequiresRestart) added = added.WithRestartReason(RestartReason.PackageRegistration);
                    restart |= added.RequiresRestart;
                    steps.Add(OperationStepResult.From("AddHomePackage", added) with { ResourceId = Path.GetFileName(package) });
                    if (!added.Success && added.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired or ApiErrorKind.AccessDenied)
                        return Finish(added);
                }
                // 并存的旧修订或不适用候选不要求全部成功；保留逐包诊断，随后只重查一次。
                query = await backend.GetFeatureStateAsync(feature).ConfigureAwait(false);
                if (!query.Success) return Finish(QueryFailure(feature, query));
                if (query.IsEmpty)
                    return Finish(ApiResponse.Fail("登记后系统仍未识别 Hyper-V；已结束本次启用，不重复登记。详见组件步骤结果。",
                        kind: ApiErrorKind.Unsupported));
            }

            string[] required = edition.Server
                ? [feature, "Microsoft-Hyper-V-Offline", "Microsoft-Hyper-V-Online"]
                : [feature, "Microsoft-Hyper-V", "Microsoft-Hyper-V-Hypervisor", "Microsoft-Hyper-V-Services"];
            var states = new Dictionary<string, DismFeatureState>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in required)
            {
                var state = name == feature ? query : await backend.GetFeatureStateAsync(name).ConfigureAwait(false);
                if (!state.HasData) return Finish(QueryFailure(name, state));
                states[name] = state.Data;
            }
            // 在任何功能修改前核对整组，不能先修改 A 才发现 B 处于待完成或异常状态。
            foreach (var (name, state) in states)
            {
                var invalid = ValidateState(name, state, allowInstallPending: restart);
                if (invalid != null) return Finish(invalid);
            }
            foreach (string name in required)
            {
                // 前面的启用可能已连带改变子功能，执行前重新查询，避免重复提交。
                var state = await backend.GetFeatureStateAsync(name).ConfigureAwait(false);
                if (!state.HasData) return Finish(QueryFailure(name, state));
                var invalid = ValidateState(name, state.Data, allowInstallPending: restart);
                if (invalid != null) return Finish(invalid);
                if (state.Data == DismFeatureState.Installed || state.Data == DismFeatureState.InstallPending) continue;
                submitted = true;
                var enabled = await backend.EnableFeatureAsync(name).ConfigureAwait(false);
                if (enabled.Response.RequiresRestart)
                    enabled = enabled with { Response = enabled.Response.WithRestartReason(RestartReason.WindowsFeatureChange) };
                restart |= enabled.Response.RequiresRestart;
                steps.Add(OperationStepResult.From("EnableFeature", enabled.Response) with { ResourceId = name });
                steps.AddRange(enabled.Response.Steps.Select(step => step with { ResourceId = name }));
                if (!enabled.Response.Success) return Finish(enabled.Response);
                // 每次调用均结束映像会话；这里重新打开查询，也覆盖 RELOAD_IMAGE_SESSION_REQUIRED。
                var actual = await backend.GetFeatureStateAsync(name).ConfigureAwait(false);
                if (!actual.HasData) return Finish(QueryFailure(name, actual));
                if (actual.Data != DismFeatureState.Installed && !(restart && actual.Data == DismFeatureState.InstallPending))
                    return Finish(ApiResponse.Fail($"DISM 已返回，但 {name} 尚未确认启用，当前状态为 {actual.Data}。未重复提交启用。",
                        enabled.NativeCode, ApiErrorSource.Win32, kind: ApiErrorKind.InvalidState));
            }
            // 顶层 Enabled 不替代必要子功能复核；本检查仍不等于文件完整性或运行就绪。
            foreach (string name in required)
            {
                var actual = await backend.GetFeatureStateAsync(name).ConfigureAwait(false);
                if (!actual.HasData) return Finish(QueryFailure(name, actual));
                if (actual.Data != DismFeatureState.Installed && !(restart && actual.Data == DismFeatureState.InstallPending))
                    return Finish(ApiResponse.Fail($"必要功能 {name} 尚未确认启用，当前状态为 {actual.Data}。",
                        kind: ApiErrorKind.InvalidState));
            }
            return Finish(ApiResponse.Ok());
        }
        catch (Exception error)
        {
            return Finish(ApiResponse.Fail($"Hyper-V 组件处理未确认完成：{error.Message}", error.HResult,
                ApiErrorSource.Win32, error,
                submitted ? ApiErrorKind.OutcomeUnknown : error is UnauthorizedAccessException
                    ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed));
        }
    }

    private static ApiResponse? ValidateState(string name, DismFeatureState state, bool allowInstallPending)
        => state switch
        {
            DismFeatureState.Installed or DismFeatureState.Staged or DismFeatureState.Removed => null,
            DismFeatureState.InstallPending when allowInstallPending => null,
            DismFeatureState.InstallPending or DismFeatureState.UninstallPending => ApiResponse.Fail(
                $"功能 {name} 仍有待完成的组件变更（{state}）；未继续启用，请在变更完成后重新查询。",
                kind: ApiErrorKind.InvalidState),
            _ => ApiResponse.Fail($"功能 {name} 的状态为 {state}，未继续修改。", kind: ApiErrorKind.InvalidState)
        };

    private static ApiResponse QueryFailure(string name, ApiResponse<DismFeatureState> result)
        => result.Success ? ApiResponse.Fail($"系统未识别必要的 Hyper-V 功能 {name}，未继续启用。", kind: ApiErrorKind.Unsupported)
            : ApiResponse.Fail($"无法查询 Hyper-V 功能 {name}：{result.Error}", result.Code,
                result.ErrorSource, result.Exception, result.ErrorKind);
    // CBS 包文件名包含包名、发布者、公用架构、语言和包自身版本。
    // 保留语言资源和本机并存的修订号；最终适用性与签名由 Windows 检查。
    internal static string[] SelectLocalPackages(IEnumerable<string> paths, string architecture)
    {
        const string prefix = "Microsoft-Hyper-V-";
        const string suffix = "-Package";
        var candidates = new List<string>();
        bool hasClientBase = false;
        foreach (string path in paths)
        {
            if (!Path.GetExtension(path).Equals(".mum", StringComparison.OrdinalIgnoreCase)) continue;
            string[] identity = Path.GetFileNameWithoutExtension(path).Split('~');
            if (identity.Length != 5 || !identity[1].Equals("31bf3856ad364e35", StringComparison.OrdinalIgnoreCase)
                || !identity[2].Equals(architecture, StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(identity[4], out var version) || version.Revision < 0) continue;
            string name = identity[0];
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || name.Length <= prefix.Length + suffix.Length) continue;
            string family = name[prefix.Length..^suffix.Length];
            if (!ClientPackageFamilies.Contains(family)) continue;
            candidates.Add(path);
            if (family.Equals("ClientEdition", StringComparison.OrdinalIgnoreCase) && identity[3].Length == 0)
                hasClientBase = true;
        }
        return hasClientBase ? candidates.Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    }

    private static readonly HashSet<string> ClientPackageFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClientEdition", "ClientEdition-WOW64", "Hypervisor", "Hypervisor-merged", "Hypervisor-onecore",
        "Offline", "Offline-merged", "Offline-Common", "Offline-Common-merged", "Offline-Core-Group",
        "Offline-Core-Group-merged", "Offline-Core-Group-onecore", "Offline-Core-Group-vm",
        "Online", "Online-Services", "Online-Services-merged", "Online-Services-vm",
        "Package-base", "Package-base-merged", "Services"
    };

    private sealed class WindowsBackend : IBackend
    {
        public (bool Server, bool Home) ReadEdition()
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var product = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            string? type = product?.GetValue("ProductType") as string;
            if (type is "ServerNT" or "LanmanNT") return (true, false);
            if (type != "WinNT") throw new InvalidOperationException("无法识别 Windows 产品类型，未提交组件修改。");
            using var version = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string? edition = version?.GetValue("EditionID") as string;
            return (false, edition is "Core" or "CoreN" or "CoreSingleLanguage" or "CoreCountrySpecific" or "CoreARM");
        }

        public Task<ApiResponse<DismFeatureState>> GetFeatureStateAsync(string feature) => DismApi.GetFeatureStateAsync(feature);
        public Task<DismFeatureOperation> EnableFeatureAsync(string feature) => DismApi.EnableFeatureWithResultAsync(feature, enableAll: true);
        public string[] FindPackages()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "servicing", "Packages");
            string architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "amd64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => throw new PlatformNotSupportedException("无法识别本机 Windows 组件架构。")
            };
            return SelectLocalPackages(Directory.EnumerateFiles(directory, "Microsoft-Hyper-V-*.mum"), architecture);
        }

        public async Task<ApiResponse> AddPackageAsync(string path)
        {
            // .mum 补装沿用已验证的系统命令；DismAddPackage 的公开合同未承诺接受 .mum。
            // 不使用 shell、UAC 或 PATH 查找；无重启、无超时杀进程，等组件维护实际结束。
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            var start = new ProcessStartInfo(Path.Combine(windows, system, "dism.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "/Online", "/NoRestart", "/Add-Package", "/PackagePath:" + path })
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            bool started = false;
            try
            {
                started = process.Start();
                if (!started) return ApiResponse.Fail("DISM 组件安装进程未启动。", kind: ApiErrorKind.OperationFailed);
                Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                int code = process.ExitCode;
                return code == 0 ? ApiResponse.Ok() : code == 3010 ? ApiResponse.Ok().WithRestartReason(RestartReason.PackageRegistration) : ApiResponse.Fail(
                    $"DISM 添加组件 {Path.GetFileName(path)} 失败：0x{code:X8}；详细信息见系统 DISM/CBS 日志。",
                    code, ApiErrorSource.Win32, kind: code is 5 or unchecked((int)0x80070005)
                        ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
            }
            catch (Exception error)
            {
                return ApiResponse.Fail($"DISM 组件安装未确认完成：{error.Message}", error.HResult,
                    ApiErrorSource.Win32, error, started ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed);
            }
        }
    }
}
