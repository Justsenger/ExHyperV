# ExHyperV.Mcp

让 AI 客户端通过 MCP 调用当前 ExHyperV.Core 的完整公开 API。项目与 Core、WPF、Web 并列，引用源码项目，构建时同步编译 Core。

调用关系：**AI 客户端 → MCP → Core → Windows / Hyper-V**。管理规则、操作顺序和能力判断继续由 Core 执行。

## 构建与接入

环境：Windows、.NET 10 SDK、VS 2026 MSBuild 和桌面构建组件。Windows PowerShell 5.1 可执行：

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File src/ExHyperV.Mcp/Build.ps1
```

默认按本机架构发布到 `.artifacts/mcp/win-arm64` 或 `.artifacts/mcp/win-x64`，包含 .NET 运行时。分发时保留整个目录。`-Runtime win-x64` 指定架构；`-FrameworkDependent` 使用机器已安装的运行时。

采用 stdio：客户端启动 EXE，通过标准输入／输出交换 MCP 消息；日志写到标准错误。采用 `mcpServers` 配置格式的客户端可填写：

```json
{
  "mcpServers": {
    "exhyperv": {
      "command": "D:\\Tools\\ExHyperV.Mcp\\ExHyperV.Mcp.exe",
      "args": []
    }
  }
}
```

替换为实际 EXE 路径，配置字段按客户端要求填写。客户端负责启动和结束进程。**管理调用要求管理员身份启动客户端**；`server_info` 返回 MCP 实际令牌状态。

## HTTP 接入

同一个 EXE 支持 **Streamable HTTP**，与 stdio 使用同一份完整工具目录。先在服务器上启动：

```powershell
.\ExHyperV.Mcp.exe --transport http
```

客户端添加 HTTP MCP，地址填写 **`http://127.0.0.1:5188/mcp`**。服务持续运行，Ctrl+C 正常退出。`/mcp` 是协议端点；使用 MCP 客户端连接。

开放到内网时，先设置访问令牌，再指定监听地址。以下 PowerShell 5.1 命令生成本轮令牌：

```powershell
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$env:EXHYPERV_MCP_TOKEN = [Convert]::ToBase64String($bytes)
.\ExHyperV.Mcp.exe --transport http --url http://0.0.0.0:5188
```

在客户端填写服务器实际 IP，以及同一令牌。采用 `mcpServers` 格式的客户端示例：

```json
{
  "mcpServers": {
    "exhyperv": {
      "url": "http://服务器IP:5188/mcp",
      "headers": { "Authorization": "Bearer 访问令牌" }
    }
  }
}
```

令牌保存在启动进程的 `EXHYPERV_MCP_TOKEN` 环境变量中，长度为 32 至 512 位。后续启动可复用保管好的令牌；服务器配置令牌后，本机连接也需要携带它。防火墙放行范围按实际内网设置。HTTP 内网访问适用于可信网络或加密隧道；HTTPS 可通过 ASP.NET Core 的 Kestrel 证书配置启用，再将 `--url` 改为 `https://地址:端口`。

**HTTP 管理调用要求以管理员身份启动服务器 EXE**，客户端只需网络连接和令牌。所有获准客户端共同使用一个管理进程，对象、事件和任务编号在这个进程中共享；进程重启后重新取得编号。后台任务在客户端断线后继续运行，重连可用原编号查询和收尾。临时 HTTP 连接结束时保留这些任务；服务器正常退出时执行原有资源清理。

协议采用 SDK 的无会话 HTTP 模式，Core 对象与后台任务由 MCP 进程独立保存。HTTP 请求与 stdio 都支持 `core_operation_status` 轮询进度、事件读取及用户决定回复。服务校验 Host、浏览器 Origin 和访问令牌，浏览器访问限同源。

## 完整 API 入口

API 目录从随程序发布的 Core 程序集读取，中文说明来自该次编译的 XML 注释和 Core 原始 API 文档。API 标识包含完整签名，重载各有自己的标识。

默认搜索展示业务成员；`includeAuxiliary: true` 同时显示编译器生成的比较、克隆等辅助成员。`group` 可按 `host`、`vm`、`devices`、`console`、`utilities`、`core` 筛选。`core_api_describe` 的 `inputSchema` 和 `valueSchema` 分别给出参数及返回值结构，保留 Core 中文说明和单位。

