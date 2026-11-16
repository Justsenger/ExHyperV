# ExHyperV.Core API

`ExHyperV.Core` 是直接嵌入 Windows 应用进程内的 Hyper-V 核心类库，提供**虚拟机管理、硬件配置、导入导出、GPU 分区部署**以及**远程控制台画面/键鼠交互**能力。

## 开始使用

首次接入只需三步：**满足运行环境 → 查出虚拟机 GUID → 按 GUID 调对应服务**。

### 运行要求

- **目标框架**：`net10.0-windows`（建议开启 `ImplicitUsings`）
- **运行权限**：以**管理员权限**运行宿主进程，并在系统启用 Hyper-V 管理功能
- **引用方式**：同仓库项目直接引用 `ExHyperV.Core.csproj`；独立发布时随应用携带 `ExHyperV.Core.dll` 及其依赖
- **常用命名空间**：

```csharp
using ExHyperV.Models;
using ExHyperV.Services;
```

### 第一段能跑通的代码

先实例化 `VmQueryService` 把本机虚拟机列出来。**记住 `vm.Key`（即虚拟机 GUID）**——因为虚拟机显示名称可以重名也可以改，后面开关机、改配置、连画面全都认 GUID，不认名称。

<!-- api-example-list:start -->

<!-- api-example-list:end -->

### 常见任务速查

