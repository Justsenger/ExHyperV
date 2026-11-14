# ExHyperV.Core API

ExHyperV.Core 为 Windows 应用提供虚拟机管理、设备配置和画面交互能力。应用直接调用 DLL；第一次接入可以从下面的查询示例开始。

## 开始使用

引用 Core，列出虚拟机，取得后续操作使用的 GUID。

调用项目使用 `net10.0-windows`，启用 `ImplicitUsings`，以管理员权限运行。同仓库项目引用 `ExHyperV.Core.csproj`；独立部署时携带 Core DLL 及其构建输出中的依赖。Hyper-V 管理功能需要宿主提供相应组件。

下面的示例方法可以放入应用的类中，文件顶部添加：

```csharp
using ExHyperV.Models;
using ExHyperV.Services;
```

<!-- api-example-list:start -->

<!-- api-example-list:end -->

`vm.Key` 是虚拟机 GUID 的字符串形式。需要修改配置或建立连接时，使用 `Guid.Parse(vm.Key)`。显示名称可以重复，也可以修改，后续操作仍按 GUID 指定目标。

接下来可按任务阅读：[查询数据](#查询数据)、[修改配置](#修改配置)、[创建导入与导出](#创建导入与导出)、[部署流程](#部署流程)、[连接与交互](#连接与交互)。具体字段和方法从左侧 API 目录查找。

## 查询数据

一次查询可以自由选择不同领域的字段。虚拟机本身的属性直接返回；网卡、磁盘等一对多对象分别放在集合中。

<!-- api-example-query:start -->

<!-- api-example-query:end -->

### 选择目标与字段

`VmQueryRequest` 的第一个参数是字段，第二个参数是虚拟机 GUID 集合。省略目标会查询全部虚拟机；传入空集合会返回空结果。重复目标和字段自动去重。没有找到的指定目标列在 `MissingVmIds` 中。

字段页的“成员”是请求中使用的对象，例如 `VmMemoryFields.Startup`；“类型”是成功读取后 `Value` 的 C# 类型。需要动态选择字段时，可从 `VmQueryFields.All` 枚举，或用 `VmQueryFields.Find("Memory.Startup")` 按标识查找。

### 读取结果

`response.HasData` 表示取得了整次查询结果；其中每个字段和设备集合还各有读取状态。字段 `HasValue` 为 true 时使用 `Value`，包括有效的 0、false 或空字符串。

| 状态 | 含义 |
| --- | --- |
| `Available` | 取得本次有效值；集合为空时表示确认没有记录 |
| `NotRequested` | 本次没有选择这个字段或集合 |
| `Unsupported` | 当前系统缺少读取所需的能力 |
| `NotApplicable` | 当前对象状态不适用，例如关机时的运行性能 |
| `Unavailable` | 本次没有取得值 |
| `WarmingUp` | 计数器正在建立基线 |
| `Ambiguous` | 无法确定数据归属 |
| `Failed` | 读取失败，原因见 `Error` 和 `Failure` |

`VmQueryRow.Key` 用于关联同一集合内的记录。虚拟机记录使用 GUID；设备和检查点使用各自的来源身份。引导项的 Key 包含顺序位置，重排后会变化。

### 连续采样

保留同一个 `VmQueryService` 实例进行周期查询，计数器才能延续采样基线。字段的 `ObservedAtUtc` 是其读取时间；一个请求内的不同来源可能在不同时刻完成。`Timings` 可用来查看哪些来源耗时较多。

只选择需要的字段。同一来源的读取会合并；增加设备详情或性能指标，可能引入新的底层查询。

<!-- api-example-monitor:start -->

<!-- api-example-monitor:end -->

## 修改配置

用虚拟机 GUID 指定目标，再把需要变更的值交给对应服务。修改查询结果对象本身不会保存配置。

<!-- api-example-operation:start -->

<!-- api-example-operation:end -->

### 读取配置并编辑

`GetVmMemorySettingsAsync` 与 `GetVmProcessorAsync` 返回 `VmSettingsSnapshot<T>`。`Values` 每次提供独立的编辑副本，`States` 按配置属性名给出本次读取状态，`ObservedAtUtc` 记录采集时间。值和状态来自同一个底层配置对象。

先检查 `States[nameof(...)] == VmQueryValueState.Available`，再使用该字段的值。有效的 0、false 与缺值分开表示。处理器的“未设置”编码会映射成 null，其读取状态仍为 Available。

<!-- api-example-settings-snapshot:start -->

<!-- api-example-settings-snapshot:end -->

`GetModificationCondition` 等条件查询返回当前状态下的可修改性、原因和所需电源状态。界面据此启用控件；提交时 Core 重新读取目标并执行相同校验。

### 提交部分字段

处理器、内存和网卡设置提供 `changedProperties`，用于明确选择本次修改的属性。没有选中的值不参与普通回写；字段之间的必要联动由对应服务处理。

<!-- api-example-processor-change:start -->

<!-- api-example-processor-change:end -->

`null` 的含义取决于属性：有的表示本次不修改，有的表示恢复未设置状态。按具体设置模型的说明提交，不把整个查询快照当作修改清单。

处理器与内存写入按 VM 在使用 Core 的进程之间排队，读取最新配置后提交本次选择；同一字段以后一次请求为准。这种排队不覆盖 PowerShell 等外部工具。

### 判断结果

普通修改检查 `Success`，失败时使用 `ErrorKind` 判断类别，`Error` 提供说明。多步骤入口还会返回 `Steps` 或专用结果，记录已经完成的修改。

配置保存和运行时生效由具体接口区分。有的修改立即生效，有的需要关闭虚拟机或重启宿主；返回的 `RequiresRestart` 和 `RestartReasons` 表示本次操作报告的宿主重启要求。

## 创建导入与导出

这些操作围绕单台虚拟机执行。批量任务由应用安排目标、顺序、并发与总进度。

<!-- api-example-create:start -->

<!-- api-example-create:end -->

创建结果提供实际名称、GUID 和文件位置。发生名称或路径避让时，看 `ActualName`、`NameAdjusted` 和 `Notice`。失败后通过 `RegistrationState`、`VmId` 和 `RetainedPaths` 确认是否已经留下虚拟机或文件。

### 删除虚拟机

`VmDeleteService.DeleteVmAsync()` 注销虚拟机并保留文件。`PurgeVmAsync()` 还会删除执行时确认属于它的配置和磁盘；无法排除其他虚拟机依赖的磁盘会保留并报告。删除预览供调用方确认，实际删除范围以执行时的归属检查为准。

<!-- api-example-import:start -->

<!-- api-example-import:end -->

导入先取得预览和会话，再为选中的虚拟机提交 `VmImportOptions`。GUID 策略必须明确选择：`Preserve` 保留来源身份，冲突时失败；`GenerateNew` 始终生成新身份。成功后使用结果中的 `VmId`。

名称和目录可以省略，使用来源名称及宿主默认放置规则。填写目录时使用实际目标目录。交换机映射按来源和目标 GUID 指定，映射为 null 表示断开。详细选项见 `VmImportOptions`。

<!-- api-example-archive-preview:start -->

<!-- api-example-archive-preview:end -->

<!-- api-example-export:start -->

<!-- api-example-export:end -->

导出可以选择磁盘、检查点和运行状态。导出成功后，如需归档，再调用打包接口。

<!-- api-example-encrypted-export:start -->

<!-- api-example-encrypted-export:end -->

## 部署流程

GPU 部署需要选择设备、处理电源、扫描磁盘并安装驱动。Core 管理步骤和规则，应用展示当前状态并提交用户决定。

<!-- api-example-workflow:start -->

<!-- api-example-workflow:end -->

`PendingDecision` 是当前等待的决定，提交时携带它的请求 Id。`AllowedActions` 列出当前允许执行的动作，应用据此提供继续、取消或清理入口。

### 处理未完成的部署

`Completed` 表示流程完成。`CleanupRequired` 表示还有资源需要处理；`OutcomeUnknown` 表示底层操作的最终结果尚未确认。后者应先通过允许的 `ReconcileAsync()` 核实，再根据新状态决定清理或结束。

<!-- api-example-gpu-recovery:start -->

<!-- api-example-gpu-recovery:end -->

恢复信息保存在原部署对象的 `Recovery` 中，包括执行记录、资源归属和待恢复状态。它用于当前进程内的这次部署。清理负责恢复哪些临时资源、哪些已完成修改会保留，见 `GpuDeploymentSession` 的对应方法。

## 连接与交互

先建立连接，再使用该连接提供的视频、控制和剪贴板能力。

| 后端 | 当前实现 |
| --- | --- |
| `RdpBasic` | 基本会话画面、键盘和鼠标 |
| `RdpEnhanced` | 增强会话画面、键盘和鼠标、剪贴板，以及分辨率请求 |

基本和增强会话均由 FreeRDP 实现。`ConsoleService.GetBackends()` 提供当前后端目录，目标是否支持增强会话还取决于来宾。

<!-- api-example-resource:start -->

<!-- api-example-resource:end -->

### 连接状态

先订阅事件，再调用 `Start()`。`Connected` 表示连接已建立，`LoginCompleted` 表示后端报告登录完成；剪贴板就绪由自身的 `IsAvailable` 表示。每个连接对象只启动一次，重连时创建新对象。

事件可能从后台线程触发。耗时工作交给其他线程，界面更新调度回界面线程；回调应及时返回，不能在其中同步等待同一连接退出。

### 画面和取图

`FrameReceived` 提供完整的 BGRA32 图像及变化区域。帧在回调期间借出；跨回调保存时先调用 `Retain()`，用完释放。`CopyPixels()` 返回独立的像素数组。

`Video.CaptureFrame()` 取得当前连接最近的完整画面；没有可用画面时返回 null，取得的帧由调用方释放。基本和增强会话各自使用自己的连接画面。

虚拟机列表预览使用 `VmScreenshotSource.CaptureAsync()`，无需连接，通过 WMI 读取基本控制台画面。它与增强会话桌面是不同来源。

### 输入与剪贴板

键盘使用 `Input.SendKey()`，组合键可用 `SendKeys()`；鼠标使用 `SendPointer()` 或 `SendClick()`，坐标按来宾桌面像素计算。界面失焦时调用 `ReleaseAllInputs()` 释放按键和按钮。

输入方法返回 true 表示请求被 Core 接收。调整分辨率也是异步请求，实际尺寸以随后收到的帧为准。

剪贴板就绪后，通过 `SetClipboard()` 发送，通过 `ClipboardReceived` 接收。具体格式和回调约定见 `IConsoleClipboard`；宿主系统剪贴板由调用应用负责接入。

## 结果、取消和资源释放

这一节说明各模块共用的结果和资源约定；具体操作的额外返回值见对应接口。

### 读取结果

`ApiResponse` 用 `Success` 表示操作是否成功。`ApiResponse<T>` 还包含数据；读取 `Data` 前检查 `HasData`。成功但没有结果用 `IsEmpty` 表示，和失败分开。

`ErrorKind` 是供程序判断的错误类别，`Error` 是供人阅读的说明。需要排查底层失败时，再看 `ErrorSource`、`Code` 和 `Exception`。使用异常形式的操作通过 `CoreOperationException.Result` 提供同样的结构化信息；VMCX 编辑等工具还有自己的异常类型。

多步骤结果会记录已完成的部分。`CleanupRequired` 表示清理尚未完成，`OutcomeUnknown` 表示执行结果未确认；先按该流程提供的核实或清理入口处理，再决定是否重新发起。

### 根据错误决定下一步

| 错误类别 | 处理方式 |
| --- | --- |
| `InvalidArgument` | 按参数范围修正输入后提交 |
| `AmbiguousTarget` | 重新查询候选，使用唯一身份指定目标 |
| `InvalidState` | 按条件查询返回的 RequiredState 调整目标状态 |
| `AccessDenied` | 使用具备目标操作权限的 Windows 身份 |
| `Cancelled` | 按结果中的步骤状态结束本次调用 |
| `NotFound` | 重新查询目标，使用当前 GUID 或设备身份 |
| `Unsupported` | 读取宿主能力，选择支持的设置 |
| `Conflict` | 按返回原因重新取得对象或流程状态 |
| `OutcomeUnknown` | 查询原操作状态；会话流程按 AllowedActions 核实 |
| `CleanupRequired` | 调用结果指向的清理入口，完成后读取新状态 |
| 其他失败 | 读取 Error、Code、ErrorSource 和 Steps 定位失败步骤 |

Core 是进程内 DLL，调用权限来自宿主 Windows 进程。Web 与 MCP 的身份验证由各自入口负责。查询返回本次请求的完整结果集；字段选择和目标 GUID 集合用于控制读取范围。

### 取消与退出

支持取消的接口提供 `CancellationToken` 或取消方法。已经提交的 WMI 或原生调用可能需要等待返回，再停止后续步骤和释放资源。停止等待本身不会撤销已经完成的修改。

| 使用的对象 | 结束方式 |
| --- | --- |
| 查询或监控实例 | 调用 Dispose；提供 DisposeAsync 时可等待释放完成 |
| 控制台连接 | 等待 DisconnectAsync 或 DisposeAsync |
| 保留或单独取得的帧 | 每份租约分别 Dispose |
| 导入会话 | 等待 DisposeAsync，检查 CleanupState 和剩余文件 |
| 磁盘挂载租约 | 在读写及恢复完成后等待 ReleaseAsync 或 DisposeAsync |

导入会话在导入中释放时，会请求停止后续步骤，等待正在进行的操作安全结束，再清理工作文件；成功导入的虚拟机保留。释放失败的详细状态仍由原会话提供。

### 共享 ARP 捕获

需要辅助 IP 来源时，调用 `ArpSnoopService.Instance.Start()`；宿主程序最终退出时等待 `SharedBackgroundServices.ShutdownAsync()`。这项清理属于进程退出，不属于页面关闭；同一进程清理后不能再次启动捕获。

## 升级已有调用

本次配置读取返回值改为 `VmSettingsSnapshot<VmMemorySettings>` 和 `VmSettingsSnapshot<VmProcessorSettings>`。原有 `response.Data.Startup` 改为读取 `response.Data.Values.Startup`，并检查相应 `States`。

Core 配置、设备和采样模型提供普通数据属性。WinUI 的属性通知、编辑草稿、下拉选项和界面线程调度由视图模型持有。字段解释由同一份 API 材料生成到 `FieldHelp.json`。



升级前对照所用 Core 版本，重点检查标识、单位和提交方式。

多数虚拟机操作已使用 GUID；设备操作使用查询得到的设备引用。旧名称调用不能直接当作 GUID 使用。Linux GPU 部署仍保留其原有名称入口。

处理器的 `Reservation`、`Limit` 使用 0.001%，100000 表示 100%；内存 `Weight` 使用原始权重；网络带宽 `*BitsPerSecond` 使用 bit/s。从旧界面数值迁移时需要换算，不能只改属性名称。

处理器、内存和网卡的组合设置只提交 `changedProperties` 选中的属性。未知值、恢复默认值和未修改字段的区别，按各模型的属性说明处理。

创建、导入、导出、删除和电源控制使用单台入口。批量顺序、命名策略和创建后启动由应用安排。导入通过 `VmImportOptions` 明确 GUID 策略及目标配置。

导入预览的 `DynamicMemory`、`HasSavedState` 改为 `bool?`，`MinimumMemoryMiB`、`MaximumMemoryMiB` 改为 `ulong?`。这四项尚未由解析器读取，返回 `null`；调用方应保留未知含义，不再把它们当作关闭或零值。

连接后端标识使用 `RdpBasic` 或 `RdpEnhanced`。旧 `Enhanced` 布尔值和 `freerdp` 标识需要在调用方转换。

其他签名变化以当前 API 参考为准。旧版本的调用示例不与本版本混用。

## 接口参考


<!-- api-reference:start -->

<!-- api-reference:end -->