分类沿用 Core 文档，精确到各成员的查询、修改或实例操作；嵌套类型保留自身归属。`core_api_describe` 同时提供字段的单位、适用条件、参考链接与修改说明，以及声明的异常；`fieldModification` 保留修改类别、输入参数和提交方式。`core_api_coverage` 返回文档源码摘要，便于核对所用 Core 与接口材料。

| 任务 | MCP 工具 |
| --- | --- |
| 按中文、类型或方法查找 API | `core_api_list` |
| 阅读参数、返回值和规则 | `core_api_describe`、`core_api_guide` |
| 查看接入范围 | `core_api_coverage` |
| 调用方法、构造对象 | `core_api_call` |
| 读取、设置属性或字段 | `core_api_get`、`core_api_set` |
| 查看执行进度、取消、回答确认 | `core_operation_status`、`core_operation_cancel`、`core_callback_reply` |
| 结束后清理操作记录 | `core_operation_release` |
| 查看、释放持有对象 | `core_objects`、`core_object_release` |
| 订阅、接收、结束事件 | `core_event_subscribe`、`core_event_read`、`core_event_unsubscribe` |
| 传递二进制参数和分块读取结果 | `core_binary_create`、`core_binary_read` |
| 直接查看画面或截图 | `core_image_read` |

目录涵盖 Core 公开的管理接口、工具、模型和业务接口，包括导入导出、磁盘和交换机、PCIe、GPU、宿主设置、控制台、键鼠、剪贴板及监控。枚举、泛型、重载、索引器、输出参数也保留。`init` 属性在构造模型时填写，创建后的快照按只读约定使用。

范围以 `ExHyperV` 命名空间下的公开契约为准。编译时嵌入的 COM 互操作类型和资源文案类属于实现材料。Core 自身标为尚未实现或当前宿主缺少支持的能力，仍按 Core 原结果报告。

原有 34 个快捷工具继续可用，适合常用查询、创建、删除、电源、内存、CPU 和 GPU 部署。完整目录负责其余调用，也可直接用于这些功能。

`host_hyperv_status` 使用 Core 的详细环境诊断：`diagnostics` 包含监控程序、VMMS、WMI 各阶段状态、首个问题和原始错误。`isInstalled` 保留三态：`true` 为已确认存在 VMMS，`false` 为已确认缺失，`null` 为本次尚未确认。按阶段状态区分 `Unknown` 和 `NotChecked`，安装、修复等决策以详细结果为准。

虚拟机文件位置通过 `vm_query_fields` 查找 `Identity.ConfigurationFilePath`、`Identity.GuestStateFilePath`、`Identity.RuntimeStateFilePath`，分别读取配置、VMGS 和 VMRS 文件的完整路径；原 `Identity.ConfigurationRoot` 已由 Core 移除。

## 调用方法

1. `core_api_list` 搜索任务，例如“导入”或 `VmNetworkService`。`kind` 可选 Method、Constructor、Type、Property、Field、Event、All。
2. 将返回的 `apiId` 交给 `core_api_describe`，查看准确参数与业务规则。
3. 调用 `core_api_call`，`arguments` 按参数名填写。静态方法直接调用；实例方法先构造或取得对象，再填写 `target`。
4. 默认在 `data.operationId` 返回操作编号，使用 `core_operation_status` 读取进度与最终结果。`background: false` 等待本次调用完成。
5. 用完资源后结束事件订阅、释放对象，再清理操作记录。

例如，读取内置控制台后端目录：

```json
{
  "apiId": "M:ExHyperV.Services.ConsoleService.GetBackends",
  "background": false
}
```

普通查询结果直接返回数据。连接、会话、导入预览等具有持续状态或 Core 身份的对象返回 `$handle` 和属性快照。构造函数自动保留对象；需要继续调用普通数据对象的实例方法时，在 `core_api_call` 设置 `retainResult: true`。后续参数用以下写法传递同一个原始对象：

```json
{ "parameters": { "$handle": "此前取得的对象编号" } }
```

