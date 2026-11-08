# ExHyperV.Mcp.Tests

检查 AI 客户端到 Core 之间的接口适配。测试使用内存对象、临时目录和仅监听回环地址的 MCP 子进程，覆盖对象释放、画面租约、操作取消、请求去重、任务恢复、工具权限、返回结构及 stdio / HTTP 通信。

项目引用 ExHyperV.Mcp，构建时同步编译当前 Core。与真实虚拟机、设备、宿主配置有关的功能验证由 ExHyperV.Core.Tests 承担。

Windows PowerShell 5.1：

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File src/ExHyperV.Mcp.Tests/Test-Mcp.ps1
```

需要 .NET 10 SDK、VS 2026 MSBuild 和桌面构建组件。`-Runtime win-x64` 或 `-Runtime win-arm64` 指定架构。输出位于 `.artifacts/mcp/tests/<架构>/`，其中 `results/` 保存 TRX 报告，`build.log` 保存构建日志。

`CorePublicContractHasBeenReviewed` 检查公开签名、参数、文档分类和只读声明。Core 更新触发该项失败时，先核对差异、修正 MCP 映射并运行测试，再将新构建 `ExHyperV.Mcp.exe --contract-fingerprint` 输出写入 `src/ExHyperV.Mcp/Api/Contracts.sha256`。测试脚本保持基线只读。

GitHub 的“MCP 接入检查”在 Core、MCP 或本项目变更时分别运行 ARM64 与 x64 检查。
