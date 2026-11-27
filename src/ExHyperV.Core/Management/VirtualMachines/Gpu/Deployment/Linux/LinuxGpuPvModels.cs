namespace ExHyperV.Models;

/// <summary>已校验的 Linux GPU-PV 部署包摘要信息。</summary>
/// <param name="FilePath">部署包 ZIP 文件的完整路径。</param>
/// <param name="FileName">部署包文件名。</param>
/// <param name="Version">部署包声明的发行版本。</param>
/// <param name="Architecture">部署包目标 CPU 架构（x86_64 或 aarch64）。</param>
/// <param name="KernelCount">包内包含的预编译 dxgkrnl 模块数量。</param>
/// <param name="FileSizeBytes">部署包 ZIP 文件大小，单位为字节。</param>
public sealed record LinuxGpuPvBundleInfo(
    string FilePath,
    string FileName,
    string Version,
    string Architecture,
    int KernelCount,
    long FileSizeBytes);