一般模型也可直接传 JSON。抽象模型用 `$type` 指定目录中的具体类型。类型泛型参数填写 `typeArguments`，方法泛型参数填写 `methodArguments`，使用目录给出的类型全名或 `string`、`int`、`Guid` 等基础名称。

数组和集合使用 JSON 数组；字典使用 JSON 对象；枚举使用成员名称；字节数据使用 Base64。大型二进制结果返回编号，通过 `core_binary_read` 分块取出。`ref` / `out` 输出返回在 `refArguments` 中。

## 进度、决定与持续运行

`CancellationToken` 随操作编号管理。进度和通知回调进入操作事件队列。可选 `Func` 回调省略时沿用 Core 默认值；需要接管时，在对应参数填写 `{"$callback":true}`。需要返回值的回调进入 `AwaitingInput`，通过 `core_callback_reply` 回答当前 `requestId`，再继续同一调用。

取消是请求：Core 的原生调用可能继续执行，应继续读取状态。`OutcomeUnknown`、失败后的保留资源和清理错误都会返回，由调用者据此决定下一步。

宿主操作响应保留 `RequiresRestart`、`RestartReasons`，以及 `Steps` 中各步骤的状态和重启原因。失败也可能携带已完成步骤的重启要求；调用方应同时读取结果与这些字段。MCP 透传 Core 的要求，重启作为单独的操作安排。

事件队列保留最近 128 条数据快照，并报告替换数量；普通事件产生的数据随队列一同释放。画面事件仅保留最新帧租约，读取后取得的帧由调用者释放。接收剪贴板、音频数据和监控事件同样走公开事件入口。Core 当前各后端的实际能力以其 API 返回值为准。

控制台调用顺序：创建连接 → 取得 Video / Input / Clipboard 等对象 → 订阅事件 → Start → 接收画面并发送输入 → 结束订阅 → 等待 DisconnectAsync 或释放连接。`core_image_read` 将帧转换为 MCP 图片供 AI 查看；鼠标、键盘和剪贴板调用对应 Core 接口。

从已连接的画面取一张图时，通过 `core_api_call` 调用 `IConsoleVideoSource.CaptureFrame`，`target` 填 Video 对象编号，再把结果中的帧编号交给 `core_image_read`，最后 `core_object_release` 释放帧。返回 `null` 表示该连接当前没有可取的画面。它取得所选基本或增强会话的最近画面；列表预览继续使用 `VmScreenshotSource.CaptureAsync` 的 WMI 基本控制台截图，两种来源各有独立用途。

对象和操作编号属于当前 MCP 进程。正常退出先请求取消、等待正在进行的操作，再解绑事件并释放持有资源。退出等待受宿主的两分钟期限约束；到期时记录仍待核对的操作，使用中的资源保持有效直到进程结束。客户端可通过 `core_operations` 找到当前进程的操作，再查询详细结果。

## 返回结构与启动配置

当前工具返回结构版本为 **2**，`server_info.data.responseContract` 可核对版本。所有工具声明输出 Schema，统一返回：

```json
{
  "success": true,
  "data": { "实际工具结果": "保留 Core 字段与状态" },
  "error": null
}
```

失败时 `success` 为 false，`error` 提供类别与说明，`data` 保留 Core 原始诊断、部分完成步骤和清理收据。原先直接读取顶层工具结果的客户端，应改为从 `data` 读取。图片继续以 MCP 图片内容返回。

启动时可选择只读模式及工具组：

```powershell
.\ExHyperV.Mcp.exe --read-only --toolsets host,vm
```

省略选项时开放全部已接入能力。只读模式保留查询、发现，以及查询的取消和资源清理入口；通用 API 同样校验读写性质与所属工具组。Core 契约发生变化时，通用只读入口等待重新核对，避免沿用过时分类。工具组和只读模式在进程启动时确定。HTTP 仍采用同一可信使用者共享进程的部署方式。

## 任务与重复请求

修改工具接受可选的 `requestKey`。重试同一请求时保持键和参数一致；当前进程复用原执行结果。使用相同键提交另一组参数会返回错误。进程重启后，原收据用于核对实际资源状态，写操作由调用方另行决定。

`server_requests` 分页读取持久收据，记录操作名、API、目标资源编号、时间、状态和操作编号；密码等参数原文保持私密。已完成记录保留七天。`server_request_release` 清理已结束记录；遗留收据需先核对实际状态，再设置 `verified: true`。