| 你想做什么 | 去找哪个入口 | 详细说明 |
| --- | --- | --- |
| 查列表、配置、IP 或运行状态 | `VmQueryService.QueryAsync` | [查询数据](#查询数据) |
| 开机、关机、重启、强制断电 | `VmPowerService.ExecuteControlActionAsync` | [修改配置](#修改配置) |
| 改名字、CPU、内存、网卡、磁盘 | `VmEditService` / `VmProcessorService` / `VmMemoryService` / `VmNetworkService` | [修改配置](#修改配置) |
| 新建、删除、导入、导出虚拟机 | `VmCreateService` / `VmDeleteService` / `VmImportService` / `VmExportService` | [创建导入与导出](#创建导入与导出) |
| 一键配置 GPU-PV 分区与驱动 | `GpuDeploymentSession` | [部署流程](#部署流程) |
| 显示虚拟机屏幕、发键鼠与剪贴板 | `ConsoleService` / `VmScreenshotSource` | [连接与交互](#连接与交互) |
| 统一处理报错、取消与资源清理 | `ApiResponse` / `CoreOperationException` | [结果、取消和资源释放](#结果取消和资源释放) |

## 查询数据

查询接口设计成“按需点菜”：你想要哪些字段就传哪些字段，不用每次拉整台虚拟机的全部信息，速度更快，也避免触发不必要的底层 WMI 或性能计数器查询。

### 按需组合字段

把需要的字段放进 `VmQueryRequest`。虚拟机自身属性（如内存大小、名称）直接从虚拟机行读取；网卡、磁盘这类一对多设备从对应的子集合读取。

<!-- api-example-query:start -->

<!-- api-example-query:end -->

几个常用细节：
- **目标范围**：`VmQueryRequest` 第二个参数传 GUID 列表；**省略不传**会查全部虚拟机，传**空列表**会直接返回空结果。查不到的 GUID 会放在 `MissingVmIds` 里。
- **动态选字段**：不知道有哪些字段名时，可以从 `VmQueryFields.All` 遍历，或用 `VmQueryFields.Find("Memory.Startup")` 按标识查找。
- **集合里的主键**：`VmQueryRow.Key` 用来区分同一集合里的不同项（虚拟机是 GUID，设备是设备标识；引导顺序的 Key 含顺序位置，重排后会变化）。

### 怎么判断有没有读到值

外层 `response.HasData` 为 `true` 只代表这趟查询整体成功；具体到某一个字段，先看 `field.HasValue`（为 `true` 时再读 `field.Value`，包括合法的 `0`、`false` 或空字符串）。

没取到值时，可以通过 `field.State` 区分原因：

| 状态 | 通俗解释 | 常见情况 |
| --- | --- | --- |
| `Available` | 读到了有效数据 | 对于网卡/磁盘集合，表示列表真实为空 |
| `NotRequested` | 这次没选这个字段 | 请求参数里没带它 |
| `NotApplicable` | 当前状态下没有意义 | 比如虚拟机关机时去读 CPU 占用率 |
| `WarmingUp` | 计数器还在热身 | 刚启动采样，等下一次轮询就有值了 |
| `Unsupported` | 当前系统不支持 | 宿主 Windows 版本或硬件不支持该特性 |
| `Unavailable` / `Ambiguous` / `Failed` | 没取到、无法归属或读取报错 | 需排障时看 `Error` 和 `Failure` |

### 实时性能监控

做 CPU、内存或网络实时曲线时，**请复用同一个 `VmQueryService` 或 `HyperVCpuMonitorService` 实例**定时轮询——性能计数器需要靠同一个实例保留上一次基线才能算出增量。

一个请求里不同来源的数据可能在不同毫秒完成，可以看字段的 `ObservedAtUtc` 了解采样时间；想排查哪一步慢，看 `Timings` 即可。

<!-- api-example-monitor:start -->

<!-- api-example-monitor:end -->

## 修改配置

改配置记住三个原则：**按 GUID 指定虚拟机、改前可先校验条件、只提交你改过的字段**。直接改查询出来的对象属性不会自动写回 Hyper-V。

### 简单操作：直接改

像改名、开关机（`VmPowerService.ExecuteControlActionAsync`）这类单步操作，直接把 `vmId` 和参数传给服务，然后看 `result.Success`：

<!-- api-example-operation:start -->

<!-- api-example-operation:end -->

### 局部修改：`changedProperties` 的用法

处理器（`VmProcessorService`）、内存（`VmMemoryService`）和网卡（`VmNetworkService`）参数很多，为防止把别人或系统刚改过的值覆盖掉，写入时都需要传一个 `changedProperties` 集合——**只有点名在集合里的属性才会被修改**。

如果你已经明确知道要改某个字段，可以直接构造请求并只点名那一项：

<!-- api-example-processor-change:start -->

<!-- api-example-processor-change:end -->

更稳妥的做法是“**先读后改**”：`GetVmMemorySettingsAsync` 和 `GetVmProcessorAsync` 会返回 `VmSettingsSnapshot<T>`，用 `States` 确认原值有效后，改完直接把改动的字段名传回去：

<!-- api-example-settings-snapshot:start -->

<!-- api-example-settings-snapshot:end -->

### 改配置时的三个常见疑问

1. **怎么提前知道某个按钮该不该灰掉？**
   调用对应服务的 `GetModificationCondition(...)`（比如 `VmProcessorService.GetModificationCondition` 或 `VmMemoryService.GetModificationCondition`），它会告诉你当前电源状态下能不能改、不能改的原因，以及需要先切到什么状态。界面上可用它控制按钮状态，提交时 Core 内部也会再校验一遍。
2. **传 `null` 是“不改”还是“恢复默认”？**
   取决于具体属性：`CpuBrandString` 传 `null` 会清空；频率、能效偏好等部分高级属性传 `null` 会恢复为“未设置”；其余可空属性传 `null` 通常表示跳过不改。具体规则以对应设置类的说明为准，不要直接把整张查询对象当提交清单。
3. **改完什么时候生效？**
   有的配置（如动态调整部分内存、接通网卡）立即生效，有的要求关机才能改；如果操作触发了需要重启宿主机才能生效的更改，返回值里的 `RequiresRestart` 会为 `true`，`RestartReasons` 会写明原因。另外，Core 对同一台虚拟机的 CPU/内存写入会在进程间自动排队，防止并发写花。

## 创建导入与导出

创建、删除、导入、导出都面向**单台虚拟机**设计。如果你在业务上要做批量创建或批量迁移，建议由上层自己控制并发、排队顺序和总进度条。

### 创建与删除

创建虚拟机时只需填充 `VmCreationParameters`；如果名字或目录撞车，Core 会按策略避让，并在结果里的 `ActualName`、`NameAdjusted`、`Notice` 告诉你最终落在哪里。

<!-- api-example-create:start -->

<!-- api-example-create:end -->

删除分两档，按风险小心选用：
- `VmDeleteService.DeleteVmAsync`：**只注销虚拟机**，配置文件和 `.vhdx` 磁盘文件都留着。
- `VmDeleteService.PurgeVmAsync`：**彻底删除**，注销后还会顺带删掉确认专属于这台虚拟机的配置和磁盘；如果某个磁盘无法排除被其他虚拟机共享依赖，Core 会保留它并在结果里报告。删除前可以先调预览方法给用户二次确认。

### 导入虚拟机

导入分两步：**先预览拿到 `VmImportBatchSession`，等用户选好配置后再真正导入**。

- **GUID 策略必须二选一**：`Preserve` 保留原虚拟机 ID（本机已有同 ID 时会失败）；`GenerateNew` 总是分配新 ID，导入成功后拿结果里的 `VmId` 当后续标识。
- **名称、目录与交换机**：名称和目标目录省略时沿用原名和宿主默认路径；虚拟交换机按“来源 GUID → 目标 GUID”映射，映射为 `null` 表示先断开。
- **用完务必 `await using` 释放**：哪怕只是看了眼预览不打算导入，也要释放会话来清理临时解压目录。

<!-- api-example-import:start -->

<!-- api-example-import:end -->

如果导入来源是**加密 ZIP 压缩包**，预览阶段就要通过 `VmImportArchiveOptions` 传入密码和解压额度限制（防压缩炸弹），预览消耗的额度会计入同一批次：

<!-- api-example-archive-preview:start -->

<!-- api-example-archive-preview:end -->

### 导出与打包

先用 `VmExportService.ExportAsync` 导出到指定目录（可按需勾选磁盘、检查点和运行状态）：

<!-- api-example-export:start -->

<!-- api-example-export:end -->

导出成功后如果想打成压缩包或加密归档，再调用 `VmExportPackagingService.CreatePackageAsync`（支持 AES-256 加密，跨工具解压建议使用纯 ASCII 密码）：

<!-- api-example-encrypted-export:start -->

<!-- api-example-encrypted-export:end -->

## 部署流程

给虚拟机配 GPU-PV（显卡分区）并安装驱动涉及多个敏感步骤：检查宿主、调整 MMIO 空间、必要时关机、扫描来宾系统分区、安装 Windows 驱动或通过 SSH 部署 Linux 驱动。因为中途必须等用户拍板，Core 把它做成了**可暂停的会话流程**（`GpuDeploymentSession`）。

### 用一个循环跑完部署

调用方只需要不断跑 `StartAsync()` / `SubmitDecisionAsync(...)`：当 `State == GpuDeploymentState.WaitingForDecision` 时，把 `PendingDecision` 里的信息弹窗给用户，拿回用户选择（带上原 `RequestId`）继续往下推；如果用户点取消，传 `null` 或调用 `CancelAsync()`。

<!-- api-example-workflow:start -->

<!-- api-example-workflow:end -->

### 部署中途失败或被取消怎么办？

看最后快照的 `State` 和 `AllowedActions` 就行，不用自己猜：
- `Completed`：全部搞定。
- `CleanupRequired`：留下了临时挂接的磁盘或刚建出来的半成品适配器。
- `OutcomeUnknown`：底层 WMI/磁盘异步任务还没返回最终结果。**此时不要直接强行删东西**，先按 `AllowedActions` 调 `ReconcileAsync()` 核实一遍真实状态，确认安全后再调 `RetryCleanupAsync()` 清理。

<!-- api-example-gpu-recovery:start -->

<!-- api-example-gpu-recovery:end -->

所有的进度记录和待清理资源清单都保存在原 `session` 实例的 `Recovery` 属性里；只有当次进程内的原会话对象能用来核实和收尾。

## 连接与交互

`ConsoleService` 负责连上虚拟机屏幕并收发键鼠/剪贴板；如果只需要列表页的静态缩略图，**不用建连接**，直接用 `VmScreenshotSource.CaptureAsync()` 通过 WMI 抓一张即可。

### 选基本会话还是增强会话

| 连接方式 | 常量标识 | 适合场景 | 能力范围 |
| --- | --- | --- | --- |
| 基本会话 | `RdpBasic` | 看开机画面、进 BIOS/UEFI、装系统、系统未启用增强会话时 | 画面、键盘、鼠标 |
| 增强会话 | `RdpEnhanced` | 日常远程桌面操作（需来宾系统支持并开启增强会话） | 画面、键盘、鼠标、双向剪贴板、动态调整分辨率 |

两种后端目前都基于 FreeRDP 实现，可通过 `ConsoleService.GetBackends()` 查当前可用列表。

### 连接与取图示例

记住两个顺序：**先订阅事件，再调 `Start()`**；**每个连接对象只能启动一次**，断开重连请创建新对象。

<!-- api-example-resource:start -->

<!-- api-example-resource:end -->

### 处理画面、输入和剪贴板的 4 个实战提醒

1. **别在 `FrameReceived` 回调里做重活**
   事件都在后台线程触发，界面更新记得调度回 UI 线程；回调本身要尽快返回，**绝对不要在回调里同步等待当前连接断开**，否则会死锁。
2. **帧内存是借用的**
   `FrameReceived` 回调给你的 `ConsoleFrame` 只在回调期间有效。如果想异步拿去渲染或存下来，先调 `frame.Retain()`，用完后一定要 `Dispose()`；或者直接调 `CopyPixels()` 拿一份独立字节数组。
3. **随时抓当前一帧**
   调 `Video.CaptureFrame()` 可以直接取当前连接的最新完整画面（没有画面时返回 `null`，取到的帧用完记得释放）。基本会话和增强会话各自的画面是独立的，互不替代。
4. **键盘、鼠标和剪贴板**
   键盘用 `Input.SendKey()` / `SendKeys()`，鼠标用 `SendPointer()` / `SendClick()`（坐标按来宾桌面像素计算）。**当应用窗口失去焦点时，记得调用 `ReleaseAllInputs()`**，防止 Ctrl/Alt/鼠标左键卡死在按住状态。剪贴板通道就绪由 `IsAvailable` 标识，准备好后即可通过 `SetClipboard()` 和 `ClipboardReceived` 收发文本/图像。

## 结果、取消和资源释放

### 怎么读返回值和报错

大多数操作返回 `ApiResponse` 或 `ApiResponse<T>`，少数方法会直接抛出 `CoreOperationException`（异常里的 `Result` 包含同样完整的错误信息）：

- **判断是否成功**：无数据看 `Success`；有数据先检查 `HasData` 再读 `Data`（成功但查无记录会标记为 `IsEmpty`，和报错分开）。
- **代码分支看 `ErrorKind`，提示用户看 `Error`**：`Error` 文本可能会本地化或调整措辞，代码里永远不要用字符串匹配错误，而是判断 `ErrorKind`；深挖底层 WMI/Win32 错误码再看 `ErrorSource`、`Code` 和 `Exception`。

| `ErrorKind` | 建议处理方式 |
| --- | --- |
| `InvalidArgument` | 参数超出范围或格式不对，修正输入再提交 |
| `NotFound` | 目标 VM 或设备已经不存在，刷新列表重新获取 GUID |
| `AmbiguousTarget` | 名称不唯一，改用 GUID 指定目标 |
| `InvalidState` | 虚拟机电源状态不对（比如开机时改了需要关机的项），先按条件调整状态 |
| `AccessDenied` | 权限不足，确认进程以管理员身份运行 |
| `Conflict` | 有其他操作冲突或状态已变化，重新读取最新状态再试 |
| `Unsupported` | 当前宿主版本或硬件不支持该功能 |
| `OutcomeUnknown` / `CleanupRequired` | 操作结果未明或残留临时资源，按对应流程的核实/清理方法处理 |
| `Cancelled` / 其他 | 检查多步骤结果 `Steps` 确认哪些已完成、在哪一步中断 |

### 取消操作与必须释放的对象

所有支持 `CancellationToken` 的操作在取消后，已经发给 Windows/WMI 的底层调用可能需要等它返回后才会停止后续步骤——**取消等待不会把已经写完的配置自动回滚**。

下列对象用完请务必释放：

| 对象 | 释放方式 | 备注 |
| --- | --- | --- |
| `VmQueryService` / `HyperVCpuMonitorService` | `Dispose` 或 `await DisposeAsync` | 会等待进行中的性能计数器采样结束并回收资源 |
| `IConsoleConnection` | `await DisconnectAsync` 或 `await DisposeAsync` | 断开桌面连接并清理 FreeRDP 会话 |
| 手动 `Retain()` 或 `CaptureFrame()` 拿到的帧 | 每份分别 `Dispose` | 避免占满帧池 |
| `VmImportBatchSession` / `VmImportSession` | `await DisposeAsync` | 会清理临时解压目录；释放后检查 `CleanupState` |
| 磁盘挂载租约（历史磁盘挂接） | `await ReleaseAsync` 或 `await DisposeAsync` | 用完及时卸载 |

另外，如果应用开启了局域网 ARP 辅助 IP 发现（`ArpSnoopService.Instance.Start()`），**在宿主程序最终退出时**等待一次 `SharedBackgroundServices.ShutdownAsync()` 即可（不要在每次切页面时关它，进程内关闭后不能重启）。

## 升级已有调用

如果你正在把老版本调用迁到当前 `ExHyperV.Core`，重点对照下面几处变化即可：

| 迁移项 | 老写法 / 旧行为 | 新版写法 / 注意事项 |
| --- | --- | --- |
| **定位虚拟机** | 传虚拟机名称字符串 | 绝大多数接口改为传 **GUID**；设备操作传查询返回的设备引用（Linux GPU 部署保留原名称入口） |
| **读取内存 / 处理器设置** | 直接读 `response.Data.Startup` | 返回值改为 `VmSettingsSnapshot<T>`：先看 `States["Startup"] == Available`，再读 `Values.Startup` |
| **部分字段写入** | 传整个设置对象覆盖 | `VmProcessorService` / `VmMemoryService` / `VmNetworkService` 必须传 `changedProperties` 指定只改哪些字段 |
| **单位换算** | 旧界面百分比/换算值 | 处理器 `Reservation`/`Limit` 用 `0.001%`（`100000` = 100%）；内存 `Weight` 用原始权重；网卡带宽用 `bit/s` |
| **创建 / 电源 / 批量任务** | 内部自带批量或默认联动 | Core 统一为单台操作入口；批量顺序、命名避让、创建后是否开机由上层编排 |
| **导入选项与未知值** | 自动按默认策略处理 | 导入通过 `VmImportOptions` 显式选 `Preserve`/`GenerateNew`；预览里的 `DynamicMemory`、`HasSavedState`、`MinimumMemoryMiB`、`MaximumMemoryMiB` 改为可空，返回 `null` 表示未解析而非关闭/零值 |
| **控制台后端标识** | `Enhanced` 布尔值或 `freerdp` | 改用 `RdpBasic` / `RdpEnhanced` |
| **UI 绑定与字段说明** | Core 模型带通知和文案 | Core 只管纯数据；属性通知、草稿状态和选项标签归 WinUI 视图模型，字段文案统一走生成的 `FieldHelp.json` |

## 接口参考

<!-- api-reference:start -->

<!-- api-reference:end -->
