---
trigger: always_on
---

# ExHyperV WinUI3 构建与启动规范

在 `ExHyperV` 仓库中构建或测试 `ExHyperV.WinUI3` 时，始终按以下步骤执行：

1. **构建前自动结束运行中的进程**：不要询问用户，先执行以下命令关闭占用的 `ExHyperV.WinUI3` 进程，避免 `.exe` 与附属资源文件锁冲突（若因该进程以管理员权限运行导致 `Stop-Process` 返回 `Access is denied`，直接停下来告知用户先关闭窗口，勿绕过权限限制）：
   ```powershell
   Get-Process -Name "ExHyperV.WinUI3" -ErrorAction SilentlyContinue | Stop-Process -Force
   Start-Sleep -Milliseconds 300
   ```
2. **使用 Visual Studio MSBuild 构建**：必须使用 Visual Studio `MSBuild.exe`（不要使用 `dotnet build`，`IMAPI2FS` 的 `ResolveComReference` 在 `dotnet` CLI 下会失败）：
   ```powershell
   & "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" src\ExHyperV.WinUI3\ExHyperV.WinUI3.csproj /p:Configuration=Debug /p:Platform=ARM64 /v:m
   ```
3. **构建完成后自动启动**：若构建成功（退出码 `0`）且未明确要求不运行 UI，则自动启动刚刚构建好的应用供用户直接验证：
   ```powershell
   Start-Process "c:\Users\saniye\Documents\GitHub\ExHyperV\src\ExHyperV.WinUI3\bin\ARM64\Debug\net10.0-windows10.0.26100.0\win-arm64\ExHyperV.WinUI3.exe"
   ```