支持标准 **Tasks 扩展**的客户端可按协议选择任务调用。用于 `core_api_call` 时设置 `background: false`，让协议任务跟踪本次 Core 调用全程。普通客户端继续使用操作编号和轮询。协议任务的取消与 Core 的实际收尾分别记录，取消后可通过 `core_operations` 继续核对。

协议任务先写入磁盘再返回编号。每个进程最多保留 256 项，已结束任务默认保留 24 小时；读取结果后可用 `server_task_release` 提前释放。旧进程的结束记录保留七天。原进程结束时仍在执行的任务，重新查询会说明执行中断并要求核对资源，避免自动重放管理操作。

`core_callback_prompt` 将 Core 的布尔确认交给支持表单交互的客户端；接受或拒绝传回 Core，关闭表单保留待定事项。其他决定继续使用 `core_callback_reply`。GPU 快捷工具与通用 API 共用对象管理，流程状态和允许动作仍由 Core 提供。

运行记录位于当前账户的 `%LOCALAPPDATA%\ExHyperV\Mcp`；`EXHYPERV_MCP_STATE_DIRECTORY` 可指定其他目录。目录内包含本机操作记录，应按管理数据保管。

资源限额：通用操作最多保留 256 项，同时执行 8 项；事件订阅最多 64 项；对象达到 4096 项后暂停接收新工作；显式二进制缓冲预算为 128 MiB。达到限额会说明需要释放的资源。清理失败或结果待核实的资源保留编号，等待处理。

Core 手册也通过 MCP Resources 提供：`exhyperv://core/api`；接入范围为 `exhyperv://core/coverage`。工具形式的 `core_api_guide` 与 `core_api_coverage` 继续可用。

## 接入验证

与主项目并列的 [ExHyperV.Mcp.Tests](../ExHyperV.Mcp.Tests/README.md) 验证传输、权限、对象管理与任务行为。测试脚本兼容 PowerShell 5.1，构建时同步编译最新 Core；GitHub 的“MCP 接入检查”分别运行 ARM64 与 x64。

`--verify-contracts` 核对公开签名、参数和分类是否与已审核基线一致。Core 更新引起差异时，先检查适配与只读声明，再更新 `Api/Contracts.sha256`；构建和测试过程保持基线只读。

## 私密参数与结果

字符串参数可从 MCP 私密进程环境读取：`{"$environment":"EXHYPERV_SSH_PASSWORD"}`。此形式仅接受 `EXHYPERV_` 前缀；对象快照中的密码字段以占位符显示。快捷 Linux GPU 工具继续使用同名环境变量。

归档导入使用 `VmImportService.PreparePreviewsAsync` 的 `VmImportArchiveOptions` 重载，填写密码、解压额度和工作目录；归档导出使用 `VmExportPackagingService.CreatePackageAsync` 的 `VmExportPackageOptions` 重载，填写压缩、加密和源文件处理方式。密码参数同样支持 `{"$environment":"EXHYPERV_ARCHIVE_PASSWORD"}`。后续导入沿用原批次对象及累计额度。

失败响应保留 Core 异常的公开诊断字段：归档读取的 `reason`、`entryName`、`limitName`、`limit`、`actual`，以及打包失败的 `failure`、`retainedPaths`、`cleanupError`。按这些字段判断密码、额度和清理情况；仍持有的批次、会话和使用期按 Core 约定释放。

修改、删除、部署和完整 API 调用都是真实操作。MCP 的工具标注供客户端安排授权，实际 Windows 权限仍来自进程令牌。Core 的成功标志、错误类别和字段采样状态分别保留，操作请求获接收也只表示已经开始处理。

可用 `ExHyperV.Mcp.exe --api-catalog` 导出当前程序的完整接入目录和构建标识。目录包含编译器生成的公开成员，数量可多于手写 API 文档。

SDK 接入参考官方 [QuickstartWeatherServer](https://github.com/modelcontextprotocol/csharp-sdk/tree/v2.3.0/samples/QuickstartWeatherServer)。版本和许可见 `THIRD-PARTY-NOTICES.txt`；业务约定见 [Core API](../ExHyperV.Core/API.md)。
