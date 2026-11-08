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

### 列举虚拟机

不传 vmIds 即查询全部虚拟机。输出 GUID 和名称，GUID 可用于后续修改或连接。

```csharp
public static async Task ListVmsAsync()
{
    await using var service = new VmQueryService();
    var response = await service.QueryAsync(new VmQueryRequest([VmIdentityFields.Name]));
    if (!response.HasData) throw new InvalidOperationException(response.Error);
    foreach (var vm in response.Data.VirtualMachines)
    {
        var name = vm.Get(VmIdentityFields.Name);
        if (name.HasValue) Console.WriteLine($"{vm.Key}  {name.Value}");
    }
}
```

<!-- api-example-list:end -->

`vm.Key` 是虚拟机 GUID 的字符串形式。需要修改配置或建立连接时，使用 `Guid.Parse(vm.Key)`。显示名称可以重复，也可以修改，后续操作仍按 GUID 指定目标。

接下来可按任务阅读：[查询数据](#查询数据)、[修改配置](#修改配置)、[创建导入与导出](#创建导入与导出)、[部署流程](#部署流程)、[连接与交互](#连接与交互)。具体字段和方法从左侧 API 目录查找。

## 查询数据

一次查询可以自由选择不同领域的字段。虚拟机本身的属性直接返回；网卡、磁盘等一对多对象分别放在集合中。

<!-- api-example-query:start -->

### 读取内存和网卡

一次请求组合不同领域的字段。虚拟机字段直接读取，网卡字段从网卡集合逐项读取。

```csharp
public static async Task<VmQueryResult> ReadSelectedFieldsAsync(Guid vmId)
{
    await using var service = new VmQueryService();
    var request = new VmQueryRequest(
        [VmMemoryFields.Startup, VmNetworkFields.MacAddress], [vmId]);
    var response = await service.QueryAsync(request);
    if (!response.HasData) throw new InvalidOperationException(response.Error);
    foreach (var vm in response.Data.VirtualMachines)
    {
        var memory = vm.Get(VmMemoryFields.Startup);
        if (memory.HasValue) Console.WriteLine($"启动内存：{memory.Value} MiB");
        var adapters = vm.GetCollection(VmQueryCollection.NetworkAdapters);
        if (adapters.State == VmQueryValueState.Available)
            foreach (var adapter in adapters.Rows)
            {
                var address = adapter.Get(VmNetworkFields.MacAddress);
                if (address.HasValue) Console.WriteLine(address.Value);
            }
    }
    return response.Data;
}
```

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

### 更新处理器监控值

只将有效的来宾处理器样本交给 update。缺测时不更新，由应用保留历史值；有效的零值仍会传出。

```csharp
public static void UpdateCpuValues(HyperVCpuMonitorService monitor, Action<Guid, int, float> update)
{
    foreach (var sample in monitor.GetCpuUsage())
    {
        if (sample.IsHost || sample.VmId is not Guid id || !sample.Sample.IsValid) continue;
        update(id, sample.CoreId, sample.Usage);
    }
}
```

<!-- api-example-monitor:end -->

## 修改配置

用虚拟机 GUID 指定目标，再把需要变更的值交给对应服务。修改查询结果对象本身不会保存配置。

<!-- api-example-operation:start -->

### 修改虚拟机名称

将 GUID 和新名称交给修改接口，再检查执行结果。这里把失败转换为异常，应用也可以自行展示错误。

```csharp
public static async Task RenameAsync(Guid vmId, string newName)
{
    var result = await VmEditService.RenameVmAsync(vmId, newName);
    if (!result.Success)
        throw new InvalidOperationException(result.Error);
}
```

<!-- api-example-operation:end -->

### 提交部分字段

处理器、内存和网卡设置提供 `changedProperties`，用于明确选择本次修改的属性。没有选中的值不参与普通回写；字段之间的必要联动由对应服务处理。

<!-- api-example-processor-change:start -->

### 修改处理器数量

只提交 Count。模型中的其他属性不参与这次修改；调用前应按处理器接口要求关闭虚拟机。

```csharp
public static async Task SetProcessorCountAsync(Guid vmId, int count)
{
    var result = await VmProcessorService.SetVmProcessorAsync(vmId,
        new VmProcessorSettings { Count = count }, [nameof(VmProcessorSettings.Count)]);
    if (!result.Success)
        throw new InvalidOperationException(result.Error, result.Exception);
}
```

<!-- api-example-processor-change:end -->

`null` 的含义取决于属性：有的表示本次不修改，有的表示恢复未设置状态。按具体设置模型的说明提交，不把整个查询快照当作修改清单。

处理器与内存写入按 VM 在使用 Core 的进程之间排队，读取最新配置后提交本次选择；同一字段以后一次请求为准。这种排队不覆盖 PowerShell 等外部工具。

### 判断结果

普通修改检查 `Success`，失败时使用 `ErrorKind` 判断类别，`Error` 提供说明。多步骤入口还会返回 `Steps` 或专用结果，记录已经完成的修改。

配置保存和运行时生效由具体接口区分。有的修改立即生效，有的需要关闭虚拟机或重启宿主；返回的 `RequiresRestart` 和 `RestartReasons` 表示本次操作报告的宿主重启要求。

## 创建导入与导出

这些操作围绕单台虚拟机执行。批量任务由应用安排目标、顺序、并发与总进度。

<!-- api-example-create:start -->

### 创建虚拟机

在指定目录创建一台启动内存为 1024 MiB、暂不附带磁盘的虚拟机。返回实际名称、GUID 和创建结果，后续启动单独安排。

```csharp
public static async Task<VmCreationResult> CreateVmAsync(string configurationDirectory)
{
    var result = await VmCreateService.CreateVirtualMachineAsync(new VmCreationParameters
    {
        Name = "Example", Path = configurationDirectory, DiskMode = 2,
        MemoryMiB = 1024 // MiB；此例稍后添加磁盘，创建不自动启动。
    });
    // Success=false 时也可能已注册或状态未知；先检查 RegistrationState 和 RetainedPaths。
    // 后续操作使用 result.VmId；NameAdjusted/Notice 报告自动避让，实际位置见 ConfigurationDirectory/DiskPath。
    return result;
}
```

<!-- api-example-create:end -->

创建结果提供实际名称、GUID 和文件位置。发生名称或路径避让时，看 `ActualName`、`NameAdjusted` 和 `Notice`。失败后通过 `RegistrationState`、`VmId` 和 `RetainedPaths` 确认是否已经留下虚拟机或文件。

### 删除虚拟机

`VmDeleteService.DeleteVmAsync()` 注销虚拟机并保留文件。`PurgeVmAsync()` 还会删除执行时确认属于它的配置和磁盘；无法排除其他虚拟机依赖的磁盘会保留并报告。删除预览供调用方确认，实际删除范围以执行时的归属检查为准。

<!-- api-example-import:start -->

### 导入并清理工作文件

解析来源并检查预览，再为选中的虚拟机提交独立选项。options 必须明确保留或生成 GUID；可另选名称、目录和交换机映射。两个回调用于报告清理结果，以及保留尚未清理完成的会话。

```csharp
public static async Task<VmImportResult> ImportVmAsync(string sourceDirectory, Guid originalVmId, VmImportOptions options,
    Action<VmImportCleanupState, IReadOnlyList<string>, string?> reportCleanup,
    Action<VmImportBatchSession> retainForCleanupRetry)
{
    var prepared = await VmImportService.PreparePreviewsAsync(
        sourceDirectory, VmImportPlacementMode.HostDirectories);
    var batch = prepared.Batch;
    try
    {
        if (!prepared.Success || batch == null)
            throw new InvalidOperationException(prepared.Result.Error);
        await using (batch)
        {
            // 实际应用展示来源预览，让用户确定 options；预览本身不接受修改。
            var selected = batch.VirtualMachines.Single(item => item.Preview.OriginalGuid == originalVmId);
            // 上层选择一个目标；多台时由上层决定执行顺序及失败后的安排。
            return await VmImportService.ImportAsync(selected, options);
        }
    }
    finally
    {
        if (batch != null)
        {
            // 预览失败时 Core 已尝试清理，也必须检查结果。
            reportCleanup(batch.CleanupState, batch.RetainedPaths, batch.CleanupError);
            // 宿主保留引用供用户重试 DisposeAsync；勿自行删除 RetainedPaths。
            if (batch.CleanupState != VmImportCleanupState.Completed)
                retainForCleanupRetry(batch);
        }
    }
}
```

<!-- api-example-import:end -->

导入先取得预览和会话，再为选中的虚拟机提交 `VmImportOptions`。GUID 策略必须明确选择：`Preserve` 保留来源身份，冲突时失败；`GenerateNew` 始终生成新身份。成功后使用结果中的 `VmId`。

名称和目录可以省略，使用来源名称及宿主默认放置规则。填写目录时使用实际目标目录。交换机映射按来源和目标 GUID 指定，映射为 null 表示断开。详细选项见 `VmImportOptions`。

<!-- api-example-archive-preview:start -->

### 带密码和额度的导入预览

从预览开始设置 ZIP 读取参数。示例额度只是调用方策略；正式导入继续使用同一批次，预览读取也计入累计额度。

```csharp
public static async Task PreviewEncryptedImportAsync(string archivePath, string password, string workDirectory,
    CancellationToken cancellationToken = default)
{
    var prepared = await VmImportService.PreparePreviewsAsync(archivePath,
        new VmImportArchiveOptions
        {
            Password = password,
            TemporaryDirectory = workDirectory,
            MaxEntries = 10000,
            MaxEntryBytes = 1024L * 1024 * 1024 * 1024,
            MaxTotalBytes = 2L * 1024 * 1024 * 1024 * 1024
        }, VmImportPlacementMode.HostDirectories, cancellationToken: cancellationToken);
    await using var batch = prepared.Batch;
    if (!prepared.Success)
    {
        if (prepared.Result.Exception is VmImportArchiveException archiveError)
            Console.WriteLine(archiveError.Reason);
        throw new InvalidOperationException(prepared.Result.Error, prepared.Result.Exception);
    }
    foreach (var session in batch!.VirtualMachines)
        Console.WriteLine(session.Preview.Name);
    // 需要导入时，在释放 batch 之前提交 VmImportOptions 并调用 ImportAsync。
}
```

<!-- api-example-archive-preview:end -->

<!-- api-example-export:start -->

### 导出一台虚拟机

导出磁盘，不导出检查点和运行状态。调用方提供一个已存在的目标根目录，并检查返回结果中的状态与保留文件。

```csharp
public static Task<VmExportResult> ExportVmAsync(Guid vmId, string destinationDirectory)
    => VmExportService.ExportAsync(vmId, destinationDirectory,
        includeVirtualHardDisks: true, excludedVirtualHardDisks: [],
        checkpointMode: VmExportCheckpointMode.None, selectedCheckpoint: null,
        includeRuntimeState: false);
// 失败时检查 RetainedPaths；OperationStateUnknown=true 不表示后台导出已停止。
```

<!-- api-example-export:end -->

导出可以选择磁盘、检查点和运行状态。导出成功后，如需归档，再调用打包接口。

<!-- api-example-encrypted-export:start -->

### 创建加密导出包

对已经导出的目录打包，选择压缩级别、AES-256 和保留源目录。密码由调用方取得；跨工具交换建议使用 ASCII 密码。

```csharp
public static async Task PackageExportAsync(string exportDirectory, string archivePath, string password,
    CancellationToken cancellationToken = default)
{
    var result = await VmExportPackagingService.CreatePackageAsync(exportDirectory, archivePath,
        new VmExportPackageOptions
        {
            CompressionLevel = VmExportCompressionLevel.Optimal,
            Encryption = VmExportEncryption.Aes256,
            Password = password,
            SourceHandling = VmExportSourceHandling.Keep
        }, cancellationToken: cancellationToken);
    if (!result.Success) throw new InvalidOperationException(result.Error, result.Exception);
    Console.WriteLine(result.Data!.ArchivePath);
}
```

<!-- api-example-encrypted-export:end -->

## 部署流程

GPU 部署需要选择设备、处理电源、扫描磁盘并安装驱动。Core 管理步骤和规则，应用展示当前状态并提交用户决定。

<!-- api-example-workflow:start -->

### 推进 GPU 部署流程

调用方提供已创建的部署会话，并通过 decide 回答本次待确认的问题。返回 null 表示取消；返回决定时使用当前请求的 Id。

```csharp
public static async Task<GpuDeploymentSnapshot> DeployGpuAsync(
    GpuDeploymentSession session,
    Func<GpuDecisionRequest, Task<GpuDeploymentDecision?>> decide)
{
    try
    {
        var status = await session.StartAsync();
        while (status.State == GpuDeploymentState.WaitingForDecision)
        {
            var request = status.PendingDecision
                ?? throw new InvalidOperationException("缺少待处理决定。");
            var decision = await decide(request);
            if (decision is null)
                return await session.CancelAsync();
            status = await session.SubmitDecisionAsync(decision);
        }
        // 调用者须检查 Completed / Failed / Cancelled / CleanupRequired 及错误字段。
        // 只有 AllowedActions 包含 RetryCleanup 且 OutcomeUnknown=false 时，才能在处理原因后重试清理。
        // OutcomeUnknown 表示原生操作尚未确认结束，不能通过重试清理来猜测是否完成。
        // 允许 Reconcile 时可先只读核实；核实成功也不会继续部署。
        return status;
    }
    catch
    {
        // 收集决定或提交失败时先等待清理；清理状态仍可从 session.GetStatus() 读取。
        await session.CancelAsync();
        throw;
    }
}
```

<!-- api-example-workflow:end -->

`PendingDecision` 是当前等待的决定，提交时携带它的请求 Id。`AllowedActions` 列出当前允许执行的动作，应用据此提供继续、取消或清理入口。

### 处理未完成的部署

`Completed` 表示流程完成。`CleanupRequired` 表示还有资源需要处理；`OutcomeUnknown` 表示底层操作的最终结果尚未确认。后者应先通过允许的 `ReconcileAsync()` 核实，再根据新状态决定清理或结束。

<!-- api-example-gpu-recovery:start -->

### 核实和清理 GPU 部署

使用原来的部署对象。先核实原任务；requestCleanup 表示调用方已经明确要求清理。方法返回后仍须检查状态和剩余资源，不循环重试写入。

```csharp
public static async Task<GpuDeploymentSnapshot> RecoverGpuAsync(GpuDeploymentSession session, bool requestCleanup)
{
    var status = session.GetStatus();
    if (status.AllowedActions.Contains(GpuDeploymentAction.Reconcile))
        status = await session.ReconcileAsync();
    if (requestCleanup && !status.OutcomeUnknown && status.AllowedActions.Contains(GpuDeploymentAction.RetryCleanup))
        status = await session.RetryCleanupAsync();
    // CleanupRequired 时检查 status.Recovery.Resources 和 status.CleanupError。
    // Failed / Cancelled 表示这次部署结束；恢复不会把它变成 Completed。
    return status;
}
```

<!-- api-example-gpu-recovery:end -->

恢复信息保存在原部署对象的 `Recovery` 中，包括执行记录、资源归属和待恢复状态。它用于当前进程内的这次部署。清理负责恢复哪些临时资源、哪些已完成修改会保留，见 `GpuDeploymentSession` 的对应方法。

## 连接与交互

先建立连接，再使用该连接提供的视频、控制和剪贴板能力。

| 后端 | 当前实现 |
| --- | --- |
| `RdpBasic` | 基本会话画面、键盘和鼠标 |
| `RdpEnhanced` | 增强会话画面、键盘和鼠标、剪贴板，以及分辨率请求 |
| `Nimue` | 尚未接入 |

基本和增强会话均由 FreeRDP 实现。内置后端尚未接入音频；向来宾输入音视频也未实现。`ConsoleService.GetBackends()` 提供当前后端目录，目标是否支持增强会话还取决于来宾。

<!-- api-example-resource:start -->

### 接收控制台画面

建立基本会话，把收到的画面交给 onFrame。取消等待或连接结束后，等待清理完成。onFrame 应快速处理；需要延后读取时自行 Retain。

```csharp
public static async Task<ConsoleDisconnectInfo> ObserveConsoleAsync(
    Guid vmId, Action<ConsoleFrame> onFrame, CancellationToken cancellationToken)
{
    await using var connection = ConsoleService.CreateConnection(
        new ConsoleConnectionOptions { VmId = vmId }, ConsoleService.RdpBasicBackendId);
    var video = connection.Video ?? throw new NotSupportedException("所选连接方式不提供画面。");
    var disconnected = new TaskCompletionSource<ConsoleDisconnectInfo>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    video.FrameReceived += onFrame;
    connection.Disconnected += info => disconnected.TrySetResult(info);
    connection.Start(); // 异步启动；成功由 Connected 事件通知，错误由 Disconnected 报告。
    try
    {
        return await disconnected.Task.WaitAsync(cancellationToken);
    }
    finally
    {
        await connection.DisconnectAsync();
        video.FrameReceived -= onFrame;
    }
}
```

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

升级前对照所用 Core 版本，重点检查标识、单位和提交方式。

多数虚拟机操作已使用 GUID；设备操作使用查询得到的设备引用。旧名称调用不能直接当作 GUID 使用。Linux GPU 部署仍保留其原有名称入口。

处理器的 `Reservation`、`Limit` 使用 0.001%，100000 表示 100%；内存 `Weight` 使用原始权重；网络带宽 `*BitsPerSecond` 使用 bit/s。从旧界面数值迁移时需要换算，不能只改属性名称。

处理器、内存和网卡的组合设置只提交 `changedProperties` 选中的属性。未知值、恢复默认值和未修改字段的区别，按各模型的属性说明处理。

创建、导入、导出、删除和电源控制使用单台入口。批量顺序、命名策略和创建后启动由应用安排。导入通过 `VmImportOptions` 明确 GUID 策略及目标配置。

导入预览的 `DynamicMemory`、`HasSavedState` 改为 `bool?`，`MinimumMemoryMiB`、`MaximumMemoryMiB` 改为 `ulong?`。这四项尚未由解析器读取，返回 `null`；调用方应保留未知含义，不再把它们当作关闭或零值。

连接后端标识使用 `RdpBasic`、`RdpEnhanced` 或尚未接入的 `Nimue`。旧 `Enhanced` 布尔值和 `freerdp` 标识需要在调用方转换。

其他签名变化以当前 API 参考为准。旧版本的调用示例不与本版本混用。

## 接口参考


<!-- api-reference:start -->

<!-- source-sha256: 28f616c5e7510345f019595fed6221db614e77fcc5dac3e3dbedafc228b8d530 -->

程序集：`ExHyperV.Core.dll` · 版本：`2.0.0-Alpha`。

本参考收录公开及受保护的自有成员，包括生成属性、命令和 record 主构造器。继承成员、record 编译器基础方法、隐式默认构造器及资源文案访问器不重复列出；`DEBUG` 专用成员不属于发布接口。

### 类型导航

#### 管理层面

##### 虚拟机

**查询**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmInstance `](#api-bcb2ffa2f3865a82) | ` ExHyperV.Models ` | 虚拟机清单及详情的可变模型；包含配置、查询时的状态和可独立刷新的性能样本。 |
| [` VmState `](#api-add6eece8fedcd7e) | ` ExHyperV.Models ` | 虚拟机当前的运行状态；不包含后端状态码。 |
| [` VmConfigurationState `](#api-4c35b7ba174517dd) | ` ExHyperV.Services ` | 一次设置操作需要的当前 VM 状态；无后台任务或进程级会话。 |
| [` VmInventorySnapshot `](#api-b63adbe1d39ffbae) | ` ExHyperV.Services ` | 一次虚拟机清单快照，包含虚拟机及未完整取得的可选信息。 |
| [` VmQueryCollection `](#api-697b0c9b00903189) | ` ExHyperV.Services ` | 字段所属的记录集合；虚拟机级字段与设备级字段分开，避免将设备属性误当全局开关。 |
| [` VmQueryCollectionResult `](#api-f21b0baf9fec63bd) | ` ExHyperV.Services ` | 一个子对象集合的查询结果；Available 且 Rows 为空才表示确认没有对象。 |
| [` VmQueryField `](#api-1bf7b2c1a1dd7920) | ` ExHyperV.Services ` | 内置可查询字段的描述；不能由调用方自行注入底层查询或执行代码。 |
| [` VmQueryField<T> `](#api-bb675fc3332742df) | ` ExHyperV.Services ` | 携带结果类型的字段；使用 VmQueryRow.Get 读取时无需强制转换。 |
| [` VmQueryFields `](#api-332b2654def2cef6) | ` ExHyperV.Services ` | Core 当前支持的全部可查询字段；目录本身不执行任何主机查询。 |
| [` VmQueryIssue `](#api-7dc8a4e125a28124) | ` ExHyperV.Services ` | 虚拟机列表中一项可选信息的读取问题；不代表已经确认该信息为空。 |
| [` VmQueryRequest `](#api-239750cc126c8774) | ` ExHyperV.Services ` | 一次字段级查询请求；字段来自 VmQueryFields.All 或各领域的字段目录。 |
| [` VmQueryResult `](#api-e685ca4677fb0596) | ` ExHyperV.Services ` | 自由组合查询的结果；必需的身份枚举失败由外层 ApiResponse 返回，其他失败保留在字段或集合中。 |
| [` VmQueryRow `](#api-0c141aad85fe3927) | ` ExHyperV.Services ` | 一台虚拟机或一个子对象的只读投影；仅包含请求字段。 |
| [` VmQueryService `](#api-043fe4f6d9c1db33) | ` ExHyperV.Services ` | 读取虚拟机列表并持有按需创建的性能计数器；调用者负责释放实例。 |
| [` VmQueryService.GpuUsageData `](#api-c6acc9d5e6fe51eb) | ` ExHyperV.Services ` | 按 GPU 引擎类别聚合的一次虚拟机采样结果。 |
| [` VmQueryService.VmDynamicMemoryData `](#api-71bb74d4d3230055) | ` ExHyperV.Services ` | 虚拟机当前内存占用与可用百分比。 |
| [` VmQueryTiming `](#api-ebac90479a0ebe98) | ` ExHyperV.Services ` | 查询引擎执行阶段的耗时，用于比较来源成本；并行阶段耗时不能直接相加作为总时间。 |
| [` VmQueryValue `](#api-6ab0c100b6d64513) | ` ExHyperV.Services ` | 供动态调用方枚举的字段结果基类。 |
| [` VmQueryValue<T> `](#api-78b4a43e7de4c00e) | ` ExHyperV.Services ` | 带类型的字段结果；读取 Value 前必须检查 HasValue。 |
| [` VmQueryValueState `](#api-4c2beb579a79ea91) | ` ExHyperV.Services ` | 一次读取的状态；只有 Available 的 Value 才是本次有效数据。 |

**查询 / 基本信息**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmIdentityFields `](#api-49c4513fba39b777) | ` ExHyperV.Services ` | 基本信息的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |

**查询 / 额外元数据**

**Core 已使用的键**

| 键名 | 用途 | 保存内容 |
| --- | --- | --- |
| ` ExHyperV.CpuAffinity ` | CPU 亲和性配置 | JSON 文本，记录所选处理器、使用的后端、宿主及处理器拓扑标识。 |
| ` ExHyperV.OSType ` | 操作系统分类 | 操作系统类型名称，例如 Linux，用于界面识别和图标显示。 |

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmMetadataFields `](#api-46296dc54cee191f) | ` ExHyperV.Services ` | 随虚拟机配置保存的自定义数据，以键和值组成一项。 |
| [` VmMetadataService `](#api-76ce30bc80984d54) | ` ExHyperV.Services ` | 通过 Hyper-V HostOnlyItems 读取和保存随虚拟机配置携带的字符串元数据。 |

**查询 / 处理器**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmCpuCoreMetric `](#api-49f6c6fb73044013) | ` ExHyperV.Models ` | 一次 CPU 核心采样；先检查 Sample.IsValid，再使用 Usage。 |
| [` CpuAffinityService `](#api-94857f108f5411f3) | ` ExHyperV.Services ` | 读取、应用和保存虚拟机 CPU 亲和性，按宿主调度器选择 CPU 组或进程接口。 |
| [` VmAffinityFields `](#api-a600131c0e0a9ab6) | ` ExHyperV.Services ` | 处理器亲和性的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmProcessorFields `](#api-a69b9ad31071d846) | ` ExHyperV.Services ` | 处理器的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmProcessorMetricFields `](#api-58662fd52bc5f375) | ` ExHyperV.Services ` | 处理器的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmProcessorService `](#api-5065bc52ce4b1bf9) | ` ExHyperV.Services ` | 读取、校验和写入虚拟处理器配置及宿主相关高级选项。 |

**查询 / 内存**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmMemoryFields `](#api-943caf626a4d8869) | ` ExHyperV.Services ` | 内存的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmMemoryService `](#api-699a5f0f3788cfe2) | ` ExHyperV.Services ` | 读取、校验和应用内存设置，处理动态内存、后备页和 vNUMA 的联动。 |

**查询 / 存储**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmDiskStatus `](#api-89d6aac76f2bf99b) | ` ExHyperV.Models ` | 虚拟机磁盘容量及性能采样。 |
| [` VmStorageFields `](#api-e23425c9bb2d0bc5) | ` ExHyperV.Services ` | 存储的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmStorageGlobalFields `](#api-7599034e4f313f52) | ` ExHyperV.Services ` | 存储的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmStorageService `](#api-7974d06edc0418d4) | ` ExHyperV.Services ` | 读取与配置虚拟机存储挂接，处理虚拟磁盘、光驱和物理磁盘。 |

**查询 / 网络**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmIpAddressResolution `](#api-0f1ce389f30b3647) | ` ExHyperV.Services ` | 合并已有地址的结果及是否仍需发起独立查询。 |
| [` VmIpService `](#api-0fc26b641e227c07) | ` ExHyperV.Services ` | 按宿主被动嗅探、来宾上报和宿主邻居缓存合并地址来源；宿主已观察到的地址优先。 |
| [` VmNetworkFields `](#api-7b60facd46cbd2aa) | ` ExHyperV.Services ` | 网络的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmNetworkService `](#api-401cb52499f1e6cd) | ` ExHyperV.Services ` | 读取网卡和交换机，配置连接、MAC、VLAN、带宽、安全及卸载能力。 |

**查询 / GPU**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmGpuFields `](#api-cfc336ca4a0f6001) | ` ExHyperV.Services ` | GPU 分区的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmGpuPerformanceFields `](#api-7a118e7aa337efa0) | ` ExHyperV.Services ` | GPU 性能的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmGpuRepairService `](#api-f4ea588e1d3931d7) | ` ExHyperV.Services ` | 失效 GPU-PV(GPU Partition 钉死的物理 GPU 路径在当前主机失配)的检测与修复。 |
| [` VmGpuService `](#api-491dd675104b9ad1) | ` ExHyperV.Services ` | 虚拟 GPU 分配及来宾驱动部署入口。 |

**查询 / PCIe**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmPcieDeviceFields `](#api-c38e41da03e378aa) | ` ExHyperV.Services ` | PCIe的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmPcieFields `](#api-455d9c1e196c68b4) | ` ExHyperV.Services ` | PCIe的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmPcieService `](#api-2166a73f1d6e8682) | ` ExHyperV.Services ` | 单台虚拟机的 PCIe 设置。 |

**查询 / 引导**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmBootFields `](#api-3749e73e66ae55f3) | ` ExHyperV.Services ` | 引导的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmBootGlobalFields `](#api-56cb0e13eca6ab99) | ` ExHyperV.Services ` | 引导的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmBootService `](#api-6b0a3fdedb6f2091) | ` ExHyperV.Services ` | 读取和配置 BIOS 或 UEFI 启动设备顺序与 Num Lock。 |

**查询 / 安全**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmSecurityFields `](#api-75f6ad683307ea45) | ` ExHyperV.Services ` | 安全的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmSecurityService `](#api-4b2f79ff06e9f37a) | ` ExHyperV.Services ` | 读取和修改第 2 代虚拟机的安全启动、虚拟 TPM、保存状态及迁移流量加密设置。 |

**查询 / 显示**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmDisplayFields `](#api-f028f6e3c9b3914e) | ` ExHyperV.Services ` | 显示的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmDisplaySettingsService `](#api-0b921ec439a343fc) | ` ExHyperV.Services ` | 读取和写入虚拟显示设备的分辨率策略。 |

**查询 / MMIO**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmMmioFields `](#api-85d07c02798ee6d2) | ` ExHyperV.Services ` | MMIO的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmMmioService `](#api-8782e1daccf68c59) | ` ExHyperV.Services ` | 读取和配置虚拟机 MMIO 地址空间，并按需通过启停目标虚拟机选择可用的宿主上限。 |

**查询 / 控制台**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmConsoleFields `](#api-e90b77e987feedc5) | ` ExHyperV.Services ` | 控制台的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmConsoleState `](#api-a83163a3d74dba4b) | ` ExHyperV.Services ` | 一次读取的控制台状态；只表示查询时的事实。 |
| [` VmConsoleSupportService `](#api-000ef782924fe6a9) | ` ExHyperV.Services ` | 读取虚拟机控制台状态，管理连接相关配置。 |

**查询 / 电源**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmBatteryState `](#api-90baee76b1294fcc) | ` ExHyperV.Services ` | 虚拟电池在当前虚拟机上的可用性和启用状态。 |
| [` VmPowerBatteryFields `](#api-23a2145335728dda) | ` ExHyperV.Services ` | 电源的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmPowerService `](#api-311d6ac85314661b) | ` ExHyperV.Services ` | 执行单台虚拟机电源控制，管理电源行为和虚拟电池。 |

**查询 / 检查点**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` SpacetimeNode `](#api-f48a66d9c8bc043c) | ` ExHyperV.Models ` | 检查点时间线节点。 |
| [` SpacetimeNodeType `](#api-390bb40cb297f1fa) | ` ExHyperV.Models ` | 时间线节点代表的对象种类。 |
| [` VmCheckpointFields `](#api-e7b7d8ebff4f68e5) | ` ExHyperV.Services ` | 检查点的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmCheckpointGlobalFields `](#api-6d7251fc824bd58c) | ` ExHyperV.Services ` | 检查点的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。 |
| [` VmSpacetimeService `](#api-2b7baa8a8ea0e2bc) | ` ExHyperV.Services ` | 管理检查点、回退及历史磁盘临时挂接；相关操作可能修改配置和磁盘文件。 |

**修改 / 网络**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` PortMonitorMode `](#api-93955a273cb0dc53) | ` ExHyperV.Models ` | 虚拟交换机端口镜像的角色。 |
| [` PvlanMode `](#api-53737ae28a159eae) | ` ExHyperV.Models ` | 专用 VLAN 中的端口隔离角色。 |
| [` VlanOperationMode `](#api-8174aba5de40c680) | ` ExHyperV.Models ` | 虚拟交换机端口的 VLAN 工作模式，对应 Hyper-V OperationMode。 |
| [` VmNetworkAdapter `](#api-0e0adcb5bf4c592f) | ` ExHyperV.Models ` | 聚合多个 Hyper-V 网络 WMI 类的网卡设置。 |
| [` VmIpAddressAssociation `](#api-b819752faee5254b) | ` ExHyperV.Services ` | 地址与请求目标之间能够确认的关联范围；不是服务器认证或防伪结论。 |
| [` VmIpAddressObservation `](#api-8bcc6af80b1bd206) | ` ExHyperV.Services ` | 一个 IPv4 地址及来源信息；多个地址分别返回，不拼成一个连接地址。 |
| [` VmIpAddressSource `](#api-5eeacef878c1a201) | ` ExHyperV.Services ` | 地址信息的来源，按宿主嗅探、来宾上报、系统邻居缓存的优先级使用。 |
| [` VmNetworkAdapterCreationResult `](#api-ca59f84f69e91911) | ` ExHyperV.Services ` | 添加网卡的结果，分别报告初始化、自动撤销和剩余资源。 |
| [` VmNetworkAdapterCreationState `](#api-2a7105bf5eb83ced) | ` ExHyperV.Services ` | 本次添加网卡在返回时已确认的资源状态。 |
| [` VmNetworkAdapterInitializationState `](#api-5ae63e7a6d49476d) | ` ExHyperV.Services ` | 本次网卡连接资源初始化步骤的执行结果，不等同于网卡当前是否连接交换机。 |
| [` VmNetworkService `](#api-401cb52499f1e6cd) | ` ExHyperV.Services ` | 读取网卡和交换机，配置连接、MAC、VLAN、带宽、安全及卸载能力。 |
| [` VmNetworkSettingsApplyResult `](#api-d1f57570480ce4e9) | ` ExHyperV.Services ` | 安全和卸载设置的组合写入结果，分别保留两步是否成功。 |

**修改**

**修改 / 基本信息**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmEditService `](#api-3152e140f9edbf4b) | ` ExHyperV.Services ` | 校验和修改虚拟机显示名称。 |

**修改 / 额外元数据**

**Core 已使用的键**

| 键名 | 用途 | 保存内容 |
| --- | --- | --- |
| ` ExHyperV.CpuAffinity ` | CPU 亲和性配置 | JSON 文本，记录所选处理器、使用的后端、宿主及处理器拓扑标识。 |
| ` ExHyperV.OSType ` | 操作系统分类 | 操作系统类型名称，例如 Linux，用于界面识别和图标显示。 |

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmMetadataService `](#api-76ce30bc80984d54) | ` ExHyperV.Services ` | 通过 Hyper-V HostOnlyItems 读取和保存随虚拟机配置携带的字符串元数据。 |
| [` OsImages `](#api-69a3d065d000a98e) | ` ExHyperV.Tools ` | 维护固定操作系统类型名称及其标准写法。 |

**修改 / 处理器**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` L3DistributionPolicy `](#api-6307fae9394bbacf) | ` ExHyperV.Models ` | L3 处理器分布策略（L3ProcessorDistributionPolicy，u8）：VP 在各虚拟 L3 缓存域之间的分布次序。 |
| [` LpiMode `](#api-331bc806004b3c0a) | ` ExHyperV.Models ` | 局部外设中断（LPI）模式。 |
| [` PageShatterMode `](#api-4f16a116b8548cb3) | ` ExHyperV.Models ` | 大页拆分缓解策略（EnablePageShattering，u8）：Default 使用主机策略，另外两个值明确请求启用或禁用缓解。 |
| [` SmtMode `](#api-0943f09d6d1027bd) | ` ExHyperV.Models ` | SMT（同时多线程）模式：继承主机 / 单线程 / 多线程。 |
| [` VmApicMode `](#api-cbf87948c5441cc2) | ` ExHyperV.Models ` | APIC 模式（Msvm\_ProcessorSettingData.ApicMode，u8）：自动 / 强制 xAPIC / 强制 x2APIC / Apic。 |
| [` VmMigrationCompatibilityMode `](#api-9565efeada622ee9) | ` ExHyperV.Models ` | 处理器迁移兼容模式（LimitProcessorFeaturesMode，u8）。 |
| [` VmProcessorCapability `](#api-542e7663b47b4a87) | ` ExHyperV.Models ` | 宿主为虚拟机提供的处理器设置项；可用不代表当前电源状态和硬件条件一定允许修改。 |
| [` VmProcessorSettings `](#api-1bd5c358d4233af8) | ` ExHyperV.Models ` | 虚拟机处理器设置的可变模型。 |
| [` CpuAffinityService `](#api-94857f108f5411f3) | ` ExHyperV.Services ` | 读取、应用和保存虚拟机 CPU 亲和性，按宿主调度器选择 CPU 组或进程接口。 |
| [` CpuAffinityService.AffinityState `](#api-47fadfc26c8a17d2) | ` ExHyperV.Services ` | 分别表示已确认的亲和性和保存的启动偏好，不把保存成功当成运行时已生效。 |
| [` CpuAffinityService.PreferenceStatus `](#api-a86b242e8bfed0d3) | ` ExHyperV.Services ` | 保存的处理器选择是否适用于当前宿主、后端和编号映射。 |
| [` CpuAffinityService.SaveResult `](#api-d5c38afbcf66d0c3) | ` ExHyperV.Services ` | 亲和性应用状态与 HostOnlyItems 元数据保存结果。 |
| [` CpuAffinityService.SaveStatus `](#api-3d34b02185eb00ea) | ` ExHyperV.Services ` | 保存亲和性请求后的应用状态；元数据持久化另由 MetadataSaved 表示。 |
| [` CpuAffinityService.StartupApplyStatus `](#api-f8d0e17de2ab6736) | ` ExHyperV.Services ` | 启动后恢复保存亲和性的结果。 |
| [` HostProcessorPlatform `](#api-be843791603a448d) | ` ExHyperV.Services ` | 虚拟处理器设置规则使用的宿主平台分类；由架构和厂商信息组合得到，不是独立的宿主查询字段。 |
| [` VmProcessorService `](#api-5065bc52ce4b1bf9) | ` ExHyperV.Services ` | 读取、校验和写入虚拟处理器配置及宿主相关高级选项。 |
| [` VmProcessorService.HostCapabilities `](#api-726a953ed0783af6) | ` ExHyperV.Services ` | 处理器配置规则所使用的宿主架构和硬件隔离能力。 |

**修改 / 内存**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` PageSizeItem `](#api-5eeaf12e93bd1e57) | ` ExHyperV.Models ` | 页面大小下拉项（与 VmMemorySettings.BackingPageSize 配套）。 |
| [` VmMemorySettings `](#api-1245af70d4ef31dd) | ` ExHyperV.Models ` | 虚拟机内存的读取结果和修改请求值；通过 changedProperties 明确选择要提交的属性。 |
| [` VmMemoryService `](#api-699a5f0f3788cfe2) | ` ExHyperV.Services ` | 读取、校验和应用内存设置，处理动态内存、后备页和 vNUMA 的联动。 |
| [` VmMemoryService.Capabilities `](#api-87d4c54605ffd52f) | ` ExHyperV.Services ` | 按运行状态计算出的内存字段编辑能力。 |

**修改 / 存储**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmStorageAttachment `](#api-5f84f3e0c5a86728) | ` ExHyperV.Models ` | VM 存储清单项：物理磁盘或虚拟磁盘均统一表达。 |
| [` VmStorageAddPreparation `](#api-dbd3558483ca1d26) | ` ExHyperV.Services ` | 存储请求的准备结果，区分可执行请求与校验或目录操作错误。 |
| [` VmStorageAddRequest `](#api-889fbbb37ca39d32) | ` ExHyperV.Services ` | 新增存储设备的完整请求；可先准备校验，再交给 AddDriveAsync 执行。 |
| [` VmStorageService `](#api-7974d06edc0418d4) | ` ExHyperV.Services ` | 读取与配置虚拟机存储挂接，处理虚拟磁盘、光驱和物理磁盘。 |
| [` VmStorageSlot `](#api-cc76a9e383bed2e6) | ` ExHyperV.Services ` | 控制器类型、从零开始的控制器编号和槽位。 |
| [` VmStorageSlotPlan `](#api-2cd3bdb239cc100c) | ` ExHyperV.Services ` | 新增设备的槽位选择结果；无可用槽位时携带说明。 |
| [` VmStorageSourceChoice `](#api-50d2022f49af22c1) | ` ExHyperV.Services ` | 按代数与物理设备限制规范化后的介质选择。 |

**修改 / GPU**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` GpuBackToPartitions `](#api-11fbc96797a74b58) | ` ExHyperV.Models ` | 请求从允许返回的待确认事项回到系统分区选择。 |
| [` GpuConfirmation `](#api-b72fad7f37732b94) | ` ExHyperV.Models ` | 接受或拒绝一项部署确认；拒绝会进入取消流程。 |
| [` GpuDecisionInput `](#api-e1c07ac7b4a8d65a) | ` ExHyperV.Models ` | 一个待确认事项所接受的输入结构。 |
| [` GpuDecisionKind `](#api-16bfedc8ae936ed7) | ` ExHyperV.Models ` | GPU 部署暂停时需要用户处理的具体事项。 |
| [` GpuDecisionRequest `](#api-640fbb80511f3d56) | ` ExHyperV.Models ` | 部署暂停时发布的待确认事项；每次请求有新的 Id，旧 ID 不能用于后续步骤。 |
| [` GpuDeploymentAction `](#api-ab1e60fe73d6e64d) | ` ExHyperV.Models ` | 当前部署快照允许调用方发起的动作。 |
| [` GpuDeploymentDecision `](#api-c5617284cebe0629) | ` ExHyperV.Models ` | 提交给部署会话的决定基类，决定必须对应当前尚未消费的请求 ID。 |
| [` GpuDeploymentMode `](#api-a5310e5188c13124) | ` ExHyperV.Models ` | 一次 GPU 部署要执行的工作范围。 |
| [` GpuDeploymentPartition `](#api-3b253a4b4e7b8932) | ` ExHyperV.Models ` | 供用户选择的系统分区快照。 |
| [` GpuDeploymentRecovery `](#api-b1a5a2f6f23a3554) | ` ExHyperV.Models ` | 部署结果不明时的核对材料；这些标识只用于诊断，不是执行任意底层命令的入口。 |
| [` GpuDeploymentSnapshot `](#api-3abc49fee5f8e4b6) | ` ExHyperV.Models ` | 部署会话发布的状态快照。 |
| [` GpuDeploymentState `](#api-b4fe6c09c7a1b03c) | ` ExHyperV.Models ` | 一次 GPU 部署的状态；调用方应以状态和 AllowedActions 决定下一次调用。 |
| [` GpuDeploymentStep `](#api-456c5ce4936ed611) | ` ExHyperV.Models ` | 部署步骤的显示状态快照。 |
| [` GpuDeploymentStepKind `](#api-6beb4e13dec8c4b8) | ` ExHyperV.Models ` | GPU 部署流程中的步骤类别。 |
| [` GpuDeploymentStepStatus `](#api-499dc8bdf05041ca) | ` ExHyperV.Models ` | 单个 GPU 部署步骤的执行状态。 |
| [` GpuLinuxInput `](#api-b7afa42a1fa76e7e) | ` ExHyperV.Models ` | 为 Linux 部署提供脚本和 SSH 凭据；会话在处理前复制二者，避免后续调用方修改影响待执行操作。 |
| [` GpuPartitionSelection `](#api-bb8b2e9014baee53) | ` ExHyperV.Models ` | 从当前候选列表中选择一个分区；不能用任意磁盘路径替代 PartitionId。 |
| [` GpuRecoveryOperation `](#api-f73dbd0a3968b7df) | ` ExHyperV.Models ` | 部署发起的一个原生操作及核实结果；原生引用只用于诊断。 |
| [` GpuRecoveryResource `](#api-04b306b383532708) | ` ExHyperV.Models ` | 本次部署需要恢复的一个临时资源，不包含驱动文件回滚。 |
| [` LinuxGpuDeploymentScript `](#api-d8954296637f67f4) | ` ExHyperV.Models ` | Linux GPU 部署脚本的可变描述。 |
| [` SshCredentials `](#api-86effeea9b57e0d7) | ` ExHyperV.Models ` | Linux GPU 部署用的 SSH 连接凭据。 |
| [` VmGpuAssignment `](#api-51ff6610dbb72a26) | ` ExHyperV.Models ` | 虚拟机已分配 GPU 的可变查询模型；修改字段不会自动改变 Hyper-V 分配。 |
| [` GpuDeploymentSession `](#api-daf38b34353e49be) | ` ExHyperV.Services ` | 管理一次进程内 GPU 部署；Core 推进各步骤，并在需要确认时暂停，等待带版本标识的明确决定。 |
| [` VmGpuRepairService `](#api-f4ea588e1d3931d7) | ` ExHyperV.Services ` | 失效 GPU-PV(GPU Partition 钉死的物理 GPU 路径在当前主机失配)的检测与修复。 |
| [` VmGpuRepairService.ConfigurationFile `](#api-423609453ea668ba) | ` ExHyperV.Services ` | 注册虚拟机的配置文件位置与当前运行状态。 |
| [` VmGpuRepairService.RebindCandidate `](#api-432c27a8ad57f0a0) | ` ExHyperV.Services ` | 当前宿主上可用于重绑的具体 GPU；由扫描结果提供。 |
| [` VmGpuRepairService.RepairResult `](#api-4e53775e4963caa5) | ` ExHyperV.Services ` | 本次提交完成的 GPU 分区修复数量。 |
| [` VmGpuRepairService.StaleGpuPartition `](#api-93567a4f8b8309b4) | ` ExHyperV.Services ` | 扫描得到的失效 GPU 分区配置；可交给 RepairAsync 修复。 |
| [` VmGpuService `](#api-491dd675104b9ad1) | ` ExHyperV.Services ` | 虚拟 GPU 分配及来宾驱动部署入口。 |
| [` VmPartitionScanResult `](#api-d9b095e53b531a9d) | ` ExHyperV.Services ` | GPU 分区扫描结果，区分扫描和恢复失败。 |

**修改 / PCIe**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmPcieDeviceSetting `](#api-75dcd2fafb7a6260) | ` ExHyperV.Models ` | 单个已分配 PCIe 设备的身份和来宾模式设置；编辑 GuestMode 后由服务应用，AppliedGuestMode 保存已应用值。 |
| [` VmPcieGuestMode `](#api-6870c8b5d182ab07) | ` ExHyperV.Models ` | 来宾访问 PCIe 设备的呈现模式。 |
| [` VmPcieSettings `](#api-0ee78ad274e05163) | ` ExHyperV.Models ` | 虚拟机 PCIe 系统设置及已分配设备的查询快照；Devices 列表仍可修改，修改不会自动写回 Hyper-V。 |
| [` PcieAssignmentDecision `](#api-c531bbc4afdd18f2) | ` ExHyperV.Services ` | 对一次特定 PCIe 确认请求的决定，不能重用到其他步骤。 |
| [` PcieAssignmentError `](#api-d16e33c86a21492c) | ` ExHyperV.Services ` | PCIe 分配流程失败所在的阶段；具体细节见状态消息。 |
| [` PcieAssignmentKind `](#api-a51fd65e89867236) | ` ExHyperV.Services ` | PCIe 设备当前归属或分配目标的种类。 |
| [` PcieAssignmentSession `](#api-0bba6ca7be99b19a) | ` ExHyperV.Services ` | 保存一次 PCIe 分配的检查、确认和执行状态，供界面或其他调用方逐步驱动。 |
| [` PcieAssignmentState `](#api-56f294436caf98bb) | ` ExHyperV.Services ` | PCIe 分配流程的不可变状态快照。 |
| [` PcieAssignmentStep `](#api-aeb7200a2284716c) | ` ExHyperV.Services ` | PCIe 分配流程当前执行或确认步骤。 |
| [` PcieAssignmentTarget `](#api-dbd5c750f9191760) | ` ExHyperV.Services ` | PCIe 设备的归属身份，不使用本地化文字或内部字符串标记表示宿主与未分配状态。 |
| [` VmPcieChangeKind `](#api-a94b7e1622e15e00) | ` ExHyperV.Services ` | 本次 PCIe 修改的目标类型。 |
| [` VmPcieChangeResult `](#api-c1479126dc2f9102) | ` ExHyperV.Services ` | PCIe 一次修改的取消标记和底层执行响应。 |
| [` VmPcieChangeSession `](#api-40f4032888198ce8) | ` ExHyperV.Services ` | 保存一次 PCIe 修改的输入、永久开启模拟确认要求和执行结果。 |
| [` VmPcieService `](#api-2166a73f1d6e8682) | ` ExHyperV.Services ` | 单台虚拟机的 PCIe 设置。 |

**修改 / 引导**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` BootDeviceKind `](#api-d8aedb8448e3e799) | ` ExHyperV.Models ` | 启动设备的业务类别，不包含界面图标或列表布局状态。 |
| [` BootOrderItem `](#api-e91e11a676cd0968) | ` ExHyperV.Models ` | 虚拟机引导项，由 VmBootService 读取；写回时必须使用同一虚拟机读取的对象。 |
| [` VmBootService `](#api-6b0a3fdedb6f2091) | ` ExHyperV.Services ` | 读取和配置 BIOS 或 UEFI 启动设备顺序与 Num Lock。 |

**修改 / 安全**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmIsolationSupport `](#api-621a83d450366f2e) | ` ExHyperV.Services ` | 宿主支持的虚拟机硬件隔离能力。 |
| [` VmSecurityService `](#api-4b2f79ff06e9f37a) | ` ExHyperV.Services ` | 读取和修改第 2 代虚拟机的安全启动、虚拟 TPM、保存状态及迁移流量加密设置。 |
| [` VmSecurityService.Capabilities `](#api-3901bdc4dc40e883) | ` ExHyperV.Services ` | 当前上下文下整组及各项安全设置的编辑许可。 |
| [` VmSecurityService.SecureBootTemplate `](#api-fd164ffe192c3a62) | ` ExHyperV.Services ` | 宿主提供的安全启动信任模板。 |
| [` VmSecurityService.Setting `](#api-5a0351ede3d222c3) | ` ExHyperV.Services ` | 需要进行互锁及状态校验的安全设置项。 |
| [` VmSecurityService.VmSecurityInfo `](#api-86e6f74c910a8e21) | ` ExHyperV.Services ` | 成功读取的安全设置快照；读取失败由外层 ApiResponse 表示。 |

**修改 / 显示**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmDisplayResolution `](#api-e3e41eea722e9f8f) | ` ExHyperV.Services ` | 虚拟显示设备的分辨率策略与物理像素尺寸。 |
| [` VmDisplaySettingsService `](#api-0b921ec439a343fc) | ` ExHyperV.Services ` | 读取和写入虚拟显示设备的分辨率策略。 |

**修改 / MMIO**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmMmioSettings `](#api-04e3b09953c73d67) | ` ExHyperV.Models ` | 虚拟机 MMIO 地址空间设置（Msvm\_VirtualSystemSettingData，单位 MiB）。 |
| [` MmioCheckResultType `](#api-9e6f4f1e1c48db2a) | ` ExHyperV.Services ` | 虚拟机 MMIO 空间与当前主机分配要求的比较结果。 |
| [` VmMmioService `](#api-8782e1daccf68c59) | ` ExHyperV.Services ` | 读取和配置虚拟机 MMIO 地址空间，并按需通过启停目标虚拟机选择可用的宿主上限。 |
| [` VmMmioService.MmioPlan `](#api-64c7d76a38e1d017) | ` ExHyperV.Services ` | MMIO 间隙方案（单位 MiB）：高位基址、高位大小、低位大小。 |

**修改 / 控制台**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmConsoleSupportService `](#api-000ef782924fe6a9) | ` ExHyperV.Services ` | 读取虚拟机控制台状态，管理连接相关配置。 |

**修改 / 电源**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmPowerAction `](#api-c38f561557cd3ed7) | ` ExHyperV.Models ` | 虚拟机支持的电源动作；强制断电必须由调用方明确选择。 |
| [` VmPowerOperationSession `](#api-a82fe27a02230e33) | ` ExHyperV.Services ` | 单台虚拟机的一次电源动作状态机，包含失败识别、修复确认及重试。 |
| [` VmPowerOperationStage `](#api-0224dda187b691bd) | ` ExHyperV.Services ` | 电源动作及启动修复确认流程的当前阶段。 |
| [` VmPowerRepairConfirmation `](#api-a8ec61904ea7c346) | ` ExHyperV.Services ` | 由 Core 生成的修复确认内容；调用者呈现后提交用户决定。 |
| [` VmPowerRepairKind `](#api-dac39fcfb92d79df) | ` ExHyperV.Services ` | 启动或电源动作失败后提出的修复类型。 |
| [` VmPowerService `](#api-311d6ac85314661b) | ` ExHyperV.Services ` | 执行单台虚拟机电源控制，管理电源行为和虚拟电池。 |

**修改 / 检查点**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` SpacetimeMode `](#api-ce8c1d96a58120c3) | ` ExHyperV.Models ` | 检查点时间线的保存模式。 |
| [` VmSpacetimeService `](#api-2b7baa8a8ea0e2bc) | ` ExHyperV.Services ` | 管理检查点、回退及历史磁盘临时挂接；相关操作可能修改配置和磁盘文件。 |

**实例**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmRegistrationState `](#api-eb65900437449cfd) | ` ExHyperV.Services ` | 操作结束时已确认的虚拟机注册状态；未知不等于仍在册或已经注销。 |

**实例 / 创建**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmCreationParameters `](#api-20add229729c5226) | ` ExHyperV.Models ` | 创建虚拟机的可变输入。 |
| [` VmCreateService `](#api-68709c8b2fa3259d) | ` ExHyperV.Services ` | 校验创建参数并完成一台虚拟机的配置、存储和网络创建。 |
| [` VmCreationOptions `](#api-dd7828c0554323b6) | ` ExHyperV.Services ` | 创建前可查询的宿主目录、配置版本、隔离能力和交换机清单；具体选项由调用方决定。 |
| [` VmCreationResult `](#api-3b4fb39ed87dbfa4) | ` ExHyperV.Services ` | 单台创建结果，分别保留业务完成、真实身份与失败后的资源状态。 |

**实例 / 删除**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmDeleteService `](#api-daf0227033e23922) | ` ExHyperV.Services ` | 提供保留磁盘与恢复配置的删除，以及注销后清理专属文件的彻底删除。 |
| [` VmDeleteService.PurgePreview `](#api-a806775d80fa93ae) | ` ExHyperV.Services ` | 彻底删除预览。 |
| [` VmDeletionResult `](#api-ca976d50d35e3e64) | ` ExHyperV.Services ` | 删除的实际完成情况；注销状态、恢复配置与文件清理分别报告。 |

**实例 / 导入**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmImportDiskPreview `](#api-4a13983a582f9896) | ` ExHyperV.Models ` | 导入预览中的磁盘文件信息。 |
| [` VmImportGuidMode `](#api-d2925feb598f9ae9) | ` ExHyperV.Models ` | 导入时对虚拟机唯一身份的处理方式；由调用方明确选择。 |
| [` VmImportOptions `](#api-7dca25c06b2f128b) | ` ExHyperV.Models ` | 一台虚拟机的导入选择。 |
| [` VmImportPlacementMode `](#api-6d79ba6e0fb88a01) | ` ExHyperV.Models ` | 导入虚拟机时配置和磁盘的放置方式。 |
| [` VmImportPreview `](#api-db92ad11e50deda7) | ` ExHyperV.Models ` | 来源配置及导入检查结果。 |
| [` VmImportSourceKind `](#api-e60536341907c3b7) | ` ExHyperV.Models ` | 虚拟机导入来源的容器形式。 |
| [` VmImportArchiveError `](#api-c6787d3b02e89ecb) | ` ExHyperV.Services ` | ZIP 读取失败中需要调用方区分的原因。 |
| [` VmImportArchiveException `](#api-d3966461a931a6ad) | ` ExHyperV.Services ` | ZIP 读取失败的异常附件，可从准备结果的 Result.Exception 取得；正式导入通过 ArchiveFailure 返回可序列化的详情。 |
| [` VmImportArchiveFailure `](#api-b713808ff11bc45e) | ` ExHyperV.Services ` | 可序列化的 ZIP 失败详情；用于导入结果，完整错误说明仍从结果的 Failure 读取。 |
| [` VmImportArchiveOptions `](#api-c111a8996844ae5b) | ` ExHyperV.Services ` | 从 ZIP 准备预览及导入时使用的密码、解压额度和预览工作目录。 |
| [` VmImportBatchSession `](#api-89518e59536dbfc5) | ` ExHyperV.Services ` | 共享来源和临时目录的一组导入会话；即使只查看预览也应释放。 |
| [` VmImportCleanupState `](#api-919fbe6209ea2900) | ` ExHyperV.Services ` | 导入会话工作资源的清理状态，与虚拟机是否已经导入分别判断。 |
| [` VmImportPreparationResult `](#api-3a7a2c1f20362d6e) | ` ExHyperV.Services ` | 导入预览的准备结果，以及仍由调用方负责释放的批次。 |
| [` VmImportResult `](#api-8e5a833b3845c549) | ` ExHyperV.Services ` | 单台导入结果；失败仍可能已经注册虚拟机或留下需要处理的工作资源。 |
| [` VmImportService `](#api-e735a2b9bb924f8e) | ` ExHyperV.Services ` | 解析和准备导入预览，再按选择实现虚拟机；会话负责暂存资源的释放。 |
| [` VmImportSession `](#api-eeb2c99295cf89c9) | ` ExHyperV.Services ` | 单台虚拟机的导入工作状态；由预览入口创建，使用后必须异步释放。 |

**实例 / 导出**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmExportCheckpointMode `](#api-96131a1b1362b9c4) | ` ExHyperV.Services ` | 本次导出包含哪些检查点。 |
| [` VmExportCompressionLevel `](#api-9ede9b4035f4d383) | ` ExHyperV.Services ` | 导出 ZIP 的压缩级别；压缩级别与是否加密互相独立。 |
| [` VmExportEncryption `](#api-f20936e8ebe738c4) | ` ExHyperV.Services ` | 新建 ZIP 的内容加密方式。 |
| [` VmExportPackageException `](#api-525cd2677b70ce87) | ` ExHyperV.Services ` | 打包失败的诊断附件，保存在失败 ApiResponse 的 Exception 属性中，不要求解析错误文本。 |
| [` VmExportPackageMode `](#api-2f3d1a9483e188d8) | ` ExHyperV.Services ` | ZIP 包的存储或压缩方式。 |
| [` VmExportPackageOptions `](#api-d564e43f787184c7) | ` ExHyperV.Services ` | 创建导出 ZIP 的选项，默认均衡压缩、不加密并保留源目录。 |
| [` VmExportPackageProgress `](#api-48e2a36722f1a3d6) | ` ExHyperV.Services ` | 当前打包阶段及该阶段的整数百分比。 |
| [` VmExportPackageResult `](#api-c33e99fb2d79a509) | ` ExHyperV.Services ` | 已完成归档的路径及独立的源目录清理结果。 |
| [` VmExportPackageStage `](#api-3212c0d6001d42a9) | ` ExHyperV.Services ` | 导出包正在创建或校验的阶段。 |
| [` VmExportPackagingService `](#api-6abc347896f94f96) | ` ExHyperV.Services ` | 将虚拟机导出目录打包为经校验的 ZIP，支持压缩、AES-256 和源目录保留选择。 |
| [` VmExportResult `](#api-f27e83724f607882) | ` ExHyperV.Services ` | 单台虚拟机导出的完成状态和保留文件。 |
| [` VmExportService `](#api-d0ef56e709d38d6f) | ` ExHyperV.Services ` | 导出一台虚拟机的配置、磁盘、检查点和运行状态。 |
| [` VmExportService.CheckpointInfo `](#api-534d3f3efef99cf5) | ` ExHyperV.Services ` | 检查点身份和父子关系；单检查点导出使用查询返回的此对象。 |
| [` VmExportService.VirtualHardDiskInfo `](#api-25182f12944e0c0c) | ` ExHyperV.Services ` | 可供导出排除选择的虚拟硬盘；将查询返回的对象传回导出方法。 |
| [` VmExportSourceHandling `](#api-cca6bbe6993f1c17) | ` ExHyperV.Services ` | 归档通过完整性校验后的源目录处理方式。 |

##### 宿主

**查询**

**修改**

**查询 / 基本信息**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostSystemInfoService `](#api-c97822da1d5f0c88) | ` ExHyperV.Services ` | 读取用于显示的宿主系统概况，并在进程内缓存首次查询任务。 |
| [` HostSystemInfoService.SystemInfo `](#api-0f0119c09ea17f4b) | ` ExHyperV.Services ` | 宿主基本信息汇总，包含系统名称与构建号、系统位数、处理器型号说明和内存容量文本。 |

**查询 / 处理器**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` CpuCoreType `](#api-ed1be2be550b34d3) | ` ExHyperV.Models ` | 宿主处理器核心的性能类别。 |
| [` HostAffinityProcessor `](#api-671735ce8d9b8726) | ` ExHyperV.Services ` | 当前亲和性后端提供的一个宿主逻辑处理器选项。 |
| [` HostProcessorPlatformService `](#api-1d2842d5ce9c4c63) | ` ExHyperV.Services ` | 分别查询宿主架构和处理器厂商；两项可以独立读取。 |
| [` HostProcessorService `](#api-abc371c87161ae39) | ` ExHyperV.Services ` | 读取宿主逻辑处理器及可用于虚拟机亲和性的处理器集合。 |
| [` HostSystemInfoService.SystemInfo `](#api-0f0119c09ea17f4b) | ` ExHyperV.Services ` | 宿主基本信息汇总，包含系统名称与构建号、系统位数、处理器型号说明和内存容量文本。 |
| [` HyperVCpuMonitorService `](#api-3d00ec7a4b276e23) | ` ExHyperV.Services ` | 维护宿主及虚拟机 CPU 性能计数器；每个实例拥有自己的后台维护任务和释放生命周期。 |

**查询 / 处理器组**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostCpuGroupInfo `](#api-e73baff26ca9c0f9) | ` ExHyperV.Services ` | 宿主 CPU 组的处理器、调度策略与成员快照。 |
| [` HostCpuGroupMember `](#api-d87f4f08ed1246c8) | ` ExHyperV.Services ` | 绑定到 CPU 组的 Hyper-V 虚拟机。 |
| [` HostCpuGroupService `](#api-0333bc67cb65a850) | ` ExHyperV.Services ` | 查询宿主 CPU 组，修改配额、优先级、空闲处理器保留数量和相对权重。 |

**查询 / 内存**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostSystemInfoService.SystemInfo `](#api-0f0119c09ea17f4b) | ` ExHyperV.Services ` | 宿主基本信息汇总，包含系统名称与构建号、系统位数、处理器型号说明和内存容量文本。 |
| [` HyperVNumaService `](#api-8469acca0f2d44da) | ` ExHyperV.Services ` | 读取和修改宿主 Hyper-V 跨 NUMA 节点分配内存策略。 |

**查询 / 存储**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostDiskInfo `](#api-4999a42e73dce6e6) | ` ExHyperV.Models ` | 宿主磁盘的身份、容量与直通候选状态，包含已挂载的虚拟磁盘。 |
| [` HostOpticalDriveInfo `](#api-b89e91519a465463) | ` ExHyperV.Models ` | 具有 PnP 身份的宿主光驱查询结果；物理光驱配置用于第 1 代虚拟机，第 2 代使用 ISO 映像。 |
| [` HostDiskMountResult `](#api-2dc1cd9f66ce73d2) | ` ExHyperV.Services ` | 映像挂载结果，分别表达挂载状态、磁盘发现和清理责任。 |
| [` HostDiskReference `](#api-b7abecd5b9d2180e) | ` ExHyperV.Services ` | 查询时选中的宿主磁盘身份；修改时原样回传，不以磁盘编号单独确认设备。 |
| [` HostDiskService `](#api-e3330b9fc8413dee) | ` ExHyperV.Services ` | 查询宿主物理磁盘、光驱和分区，并执行磁盘挂载、脱机、只读及盘符操作。 |
| [` HostNvmeService `](#api-d11d60064e518797) | ` ExHyperV.Services ` | 管理主机原生 NVMe 功能的注册表覆盖开关；配置需要管理员权限并在重启后由系统采用。 |
| [` HostPartitionReference `](#api-7c77af8af6b14c8a) | ` ExHyperV.Services ` | 查询时选中的宿主分区身份，包含所属磁盘；添加或移除盘符时原样回传。 |
| [` HostVolumeAutoMountService `](#api-4d32cc978f36178b) | ` ExHyperV.Services ` | 管理 Windows Mount Manager 的宿主全局卷自动挂载策略。 |
| [` HyperVHostSettings `](#api-4ad42a943b9e96dd) | ` ExHyperV.Services ` | 宿主 Hyper-V 管理服务的配置快照。 |

**查询 / 网络**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` SwitchEndpointInfo `](#api-4c71af5310c751ad) | ` ExHyperV.Models ` | 交换机端点的显示摘要：虚拟机网卡或宿主网卡。 |
| [` SwitchInfo `](#api-920c0d414c414611) | ` ExHyperV.Models ` | 虚拟交换机的连接模式、上游网卡与宿主接入状态。 |
| [` SwitchMode `](#api-84f5dcdc201f39f8) | ` ExHyperV.Models ` | Hyper-V 交换机的桥接、ICS NAT 和隔离配置模式。 |
| [` SwitchUpstreamAdapter `](#api-2cff6962c24db25a) | ` ExHyperV.Models ` | 可用的上游物理网卡；使用连接 GUID 和设备标识匹配，不依赖显示名称。 |
| [` HyperVHostSettings `](#api-4ad42a943b9e96dd) | ` ExHyperV.Services ` | 宿主 Hyper-V 管理服务的配置快照。 |
| [` HyperVSwitchService `](#api-0f66c11c037b51ce) | ` ExHyperV.Services ` | 读取和配置本机 Hyper-V 虚拟交换机及 ICS 上游共享关系。 |
| [` SwitchConfigurationConflictException `](#api-c8f4e6a6deee535d) | ` ExHyperV.Services ` | 表示交换机变更违反当前配置保护或冲突规则。 |
| [` SwitchConfigurationRules `](#api-942b8beb34283a23) | ` ExHyperV.Services ` | 对交换机配置快照进行规则判断，不直接修改主机网络。 |

**查询 / GPU**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostGpuInfo `](#api-7e834c339070727a) | ` ExHyperV.Models ` | 宿主当前存在的显示适配器，由 HostGpuService.GetHostGpusAsync 查询产生；IsPartitionable 表示当前是否存在可分区资源。 |
| [` HostGpuService `](#api-9579b5f504d1fb87) | ` ExHyperV.Services ` | 读取宿主 GPU 及分区能力，并配置宿主 GPU 分配策略。 |

**查询 / PCIe**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` PcieDeviceInfo `](#api-f5ca6ddfa1385521) | ` ExHyperV.Models ` | 具有 DDA 位置路径的 PCIe 设备及其当前归属。 |
| [` ControllerDisk `](#api-300a4de29fad713b) | ` ExHyperV.Services ` | PCIe 存储控制器名下的物理磁盘，用于直通前确认能否脱机。 |
| [` HostPcieService `](#api-35590b81f811c4b0) | ` ExHyperV.Services ` | 读取宿主 PCIe/DDA 分配状态并执行设备转移。 |
| [` PcieHostInfo `](#api-8c62d8d2a672175b) | ` ExHyperV.Services ` | 宿主 PCIe 设备归属和可分配虚拟机的同次查询快照。 |

**查询 / 调度器**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HyperVSchedulerService `](#api-631237297addc003) | ` ExHyperV.Services ` | 分别读取宿主当前运行的调度器和引导配置，并设置后续启动的调度器选择。 |
| [` HyperVSchedulerType `](#api-0e00c4ef25825f2b) | ` ExHyperV.Services ` | Hyper-V 调度器模式；Unknown 表示未识别的结果。 |

**查询 / 电源**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostPowerPlan `](#api-a57b694781de3497) | ` ExHyperV.Services ` | 宿主电源计划的显示名称与稳定 GUID。 |
| [` HostPowerPlanService `](#api-79f9ca4dbc8029c4) | ` ExHyperV.Services ` | 枚举宿主电源计划并设置当前活动计划；设置影响整个宿主。 |

**查询 / Hyper-V**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HyperVCheckResult `](#api-3df2cf54f9233c21) | ` ExHyperV.Services ` | 一个检查阶段的状态及失败信息，不包含修改系统的动作。 |
| [` HyperVCheckStage `](#api-5a39edf418e8defa) | ` ExHyperV.Services ` | Hyper-V 运行检查的阶段，顺序也是首个问题的判定优先级。 |
| [` HyperVCheckState `](#api-15466f3ac21ed151) | ` ExHyperV.Services ` | 一个运行检查的结论；无法确认不等于明确不可用。 |
| [` HyperVDiagnosticKind `](#api-2f2bab5dd6082806) | ` ExHyperV.Services ` | 运行诊断的机器可读分类；原始错误另见检查结果的 Failure。 |
| [` HyperVEnvironmentStatus `](#api-ea63144f831629dc) | ` ExHyperV.Services ` | Hyper-V 运行环境的结构化检查结果；按阶段顺序遇到首个问题后停止后续检查。 |
| [` HyperVHostService `](#api-d765f6dc004e22df) | ` ExHyperV.Services ` | 检测本机 Hyper-V 环境，并提交 Windows 功能及引导配置变更。 |
| [` HyperVHostSettings `](#api-4ad42a943b9e96dd) | ` ExHyperV.Services ` | 宿主 Hyper-V 管理服务的配置快照。 |
| [` HyperVHostSettingsService `](#api-7d8a2f197b518ae9) | ` ExHyperV.Services ` | 读取宿主 Hyper-V 配置快照，修改新建虚拟机的默认配置目录。 |
| [` VmHostDefaultPaths `](#api-4b902ac18bf9c3fd) | ` ExHyperV.Services ` | 宿主创建虚拟机和虚拟硬盘时使用的默认目录。 |
| [` VmmsImageInfo `](#api-406890c926a27d9e) | ` ExHyperV.Services ` | VMMS 程序文件的路径与存在状态。 |
| [` VmmsImageState `](#api-897673c08040ffe4) | ` ExHyperV.Services ` | VMMS 程序路径的诊断状态。 |
| [` VmmsServiceInfo `](#api-7f0ea6f367307b55) | ` ExHyperV.Services ` | VMMS Windows 服务的状态、启动配置与程序信息。 |

**查询 / 控制台**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HyperVHostSettings `](#api-4ad42a943b9e96dd) | ` ExHyperV.Services ` | 宿主 Hyper-V 管理服务的配置快照。 |

**查询 / 分组**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmGroup `](#api-4eb8163b9d96a53e) | ` ExHyperV.Models ` | Hyper-V 分组及其成员快照。 |
| [` VmGroupKind `](#api-7009c707c715a19c) | ` ExHyperV.Models ` | Hyper-V 原生分组允许包含的成员类别。 |
| [` VmGroupSnapshot `](#api-5ae62ce278562650) | ` ExHyperV.Models ` | 原生分组的成员关系构成图结构；同一虚拟机或分组可以属于多个父分组。 |
| [` VmGroupFields `](#api-e8293dd4c364f26d) | ` ExHyperV.Services ` | 虚拟机直接所属分组的查询成员。 |
| [` VmGroupMoveResult `](#api-cc37958683c518ee) | ` ExHyperV.Services ` | 分组移动后的目标标识，以及目标组是否发生重建。 |
| [` VmGroupService `](#api-99e3f6668ba1efe6) | ` ExHyperV.Services ` | 通过 Hyper-V 分组接口读取及修改分组；本进程内串行执行写入。 |

**查询 / ARP 嗅探**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` ArpSnoopService `](#api-ba368a7c161ab2aa) | ` ExHyperV.Services ` | 捕获本机 ARP 流量并维护 MAC 到 IPv4 的近期映射，作为来宾上报地址之外的辅助来源。 |

**查询 / OpenHCL**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostOpenHclService `](#api-1c6d8e37aafe2c57) | ` ExHyperV.Services ` | 读取和设置宿主从文件加载 OpenHCL 固件的策略。 |

**查询 / 系统产品类型**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostProductTypePendingSwitch `](#api-5e4d7f06de4c8c7b) | ` ExHyperV.Services ` | 此前产品类型切换的挂起记录。 |
| [` HostProductTypeService `](#api-b3bf26b1b466b9c0) | ` ExHyperV.Services ` | 读取和实验性修改宿主 Windows 产品类型，通过 SYSTEM hive 替换安排重启后生效。 |
| [` HostProductTypeSwitchResult `](#api-9f55bd406a2d2ac3) | ` ExHyperV.Services ` | 产品类型切换结果；即使失败也保留恢复材料和未卸载注册表键。 |
| [` HostProductTypeSwitchState `](#api-2ced745c4ac2dd94) | ` ExHyperV.Services ` | 实验性系统产品类型切换的提交状态。 |

**修改 / 处理器**

本分类暂未归入接口。

**修改 / 处理器组**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostCpuGroupService `](#api-0333bc67cb65a850) | ` ExHyperV.Services ` | 查询宿主 CPU 组，修改配额、优先级、空闲处理器保留数量和相对权重。 |

**修改 / 内存**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HyperVNumaService `](#api-8469acca0f2d44da) | ` ExHyperV.Services ` | 读取和修改宿主 Hyper-V 跨 NUMA 节点分配内存策略。 |

**修改 / 存储**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostDiskMountLease `](#api-f899a0c126136eb7) | ` ExHyperV.Services ` | 一次宿主映像挂载的独占使用期。 |
| [` HostDiskService `](#api-e3330b9fc8413dee) | ` ExHyperV.Services ` | 查询宿主物理磁盘、光驱和分区，并执行磁盘挂载、脱机、只读及盘符操作。 |
| [` HostNvmeService `](#api-d11d60064e518797) | ` ExHyperV.Services ` | 管理主机原生 NVMe 功能的注册表覆盖开关；配置需要管理员权限并在重启后由系统采用。 |
| [` HostVolumeAutoMountService `](#api-4d32cc978f36178b) | ` ExHyperV.Services ` | 管理 Windows Mount Manager 的宿主全局卷自动挂载策略。 |

**修改 / 网络**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostNetworkService `](#api-90a99e60c4861486) | ` ExHyperV.Services ` | 校验并修改宿主动态 MAC 地址池。 |
| [` HyperVSwitchService `](#api-0f66c11c037b51ce) | ` ExHyperV.Services ` | 读取和配置本机 Hyper-V 虚拟交换机及 ICS 上游共享关系。 |
| [` SwitchConfigurationRules `](#api-942b8beb34283a23) | ` ExHyperV.Services ` | 对交换机配置快照进行规则判断，不直接修改主机网络。 |

**修改 / GPU**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostGpuService `](#api-9579b5f504d1fb87) | ` ExHyperV.Services ` | 读取宿主 GPU 及分区能力，并配置宿主 GPU 分配策略。 |

**修改 / PCIe**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostPcieService `](#api-35590b81f811c4b0) | ` ExHyperV.Services ` | 读取宿主 PCIe/DDA 分配状态并执行设备转移。 |

**修改 / 调度器**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HyperVSchedulerService `](#api-631237297addc003) | ` ExHyperV.Services ` | 分别读取宿主当前运行的调度器和引导配置，并设置后续启动的调度器选择。 |

**修改 / 电源**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostPowerPlanService `](#api-79f9ca4dbc8029c4) | ` ExHyperV.Services ` | 枚举宿主电源计划并设置当前活动计划；设置影响整个宿主。 |

**修改 / Hyper-V**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HyperVHostService `](#api-d765f6dc004e22df) | ` ExHyperV.Services ` | 检测本机 Hyper-V 环境，并提交 Windows 功能及引导配置变更。 |
| [` HyperVHostSettingsService `](#api-7d8a2f197b518ae9) | ` ExHyperV.Services ` | 读取宿主 Hyper-V 配置快照，修改新建虚拟机的默认配置目录。 |

**修改 / 控制台**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostConsoleService `](#api-692d77f6af1a6a5b) | ` ExHyperV.Services ` | 配置宿主的虚拟机控制台连接策略。 |

**修改 / 分组**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmGroupMove `](#api-de5bea964cb743a6) | ` ExHyperV.Models ` | 一次分组成员移动所需的源成员及原父组信息。 |
| [` VmGroupSnapshot `](#api-5ae62ce278562650) | ` ExHyperV.Models ` | 原生分组的成员关系构成图结构；同一虚拟机或分组可以属于多个父分组。 |
| [` VmGroupMoveRules `](#api-cafcb4c2db564ebe) | ` ExHyperV.Services ` | 仅根据分组快照判断移动是否合法，不访问或修改宿主。 |
| [` VmGroupService `](#api-99e3f6668ba1efe6) | ` ExHyperV.Services ` | 通过 Hyper-V 分组接口读取及修改分组；本进程内串行执行写入。 |

**修改 / ARP 嗅探**

本分类暂未归入接口。

**修改 / OpenHCL**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostOpenHclService `](#api-1c6d8e37aafe2c57) | ` ExHyperV.Services ` | 读取和设置宿主从文件加载 OpenHCL 固件的策略。 |

**修改 / 系统产品类型**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` HostProductTypeService `](#api-b3bf26b1b466b9c0) | ` ExHyperV.Services ` | 读取和实验性修改宿主 Windows 产品类型，通过 SYSTEM hive 替换安排重启后生效。 |

##### 工具

**VMCX 文件**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmcxDevice `](#api-a52eae53275a8dfa) | ` ExHyperV.Vmcx ` | 由 manifest 读取的设备描述。 |
| [` VmcxDeviceKind `](#api-70be9275005910c6) | ` ExHyperV.Vmcx ` | 可向现有配置添加的设备定义种类。 |
| [` VmcxDocument `](#api-bf0a3c9cc5591d9f) | ` ExHyperV.Vmcx ` | VMCX 配置文件的只读快照与编辑入口。 |
| [` VmcxEditException `](#api-c115085593ccde9c) | ` ExHyperV.Vmcx ` | 文件提交失败，包含可用于人工恢复的路径和状态。 |
| [` VmcxEditResult `](#api-955a4f3832150289) | ` ExHyperV.Vmcx ` | 一次 VMCX 文件提交的结果；备份由调用方保留或自行归档。 |
| [` VmcxEditSession `](#api-0dd8fc721201a6a9) | ` ExHyperV.Vmcx ` | 针对现有 VMCX 快照的单次编辑批次。 |
| [` VmcxEntry `](#api-d80d4fe6c5be0ca7) | ` ExHyperV.Vmcx ` | 现有 VMCX 中的一个节点快照。 |
| [` VmcxException `](#api-3156373219b7a844) | ` ExHyperV.Vmcx ` | 表示 VMCX 原生存储操作失败，保留其返回码供诊断。 |
| [` VmcxValue `](#api-727cf80a4878b3e2) | ` ExHyperV.Vmcx ` | 不可变的 VMCX 类型值。 |
| [` VmcxValueType `](#api-1dcabda321eccb67) | ` ExHyperV.Vmcx ` | VMCX 值节点支持的存储类型；容器不是值。 |

**ISO 构建**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` IsoBuilderService `](#api-b3d6436c4464b7b7) | ` ExHyperV.Services ` | 使用 Windows IMAPI 将目录构建为包含 ISO 9660 与 UDF 文件系统的光盘映像。 |

**磁盘分区解析**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` DiskPartitionInfo `](#api-c2f9b2df9da28fb2) | ` ExHyperV.Models ` | 磁盘分区扫描结果。 |
| [` OperatingSystemType `](#api-f9be4f964cd193ac) | ` ExHyperV.Models ` | 扫描磁盘分区得到的系统或分区用途分类。 |
| [` DiskParser `](#api-9f824e626b7d8430) | ` ExHyperV.Tools ` | 读取块设备上的 GPT/MBR 分区结构，用于筛选可供来宾部署的数据分区。 |

#### 业务层面

##### 连接

选择后端，建立和关闭虚拟机通信连接。

连接对象提供状态通知和视频、控制、剪贴板等接口。控制包含键盘和鼠标，音频为预留接口。



调用顺序：选择后端 → 创建连接 → 订阅事件 → Start → 使用所需能力 → DisconnectAsync 或 DisposeAsync。



RdpBasic 已提供视频和键鼠；RdpEnhanced 还提供剪贴板和分辨率调整。Nimue 尚未接入，音频目前也没有可用实现。



从 `ConsoleService` 选择后端；完整连接示例见 `IConsoleConnection`。 连接建立、来宾登录完成、剪贴板通道就绪是三个不同的时点。

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` ConsoleBackendInfo `](#api-0bbffb055adaf595) | ` ExHyperV.Services ` | 一个控制台后端的名称、实现状态和功能列表。 |
| [` ConsoleCapabilities `](#api-a7effae4ff578e31) | ` ExHyperV.Services ` | 控制台后端实现的功能，可使用按位运算组合和检查。 |
| [` ConsoleConnectionOptions `](#api-e62d45486843fafd) | ` ExHyperV.Services ` | 连接目标、初始桌面尺寸、缩放比例和连接超时。 |
| [` ConsoleConnectionState `](#api-ea5482a340e8774c) | ` ExHyperV.Services ` | 控制台连接当前处于连接中、已连接还是断开阶段。 |
| [` ConsoleDisconnectInfo `](#api-29568228a14c6559) | ` ExHyperV.Services ` | 连接结束时提供的诊断信息，用于显示原因或记录日志。 |
| [` ConsoleService `](#api-78a7a0c2e5b1433b) | ` ExHyperV.Services ` | 选择连接方式，创建用于查看和操作虚拟机桌面的控制台连接。 |
| [` IConsoleConnection `](#api-53f629596085b5c9) | ` ExHyperV.Services ` | 一条虚拟机桌面连接，提供画面、输入和其他受支持的交互能力。 |

##### 视频

读取虚拟机画面，或在需要时获取一张截图。

持续显示桌面使用连接的 Video 接口和 FrameReceived 事件。画面已解码为 BGRA32 像素，包含远端光标； 帧的保留、复制和局部刷新规则见 `ConsoleFrame`。



对当前连接截图使用 `CaptureFrame`，取得最近一张完整画面的独立租约；基本、增强会话各取自己的画面。 它不等待新帧，也不回退到其他画面来源。用完后 Dispose；需要独立字节副本时调用 CopyPixels。



未建立连接的列表缩略图使用 `VmScreenshotSource.CaptureAsync`。 该接口通过 Hyper-V WMI 获取基本控制台的 RGB565 图像，不能代替增强会话桌面的截图。

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` VmScreenshotImage `](#api-3580d41605c9185d) | ` ExHyperV.Models ` | 一张独立保存的截图，包含尺寸和图像字节，可转换为 JPEG。 |
| [` ConsoleFrame `](#api-972d5767e28038af) | ` ExHyperV.Services ` | 一帧虚拟机桌面画面，包含完整像素、尺寸、序号和本次变化区域。 |
| [` ConsoleFrameRegion `](#api-323b953e417e6d27) | ` ExHyperV.Services ` | 桌面上需要更新的一块矩形区域，位置和大小以像素表示。 |
| [` IConsoleVideoSource `](#api-f1ecb47435b21fa3) | ` ExHyperV.Services ` | 从同一连接获取单帧或持续接收画面，并在后端支持时请求调整分辨率。 |
| [` VmScreenshotSource `](#api-8ef86a18b6b1159d) | ` ExHyperV.Services ` | 不建立连接，通过 Hyper-V WMI 获取虚拟机基本控制台的一张截图。 |

##### 音频

来宾声音接收；当前尚未实现。

接收方向为“来宾 → 调用方”，数据格式为 PCM；播放由调用方负责。RdpBasic 和 RdpEnhanced 尚未接入音频。



采样格式和数据包约定见 `ConsoleAudioFormat`、`ConsoleAudioPacket`。向来宾输入音视频不属于当前实现范围。

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` ConsoleAudioFormat `](#api-34f8d5f444bc1540) | ` ExHyperV.Services ` | 一批音频采样的采样率、声道数和数值格式。 |
| [` ConsoleAudioPacket `](#api-dca91d63ad5a1a97) | ` ExHyperV.Services ` | 一段音频采样及其格式、包序号和时间戳。 |
| [` ConsoleAudioSampleFormat `](#api-bfefa55b473f5a20) | ` ExHyperV.Services ` | PCM 音频中每个采样数值的编码方式。 |
| [` IConsoleAudioInput `](#api-d949c0cb1a4fd072) | ` ExHyperV.Services ` | 音频输入的历史预留接口，未实现，不属于当前 Core 的功能范围。 |
| [` IConsoleAudioOutput `](#api-8982aaf97ec63492) | ` ExHyperV.Services ` | 来宾声音接收的预留接口；当前内置后端尚未实现。 |

##### 控制

向来宾发送键盘和鼠标输入，两者共用连接的 Input 接口。

键盘：发送按键的按下、释放事件，并同步 Caps Lock、Num Lock 等锁定状态。输入使用 PC 扫描码， 不是字符编码或 Windows 虚拟键码；具体例子见 `IConsoleInput.SendKey`。



需要 Ctrl+Alt+Del 时，也可按虚拟机 GUID 调用 `VmInputService.SendCtrlAltDelAsync`，无需建立 RDP 连接。



鼠标：使用 `IConsoleInput.SendPointer` 发送移动、左中右键和垂直滚轮输入。坐标以收到的桌面帧为准； 如果画面在界面中被缩放或留有黑边，先换算为来宾像素坐标，再发送。



一次点击由按下、释放两个事件组成；拖动则是在两者之间发送移动。滚轮每格通常为 120，正负值表示方向。



输入返回 true 表示 Core 已接收，不表示来宾已处理。失去焦点时调用 `IConsoleInput.ReleaseAllInputs` 释放键鼠状态。

**键盘**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` ConsoleKeyEvent `](#api-fc9ac7bacf776039) | ` ExHyperV.Services ` | 按键序列中的一个扫描码事件；按下和释放分别占一项。 |
| [` ConsoleKeyboardToggleState `](#api-2583c9fa0f39f88e) | ` ExHyperV.Services ` | 需要在来宾中开启的键盘锁定键，可用 \| 组合。 |
| [` IConsoleInput `](#api-ab4762a60b6762dc) | ` ExHyperV.Services ` | 向已经建立的控制台连接发送键盘和鼠标输入。 |
| [` VmInputService `](#api-bb863f5938e764bd) | ` ExHyperV.Services ` | 直接通过 Hyper-V 向虚拟机发送 Ctrl+Alt+Del。 |

**鼠标**

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` ConsolePointerButton `](#api-ed483ee678cf01e1) | ` ExHyperV.Services ` | 鼠标输入中要按下或释放的一个按键。 |
| [` IConsoleInput `](#api-ab4762a60b6762dc) | ` ExHyperV.Services ` | 向已经建立的控制台连接发送键盘和鼠标输入。 |

##### 剪贴板

收发来宾剪贴板中的文本、位图和 HTML。

当前仅 RdpEnhanced 提供此能力。取得 Clipboard 后订阅事件，等待 IsAvailable，再提交数据。



Core 负责传输；宿主剪贴板的读取和写入由调用方完成。 一份内容可以同时提供多种格式，接收方选择合适的表示方式。双向文字示例见 `IConsoleClipboard`。

| 类型 | 命名空间 | 简介 |
| --- | --- | --- |
| [` ConsoleClipboardData `](#api-382082b2780091a0) | ` ExHyperV.Services ` | 一次剪贴板内容，以及这份内容可供接收方选择的不同格式。 |
| [` ConsoleClipboardFormat `](#api-30176205ed3087ed) | ` ExHyperV.Services ` | 剪贴板内容的一种表示方式，由格式编号、名称和原始字节组成。 |
| [` IConsoleClipboard `](#api-199dbda02346a96d) | ` ExHyperV.Services ` | 与来宾交换剪贴板内容，例如双向复制粘贴文字。 |

<a id="api-d8aedb8448e3e799"></a>

### `BootDeviceKind` 枚举

启动设备的业务类别，不包含界面图标或列表布局状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 引导 |
| 源文件 | ` Management/VirtualMachines/Boot/BootOrderItem.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Firmware = 6 `](#api-60c4af9420b0e5f5) | 固件内置引导项。 |
| [` FirmwareFile = 5 `](#api-11b2b0d6cdcaa2f2) | 固件文件引导项。 |
| [` Floppy = 1 `](#api-fd3064956013f9f4) | 软盘驱动器。 |
| [` HardDisk = 3 `](#api-9473e440e16278da) | 虚拟或物理硬盘。 |
| [` Network = 4 `](#api-fcf1743e0a233eea) | 网络启动设备。 |
| [` OpticalDrive = 2 `](#api-593588a8f5a7295a) | 光盘驱动器。 |
| [` Unknown = 0 `](#api-db2558a49709b0e2) | 无法识别的启动设备。 |

#### 详细说明

```csharp
public enum BootDeviceKind
```

#### 枚举值说明

<a id="api-60c4af9420b0e5f5"></a>

##### Firmware · 枚举值

```csharp
Firmware = 6
```

固件内置引导项。

<a id="api-11b2b0d6cdcaa2f2"></a>

##### FirmwareFile · 枚举值

```csharp
FirmwareFile = 5
```

固件文件引导项。

<a id="api-fd3064956013f9f4"></a>

##### Floppy · 枚举值

```csharp
Floppy = 1
```

软盘驱动器。

<a id="api-9473e440e16278da"></a>

##### HardDisk · 枚举值

```csharp
HardDisk = 3
```

虚拟或物理硬盘。

<a id="api-fcf1743e0a233eea"></a>

##### Network · 枚举值

```csharp
Network = 4
```

网络启动设备。

<a id="api-593588a8f5a7295a"></a>

##### OpticalDrive · 枚举值

```csharp
OpticalDrive = 2
```

光盘驱动器。

<a id="api-db2558a49709b0e2"></a>

##### Unknown · 枚举值

```csharp
Unknown = 0
```

无法识别的启动设备。

<a id="api-e91e11a676cd0968"></a>

### `BootOrderItem` 类

虚拟机引导项，由 VmBootService 读取；写回时必须使用同一虚拟机读取的对象。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 引导 |
| 源文件 | ` Management/VirtualMachines/Boot/BootOrderItem.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Description { get; set; } = string.Empty `](#api-118dcdafddd67646) | 启动设备的路径或地址说明，用于显示。 |
| [` public BootDeviceKind Kind { get; set; } `](#api-7f92fcc74464503d) | 启动设备的业务类别。 |
| [` public string Name { get; set; } = string.Empty `](#api-d39f45588b9af36a) | 启动设备的显示名称。 |

#### 详细说明

```csharp
public partial class BootOrderItem : ObservableObject
```

#### 属性说明

<a id="api-118dcdafddd67646"></a>

##### Description · 生成属性

```csharp
public string Description { get; set; } = string.Empty;
```

启动设备的路径或地址说明，用于显示。

<a id="api-7f92fcc74464503d"></a>

##### Kind · 生成属性

```csharp
public BootDeviceKind Kind { get; set; }
```

启动设备的业务类别。

<a id="api-d39f45588b9af36a"></a>

##### Name · 生成属性

```csharp
public string Name { get; set; } = string.Empty;
```

启动设备的显示名称。

<a id="api-ed1be2be550b34d3"></a>

### `CpuCoreType` 枚举

宿主处理器核心的性能类别。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器 |
| 源文件 | ` Management/Host/Processor/CpuCoreType.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Efficient = 2 `](#api-554318f1b970d8ab) | 能效核心。 |
| [` Performance = 1 `](#api-81004abc96524c0b) | 性能核心。 |
| [` Unknown = 0 `](#api-15abd67bae736749) | 尚未识别核心类别。 |

#### 详细说明

```csharp
public enum CpuCoreType
```

#### 枚举值说明

<a id="api-554318f1b970d8ab"></a>

##### Efficient · 枚举值

```csharp
Efficient = 2
```

能效核心。

<a id="api-81004abc96524c0b"></a>

##### Performance · 枚举值

```csharp
Performance = 1
```

性能核心。

<a id="api-15abd67bae736749"></a>

##### Unknown · 枚举值

```csharp
Unknown = 0
```

尚未识别核心类别。

<a id="api-c2f9b2df9da28fb2"></a>

### `DiskPartitionInfo` 类

磁盘分区扫描结果。分区事实在构造时确定；所属磁盘信息可补充，修改对象不会修改磁盘。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / 磁盘分区解析 |
| 源文件 | ` Management/Tools/Partitions/DiskPartitionInfo.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public DiskPartitionInfo(int number, ulong size, OperatingSystemType osType, string typeDescription) `](#api-cc53e3830a712bc3) | 建立分区事实记录；磁盘路径和物理盘标记由调用方另行填充。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DiskDisplayName { get; set; } = string.Empty `](#api-c2ae757928720c5f) | 所属磁盘的友好显示名称，不作为操作标识。 |
| [` public string DiskPath { get; set; } = string.Empty `](#api-37e7cb3fe5f97b2a) | 虚拟磁盘文件路径；物理直通盘则为十进制磁盘编号字符串。 |
| [` public string DisplayName { get; } `](#api-9548137ae215094d) | 包含磁盘名称、分区编号和容量的本地化显示文本。 |
| [` public bool IsBitLocker { get; set; } `](#api-029eab86646156d3) | 是否检测到 BitLocker 分区首扇区签名；为 true 时 GPU 部署要求先解锁。 |
| [` public bool IsPhysicalDisk { get; set; } `](#api-647582ff53c828ac) | 是否为物理直通磁盘上的分区。 |
| [` public OperatingSystemType OsType { get; } `](#api-5069018406e6b2a1) | 由分区类型 GUID 或 MBR 类型号推断的用途，不检查分区中的系统文件。 |
| [` public int PartitionNumber { get; } `](#api-2e75a37d34df9d9b) | 扫描器按分区表位置分配的编号；GPT 条目和 MBR 主分区从 1 开始，MBR 逻辑分区从 5 开始。 |
| [` public double SizeGiB { get; } `](#api-d8e53eb0ec0a56dc) | 按 1024&#179; 字节换算的分区容量，单位为 GiB。 |
| [` public ulong SizeInBytes { get; } `](#api-c987017560b2fd61) | 分区容量，单位为字节。 |
| [` public string TypeDescription { get; } `](#api-073e32b33cc09bcf) | 文件系统或分区类型的显示说明。 |

#### 详细说明

```csharp
public class DiskPartitionInfo
```

#### 构造函数说明

<a id="api-cc53e3830a712bc3"></a>

##### DiskPartitionInfo · 构造器

```csharp
public DiskPartitionInfo(int number, ulong size, OperatingSystemType osType, string typeDescription);
```

建立分区事实记录；磁盘路径和物理盘标记由调用方另行填充。

| 参数 | 说明 |
| --- | --- |
| `number` | 分区编号。 |
| `size` | 分区容量，单位为字节。 |
| `osType` | 扫描识别的系统类型。 |
| `typeDescription` | 分区类型显示说明。 |

#### 属性说明

<a id="api-c2ae757928720c5f"></a>

##### DiskDisplayName · 属性

```csharp
public string DiskDisplayName { get; set; } = string.Empty;
```

所属磁盘的友好显示名称，不作为操作标识。

<a id="api-37e7cb3fe5f97b2a"></a>

##### DiskPath · 属性

```csharp
public string DiskPath { get; set; } = string.Empty;
```

虚拟磁盘文件路径；物理直通盘则为十进制磁盘编号字符串。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 分区所属的磁盘。 |
| 取值与默认 | 虚拟磁盘使用宿主文件路径，例如 `D:\VMs\Disk.vhdx`；物理直通盘使用十进制磁盘编号，例如 `2`。结合 `IsPhysicalDisk` 区分。 |

<a id="api-9548137ae215094d"></a>

##### DisplayName · 属性

```csharp
public string DisplayName { get; }
```

包含磁盘名称、分区编号和容量的本地化显示文本。

<a id="api-029eab86646156d3"></a>

##### IsBitLocker · 属性

```csharp
public bool IsBitLocker { get; set; }
```

是否检测到 BitLocker 分区首扇区签名；为 true 时 GPU 部署要求先解锁。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 根据分区首扇区识别 BitLocker；GPU 部署前需要解锁此类分区。 |
| 取值与默认 | `true`：检测到 BitLocker 签名；`false`：未检测到。 |

<a id="api-647582ff53c828ac"></a>

##### IsPhysicalDisk · 属性

```csharp
public bool IsPhysicalDisk { get; set; }
```

是否为物理直通磁盘上的分区。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 所属磁盘的来源类型。 |
| 取值与默认 | `true`：物理直通盘；`false`：虚拟磁盘文件。 |

<a id="api-5069018406e6b2a1"></a>

##### OsType · 属性

```csharp
public OperatingSystemType OsType { get; }
```

由分区类型 GUID 或 MBR 类型号推断的用途，不检查分区中的系统文件。

<a id="api-2e75a37d34df9d9b"></a>

##### PartitionNumber · 属性

```csharp
public int PartitionNumber { get; }
```

扫描器按分区表位置分配的编号；GPT 条目和 MBR 主分区从 1 开始，MBR 逻辑分区从 5 开始。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 分区在磁盘分区表中的编号。 |
| 取值与默认 | GPT 条目和 MBR 主分区从 1 开始；MBR 逻辑分区从 5 开始。 |

<a id="api-d8e53eb0ec0a56dc"></a>

##### SizeGiB · 属性

```csharp
public double SizeGiB { get; }
```

按 1024&#179; 字节换算的分区容量，单位为 GiB。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 换算为 GiB 的分区容量。 |
| 取值与默认 | 单位为 GiB，1 GiB = 1,073,741,824 字节。 |

<a id="api-c987017560b2fd61"></a>

##### SizeInBytes · 属性

```csharp
public ulong SizeInBytes { get; }
```

分区容量，单位为字节。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 分区容量。 |
| 取值与默认 | 非负整数，单位为字节。 |

<a id="api-073e32b33cc09bcf"></a>

##### TypeDescription · 属性

```csharp
public string TypeDescription { get; }
```

文件系统或分区类型的显示说明。

<a id="api-11fbc96797a74b58"></a>

### `GpuBackToPartitions` record 类

请求从允许返回的待确认事项回到系统分区选择。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuBackToPartitions(Guid RequestId) `](#api-2bc19f96fb91b787) |  |

#### 详细说明

```csharp
public sealed record GpuBackToPartitions(Guid RequestId) : GpuDeploymentDecision(RequestId)
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |

#### 构造函数说明

<a id="api-2bc19f96fb91b787"></a>

##### GpuBackToPartitions · 主构造器

```csharp
public GpuBackToPartitions(Guid RequestId);
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |

<a id="api-b72fad7f37732b94"></a>

### `GpuConfirmation` record 类

接受或拒绝一项部署确认；拒绝会进入取消流程。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuConfirmation(Guid RequestId, bool Accepted) `](#api-b9bd1100c349fb7c) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool Accepted { get; init; } `](#api-cf1bc34d03406293) | true 继续确认的步骤；false 取消部署。 |

#### 详细说明

```csharp
public sealed record GpuConfirmation(Guid RequestId, bool Accepted) : GpuDeploymentDecision(RequestId)
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |
| `Accepted` | true 继续确认的步骤；false 取消部署。 |

#### 构造函数说明

<a id="api-b9bd1100c349fb7c"></a>

##### GpuConfirmation · 主构造器

```csharp
public GpuConfirmation(Guid RequestId, bool Accepted);
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |
| `Accepted` | true 继续确认的步骤；false 取消部署。 |

#### 属性说明

<a id="api-cf1bc34d03406293"></a>

##### Accepted · record 属性

```csharp
public bool Accepted { get; init; }
```

true 继续确认的步骤；false 取消部署。

<a id="api-e1c07ac7b4a8d65a"></a>

### `GpuDecisionInput` 枚举

一个待确认事项所接受的输入结构。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Confirmation = 0 `](#api-7db0f9d1f503457d) | 使用 GpuConfirmation 接受或拒绝。 |
| [` LinuxCredentials = 2 `](#api-7075f4eb2774fa7c) | 使用 GpuLinuxInput 提交脚本和 SSH 凭据。 |
| [` Partition = 1 `](#api-20f31cbd82caa987) | 使用 GpuPartitionSelection 提交分区 ID。 |

#### 详细说明

```csharp
public enum GpuDecisionInput
```

#### 枚举值说明

<a id="api-7db0f9d1f503457d"></a>

##### Confirmation · 枚举值

```csharp
Confirmation = 0
```

使用 GpuConfirmation 接受或拒绝。

<a id="api-7075f4eb2774fa7c"></a>

##### LinuxCredentials · 枚举值

```csharp
LinuxCredentials = 2
```

使用 GpuLinuxInput 提交脚本和 SSH 凭据。

<a id="api-20f31cbd82caa987"></a>

##### Partition · 枚举值

```csharp
Partition = 1
```

使用 GpuPartitionSelection 提交分区 ID。

<a id="api-16bfedc8ae936ed7"></a>

### `GpuDecisionKind` 枚举

GPU 部署暂停时需要用户处理的具体事项。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` InstallWindows = 2 `](#api-b07bdce72c32c2c6) | 确认向离线 Windows 系统分区部署驱动文件。 |
| [` LinuxCredentials = 4 `](#api-2eefc990bc70e3ad) | 提交部署脚本和 SSH 凭据。 |
| [` PowerOff = 0 `](#api-0484eb23f552e201) | 确认将虚拟机直接关闭（TurnOff），不是来宾正常关机。 |
| [` SelectPartition = 1 `](#api-8f7d20f374dc3012) | 选择目标系统分区。 |
| [` StartLinux = 3 `](#api-aaeb19df55854987) | 确认启动 Linux 来宾以继续在线部署。 |

#### 详细说明

```csharp
public enum GpuDecisionKind
```

#### 枚举值说明

<a id="api-b07bdce72c32c2c6"></a>

##### InstallWindows · 枚举值

```csharp
InstallWindows = 2
```

确认向离线 Windows 系统分区部署驱动文件。

<a id="api-2eefc990bc70e3ad"></a>

##### LinuxCredentials · 枚举值

```csharp
LinuxCredentials = 4
```

提交部署脚本和 SSH 凭据。

<a id="api-0484eb23f552e201"></a>

##### PowerOff · 枚举值

```csharp
PowerOff = 0
```

确认将虚拟机直接关闭（TurnOff），不是来宾正常关机。

<a id="api-8f7d20f374dc3012"></a>

##### SelectPartition · 枚举值

```csharp
SelectPartition = 1
```

选择目标系统分区。

<a id="api-aaeb19df55854987"></a>

##### StartLinux · 枚举值

```csharp
StartLinux = 3
```

确认启动 Linux 来宾以继续在线部署。

<a id="api-640fbb80511f3d56"></a>

### `GpuDecisionRequest` record 类

部署暂停时发布的待确认事项；每次请求有新的 Id，旧 ID 不能用于后续步骤。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuDecisionRequest(Guid Id, GpuDecisionKind Kind, string Title, string Message, IReadOnlyList<GpuDeploymentPartition> Partitions, string SuggestedHost = "", bool CanGoBack = false) `](#api-ee9c33991284f7c4) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool CanGoBack { get; init; } `](#api-7f1293746d9ac0a3) | 是否允许从当前事项返回分区选择。 |
| [` public Guid Id { get; init; } `](#api-6447be23505377ce) | 提交决定时必须原样携带的请求 ID。 |
| [` public GpuDecisionInput Input { get; } `](#api-3d77add0d29ac681) | 由 Kind 推导出的输入结构类别。 |
| [` public bool IsDestructive { get; } `](#api-12a4fe0905f6c470) | 是否为直接关闭虚拟机或写入离线 Windows 驱动的确认；其他步骤仍可能有配置副作用。 |
| [` public GpuDecisionKind Kind { get; init; } `](#api-7035d4b4ecb0f2d9) | 待确认事项的种类。 |
| [` public string Message { get; init; } `](#api-bc2680691fa17847) | 本地化操作说明。 |
| [` public IReadOnlyList<GpuDeploymentPartition> Partitions { get; init; } `](#api-aecfba65fe78949a) | 可供选择的分区快照；会话发布的是只读列表。 |
| [` public string SuggestedHost { get; init; } `](#api-69fd2fda7c0b3588) | 建议的 Linux IPv4 地址；空字符串表示未取得。 |
| [` public string Title { get; init; } `](#api-0e384572dcd1d5c8) | 本地化标题。 |

#### 详细说明

```csharp
public sealed record GpuDecisionRequest(Guid Id, GpuDecisionKind Kind, string Title, string Message,
    IReadOnlyList<GpuDeploymentPartition> Partitions, string SuggestedHost = "", bool CanGoBack = false)
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 提交决定时必须原样携带的请求 ID。 |
| `Kind` | 待确认事项的种类。 |
| `Title` | 本地化标题。 |
| `Message` | 本地化操作说明。 |
| `Partitions` | 可供选择的分区快照；会话发布的是只读列表。 |
| `SuggestedHost` | 建议的 Linux IPv4 地址；空字符串表示未取得。 |
| `CanGoBack` | 是否允许从当前事项返回分区选择。 |

#### 构造函数说明

<a id="api-ee9c33991284f7c4"></a>

##### GpuDecisionRequest · 主构造器

```csharp
public GpuDecisionRequest(Guid Id, GpuDecisionKind Kind, string Title, string Message, IReadOnlyList<GpuDeploymentPartition> Partitions, string SuggestedHost = "", bool CanGoBack = false);
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 提交决定时必须原样携带的请求 ID。 |
| `Kind` | 待确认事项的种类。 |
| `Title` | 本地化标题。 |
| `Message` | 本地化操作说明。 |
| `Partitions` | 可供选择的分区快照；会话发布的是只读列表。 |
| `SuggestedHost` | 建议的 Linux IPv4 地址；空字符串表示未取得。 |
| `CanGoBack` | 是否允许从当前事项返回分区选择。 |

#### 属性说明

<a id="api-7f1293746d9ac0a3"></a>

##### CanGoBack · record 属性

```csharp
public bool CanGoBack { get; init; }
```

是否允许从当前事项返回分区选择。

<a id="api-6447be23505377ce"></a>

##### Id · record 属性

```csharp
public Guid Id { get; init; }
```

提交决定时必须原样携带的请求 ID。

<a id="api-3d77add0d29ac681"></a>

##### Input · 属性

```csharp
public GpuDecisionInput Input { get; }
```

由 Kind 推导出的输入结构类别。

<a id="api-12a4fe0905f6c470"></a>

##### IsDestructive · 属性

```csharp
public bool IsDestructive { get; }
```

是否为直接关闭虚拟机或写入离线 Windows 驱动的确认；其他步骤仍可能有配置副作用。

<a id="api-7035d4b4ecb0f2d9"></a>

##### Kind · record 属性

```csharp
public GpuDecisionKind Kind { get; init; }
```

待确认事项的种类。

<a id="api-bc2680691fa17847"></a>

##### Message · record 属性

```csharp
public string Message { get; init; }
```

本地化操作说明。

<a id="api-aecfba65fe78949a"></a>

##### Partitions · record 属性

```csharp
public IReadOnlyList<GpuDeploymentPartition> Partitions { get; init; }
```

可供选择的分区快照；会话发布的是只读列表。

<a id="api-69fd2fda7c0b3588"></a>

##### SuggestedHost · record 属性

```csharp
public string SuggestedHost { get; init; }
```

建议的 Linux IPv4 地址；空字符串表示未取得。

<a id="api-0e384572dcd1d5c8"></a>

##### Title · record 属性

```csharp
public string Title { get; init; }
```

本地化标题。

<a id="api-ab1e60fe73d6e64d"></a>

### `GpuDeploymentAction` 枚举

当前部署快照允许调用方发起的动作。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` BackToPartitions = 2 `](#api-a52805fe5de185e0) | 返回分区选择，需使用当前请求 ID。 |
| [` Cancel = 3 `](#api-16cf1b79ac6c0bb7) | 请求取消并等待可执行的清理。 |
| [` Reconcile = 5 `](#api-9f22aaaad776fcd7) | 只读核实本次部署和清理的原任务及资源状态，不重新执行写入。 |
| [` RetryCleanup = 4 `](#api-ba588a70efe83cce) | 恢复临时磁盘资源并清理本次新增的 GPU 适配器，不继续部署。 |
| [` Start = 0 `](#api-a3a5178a7b4a83fe) | 启动尚未开始的部署。 |
| [` SubmitDecision = 1 `](#api-63ca9f39ea1d2787) | 提交当前待确认事项的决定。 |

#### 详细说明

```csharp
public enum GpuDeploymentAction
```

#### 枚举值说明

<a id="api-a52805fe5de185e0"></a>

##### BackToPartitions · 枚举值

```csharp
BackToPartitions = 2
```

返回分区选择，需使用当前请求 ID。

<a id="api-16cf1b79ac6c0bb7"></a>

##### Cancel · 枚举值

```csharp
Cancel = 3
```

请求取消并等待可执行的清理。

<a id="api-9f22aaaad776fcd7"></a>

##### Reconcile · 枚举值

```csharp
Reconcile = 5
```

只读核实本次部署和清理的原任务及资源状态，不重新执行写入。

<a id="api-ba588a70efe83cce"></a>

##### RetryCleanup · 枚举值

```csharp
RetryCleanup = 4
```

恢复临时磁盘资源并清理本次新增的 GPU 适配器，不继续部署。

<a id="api-a3a5178a7b4a83fe"></a>

##### Start · 枚举值

```csharp
Start = 0
```

启动尚未开始的部署。

<a id="api-63ca9f39ea1d2787"></a>

##### SubmitDecision · 枚举值

```csharp
SubmitDecision = 1
```

提交当前待确认事项的决定。

<a id="api-c5617284cebe0629"></a>

### `GpuDeploymentDecision` record 类

提交给部署会话的决定基类，决定必须对应当前尚未消费的请求 ID。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuDeploymentDecision(Guid RequestId) `](#api-d2b5667cd106c2c2) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public Guid RequestId { get; init; } `](#api-a23a3b6386d94370) | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |

#### 详细说明

```csharp
public abstract record GpuDeploymentDecision(Guid RequestId)
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |

#### 构造函数说明

<a id="api-d2b5667cd106c2c2"></a>

##### GpuDeploymentDecision · 主构造器

```csharp
public GpuDeploymentDecision(Guid RequestId);
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |

#### 属性说明

<a id="api-a23a3b6386d94370"></a>

##### RequestId · record 属性

```csharp
public Guid RequestId { get; init; }
```

当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。

<a id="api-a5310e5188c13124"></a>

### `GpuDeploymentMode` 枚举

一次 GPU 部署要执行的工作范围。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` AddWithDrivers = 1 `](#api-098175c825f1ad05) | 配置并分配 GPU，再按确认流程部署来宾驱动。 |
| [` AddWithoutDrivers = 0 `](#api-c552d400afb7f1a2) | 配置并分配 GPU，不部署来宾驱动。 |
| [` UpdateDrivers = 2 `](#api-ddde5fa7477202a5) | 针对已有分配更新来宾驱动，不新增 GPU 适配器。 |

#### 详细说明

```csharp
public enum GpuDeploymentMode
```

#### 枚举值说明

<a id="api-098175c825f1ad05"></a>

##### AddWithDrivers · 枚举值

```csharp
AddWithDrivers = 1
```

配置并分配 GPU，再按确认流程部署来宾驱动。

<a id="api-c552d400afb7f1a2"></a>

##### AddWithoutDrivers · 枚举值

```csharp
AddWithoutDrivers = 0
```

配置并分配 GPU，不部署来宾驱动。

<a id="api-ddde5fa7477202a5"></a>

##### UpdateDrivers · 枚举值

```csharp
UpdateDrivers = 2
```

针对已有分配更新来宾驱动，不新增 GPU 适配器。

<a id="api-3b253a4b4e7b8932"></a>

### `GpuDeploymentPartition` record 类

供用户选择的系统分区快照。会话以 Id 重新匹配内部记录，不接受调用方改写的磁盘路径。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuDeploymentPartition(string Id, int PartitionNumber, ulong SizeInBytes, OperatingSystemType OsType, string TypeDescription, string DiskPath, string DiskDisplayName, bool IsPhysicalDisk, bool IsBitLocker) `](#api-b32df9a217bae180) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DiskDisplayName { get; init; } `](#api-f0e5711694c4f6d8) | 所属磁盘的显示名称。 |
| [` public string DiskPath { get; init; } `](#api-232efb38873f2fd8) | 虚拟磁盘文件路径或物理磁盘编号字符串。 |
| [` public string DisplayName { get; } `](#api-5de66bd086578dae) | 磁盘名称、分区编号和容量组合的本地化文本。 |
| [` public string Id { get; init; } `](#api-b3dc29a94aed7e69) | 本次分区列表中的选择标识，只应用于当前部署。 |
| [` public bool IsBitLocker { get; init; } `](#api-02d0aa26e4b2ac0a) | 是否检测到 BitLocker 签名；加密分区不能直接用于此部署。 |
| [` public bool IsPhysicalDisk { get; init; } `](#api-411828e7d6d8be13) | 是否来自物理直通磁盘。 |
| [` public OperatingSystemType OsType { get; init; } `](#api-42e9a21e5fca564b) | 识别的系统类型。 |
| [` public int PartitionNumber { get; init; } `](#api-f47310d5aa2a80f1) | 分区编号。 |
| [` public double SizeGiB { get; } `](#api-b802d8b763ba221a) | 按 1024&#179; 字节换算的分区容量，单位为 GiB。 |
| [` public ulong SizeInBytes { get; init; } `](#api-8fb1c899036b75e2) | 分区容量，单位为字节。 |
| [` public string TypeDescription { get; init; } `](#api-d2099aa8e6310c14) | 分区类型显示说明。 |

#### 详细说明

```csharp
public sealed record GpuDeploymentPartition(string Id, int PartitionNumber, ulong SizeInBytes,
    OperatingSystemType OsType, string TypeDescription, string DiskPath, string DiskDisplayName,
    bool IsPhysicalDisk, bool IsBitLocker)
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 本次分区列表中的选择标识，只应用于当前部署。 |
| `PartitionNumber` | 分区编号。 |
| `SizeInBytes` | 分区容量，单位为字节。 |
| `OsType` | 识别的系统类型。 |
| `TypeDescription` | 分区类型显示说明。 |
| `DiskPath` | 虚拟磁盘文件路径或物理磁盘编号字符串。 |
| `DiskDisplayName` | 所属磁盘的显示名称。 |
| `IsPhysicalDisk` | 是否来自物理直通磁盘。 |
| `IsBitLocker` | 是否检测到 BitLocker 签名；加密分区不能直接用于此部署。 |

#### 构造函数说明

<a id="api-b32df9a217bae180"></a>

##### GpuDeploymentPartition · 主构造器

```csharp
public GpuDeploymentPartition(string Id, int PartitionNumber, ulong SizeInBytes, OperatingSystemType OsType, string TypeDescription, string DiskPath, string DiskDisplayName, bool IsPhysicalDisk, bool IsBitLocker);
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 本次分区列表中的选择标识，只应用于当前部署。 |
| `PartitionNumber` | 分区编号。 |
| `SizeInBytes` | 分区容量，单位为字节。 |
| `OsType` | 识别的系统类型。 |
| `TypeDescription` | 分区类型显示说明。 |
| `DiskPath` | 虚拟磁盘文件路径或物理磁盘编号字符串。 |
| `DiskDisplayName` | 所属磁盘的显示名称。 |
| `IsPhysicalDisk` | 是否来自物理直通磁盘。 |
| `IsBitLocker` | 是否检测到 BitLocker 签名；加密分区不能直接用于此部署。 |

#### 属性说明

<a id="api-f0e5711694c4f6d8"></a>

##### DiskDisplayName · record 属性

```csharp
public string DiskDisplayName { get; init; }
```

所属磁盘的显示名称。

<a id="api-232efb38873f2fd8"></a>

##### DiskPath · record 属性

```csharp
public string DiskPath { get; init; }
```

虚拟磁盘文件路径或物理磁盘编号字符串。

<a id="api-5de66bd086578dae"></a>

##### DisplayName · 属性

```csharp
public string DisplayName { get; }
```

磁盘名称、分区编号和容量组合的本地化文本。

<a id="api-b3dc29a94aed7e69"></a>

##### Id · record 属性

```csharp
public string Id { get; init; }
```

本次分区列表中的选择标识，只应用于当前部署。

<a id="api-02d0aa26e4b2ac0a"></a>

##### IsBitLocker · record 属性

```csharp
public bool IsBitLocker { get; init; }
```

是否检测到 BitLocker 签名；加密分区不能直接用于此部署。

<a id="api-411828e7d6d8be13"></a>

##### IsPhysicalDisk · record 属性

```csharp
public bool IsPhysicalDisk { get; init; }
```

是否来自物理直通磁盘。

<a id="api-42e9a21e5fca564b"></a>

##### OsType · record 属性

```csharp
public OperatingSystemType OsType { get; init; }
```

识别的系统类型。

<a id="api-f47310d5aa2a80f1"></a>

##### PartitionNumber · record 属性

```csharp
public int PartitionNumber { get; init; }
```

分区编号。

<a id="api-b802d8b763ba221a"></a>

##### SizeGiB · 属性

```csharp
public double SizeGiB { get; }
```

按 1024&#179; 字节换算的分区容量，单位为 GiB。

<a id="api-8fb1c899036b75e2"></a>

##### SizeInBytes · record 属性

```csharp
public ulong SizeInBytes { get; init; }
```

分区容量，单位为字节。

<a id="api-d2099aa8e6310c14"></a>

##### TypeDescription · record 属性

```csharp
public string TypeDescription { get; init; }
```

分区类型显示说明。

<a id="api-b1a5a2f6f23a3554"></a>

### `GpuDeploymentRecovery` record 类

部署结果不明时的核对材料；这些标识只用于诊断，不是执行任意底层命令的入口。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentRecovery.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuDeploymentRecovery(Guid OperationId, Guid VmId, DateTimeOffset StartedAtUtc, string? JobReference, IReadOnlyList<string> ResourceReferences) `](#api-089676b7423e8849) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? JobReference { get; init; } `](#api-f0b17e0e653ae1be) | GPU 适配器创建的原生作业引用；未取得时为 null，其他阶段见 Operations。 |
| [` public IReadOnlyDictionary<string, string> ObservedValues { get; init; } = new Dictionary<string, string>() `](#api-06d40deec309a784) | 最近核实的当前值：Power.State 为电源状态，Mmio.LowSizeMiB、Mmio.HighSizeMiB、Mmio.HighBaseMiB 为 MMIO 大小和基址，单位 MiB；未提供的值为 Unavailable。 |
| [` public Guid OperationId { get; init; } `](#api-ff8015aac46bac86) | Core 为本次部署恢复记录生成的编号，各原生调用另有 Operations.Id。 |
| [` public IReadOnlyList<GpuRecoveryOperation> Operations { get; init; } = [] `](#api-7e7b811749c0c89b) | 电源、MMIO、磁盘和适配器移除等步骤的原生操作；适配器创建使用 JobReference 和 ResourceReferences。 |
| [` public IReadOnlyList<string> ResourceReferences { get; init; } `](#api-6fea4a4bbbeb4c02) | GPU 适配器创建回执的资源引用；只有 AddedAdapterId 表示已验证属于目标 VM 的 GPU 设备。 |
| [` public IReadOnlyList<GpuRecoveryResource> Resources { get; init; } = [] `](#api-6196f5420e906256) | 临时资源及原状态；Restored 为 true 表示最近核实已经恢复，尚未恢复的项由 RetryCleanupAsync 处理。 |
| [` public DateTimeOffset StartedAtUtc { get; init; } `](#api-125e1c59d9fe64df) | 创建此恢复记录的时间。 |
| [` public Guid VmId { get; init; } `](#api-bceb2324a25a90df) | 此次操作固定的虚拟机 GUID。 |

#### 详细说明

```csharp
public sealed record GpuDeploymentRecovery(Guid OperationId, Guid VmId, DateTimeOffset StartedAtUtc,
    string? JobReference, IReadOnlyList<string> ResourceReferences)
```

| 参数 | 说明 |
| --- | --- |
| `OperationId` | Core 为本次部署恢复记录生成的编号，各原生调用另有 Operations.Id。 |
| `VmId` | 此次操作固定的虚拟机 GUID。 |
| `StartedAtUtc` | 创建此恢复记录的时间。 |
| `JobReference` | GPU 适配器创建的原生作业引用；未取得时为 null，其他阶段见 Operations。 |
| `ResourceReferences` | GPU 适配器创建回执的资源引用；只有 AddedAdapterId 表示已验证属于目标 VM 的 GPU 设备。其他临时资源见 Resources。 |

#### 构造函数说明

<a id="api-089676b7423e8849"></a>

##### GpuDeploymentRecovery · 主构造器

```csharp
public GpuDeploymentRecovery(Guid OperationId, Guid VmId, DateTimeOffset StartedAtUtc, string? JobReference, IReadOnlyList<string> ResourceReferences);
```

| 参数 | 说明 |
| --- | --- |
| `OperationId` | Core 为本次部署恢复记录生成的编号，各原生调用另有 Operations.Id。 |
| `VmId` | 此次操作固定的虚拟机 GUID。 |
| `StartedAtUtc` | 创建此恢复记录的时间。 |
| `JobReference` | GPU 适配器创建的原生作业引用；未取得时为 null，其他阶段见 Operations。 |
| `ResourceReferences` | GPU 适配器创建回执的资源引用；只有 AddedAdapterId 表示已验证属于目标 VM 的 GPU 设备。其他临时资源见 Resources。 |

#### 属性说明

<a id="api-f0b17e0e653ae1be"></a>

##### JobReference · record 属性

```csharp
public string? JobReference { get; init; }
```

GPU 适配器创建的原生作业引用；未取得时为 null，其他阶段见 Operations。

<a id="api-06d40deec309a784"></a>

##### ObservedValues · 属性

```csharp
public IReadOnlyDictionary<string, string> ObservedValues { get; init; } = new Dictionary<string, string>();
```

最近核实的当前值：Power.State 为电源状态，Mmio.LowSizeMiB、Mmio.HighSizeMiB、Mmio.HighBaseMiB 为 MMIO 大小和基址，单位 MiB；未提供的值为 Unavailable。这里只包含本次涉及的步骤，不作为新的写入请求。

<a id="api-ff8015aac46bac86"></a>

##### OperationId · record 属性

```csharp
public Guid OperationId { get; init; }
```

Core 为本次部署恢复记录生成的编号，各原生调用另有 Operations.Id。

<a id="api-7e7b811749c0c89b"></a>

##### Operations · 属性

```csharp
public IReadOnlyList<GpuRecoveryOperation> Operations { get; init; } = [];
```

电源、MMIO、磁盘和适配器移除等步骤的原生操作；适配器创建使用 JobReference 和 ResourceReferences。IsSettled 为 false 表示尚未确认结束。

<a id="api-6fea4a4bbbeb4c02"></a>

##### ResourceReferences · record 属性

```csharp
public IReadOnlyList<string> ResourceReferences { get; init; }
```

GPU 适配器创建回执的资源引用；只有 AddedAdapterId 表示已验证属于目标 VM 的 GPU 设备。其他临时资源见 Resources。

<a id="api-6196f5420e906256"></a>

##### Resources · 属性

```csharp
public IReadOnlyList<GpuRecoveryResource> Resources { get; init; } = [];
```

临时资源及原状态；Restored 为 true 表示最近核实已经恢复，尚未恢复的项由 RetryCleanupAsync 处理。

<a id="api-125e1c59d9fe64df"></a>

##### StartedAtUtc · record 属性

```csharp
public DateTimeOffset StartedAtUtc { get; init; }
```

创建此恢复记录的时间。

<a id="api-bceb2324a25a90df"></a>

##### VmId · record 属性

```csharp
public Guid VmId { get; init; }
```

此次操作固定的虚拟机 GUID。

<a id="api-3abc49fee5f8e4b6"></a>

### `GpuDeploymentSnapshot` record 类

部署会话发布的状态快照。步骤、动作和分区集合为只读；手动构造时应传入不可变集合。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuDeploymentSnapshot(Guid SessionId, long Revision, GpuDeploymentState State, IReadOnlyList<GpuDeploymentStep> Steps, GpuDecisionRequest? PendingDecision, IReadOnlyList<GpuDeploymentAction> AllowedActions, bool HasAddedAdapter, bool HasPendingCleanup, string? Error = null, string? CleanupError = null, string? LastLog = null) `](#api-341e12306d7ecc95) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? AddedAdapterId { get; init; } `](#api-9c68cb08f048daa9) | 本次原生回执返回且已核对属于目标 VM 的 GPU 适配器身份；未确认时为 null，不通过清单差异推断。 |
| [` public IReadOnlyList<GpuDeploymentAction> AllowedActions { get; init; } `](#api-a30b434db3c93a67) | 当前状态允许的调用动作。 |
| [` public string? CleanupError { get; init; } `](#api-58374e828624fe3d) | 清理失败说明；没有清理错误时为 null。 |
| [` public string? Error { get; init; } `](#api-2a031d092e685776) | 部署错误说明；无错误时为 null。 |
| [` public bool HasAddedAdapter { get; init; } `](#api-3b95afd42de013fb) | 本次部署是否新增并识别到适配器。 |
| [` public bool HasPendingCleanup { get; init; } `](#api-5db4fba4b5816944) | 是否有已识别且尚待清理的适配器；false 时仍应检查 State 和 CleanupError。 |
| [` public string? LastLog { get; init; } `](#api-d51e4de7fc4a52ae) | 最近一条步骤日志；尚无日志时为 null。 |
| [` public bool OutcomeUnknown { get; init; } `](#api-293632a781a3d836) | 底层操作结果尚未确认；此状态不允许重试清理。 |
| [` public string? PendingCleanupAdapterId { get; init; } `](#api-5d5aecaca834bbb0) | 已确认归属且仍待清理的适配器身份；不代表当前允许清理，另查 AllowedActions。 |
| [` public GpuDecisionRequest? PendingDecision { get; init; } `](#api-0dce5ae59f56b76d) | 当前待确认事项；不在等待决定时为 null。 |
| [` public GpuDeploymentRecovery? Recovery { get; init; } `](#api-e0251fb0578ad929) | 本次部署和恢复的原任务、资源身份、原状态与核实结果；尚未留下记录时为 null。 |
| [` public long Revision { get; init; } `](#api-da1d46719893e43e) | 同一会话中递增的快照修订号。 |
| [` public Guid SessionId { get; init; } `](#api-bea533855f89f637) | 部署会话的稳定标识。 |
| [` public GpuDeploymentState State { get; init; } `](#api-1987d6fea8c8ad21) | 部署当前状态。 |
| [` public IReadOnlyList<GpuDeploymentStep> Steps { get; init; } `](#api-091d68578048fd7c) | 当前各步骤的状态快照。 |

#### 详细说明

```csharp
public sealed record GpuDeploymentSnapshot(Guid SessionId, long Revision, GpuDeploymentState State,
    IReadOnlyList<GpuDeploymentStep> Steps, GpuDecisionRequest? PendingDecision,
    IReadOnlyList<GpuDeploymentAction> AllowedActions, bool HasAddedAdapter, bool HasPendingCleanup,
    string? Error = null, string? CleanupError = null, string? LastLog = null)
```

| 参数 | 说明 |
| --- | --- |
| `SessionId` | 部署会话的稳定标识。 |
| `Revision` | 同一会话中递增的快照修订号。 |
| `State` | 部署当前状态。 |
| `Steps` | 当前各步骤的状态快照。 |
| `PendingDecision` | 当前待确认事项；不在等待决定时为 null。 |
| `AllowedActions` | 当前状态允许的调用动作。 |
| `HasAddedAdapter` | 本次部署是否新增并识别到适配器。 |
| `HasPendingCleanup` | 是否有已识别且尚待清理的适配器；false 时仍应检查 State 和 CleanupError。 |
| `Error` | 部署错误说明；无错误时为 null。 |
| `CleanupError` | 清理失败说明；没有清理错误时为 null。 |
| `LastLog` | 最近一条步骤日志；尚无日志时为 null。 |

#### 构造函数说明

<a id="api-341e12306d7ecc95"></a>

##### GpuDeploymentSnapshot · 主构造器

```csharp
public GpuDeploymentSnapshot(Guid SessionId, long Revision, GpuDeploymentState State, IReadOnlyList<GpuDeploymentStep> Steps, GpuDecisionRequest? PendingDecision, IReadOnlyList<GpuDeploymentAction> AllowedActions, bool HasAddedAdapter, bool HasPendingCleanup, string? Error = null, string? CleanupError = null, string? LastLog = null);
```

| 参数 | 说明 |
| --- | --- |
| `SessionId` | 部署会话的稳定标识。 |
| `Revision` | 同一会话中递增的快照修订号。 |
| `State` | 部署当前状态。 |
| `Steps` | 当前各步骤的状态快照。 |
| `PendingDecision` | 当前待确认事项；不在等待决定时为 null。 |
| `AllowedActions` | 当前状态允许的调用动作。 |
| `HasAddedAdapter` | 本次部署是否新增并识别到适配器。 |
| `HasPendingCleanup` | 是否有已识别且尚待清理的适配器；false 时仍应检查 State 和 CleanupError。 |
| `Error` | 部署错误说明；无错误时为 null。 |
| `CleanupError` | 清理失败说明；没有清理错误时为 null。 |
| `LastLog` | 最近一条步骤日志；尚无日志时为 null。 |

#### 属性说明

<a id="api-9c68cb08f048daa9"></a>

##### AddedAdapterId · 属性

```csharp
public string? AddedAdapterId { get; init; }
```

本次原生回执返回且已核对属于目标 VM 的 GPU 适配器身份；未确认时为 null，不通过清单差异推断。

<a id="api-a30b434db3c93a67"></a>

##### AllowedActions · record 属性

```csharp
public IReadOnlyList<GpuDeploymentAction> AllowedActions { get; init; }
```

当前状态允许的调用动作。

<a id="api-58374e828624fe3d"></a>

##### CleanupError · record 属性

```csharp
public string? CleanupError { get; init; }
```

清理失败说明；没有清理错误时为 null。

<a id="api-2a031d092e685776"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

部署错误说明；无错误时为 null。

<a id="api-3b95afd42de013fb"></a>

##### HasAddedAdapter · record 属性

```csharp
public bool HasAddedAdapter { get; init; }
```

本次部署是否新增并识别到适配器。

<a id="api-5db4fba4b5816944"></a>

##### HasPendingCleanup · record 属性

```csharp
public bool HasPendingCleanup { get; init; }
```

是否有已识别且尚待清理的适配器；false 时仍应检查 State 和 CleanupError。

<a id="api-d51e4de7fc4a52ae"></a>

##### LastLog · record 属性

```csharp
public string? LastLog { get; init; }
```

最近一条步骤日志；尚无日志时为 null。

<a id="api-293632a781a3d836"></a>

##### OutcomeUnknown · 属性

```csharp
public bool OutcomeUnknown { get; init; }
```

底层操作结果尚未确认；此状态不允许重试清理。AllowedActions 包含 Reconcile 时可只读核实原任务和资源。

<a id="api-5d5aecaca834bbb0"></a>

##### PendingCleanupAdapterId · 属性

```csharp
public string? PendingCleanupAdapterId { get; init; }
```

已确认归属且仍待清理的适配器身份；不代表当前允许清理，另查 AllowedActions。

<a id="api-0dce5ae59f56b76d"></a>

##### PendingDecision · record 属性

```csharp
public GpuDecisionRequest? PendingDecision { get; init; }
```

当前待确认事项；不在等待决定时为 null。

<a id="api-e0251fb0578ad929"></a>

##### Recovery · 属性

```csharp
public GpuDeploymentRecovery? Recovery { get; init; }
```

本次部署和恢复的原任务、资源身份、原状态与核实结果；尚未留下记录时为 null。可保存用于排查，但序列化材料不能在新进程中恢复此会话。

<a id="api-da1d46719893e43e"></a>

##### Revision · record 属性

```csharp
public long Revision { get; init; }
```

同一会话中递增的快照修订号。

<a id="api-bea533855f89f637"></a>

##### SessionId · record 属性

```csharp
public Guid SessionId { get; init; }
```

部署会话的稳定标识。

<a id="api-1987d6fea8c8ad21"></a>

##### State · record 属性

```csharp
public GpuDeploymentState State { get; init; }
```

部署当前状态。

<a id="api-091d68578048fd7c"></a>

##### Steps · record 属性

```csharp
public IReadOnlyList<GpuDeploymentStep> Steps { get; init; }
```

当前各步骤的状态快照。

<a id="api-b4fe6c09c7a1b03c"></a>

### `GpuDeploymentState` 枚举

一次 GPU 部署的状态；调用方应以状态和 AllowedActions 决定下一次调用。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Cancelled = 5 `](#api-84b9345cc6ab20e6) | 部署取消流程已结束，已完成的配置修改予以保留。 |
| [` CleanupRequired = 6 `](#api-33ef8617e3dc9e3e) | 取消或失败后的任务结果、临时磁盘恢复或新增适配器清理尚未解决；按允许动作核实或重试。 |
| [` Completed = 3 `](#api-00390e0f4d5c56f3) | 部署已完成。 |
| [` Created = 0 `](#api-37b2be1efe42f1b1) | 已创建，尚未执行部署，可调用 StartAsync。 |
| [` Failed = 4 `](#api-83e07a9a9bcb8ead) | 部署失败，相关错误保存在快照中。 |
| [` Running = 1 `](#api-67643aa629a1362c) | 正在执行当前步骤。 |
| [` WaitingForDecision = 2 `](#api-cfafa4edc1d9cc29) | 已暂停并等待匹配 PendingDecision.Id 的用户决定。 |

#### 详细说明

```csharp
public enum GpuDeploymentState
```

#### 枚举值说明

<a id="api-84b9345cc6ab20e6"></a>

##### Cancelled · 枚举值

```csharp
Cancelled = 5
```

部署取消流程已结束，已完成的配置修改予以保留。

<a id="api-33ef8617e3dc9e3e"></a>

##### CleanupRequired · 枚举值

```csharp
CleanupRequired = 6
```

取消或失败后的任务结果、临时磁盘恢复或新增适配器清理尚未解决；按允许动作核实或重试。

<a id="api-00390e0f4d5c56f3"></a>

##### Completed · 枚举值

```csharp
Completed = 3
```

部署已完成。

<a id="api-37b2be1efe42f1b1"></a>

##### Created · 枚举值

```csharp
Created = 0
```

已创建，尚未执行部署，可调用 StartAsync。

<a id="api-83e07a9a9bcb8ead"></a>

##### Failed · 枚举值

```csharp
Failed = 4
```

部署失败，相关错误保存在快照中。

<a id="api-67643aa629a1362c"></a>

##### Running · 枚举值

```csharp
Running = 1
```

正在执行当前步骤。

<a id="api-cfafa4edc1d9cc29"></a>

##### WaitingForDecision · 枚举值

```csharp
WaitingForDecision = 2
```

已暂停并等待匹配 PendingDecision.Id 的用户决定。

<a id="api-456c5ce4936ed611"></a>

### `GpuDeploymentStep` record 类

部署步骤的显示状态快照。名称和说明用于展示，程序分支应使用 Type 和 Status。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuDeploymentStep(GpuDeploymentStepKind Type, string Name, string Description, GpuDeploymentStepStatus Status) `](#api-b1472caf6c86df8f) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Description { get; init; } `](#api-28c8295f24bc93e1) | 步骤当前进展说明。 |
| [` public string Name { get; init; } `](#api-0fc433befe5eea77) | 步骤显示名称。 |
| [` public GpuDeploymentStepStatus Status { get; init; } `](#api-69d3a2d8ea9d52c8) | 步骤执行状态。 |
| [` public GpuDeploymentStepKind Type { get; init; } `](#api-0fc2418077774c70) | 步骤类别。 |

#### 详细说明

```csharp
public sealed record GpuDeploymentStep(GpuDeploymentStepKind Type, string Name, string Description, GpuDeploymentStepStatus Status)
```

| 参数 | 说明 |
| --- | --- |
| `Type` | 步骤类别。 |
| `Name` | 步骤显示名称。 |
| `Description` | 步骤当前进展说明。 |
| `Status` | 步骤执行状态。 |

#### 构造函数说明

<a id="api-b1472caf6c86df8f"></a>

##### GpuDeploymentStep · 主构造器

```csharp
public GpuDeploymentStep(GpuDeploymentStepKind Type, string Name, string Description, GpuDeploymentStepStatus Status);
```

| 参数 | 说明 |
| --- | --- |
| `Type` | 步骤类别。 |
| `Name` | 步骤显示名称。 |
| `Description` | 步骤当前进展说明。 |
| `Status` | 步骤执行状态。 |

#### 属性说明

<a id="api-28c8295f24bc93e1"></a>

##### Description · record 属性

```csharp
public string Description { get; init; }
```

步骤当前进展说明。

<a id="api-0fc433befe5eea77"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

步骤显示名称。

<a id="api-69d3a2d8ea9d52c8"></a>

##### Status · record 属性

```csharp
public GpuDeploymentStepStatus Status { get; init; }
```

步骤执行状态。

<a id="api-0fc2418077774c70"></a>

##### Type · record 属性

```csharp
public GpuDeploymentStepKind Type { get; init; }
```

步骤类别。

<a id="api-6beb4e13dec8c4b8"></a>

### `GpuDeploymentStepKind` 枚举

GPU 部署流程中的步骤类别。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Assign = 4 `](#api-141329c91b53c21f) | 新增虚拟 GPU 分区适配器。 |
| [` ConfigCheck = 1 `](#api-816ee759dbdb8e3c) | 检查虚拟机的 GPU 所需设置。 |
| [` Driver = 5 `](#api-1dcdb18560bd523b) | 安装或更新来宾 GPU 驱动。 |
| [` Optimization = 3 `](#api-66a3170a736ca7ef) | 调整 GPU 所需的缓存和 MMIO 设置。 |
| [` PowerCheck = 2 `](#api-d17eb32c160bde76) | 检查并按用户决定处理虚拟机电源状态。 |
| [` Prepare = 0 `](#api-c4495c210703f25d) | 准备宿主 GPU 分配环境。 |

#### 详细说明

```csharp
public enum GpuDeploymentStepKind
```

#### 枚举值说明

<a id="api-141329c91b53c21f"></a>

##### Assign · 枚举值

```csharp
Assign = 4
```

新增虚拟 GPU 分区适配器。

<a id="api-816ee759dbdb8e3c"></a>

##### ConfigCheck · 枚举值

```csharp
ConfigCheck = 1
```

检查虚拟机的 GPU 所需设置。

<a id="api-1dcdb18560bd523b"></a>

##### Driver · 枚举值

```csharp
Driver = 5
```

安装或更新来宾 GPU 驱动。

<a id="api-66a3170a736ca7ef"></a>

##### Optimization · 枚举值

```csharp
Optimization = 3
```

调整 GPU 所需的缓存和 MMIO 设置。

<a id="api-d17eb32c160bde76"></a>

##### PowerCheck · 枚举值

```csharp
PowerCheck = 2
```

检查并按用户决定处理虚拟机电源状态。

<a id="api-c4495c210703f25d"></a>

##### Prepare · 枚举值

```csharp
Prepare = 0
```

准备宿主 GPU 分配环境。

<a id="api-499dc8bdf05041ca"></a>

### `GpuDeploymentStepStatus` 枚举

单个 GPU 部署步骤的执行状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Failed = 3 `](#api-731cdee5b3b5f485) | 步骤执行失败。 |
| [` Pending = 0 `](#api-b3f34b6949942824) | 尚未执行该步骤。 |
| [` Running = 1 `](#api-bc6ed56d3d4a3f57) | 步骤正在执行或等待其所需决定。 |
| [` Success = 2 `](#api-828dce9ab940883b) | 步骤已成功结束。 |

#### 详细说明

```csharp
public enum GpuDeploymentStepStatus
```

#### 枚举值说明

<a id="api-731cdee5b3b5f485"></a>

##### Failed · 枚举值

```csharp
Failed = 3
```

步骤执行失败。

<a id="api-b3f34b6949942824"></a>

##### Pending · 枚举值

```csharp
Pending = 0
```

尚未执行该步骤。

<a id="api-bc6ed56d3d4a3f57"></a>

##### Running · 枚举值

```csharp
Running = 1
```

步骤正在执行或等待其所需决定。

<a id="api-828dce9ab940883b"></a>

##### Success · 枚举值

```csharp
Success = 2
```

步骤已成功结束。

<a id="api-b7afa42a1fa76e7e"></a>

### `GpuLinuxInput` record 类

为 Linux 部署提供脚本和 SSH 凭据；会话在处理前复制二者，避免后续调用方修改影响待执行操作。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuLinuxInput(Guid RequestId, LinuxGpuDeploymentScript? Script, SshCredentials? Credentials) `](#api-e8e6f797f4627924) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public SshCredentials? Credentials { get; init; } `](#api-de53addd2b67d826) | SSH 凭据；类型允许 null，但提交 Linux 决定时不能为空，端口及代理需有效。 |
| [` public LinuxGpuDeploymentScript? Script { get; init; } `](#api-13bf473ff669cd8a) | 选定的部署脚本；类型允许 null，但提交 Linux 决定时不能为空。 |

#### 详细说明

```csharp
public sealed record GpuLinuxInput(Guid RequestId, LinuxGpuDeploymentScript? Script, SshCredentials? Credentials) : GpuDeploymentDecision(RequestId)
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |
| `Script` | 选定的部署脚本；类型允许 null，但提交 Linux 决定时不能为空。 |
| `Credentials` | SSH 凭据；类型允许 null，但提交 Linux 决定时不能为空，端口及代理需有效。 |

#### 构造函数说明

<a id="api-e8e6f797f4627924"></a>

##### GpuLinuxInput · 主构造器

```csharp
public GpuLinuxInput(Guid RequestId, LinuxGpuDeploymentScript? Script, SshCredentials? Credentials);
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |
| `Script` | 选定的部署脚本；类型允许 null，但提交 Linux 决定时不能为空。 |
| `Credentials` | SSH 凭据；类型允许 null，但提交 Linux 决定时不能为空，端口及代理需有效。 |

#### 属性说明

<a id="api-de53addd2b67d826"></a>

##### Credentials · record 属性

```csharp
public SshCredentials? Credentials { get; init; }
```

SSH 凭据；类型允许 null，但提交 Linux 决定时不能为空，端口及代理需有效。

<a id="api-13bf473ff669cd8a"></a>

##### Script · record 属性

```csharp
public LinuxGpuDeploymentScript? Script { get; init; }
```

选定的部署脚本；类型允许 null，但提交 Linux 决定时不能为空。

<a id="api-bb8b2e9014baee53"></a>

### `GpuPartitionSelection` record 类

从当前候选列表中选择一个分区；不能用任意磁盘路径替代 PartitionId。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentModels.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuPartitionSelection(Guid RequestId, string PartitionId) `](#api-f285de7494c8d272) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string PartitionId { get; init; } `](#api-5e04f2ff1a7a1ceb) | 当前 PendingDecision.Partitions 中的分区 Id。 |

#### 详细说明

```csharp
public sealed record GpuPartitionSelection(Guid RequestId, string PartitionId) : GpuDeploymentDecision(RequestId)
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |
| `PartitionId` | 当前 PendingDecision.Partitions 中的分区 Id。 |

#### 构造函数说明

<a id="api-f285de7494c8d272"></a>

##### GpuPartitionSelection · 主构造器

```csharp
public GpuPartitionSelection(Guid RequestId, string PartitionId);
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。 |
| `PartitionId` | 当前 PendingDecision.Partitions 中的分区 Id。 |

#### 属性说明

<a id="api-5e04f2ff1a7a1ceb"></a>

##### PartitionId · record 属性

```csharp
public string PartitionId { get; init; }
```

当前 PendingDecision.Partitions 中的分区 Id。

<a id="api-f73dbd0a3968b7df"></a>

### `GpuRecoveryOperation` record 类

部署发起的一个原生操作及核实结果；原生引用只用于诊断。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentRecovery.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuRecoveryOperation(Guid Id, string Stage, string Method, string TargetReference, string? JobReference, DateTimeOffset StartedAtUtc, bool IsSettled, ExHyperV.Tools.ApiFailure? Failure) `](#api-245d2f06fef917d0) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ExHyperV.Tools.ApiFailure? Failure { get; init; } `](#api-01a74fdd008a83bf) | 最新失败详情；正在执行且未取得结果时也可能为 null，需结合 IsSettled。 |
| [` public Guid Id { get; init; } `](#api-89cefdc1a6f0a899) | 本次原生调用的 Core 编号。 |
| [` public bool IsSettled { get; init; } `](#api-76c4e0e8b3da72d4) | 是否已确认结束；不表示成功，失败详情见 Failure。 |
| [` public string? JobReference { get; init; } `](#api-e981884799d16591) | 原生作业引用；同步调用或未取得时为 null。 |
| [` public string Method { get; init; } `](#api-2f3051294368beec) | 实际调用的原生方法名。 |
| [` public string Stage { get; init; } `](#api-86a8e72f6c3a98c8) | 部署步骤标识，例如 Power:Start、Mmio、Partitions、WindowsDrivers、RemoveAdapter、Recovery:资源类型。 |
| [` public DateTimeOffset StartedAtUtc { get; init; } `](#api-cbd4e76d355433af) | 调用开始时间。 |
| [` public string TargetReference { get; init; } `](#api-223141406b77ee13) | 由 Core 查询取得的目标引用。 |

#### 详细说明

```csharp
public sealed record GpuRecoveryOperation(Guid Id, string Stage, string Method, string TargetReference,
    string? JobReference, DateTimeOffset StartedAtUtc, bool IsSettled, ExHyperV.Tools.ApiFailure? Failure)
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 本次原生调用的 Core 编号。 |
| `Stage` | 部署步骤标识，例如 Power:Start、Mmio、Partitions、WindowsDrivers、RemoveAdapter、Recovery:资源类型。 |
| `Method` | 实际调用的原生方法名。 |
| `TargetReference` | 由 Core 查询取得的目标引用。 |
| `JobReference` | 原生作业引用；同步调用或未取得时为 null。 |
| `StartedAtUtc` | 调用开始时间。 |
| `IsSettled` | 是否已确认结束；不表示成功，失败详情见 Failure。 |
| `Failure` | 最新失败详情；正在执行且未取得结果时也可能为 null，需结合 IsSettled。 |

#### 构造函数说明

<a id="api-245d2f06fef917d0"></a>

##### GpuRecoveryOperation · 主构造器

```csharp
public GpuRecoveryOperation(Guid Id, string Stage, string Method, string TargetReference, string? JobReference, DateTimeOffset StartedAtUtc, bool IsSettled, ExHyperV.Tools.ApiFailure? Failure);
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 本次原生调用的 Core 编号。 |
| `Stage` | 部署步骤标识，例如 Power:Start、Mmio、Partitions、WindowsDrivers、RemoveAdapter、Recovery:资源类型。 |
| `Method` | 实际调用的原生方法名。 |
| `TargetReference` | 由 Core 查询取得的目标引用。 |
| `JobReference` | 原生作业引用；同步调用或未取得时为 null。 |
| `StartedAtUtc` | 调用开始时间。 |
| `IsSettled` | 是否已确认结束；不表示成功，失败详情见 Failure。 |
| `Failure` | 最新失败详情；正在执行且未取得结果时也可能为 null，需结合 IsSettled。 |

#### 属性说明

<a id="api-01a74fdd008a83bf"></a>

##### Failure · record 属性

```csharp
public ExHyperV.Tools.ApiFailure? Failure { get; init; }
```

最新失败详情；正在执行且未取得结果时也可能为 null，需结合 IsSettled。

<a id="api-89cefdc1a6f0a899"></a>

##### Id · record 属性

```csharp
public Guid Id { get; init; }
```

本次原生调用的 Core 编号。

<a id="api-76c4e0e8b3da72d4"></a>

##### IsSettled · record 属性

```csharp
public bool IsSettled { get; init; }
```

是否已确认结束；不表示成功，失败详情见 Failure。

<a id="api-e981884799d16591"></a>

##### JobReference · record 属性

```csharp
public string? JobReference { get; init; }
```

原生作业引用；同步调用或未取得时为 null。

<a id="api-2f3051294368beec"></a>

##### Method · record 属性

```csharp
public string Method { get; init; }
```

实际调用的原生方法名。

<a id="api-86a8e72f6c3a98c8"></a>

##### Stage · record 属性

```csharp
public string Stage { get; init; }
```

部署步骤标识，例如 Power:Start、Mmio、Partitions、WindowsDrivers、RemoveAdapter、Recovery:资源类型。

<a id="api-cbd4e76d355433af"></a>

##### StartedAtUtc · record 属性

```csharp
public DateTimeOffset StartedAtUtc { get; init; }
```

调用开始时间。

<a id="api-223141406b77ee13"></a>

##### TargetReference · record 属性

```csharp
public string TargetReference { get; init; }
```

由 Core 查询取得的目标引用。

<a id="api-04b306b383532708"></a>

### `GpuRecoveryResource` record 类

本次部署需要恢复的一个临时资源，不包含驱动文件回滚。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentRecovery.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuRecoveryResource(string Kind, string Resource, IReadOnlyDictionary<string, string> OriginalValues, bool Restored, ExHyperV.Tools.ApiFailure? Failure) `](#api-14a91be6f04f1c6c) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ExHyperV.Tools.ApiFailure? Failure { get; init; } `](#api-d9c185085c4c6673) | 最近恢复或核实失败的详情。 |
| [` public string Kind { get; init; } `](#api-18c0f950c753160e) | 资源种类：DiskState、ImageMount、PhysicalAttachment、DriveLetter。 |
| [` public IReadOnlyDictionary<string, string> OriginalValues { get; init; } `](#api-fef809f4a6456c00) | 原状态与恢复目标。 |
| [` public string Resource { get; init; } `](#api-3bdb3941c9b22c1d) | 原磁盘、映像、挂接或分区的身份说明。 |
| [` public bool Restored { get; init; } `](#api-c424be263a29ae59) | 最近核实是否已经恢复。 |

#### 详细说明

```csharp
public sealed record GpuRecoveryResource(string Kind, string Resource,
    IReadOnlyDictionary<string, string> OriginalValues, bool Restored, ExHyperV.Tools.ApiFailure? Failure)
```

| 参数 | 说明 |
| --- | --- |
| `Kind` | 资源种类：DiskState、ImageMount、PhysicalAttachment、DriveLetter。 |
| `Resource` | 原磁盘、映像、挂接或分区的身份说明。 |
| `OriginalValues` | 原状态与恢复目标。DiskState 含 DiskNumber/IsOffline/IsReadOnly；ImageMount 含 ImagePath/OriginallyMounted；PhysicalAttachment 含 VmId/ControllerId/ControllerType/ControllerNumber/Location；DriveLetter 含 AccessPath/OriginallyPresent。布尔值使用 True/False。 |
| `Restored` | 最近核实是否已经恢复。 |
| `Failure` | 最近恢复或核实失败的详情。 |

#### 构造函数说明

<a id="api-14a91be6f04f1c6c"></a>

##### GpuRecoveryResource · 主构造器

```csharp
public GpuRecoveryResource(string Kind, string Resource, IReadOnlyDictionary<string, string> OriginalValues, bool Restored, ExHyperV.Tools.ApiFailure? Failure);
```

| 参数 | 说明 |
| --- | --- |
| `Kind` | 资源种类：DiskState、ImageMount、PhysicalAttachment、DriveLetter。 |
| `Resource` | 原磁盘、映像、挂接或分区的身份说明。 |
| `OriginalValues` | 原状态与恢复目标。DiskState 含 DiskNumber/IsOffline/IsReadOnly；ImageMount 含 ImagePath/OriginallyMounted；PhysicalAttachment 含 VmId/ControllerId/ControllerType/ControllerNumber/Location；DriveLetter 含 AccessPath/OriginallyPresent。布尔值使用 True/False。 |
| `Restored` | 最近核实是否已经恢复。 |
| `Failure` | 最近恢复或核实失败的详情。 |

#### 属性说明

<a id="api-d9c185085c4c6673"></a>

##### Failure · record 属性

```csharp
public ExHyperV.Tools.ApiFailure? Failure { get; init; }
```

最近恢复或核实失败的详情。

<a id="api-18c0f950c753160e"></a>

##### Kind · record 属性

```csharp
public string Kind { get; init; }
```

资源种类：DiskState、ImageMount、PhysicalAttachment、DriveLetter。

<a id="api-fef809f4a6456c00"></a>

##### OriginalValues · record 属性

```csharp
public IReadOnlyDictionary<string, string> OriginalValues { get; init; }
```

原状态与恢复目标。DiskState 含 DiskNumber/IsOffline/IsReadOnly；ImageMount 含 ImagePath/OriginallyMounted；PhysicalAttachment 含 VmId/ControllerId/ControllerType/ControllerNumber/Location；DriveLetter 含 AccessPath/OriginallyPresent。布尔值使用 True/False。

<a id="api-3bdb3941c9b22c1d"></a>

##### Resource · record 属性

```csharp
public string Resource { get; init; }
```

原磁盘、映像、挂接或分区的身份说明。

<a id="api-c424be263a29ae59"></a>

##### Restored · record 属性

```csharp
public bool Restored { get; init; }
```

最近核实是否已经恢复。

<a id="api-4999a42e73dce6e6"></a>

### `HostDiskInfo` 类

宿主磁盘的身份、容量与直通候选状态，包含已挂载的虚拟磁盘。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储 |
| 源文件 | ` Management/Host/Storage/HostDiskInfo.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool CanPassthrough { get; init; } `](#api-5ac578ba27641fe5) | 该磁盘是否可以进入直通前的进一步检查。 |
| [` public string FriendlyName { get; init; } = string.Empty `](#api-053d1df35fd1010f) | 磁盘型号或友好名称。 |
| [` public int Number { get; init; } `](#api-4eca52c525a05420) | Windows 为该磁盘分配的编号。 |
| [` public Services.HostDiskReference Reference { get; } `](#api-5efe5d6c8a849a78) | 该磁盘的身份摘要，用于后续联机、只读和分区盘符修改。 |
| [` public string SerialNumber { get; init; } = string.Empty `](#api-6a3a85fd2d5f0097) | 磁盘的硬件序列号。 |
| [` public long SizeBytes { get; init; } `](#api-2aeb9591a84bfc27) | 磁盘的总容量。 |
| [` public double SizeGiB { get; init; } `](#api-0610ab0863bf51f6) | 以 GiB 表示的磁盘容量。 |
| [` public string Status { get; init; } = string.Empty `](#api-3a3337f1422acde5) | 磁盘的主要状态说明，按系统盘、已分配、只读等优先级选取。 |
| [` public string UniqueId { get; init; } = string.Empty `](#api-3e55cc8acf07c4a9) | 存储系统提供的磁盘标识，用于核对修改目标。 |

#### 详细说明

```csharp
public class HostDiskInfo
```

#### 属性说明

<a id="api-5ac578ba27641fe5"></a>

##### CanPassthrough · 属性

```csharp
public bool CanPassthrough { get; init; }
```

该磁盘是否可以进入直通前的进一步检查。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 该磁盘是否可以进入直通前的进一步检查。 |
| 取值与默认 | true：未识别为系统盘、启动盘或已分配磁盘；false：被上述条件排除。 |

<a id="api-053d1df35fd1010f"></a>

##### FriendlyName · 属性

```csharp
public string FriendlyName { get; init; } = string.Empty;
```

磁盘型号或友好名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 磁盘型号或友好名称。 |

<a id="api-4eca52c525a05420"></a>

##### Number · 属性

```csharp
public int Number { get; init; }
```

Windows 为该磁盘分配的编号。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | Windows 为该磁盘分配的编号。 |
| 取值与默认 | 非负整数，例如 0 表示磁盘 0。 |

<a id="api-5efe5d6c8a849a78"></a>

##### Reference · 属性

```csharp
public Services.HostDiskReference Reference { get; }
```

该磁盘的身份摘要，用于后续联机、只读和分区盘符修改。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 该磁盘的身份摘要，用于后续联机、只读和分区盘符修改。 |

<a id="api-6a3a85fd2d5f0097"></a>

##### SerialNumber · 属性

```csharp
public string SerialNumber { get; init; } = string.Empty;
```

磁盘的硬件序列号。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 磁盘的硬件序列号。 |
| 取值与默认 | 序列号文本；未取得时为空字符串。 |

<a id="api-2aeb9591a84bfc27"></a>

##### SizeBytes · 属性

```csharp
public long SizeBytes { get; init; }
```

磁盘的总容量。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 磁盘的总容量。 |
| 取值与默认 | 单位为字节。 |

<a id="api-0610ab0863bf51f6"></a>

##### SizeGiB · 属性

```csharp
public double SizeGiB { get; init; }
```

以 GiB 表示的磁盘容量。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 以 GiB 表示的磁盘容量。 |
| 取值与默认 | 1 GiB = 1,073,741,824 字节，保留两位小数。精确字节数见 SizeBytes。 |

<a id="api-3a3337f1422acde5"></a>

##### Status · 属性

```csharp
public string Status { get; init; } = string.Empty;
```

磁盘的主要状态说明，按系统盘、已分配、只读等优先级选取。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 磁盘的主要状态说明，按系统盘、已分配、只读等优先级选取。 |
| 取值与默认 | 本地化文本。 |

<a id="api-3e55cc8acf07c4a9"></a>

##### UniqueId · 属性

```csharp
public string UniqueId { get; init; } = string.Empty;
```

存储系统提供的磁盘标识，用于核对修改目标。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 存储系统提供的磁盘标识，用于核对修改目标。 |

<a id="api-7e834c339070727a"></a>

### `HostGpuInfo` 类

宿主当前存在的显示适配器，由 HostGpuService.GetHostGpusAsync 查询产生；IsPartitionable 表示当前是否存在可分区资源。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / GPU |
| 源文件 | ` Management/Host/Gpu/HostGpuInfo.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DriverVersion { get; init; } = string.Empty `](#api-e1ee4e8ca70cc78f) | 宿主为该 GPU 安装的驱动版本。 |
| [` public string InstanceId { get; init; } = string.Empty `](#api-6df3f93066275072) | 显示适配器的 PnP 设备实例标识，用于匹配 GPU。 |
| [` public bool IsPartitionable { get; } `](#api-79d05e2a353b1f39) | 当前是否找到与该 GPU 对应的可分区资源。 |
| [` public string Manu { get; init; } = string.Empty `](#api-be44e243c7ce20c1) | 显示适配器的制造商名称，由 Windows 设备属性或 PCI 厂商信息提供。 |
| [` public ulong? MemoryBytes { get; set; } `](#api-49d88ae65e6ad164) | GPU 的专用显存容量。 |
| [` public string Name { get; init; } = string.Empty `](#api-b00e9a6e237303b8) | 宿主显示适配器的设备名称。 |
| [` public string Vendor { get; init; } = string.Empty `](#api-0bbab15e5270b874) | 板卡厂商名称，优先使用 PCI 子系统厂商信息。 |

#### 详细说明

```csharp
public class HostGpuInfo
```

#### 属性说明

<a id="api-e1ee4e8ca70cc78f"></a>

##### DriverVersion · 属性

```csharp
public string DriverVersion { get; init; } = string.Empty;
```

宿主为该 GPU 安装的驱动版本。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 宿主为该 GPU 安装的驱动版本。 |
| 取值与默认 | 版本字符串。 |

<a id="api-6df3f93066275072"></a>

##### InstanceId · 属性

```csharp
public string InstanceId { get; init; } = string.Empty;
```

显示适配器的 PnP 设备实例标识，用于匹配 GPU。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 显示适配器的 PnP 设备实例标识，用于匹配 GPU。 |

<a id="api-79d05e2a353b1f39"></a>

##### IsPartitionable · 属性

```csharp
public bool IsPartitionable { get; }
```

当前是否找到与该 GPU 对应的可分区资源。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前是否找到与该 GPU 对应的可分区资源。 |
| 取值与默认 | true：存在可分区资源；false：未找到。 |

<a id="api-be44e243c7ce20c1"></a>

##### Manu · 属性

```csharp
public string Manu { get; init; } = string.Empty;
```

显示适配器的制造商名称，由 Windows 设备属性或 PCI 厂商信息提供。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 显示适配器的制造商名称，由 Windows 设备属性或 PCI 厂商信息提供。 |

<a id="api-49d88ae65e6ad164"></a>

##### MemoryBytes · 属性

```csharp
public ulong? MemoryBytes { get; set; }
```

GPU 的专用显存容量。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | GPU 的专用显存容量。 |
| 取值与默认 | 单位为字节；0 表示没有专用显存，null 表示未取得。 |

<a id="api-b00e9a6e237303b8"></a>

##### Name · 属性

```csharp
public string Name { get; init; } = string.Empty;
```

宿主显示适配器的设备名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 宿主显示适配器的设备名称。 |

<a id="api-0bbab15e5270b874"></a>

##### Vendor · 属性

```csharp
public string Vendor { get; init; } = string.Empty;
```

板卡厂商名称，优先使用 PCI 子系统厂商信息。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 板卡厂商名称，优先使用 PCI 子系统厂商信息。 |

<a id="api-b89e91519a465463"></a>

### `HostOpticalDriveInfo` 类

具有 PnP 身份的宿主光驱查询结果；物理光驱配置用于第 1 代虚拟机，第 2 代使用 ISO 映像。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储 |
| 源文件 | ` Management/Host/Storage/HostOpticalDriveInfo.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Drive { get; init; } = string.Empty `](#api-44884fe943e7c151) | 宿主为光驱分配的盘符。 |
| [` public string Model { get; init; } = string.Empty `](#api-88cdc9d34d8169aa) | 光驱型号或设备显示名称。 |
| [` public string PnpDeviceId { get; init; } = string.Empty `](#api-e3d943338e63f090) | 物理光驱的 PnP 设备标识，用于第 1 代虚拟机的光驱直通。 |

#### 详细说明

```csharp
public class HostOpticalDriveInfo
```

#### 属性说明

<a id="api-44884fe943e7c151"></a>

##### Drive · 属性

```csharp
public string Drive { get; init; } = string.Empty;
```

宿主为光驱分配的盘符。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 宿主为光驱分配的盘符。 |
| 取值与默认 | 字母与冒号，例如 E:。 |

<a id="api-88cdc9d34d8169aa"></a>

##### Model · 属性

```csharp
public string Model { get; init; } = string.Empty;
```

光驱型号或设备显示名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 光驱型号或设备显示名称。 |

<a id="api-e3d943338e63f090"></a>

##### PnpDeviceId · 属性

```csharp
public string PnpDeviceId { get; init; } = string.Empty;
```

物理光驱的 PnP 设备标识，用于第 1 代虚拟机的光驱直通。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 物理光驱的 PnP 设备标识，用于第 1 代虚拟机的光驱直通。 |

<a id="api-6307fae9394bbacf"></a>

### `L3DistributionPolicy` 枚举

L3 处理器分布策略（L3ProcessorDistributionPolicy，u8）：VP 在各虚拟 L3 缓存域之间的分布次序。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorSettings.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` EvenLargeToSmall = 3 `](#api-eefe799ccc12274a) | 均匀分布，按大到小顺序选择域。 |
| [` EvenSmallToLarge = 2 `](#api-58c509426154efef) | 均匀分布，按小到大顺序选择域。 |
| [` LargeToSmall = 1 `](#api-d58b13abe44b7924) | 按大到小顺序填充分布域。 |
| [` SmallToLarge = 0 `](#api-7e695e8f54979bbe) | 按小到大顺序填充分布域。 |

#### 详细说明

```csharp
public enum L3DistributionPolicy : byte
```

#### 枚举值说明

<a id="api-eefe799ccc12274a"></a>

##### EvenLargeToSmall · 枚举值

```csharp
EvenLargeToSmall = 3
```

均匀分布，按大到小顺序选择域。

<a id="api-58c509426154efef"></a>

##### EvenSmallToLarge · 枚举值

```csharp
EvenSmallToLarge = 2
```

均匀分布，按小到大顺序选择域。

<a id="api-d58b13abe44b7924"></a>

##### LargeToSmall · 枚举值

```csharp
LargeToSmall = 1
```

按大到小顺序填充分布域。

<a id="api-7e695e8f54979bbe"></a>

##### SmallToLarge · 枚举值

```csharp
SmallToLarge = 0
```

按小到大顺序填充分布域。

<a id="api-d8954296637f67f4"></a>

### `LinuxGpuDeploymentScript` 类

Linux GPU 部署脚本的可变描述。部署服务按 SourceUrl 下载 FileName 并在来宾执行；名称和说明只用于展示。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/LinuxGpuDeploymentScript.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Author { get; set; } = "Unknown" `](#api-82aaa35075a853ad) | 脚本作者的显示名称。 |
| [` public string Description { get; set; } = string.Empty `](#api-cea4b182c216c82a) | 脚本适用系统和用途的显示说明。 |
| [` public string FileName { get; set; } = string.Empty `](#api-a4963c2d55c173bf) | 下载后执行的脚本文件名；部署时必须非空。 |
| [` public string Name { get; set; } = "Unknown" `](#api-ad02cb35c0fe721a) | 脚本显示名称，未指定时为 Unknown。 |
| [` public string SourceUrl { get; set; } = string.Empty `](#api-2db21ea6cd4b1276) | 来宾下载部署脚本所用的 URL；部署时必须非空。 |
| [` public string Version { get; set; } = "1.0.0" `](#api-3dab3d350c3c2cc0) | 脚本版本字符串。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public override string ToString() `](#api-64b688add0b0794e) | 取得脚本的显示名称。 |

#### 详细说明

```csharp
public class LinuxGpuDeploymentScript
```

#### 属性说明

<a id="api-82aaa35075a853ad"></a>

##### Author · 属性

```csharp
public string Author { get; set; } = "Unknown";
```

脚本作者的显示名称。

<a id="api-cea4b182c216c82a"></a>

##### Description · 属性

```csharp
public string Description { get; set; } = string.Empty;
```

脚本适用系统和用途的显示说明。

<a id="api-a4963c2d55c173bf"></a>

##### FileName · 属性

```csharp
public string FileName { get; set; } = string.Empty;
```

下载后执行的脚本文件名；部署时必须非空。

<a id="api-ad02cb35c0fe721a"></a>

##### Name · 属性

```csharp
public string Name { get; set; } = "Unknown";
```

脚本显示名称，未指定时为 Unknown。

<a id="api-2db21ea6cd4b1276"></a>

##### SourceUrl · 属性

```csharp
public string SourceUrl { get; set; } = string.Empty;
```

来宾下载部署脚本所用的 URL；部署时必须非空。

<a id="api-3dab3d350c3c2cc0"></a>

##### Version · 属性

```csharp
public string Version { get; set; } = "1.0.0";
```

脚本版本字符串。

#### 方法说明

<a id="api-64b688add0b0794e"></a>

##### ToString · 方法

```csharp
public override string ToString();
```

取得脚本的显示名称。

返回：当前 Name。

<a id="api-331bc806004b3c0a"></a>

### `LpiMode` 枚举

局部外设中断（LPI）模式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorSettings.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Default = 0 `](#api-67262a7864d046aa) | 使用默认的局部外设中断策略。 |
| [` Disabled = 2 `](#api-6e9116bf6a8ec673) | 禁用局部外设中断模式，编码为 2。 |
| [` Enabled = 3 `](#api-b4d80506b6e2d029) | 启用局部外设中断模式，编码为 3。 |

#### 详细说明

```csharp
public enum LpiMode : byte
```

#### 枚举值说明

<a id="api-67262a7864d046aa"></a>

##### Default · 枚举值

```csharp
Default = 0
```

使用默认的局部外设中断策略。

<a id="api-6e9116bf6a8ec673"></a>

##### Disabled · 枚举值

```csharp
Disabled = 2
```

禁用局部外设中断模式，编码为 2。

<a id="api-b4d80506b6e2d029"></a>

##### Enabled · 枚举值

```csharp
Enabled = 3
```

启用局部外设中断模式，编码为 3。

<a id="api-f9be4f964cd193ac"></a>

### `OperatingSystemType` 枚举

扫描磁盘分区得到的系统或分区用途分类。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / 磁盘分区解析 |
| 源文件 | ` Management/Tools/Partitions/DiskPartitionInfo.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` EFI = 3 `](#api-a84838fc7dcc78d2) | EFI 系统分区，不作为驱动部署的操作系统目标。 |
| [` Linux = 2 `](#api-4df4755551fd2b98) | 按分区类型识别为 Linux 用途；不代表已安装 Linux。 |
| [` Other = 4 `](#api-9d2154baa3e11af8) | 已识别为其他用途的分区。 |
| [` Unknown = 0 `](#api-8fde8b45b31e5e5d) | 未能识别系统类型。 |
| [` Windows = 1 `](#api-1b99c99e9b7b7261) | 按分区类型识别为 Windows 数据分区；不代表已安装 Windows。 |

#### 详细说明

```csharp
public enum OperatingSystemType
```

#### 枚举值说明

<a id="api-a84838fc7dcc78d2"></a>

##### EFI · 枚举值

```csharp
EFI = 3
```

EFI 系统分区，不作为驱动部署的操作系统目标。

<a id="api-4df4755551fd2b98"></a>

##### Linux · 枚举值

```csharp
Linux = 2
```

按分区类型识别为 Linux 用途；不代表已安装 Linux。

<a id="api-9d2154baa3e11af8"></a>

##### Other · 枚举值

```csharp
Other = 4
```

已识别为其他用途的分区。

<a id="api-8fde8b45b31e5e5d"></a>

##### Unknown · 枚举值

```csharp
Unknown = 0
```

未能识别系统类型。

<a id="api-1b99c99e9b7b7261"></a>

##### Windows · 枚举值

```csharp
Windows = 1
```

按分区类型识别为 Windows 数据分区；不代表已安装 Windows。

<a id="api-4f16a116b8548cb3"></a>

### `PageShatterMode` 枚举

大页拆分缓解策略（EnablePageShattering，u8）：Default 使用主机策略，另外两个值明确请求启用或禁用缓解。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorSettings.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` AlwaysDisabled = 2 `](#api-e0d5e335701dbb64) | 明确禁用大页拆分缓解。 |
| [` AlwaysEnabled = 1 `](#api-33cb3876e957e269) | 明确启用大页拆分缓解。 |
| [` Default = 0 `](#api-5f2ea6f506f16b80) | 继承宿主的大页拆分缓解策略。 |

#### 详细说明

```csharp
public enum PageShatterMode : byte
```

#### 枚举值说明

<a id="api-e0d5e335701dbb64"></a>

##### AlwaysDisabled · 枚举值

```csharp
AlwaysDisabled = 2
```

明确禁用大页拆分缓解。

<a id="api-33cb3876e957e269"></a>

##### AlwaysEnabled · 枚举值

```csharp
AlwaysEnabled = 1
```

明确启用大页拆分缓解。

<a id="api-5f2ea6f506f16b80"></a>

##### Default · 枚举值

```csharp
Default = 0
```

继承宿主的大页拆分缓解策略。

<a id="api-5eeaf12e93bd1e57"></a>

### `PageSizeItem` 类

页面大小下拉项（与 VmMemorySettings.BackingPageSize 配套）。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 内存 |
| 源文件 | ` Management/VirtualMachines/Memory/VmMemorySettings.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Description { get; set; } = string.Empty `](#api-e005c4443bbefdb3) | 页面大小选项的本地化显示说明。 |
| [` public byte Value { get; set; } `](#api-ccde854bf8fc75e7) | 统一页面大小编码：0 标准页、1 大页、2 巨页。 |

#### 详细说明

```csharp
public class PageSizeItem
```

#### 属性说明

<a id="api-e005c4443bbefdb3"></a>

##### Description · 属性

```csharp
public string Description { get; set; } = string.Empty;
```

页面大小选项的本地化显示说明。

<a id="api-ccde854bf8fc75e7"></a>

##### Value · 属性

```csharp
public byte Value { get; set; }
```

统一页面大小编码：0 标准页、1 大页、2 巨页。

<a id="api-f5ca6ddfa1385521"></a>

### `PcieDeviceInfo` 类

具有 DDA 位置路径的 PCIe 设备及其当前归属。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / PCIe |
| 源文件 | ` Management/Host/Pcie/PcieDeviceInfo.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public PcieAssignmentTarget Assignment { get; set; } = PcieAssignmentTarget.Unassigned `](#api-e6315f7c1d691a07) | 设备当前归属的宿主、虚拟机或未分配状态。 |
| [` public string ClassType { get; init; } = string.Empty `](#api-8cf16802c6bcef3c) | Windows 驱动提供的 PnP 设备类别，用于设备类型判断。 |
| [` public string DisplayClassType { get; init; } = string.Empty `](#api-eb1caba95827d988) | 用于显示的设备类别；系统设备可附带其非 PCI 子设备的类别。 |
| [` public string FriendlyName { get; init; } = string.Empty `](#api-125d55e58d165a64) | PCIe 设备的友好名称。 |
| [` public string InstanceId { get; init; } = string.Empty `](#api-2996d719dac0c695) | PCIe 设备的 PnP 实例标识，用于核对设备身份。 |
| [` public string Path { get; init; } = string.Empty `](#api-5777150ec481d057) | DDA 分配使用的 PCIe 位置路径。 |
| [` public string Vendor { get; init; } = string.Empty `](#api-5d9fa510299579b6) | PCIe 设备制造商名称。 |

#### 详细说明

```csharp
public partial class PcieDeviceInfo : ObservableObject
```

#### 属性说明

<a id="api-e6315f7c1d691a07"></a>

##### Assignment · 生成属性

```csharp
public PcieAssignmentTarget Assignment { get; set; } = PcieAssignmentTarget.Unassigned;
```

设备当前归属的宿主、虚拟机或未分配状态。实际转移通过 HostPcieService 执行。

<a id="api-8cf16802c6bcef3c"></a>

##### ClassType · 属性

```csharp
public string ClassType { get; init; } = string.Empty;
```

Windows 驱动提供的 PnP 设备类别，用于设备类型判断。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | Windows 驱动提供的 PnP 设备类别，用于设备类型判断。 |
| 取值与默认 | 设备类别文本，例如 Display、Net、USB。 |

<a id="api-eb1caba95827d988"></a>

##### DisplayClassType · 属性

```csharp
public string DisplayClassType { get; init; } = string.Empty;
```

用于显示的设备类别；系统设备可附带其非 PCI 子设备的类别。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 用于显示的设备类别；系统设备可附带其非 PCI 子设备的类别。 |

<a id="api-125d55e58d165a64"></a>

##### FriendlyName · 属性

```csharp
public string FriendlyName { get; init; } = string.Empty;
```

PCIe 设备的友好名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | PCIe 设备的友好名称。 |

<a id="api-2996d719dac0c695"></a>

##### InstanceId · 属性

```csharp
public string InstanceId { get; init; } = string.Empty;
```

PCIe 设备的 PnP 实例标识，用于核对设备身份。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | PCIe 设备的 PnP 实例标识，用于核对设备身份。 |

<a id="api-5777150ec481d057"></a>

##### Path · 属性

```csharp
public string Path { get; init; } = string.Empty;
```

DDA 分配使用的 PCIe 位置路径。操作时与同次查询的 InstanceId 一起传入，以匹配位置上的设备。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | DDA 分配使用的 PCIe 位置路径。操作时与同次查询的 InstanceId 一起传入，以匹配位置上的设备。 |

<a id="api-5d9fa510299579b6"></a>

##### Vendor · 属性

```csharp
public string Vendor { get; init; } = string.Empty;
```

PCIe 设备制造商名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | PCIe 设备制造商名称。 |

<a id="api-9943890d17e165f0"></a>

### `PerformanceSampleInfo` record 类

性能值对应的本次读取状态；调用方应先检查 IsValid 再更新数值和历史曲线。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/PerformanceSampleInfo.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public PerformanceSampleInfo(PerformanceSampleState State, DateTimeOffset ObservedAtUtc, string? Error = null) `](#api-c89fb22626b1f917) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? Error { get; init; } `](#api-99f079c50eb91fb6) | 缺测、失败或归属不明确的说明；不应通过解析此文本决定业务分支。 |
| [` public bool IsValid { get; } `](#api-8e446d0baa5bf74a) | 是否取得了可用的新样本；仅 Valid 为 true。 |
| [` public DateTimeOffset ObservedAtUtc { get; init; } `](#api-2cad823d169c9752) | 本次读取或确认缺测的 UTC 时间；NotSampled 时为默认值，不代表有效采样时间。 |
| [` public PerformanceSampleState State { get; init; } `](#api-86d39ff514b16abf) | 本次读取状态；不是对上次有效数值的状态描述。 |

#### 详细说明

```csharp
public sealed record PerformanceSampleInfo(PerformanceSampleState State, DateTimeOffset ObservedAtUtc, string? Error = null)
```

| 参数 | 说明 |
| --- | --- |
| `State` | 本次读取状态；不是对上次有效数值的状态描述。 |
| `ObservedAtUtc` | 本次读取或确认缺测的 UTC 时间；NotSampled 时为默认值，不代表有效采样时间。 |
| `Error` | 缺测、失败或归属不明确的说明；不应通过解析此文本决定业务分支。 |

调用约定：状态无效时，关联数值可能是默认值或此前保留值，均不可当作本次新样本。是否继续显示旧值由调用方决定。

#### 构造函数说明

<a id="api-c89fb22626b1f917"></a>

##### PerformanceSampleInfo · 主构造器

```csharp
public PerformanceSampleInfo(PerformanceSampleState State, DateTimeOffset ObservedAtUtc, string? Error = null);
```

| 参数 | 说明 |
| --- | --- |
| `State` | 本次读取状态；不是对上次有效数值的状态描述。 |
| `ObservedAtUtc` | 本次读取或确认缺测的 UTC 时间；NotSampled 时为默认值，不代表有效采样时间。 |
| `Error` | 缺测、失败或归属不明确的说明；不应通过解析此文本决定业务分支。 |

#### 属性说明

<a id="api-99f079c50eb91fb6"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

缺测、失败或归属不明确的说明；不应通过解析此文本决定业务分支。

<a id="api-8e446d0baa5bf74a"></a>

##### IsValid · 属性

```csharp
public bool IsValid { get; }
```

是否取得了可用的新样本；仅 Valid 为 true。

<a id="api-2cad823d169c9752"></a>

##### ObservedAtUtc · record 属性

```csharp
public DateTimeOffset ObservedAtUtc { get; init; }
```

本次读取或确认缺测的 UTC 时间；NotSampled 时为默认值，不代表有效采样时间。

<a id="api-86d39ff514b16abf"></a>

##### State · record 属性

```csharp
public PerformanceSampleState State { get; init; }
```

本次读取状态；不是对上次有效数值的状态描述。

<a id="api-6f344a157f783f3a"></a>

### `PerformanceSampleState` 枚举

一次性能读取的状态；只有 Valid 表示本次取得了可使用的新数值。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/PerformanceSampleInfo.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Ambiguous = 6 `](#api-e35de8616f80c022) | 样本无法唯一对应到目标，未采用有歧义的数据。 |
| [` Failed = 5 `](#api-881696474e67a39a) | 本次读取失败。 |
| [` NotApplicable = 7 `](#api-f6f86ddebe2ffcc3) | 当前目标状态不适用此指标，例如虚拟机已关闭。 |
| [` NotSampled = 0 `](#api-2720bce2d8ae8cbb) | 尚未执行读取。 |
| [` Unavailable = 3 `](#api-34031c465c7651a6) | 本次未取得样本或必需字段，不能推断为零。 |
| [` Unsupported = 4 `](#api-ba261b4cb89dd7f3) | 当前环境未提供该采集能力。 |
| [` Valid = 1 `](#api-b726b901aa54b577) | 本次读取成功，数值有效；零也可以是有效数值。 |
| [` WarmingUp = 2 `](#api-ae93fd02dbf9073d) | 计数器正在建立基线，尚无有效的时间差样本。 |

#### 详细说明

```csharp
public enum PerformanceSampleState
```

#### 枚举值说明

<a id="api-e35de8616f80c022"></a>

##### Ambiguous · 枚举值

```csharp
Ambiguous = 6
```

样本无法唯一对应到目标，未采用有歧义的数据。

<a id="api-881696474e67a39a"></a>

##### Failed · 枚举值

```csharp
Failed = 5
```

本次读取失败。

<a id="api-f6f86ddebe2ffcc3"></a>

##### NotApplicable · 枚举值

```csharp
NotApplicable = 7
```

当前目标状态不适用此指标，例如虚拟机已关闭。

<a id="api-2720bce2d8ae8cbb"></a>

##### NotSampled · 枚举值

```csharp
NotSampled = 0
```

尚未执行读取。

<a id="api-34031c465c7651a6"></a>

##### Unavailable · 枚举值

```csharp
Unavailable = 3
```

本次未取得样本或必需字段，不能推断为零。

<a id="api-ba261b4cb89dd7f3"></a>

##### Unsupported · 枚举值

```csharp
Unsupported = 4
```

当前环境未提供该采集能力。

<a id="api-b726b901aa54b577"></a>

##### Valid · 枚举值

```csharp
Valid = 1
```

本次读取成功，数值有效；零也可以是有效数值。

<a id="api-ae93fd02dbf9073d"></a>

##### WarmingUp · 枚举值

```csharp
WarmingUp = 2
```

计数器正在建立基线，尚无有效的时间差样本。

<a id="api-93955a273cb0dc53"></a>

### `PortMonitorMode` 枚举

虚拟交换机端口镜像的角色。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkAdapter.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Destination = 1 `](#api-8ba1df1a0da7097b) | 接收镜像流量的目标端口。 |
| [` None = 0 `](#api-315e67596c8d9837) | 不参与端口镜像。 |
| [` Source = 2 `](#api-29705aa8cd7d5da3) | 提供被镜像流量的源端口。 |

#### 详细说明

```csharp
public enum PortMonitorMode
```

#### 枚举值说明

<a id="api-8ba1df1a0da7097b"></a>

##### Destination · 枚举值

```csharp
Destination = 1
```

接收镜像流量的目标端口。

<a id="api-315e67596c8d9837"></a>

##### None · 枚举值

```csharp
None = 0
```

不参与端口镜像。

<a id="api-29705aa8cd7d5da3"></a>

##### Source · 枚举值

```csharp
Source = 2
```

提供被镜像流量的源端口。

<a id="api-53737ae28a159eae"></a>

### `PvlanMode` 枚举

专用 VLAN 中的端口隔离角色。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkAdapter.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Community = 2 `](#api-3dfe0683881265f9) | 社区端口。 |
| [` Isolated = 1 `](#api-b72bdb0c9d3ccec7) | 隔离端口。 |
| [` None = 0 `](#api-f81b74c1a6789556) | 不使用专用 VLAN 角色。 |
| [` Promiscuous = 3 `](#api-9f86cb19fcfdb666) | 混杂端口。 |

#### 详细说明

```csharp
public enum PvlanMode
```

#### 枚举值说明

<a id="api-3dfe0683881265f9"></a>

##### Community · 枚举值

```csharp
Community = 2
```

社区端口。

<a id="api-b72bdb0c9d3ccec7"></a>

##### Isolated · 枚举值

```csharp
Isolated = 1
```

隔离端口。

<a id="api-f81b74c1a6789556"></a>

##### None · 枚举值

```csharp
None = 0
```

不使用专用 VLAN 角色。

<a id="api-9f86cb19fcfdb666"></a>

##### Promiscuous · 枚举值

```csharp
Promiscuous = 3
```

混杂端口。

<a id="api-0943f09d6d1027bd"></a>

### `SmtMode` 枚举

SMT（同时多线程）模式：继承主机 / 单线程 / 多线程。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorSettings.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Inherit = 0 `](#api-0a82765575155f03) | 继承宿主的每核心线程策略。 |
| [` MultiThread = 2 `](#api-a383e3edcdbb5655) | 启用每核心多线程呈现。 |
| [` SingleThread = 1 `](#api-c44b6b261989219a) | 每个核心呈现一个线程。 |

#### 详细说明

```csharp
public enum SmtMode
```

#### 枚举值说明

<a id="api-0a82765575155f03"></a>

##### Inherit · 枚举值

```csharp
Inherit = 0
```

继承宿主的每核心线程策略。

<a id="api-a383e3edcdbb5655"></a>

##### MultiThread · 枚举值

```csharp
MultiThread = 2
```

启用每核心多线程呈现。

<a id="api-c44b6b261989219a"></a>

##### SingleThread · 枚举值

```csharp
SingleThread = 1
```

每个核心呈现一个线程。

<a id="api-ce8c1d96a58120c3"></a>

### `SpacetimeMode` 枚举

检查点时间线的保存模式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 检查点 |
| 源文件 | ` Management/VirtualMachines/Checkpoints/SpacetimeNode.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Continuous = 0 `](#api-feeec56701ff09f6) | 连续模式，保留运行状态的检查点流程。 |
| [` Still = 1 `](#api-caa11789606f0ad7) | 静态模式，Running 状态下请求生产检查点；其他状态请求标准检查点。 |

#### 详细说明

```csharp
public enum SpacetimeMode
```

#### 枚举值说明

<a id="api-feeec56701ff09f6"></a>

##### Continuous · 枚举值

```csharp
Continuous = 0
```

连续模式，保留运行状态的检查点流程。

<a id="api-caa11789606f0ad7"></a>

##### Still · 枚举值

```csharp
Still = 1
```

静态模式，Running 状态下请求生产检查点；其他状态请求标准检查点。不支持快照的磁盘可被生产检查点跳过。

<a id="api-f48a66d9c8bc043c"></a>

### `SpacetimeNode` 类

检查点时间线节点。修改模型不会修改检查点；重命名、回退和历史磁盘挂接均由服务执行。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 检查点 |
| 源文件 | ` Management/VirtualMachines/Checkpoints/SpacetimeNode.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public DateTime CreatedDate { get; set; } `](#api-932c968f1ac54dcf) | 真实检查点的本地创建时间；Genesis 为推算的时间线起点，Current 为本次读取时间，均不能作为 VM 创建时间。 |
| [` public string Id { get; set; } = string.Empty `](#api-17d81ba7ab599f82) | 检查点 GUID 文本或 GenesisId、CurrentId 逻辑节点标识，不包含 WMI 实例路径。 |
| [` public bool IsCurrent { get; set; } `](#api-9d1b99c00bdf6fad) | 是否标记为当前时间线位置；Current 节点和它的直接父节点都会为 true，因此并非唯一选中标记。 |
| [` public bool IsLogicalNode { get; } `](#api-eb108cfa9afe1887) | 是否为起点或当前状态逻辑节点；这两类节点不能作为真实检查点修改。 |
| [` public bool IsWormhole { get; } `](#api-bf15766522ba1990) | 查询或操作后确认的历史磁盘挂接状态；由 Core 更新。 |
| [` public string Name { get; set; } = string.Empty `](#api-0aaa96d0bd2e9e58) | 节点当前显示名称。 |
| [` public SpacetimeNodeType NodeType { get; set; } `](#api-82f5beda720ce938) | 真实检查点、起点或当前状态节点的分类。 |
| [` public string? ParentId { get; set; } `](#api-5db1f83c52702bb5) | 父节点标识；null 表示没有父节点。 |
| [` public VmScreenshotImage? Thumbnail { get; set; } `](#api-3ed86ee615e47c27) | 节点缩略图；null 表示未提供图像。 |
| [` public string VhdPath { get; set; } = string.Empty `](#api-aa7e0d0c9f6b696c) | 查询选出的一个磁盘文件路径，可能为 .avhdx；供历史磁盘挂接使用，不是检查点的完整磁盘清单。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public const string CurrentId = "CURRENT_RUNNING" `](#api-a6aa469e86953d7d) | 当前运行位置的固定逻辑标识 CURRENT\_RUNNING。 |
| [` public const string GenesisId = "GENESIS_ROOT" `](#api-f453bbc87541f296) | 时间线起点的固定逻辑标识 GENESIS\_ROOT。 |

#### 详细说明

```csharp
public partial class SpacetimeNode : ObservableObject
```

#### 属性说明

<a id="api-932c968f1ac54dcf"></a>

##### CreatedDate · 属性

```csharp
public DateTime CreatedDate { get; set; }
```

真实检查点的本地创建时间；Genesis 为推算的时间线起点，Current 为本次读取时间，均不能作为 VM 创建时间。

<a id="api-17d81ba7ab599f82"></a>

##### Id · 属性

```csharp
public string Id { get; set; } = string.Empty;
```

检查点 GUID 文本或 GenesisId、CurrentId 逻辑节点标识，不包含 WMI 实例路径。

<a id="api-9d1b99c00bdf6fad"></a>

##### IsCurrent · 属性

```csharp
public bool IsCurrent { get; set; }
```

是否标记为当前时间线位置；Current 节点和它的直接父节点都会为 true，因此并非唯一选中标记。

<a id="api-eb108cfa9afe1887"></a>

##### IsLogicalNode · 属性

```csharp
public bool IsLogicalNode { get; }
```

是否为起点或当前状态逻辑节点；这两类节点不能作为真实检查点修改。

<a id="api-bf15766522ba1990"></a>

##### IsWormhole · 属性

```csharp
public bool IsWormhole { get; }
```

查询或操作后确认的历史磁盘挂接状态；由 Core 更新。

<a id="api-0aaa96d0bd2e9e58"></a>

##### Name · 生成属性

```csharp
public string Name { get; set; } = string.Empty;
```

节点当前显示名称。

<a id="api-82f5beda720ce938"></a>

##### NodeType · 属性

```csharp
public SpacetimeNodeType NodeType { get; set; }
```

真实检查点、起点或当前状态节点的分类。

<a id="api-5db1f83c52702bb5"></a>

##### ParentId · 属性

```csharp
public string? ParentId { get; set; }
```

父节点标识；null 表示没有父节点。

<a id="api-3ed86ee615e47c27"></a>

##### Thumbnail · 属性

```csharp
public VmScreenshotImage? Thumbnail { get; set; }
```

节点缩略图；null 表示未提供图像。

<a id="api-aa7e0d0c9f6b696c"></a>

##### VhdPath · 属性

```csharp
public string VhdPath { get; set; } = string.Empty;
```

查询选出的一个磁盘文件路径，可能为 .avhdx；供历史磁盘挂接使用，不是检查点的完整磁盘清单。

#### 字段与常量说明

<a id="api-a6aa469e86953d7d"></a>

##### CurrentId · 字段

```csharp
public const string CurrentId = "CURRENT_RUNNING";
```

当前运行位置的固定逻辑标识 CURRENT_RUNNING。

<a id="api-f453bbc87541f296"></a>

##### GenesisId · 字段

```csharp
public const string GenesisId = "GENESIS_ROOT";
```

时间线起点的固定逻辑标识 GENESIS_ROOT。

<a id="api-390bb40cb297f1fa"></a>

### `SpacetimeNodeType` 枚举

时间线节点代表的对象种类。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 检查点 |
| 源文件 | ` Management/VirtualMachines/Checkpoints/SpacetimeNode.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Current = 2 `](#api-9461d3306ccc1b2e) | 时间线中标记当前运行位置的逻辑节点。 |
| [` Genesis = 0 `](#api-d071ef197ec4fab6) | 时间线的逻辑起点。 |
| [` Snapshot = 1 `](#api-72c5f4410ce7196c) | 可操作的真实检查点。 |

#### 详细说明

```csharp
public enum SpacetimeNodeType
```

#### 枚举值说明

<a id="api-9461d3306ccc1b2e"></a>

##### Current · 枚举值

```csharp
Current = 2
```

时间线中标记当前运行位置的逻辑节点。

<a id="api-d071ef197ec4fab6"></a>

##### Genesis · 枚举值

```csharp
Genesis = 0
```

时间线的逻辑起点。

<a id="api-72c5f4410ce7196c"></a>

##### Snapshot · 枚举值

```csharp
Snapshot = 1
```

可操作的真实检查点。

<a id="api-86effeea9b57e0d7"></a>

### `SshCredentials` 类

Linux GPU 部署用的 SSH 连接凭据。Host 在部署时会被改写为解析后的目标 IP，故可变。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/SshCredentials.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Host { get; set; } = string.Empty `](#api-ad6f7b1fbd4a77df) | 来宾 IPv4 地址；直接调用部署服务时会被改写为选出的目标地址，会话入口会先复制凭据。 |
| [` public bool InstallGraphics { get; set; } = true `](#api-f463d62aea11f1a2) | 是否请求脚本安装图形支持，默认 true。 |
| [` public string Password { get; set; } = string.Empty `](#api-18a69273299857fe) | SSH 密码及部署所需的 sudo 密码；由调用方安全保管，不应写入日志或示例。 |
| [` public int Port { get; set; } `](#api-ecd19ab0f5676244) | SSH 端口，调用方须指定 1 至 65535；模型默认值 0 不是有效端口。 |
| [` public string? ProxyHost { get; set; } `](#api-b66aa8be0396a7f6) | 来宾部署脚本下载所用 HTTP 代理主机；不启用代理时可为 null，并非 SSH 连接代理。 |
| [` public int? ProxyPort { get; set; } `](#api-399c8b9059b36aec) | HTTP 代理端口；不启用代理时可为 null。 |
| [` public bool UseProxy { get; set; } `](#api-c45ebfc6500fefe2) | 是否把代理配置传给来宾部署脚本；为 true 时需同时提供主机和有效端口。 |
| [` public string Username { get; set; } = string.Empty `](#api-f686e697e9e0aaa3) | 来宾 SSH 用户名，Linux 部署要求非空。 |

#### 详细说明

```csharp
public class SshCredentials
```

#### 属性说明

<a id="api-ad6f7b1fbd4a77df"></a>

##### Host · 属性

```csharp
public string Host { get; set; } = string.Empty;
```

来宾 IPv4 地址；直接调用部署服务时会被改写为选出的目标地址，会话入口会先复制凭据。

<a id="api-f463d62aea11f1a2"></a>

##### InstallGraphics · 属性

```csharp
public bool InstallGraphics { get; set; } = true;
```

是否请求脚本安装图形支持，默认 true。

<a id="api-18a69273299857fe"></a>

##### Password · 属性

```csharp
public string Password { get; set; } = string.Empty;
```

SSH 密码及部署所需的 sudo 密码；由调用方安全保管，不应写入日志或示例。

<a id="api-ecd19ab0f5676244"></a>

##### Port · 属性

```csharp
public int Port { get; set; }
```

SSH 端口，调用方须指定 1 至 65535；模型默认值 0 不是有效端口。

<a id="api-b66aa8be0396a7f6"></a>

##### ProxyHost · 属性

```csharp
public string? ProxyHost { get; set; }
```

来宾部署脚本下载所用 HTTP 代理主机；不启用代理时可为 null，并非 SSH 连接代理。

<a id="api-399c8b9059b36aec"></a>

##### ProxyPort · 属性

```csharp
public int? ProxyPort { get; set; }
```

HTTP 代理端口；不启用代理时可为 null。

<a id="api-c45ebfc6500fefe2"></a>

##### UseProxy · 属性

```csharp
public bool UseProxy { get; set; }
```

是否把代理配置传给来宾部署脚本；为 true 时需同时提供主机和有效端口。

<a id="api-f686e697e9e0aaa3"></a>

##### Username · 属性

```csharp
public string Username { get; set; } = string.Empty;
```

来宾 SSH 用户名，Linux 部署要求非空。

<a id="api-4c71af5310c751ad"></a>

### `SwitchEndpointInfo` 类

交换机端点的显示摘要：虚拟机网卡或宿主网卡。未携带稳定端点身份，不能作为修改目标。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 网络 |
| 源文件 | ` Management/Host/Network/SwitchEndpointInfo.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string IpAddress { get; set; } = string.Empty `](#api-d4bc1493450d5098) | 从已取得的地址中选出的端点 IPv4 地址。 |
| [` public string MacAddress { get; set; } = string.Empty `](#api-dce2f0a6b8e68881) | 交换机端点的 MAC 地址。 |
| [` public string Name { get; set; } = string.Empty `](#api-391ceb9ea467d698) | 接入交换机的虚拟机名称或宿主管理操作系统名称。 |

#### 详细说明

```csharp
public class SwitchEndpointInfo
```

#### 属性说明

<a id="api-d4bc1493450d5098"></a>

##### IpAddress · 属性

```csharp
public string IpAddress { get; set; } = string.Empty;
```

从已取得的地址中选出的端点 IPv4 地址。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 从已取得的地址中选出的端点 IPv4 地址。 |
| 取值与默认 | IPv4 地址文本，例如 192.168.1.10；未取得时为空字符串。 |

<a id="api-dce2f0a6b8e68881"></a>

##### MacAddress · 属性

```csharp
public string MacAddress { get; set; } = string.Empty;
```

交换机端点的 MAC 地址。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 交换机端点的 MAC 地址。 |

<a id="api-391ceb9ea467d698"></a>

##### Name · 属性

```csharp
public string Name { get; set; } = string.Empty;
```

接入交换机的虚拟机名称或宿主管理操作系统名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 接入交换机的虚拟机名称或宿主管理操作系统名称。 |

<a id="api-920c0d414c414611"></a>

### `SwitchInfo` 类

虚拟交换机的连接模式、上游网卡与宿主接入状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 网络 |
| 源文件 | ` Management/Host/Network/SwitchInfo.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool AllowManagementOS { get; init; } `](#api-f66af83af4474e53) | 宿主是否通过内部端口接入该交换机。 |
| [` public string Id { get; init; } = string.Empty `](#api-4954dc10325e1ac2) | 虚拟交换机的唯一标识，用于查询和修改交换机。 |
| [` public string? StateError { get; init; } `](#api-62ff4d69c0c5a24f) | 本次查询或识别交换机状态时的错误。 |
| [` public string SwitchName { get; init; } = string.Empty `](#api-c60ae6f392da42db) | 虚拟交换机的显示名称。 |
| [` public SwitchMode SwitchType { get; init; } `](#api-1d4a2ead57e1b5b9) | 按物理上联和 ICS 共享关系识别的交换机模式。 |
| [` public SwitchUpstreamAdapter? Upstream { get; init; } `](#api-bbc6b7edd6b75f00) | 交换机使用的宿主上游网卡。 |

#### 详细说明

```csharp
public class SwitchInfo
```

#### 属性说明

<a id="api-f66af83af4474e53"></a>

##### AllowManagementOS · 属性

```csharp
public bool AllowManagementOS { get; init; }
```

宿主是否通过内部端口接入该交换机。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 宿主是否通过内部端口接入该交换机。 |
| 取值与默认 | true：已发现宿主端口；false：未发现。 |

<a id="api-4954dc10325e1ac2"></a>

##### Id · 属性

```csharp
public string Id { get; init; } = string.Empty;
```

虚拟交换机的唯一标识，用于查询和修改交换机。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 虚拟交换机的唯一标识，用于查询和修改交换机。 |
| 取值与默认 | GUID 字符串。 |

<a id="api-62ff4d69c0c5a24f"></a>

##### StateError · 属性

```csharp
public string? StateError { get; init; }
```

本次查询或识别交换机状态时的错误。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 本次查询或识别交换机状态时的错误。 |
| 取值与默认 | 错误说明；未记录错误时为 null。 |

<a id="api-c60ae6f392da42db"></a>

##### SwitchName · 属性

```csharp
public string SwitchName { get; init; } = string.Empty;
```

虚拟交换机的显示名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 虚拟交换机的显示名称。 |

<a id="api-1d4a2ead57e1b5b9"></a>

##### SwitchType · 属性

```csharp
public SwitchMode SwitchType { get; init; }
```

按物理上联和 ICS 共享关系识别的交换机模式。StateError 非空时表示本次识别未完整成功。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 按物理上联和 ICS 共享关系识别的交换机模式。StateError 非空时表示本次识别未完整成功。 |

<a id="api-bbc6b7edd6b75f00"></a>

##### Upstream · 属性

```csharp
public SwitchUpstreamAdapter? Upstream { get; init; }
```

交换机使用的宿主上游网卡。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 交换机使用的宿主上游网卡。 |
| 取值与默认 | 网卡信息；无上游或未解析成功时为 null。 |

<a id="api-84f5dcdc201f39f8"></a>

### `SwitchMode` 枚举

Hyper-V 交换机的桥接、ICS NAT 和隔离配置模式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 网络 |
| 源文件 | ` Management/Host/Network/SwitchInfo.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Bridge = 0 `](#api-c617f86e50342086) | 外部桥接模式，通过宿主物理网卡连接上游。 |
| [` Isolated = 2 `](#api-050d88a50556b856) | 未识别到外部端口或经典 ICS 共享；不能据此排除 HCN、WinNAT 或其他转发配置。 |
| [` NAT = 1 `](#api-7126e5e20379cd04) | 内部交换机配合 ICS 共享的模式。 |

#### 详细说明

```csharp
public enum SwitchMode
```

#### 枚举值说明

<a id="api-c617f86e50342086"></a>

##### Bridge · 枚举值

```csharp
Bridge = 0
```

外部桥接模式，通过宿主物理网卡连接上游。

<a id="api-050d88a50556b856"></a>

##### Isolated · 枚举值

```csharp
Isolated = 2
```

未识别到外部端口或经典 ICS 共享；不能据此排除 HCN、WinNAT 或其他转发配置。

<a id="api-7126e5e20379cd04"></a>

##### NAT · 枚举值

```csharp
NAT = 1
```

内部交换机配合 ICS 共享的模式。

<a id="api-2cff6962c24db25a"></a>

### `SwitchUpstreamAdapter` record 类

可用的上游物理网卡；使用连接 GUID 和设备标识匹配，不依赖显示名称。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 网络 |
| 源文件 | ` Management/Host/Network/SwitchUpstreamAdapter.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public SwitchUpstreamAdapter(Guid ConnectionId, string DeviceId, string Description, string Name, bool LinkUp) `](#api-eabc9bf38dc02491) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public Guid ConnectionId { get; init; } `](#api-629d62da86181cd4) | 宿主网络连接 GUID，供连接及共享接口识别。 |
| [` public string Description { get; init; } `](#api-1d1862665f1f0b57) | 网卡设备描述。 |
| [` public string DeviceId { get; init; } `](#api-56d4170ac9f0a1c0) | 宿主网络设备标识。 |
| [` public string DisplayName { get; } `](#api-9ee78044447f5418) | 设备描述与连接名称组合的显示文本，不用于设备匹配。 |
| [` public bool LinkUp { get; init; } `](#api-6127b9d4cee1b451) | 查询时物理链路是否已连接。 |
| [` public string Name { get; init; } `](#api-b5e0b714991bfd8e) | 用户可修改的连接显示名称。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public override string ToString() `](#api-987869ee4db4e853) | 取得上游网卡的显示名称。 |

#### 详细说明

```csharp
public sealed record SwitchUpstreamAdapter(Guid ConnectionId, string DeviceId, string Description, string Name,
    bool LinkUp)
```

| 参数 | 说明 |
| --- | --- |
| `ConnectionId` | 宿主网络连接 GUID，供连接及共享接口识别。 |
| `DeviceId` | 宿主网络设备标识。 |
| `Description` | 网卡设备描述。 |
| `Name` | 用户可修改的连接显示名称。 |
| `LinkUp` | 查询时物理链路是否已连接。 |

调用约定：桥接修改须使用当前查询返回的实例，其中还保存了桥接所需的端口身份；通过公开构造器重建或反序列化的实例不包含该信息。

#### 构造函数说明

<a id="api-eabc9bf38dc02491"></a>

##### SwitchUpstreamAdapter · 主构造器

```csharp
public SwitchUpstreamAdapter(Guid ConnectionId, string DeviceId, string Description, string Name, bool LinkUp);
```

| 参数 | 说明 |
| --- | --- |
| `ConnectionId` | 宿主网络连接 GUID，供连接及共享接口识别。 |
| `DeviceId` | 宿主网络设备标识。 |
| `Description` | 网卡设备描述。 |
| `Name` | 用户可修改的连接显示名称。 |
| `LinkUp` | 查询时物理链路是否已连接。 |

#### 属性说明

<a id="api-629d62da86181cd4"></a>

##### ConnectionId · record 属性

```csharp
public Guid ConnectionId { get; init; }
```

宿主网络连接 GUID，供连接及共享接口识别。

<a id="api-1d1862665f1f0b57"></a>

##### Description · record 属性

```csharp
public string Description { get; init; }
```

网卡设备描述。

<a id="api-56d4170ac9f0a1c0"></a>

##### DeviceId · record 属性

```csharp
public string DeviceId { get; init; }
```

宿主网络设备标识。

<a id="api-9ee78044447f5418"></a>

##### DisplayName · 属性

```csharp
public string DisplayName { get; }
```

设备描述与连接名称组合的显示文本，不用于设备匹配。

<a id="api-6127b9d4cee1b451"></a>

##### LinkUp · record 属性

```csharp
public bool LinkUp { get; init; }
```

查询时物理链路是否已连接。

<a id="api-b5e0b714991bfd8e"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

用户可修改的连接显示名称。

#### 方法说明

<a id="api-987869ee4db4e853"></a>

##### ToString · 方法

```csharp
public override string ToString();
```

取得上游网卡的显示名称。

返回：当前 DisplayName。

<a id="api-8174aba5de40c680"></a>

### `VlanOperationMode` 枚举

虚拟交换机端口的 VLAN 工作模式，对应 Hyper-V OperationMode。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkAdapter.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Access = 1 `](#api-c38a42e03f62fc32) | 接入模式，端口使用单个接入 VLAN。 |
| [` Private = 3 `](#api-4a55ed70e3d77810) | 专用 VLAN 模式。 |
| [` Trunk = 2 `](#api-ed5749cc0f79e6fa) | 中继模式，使用本征 VLAN 和允许 VLAN 列表。 |
| [` Unknown = 0 `](#api-3d6d8d2d5dc3b748) | 尚未指定或识别模式。 |

#### 详细说明

```csharp
public enum VlanOperationMode
```

#### 枚举值说明

<a id="api-c38a42e03f62fc32"></a>

##### Access · 枚举值

```csharp
Access = 1
```

接入模式，端口使用单个接入 VLAN。

<a id="api-4a55ed70e3d77810"></a>

##### Private · 枚举值

```csharp
Private = 3
```

专用 VLAN 模式。

<a id="api-ed5749cc0f79e6fa"></a>

##### Trunk · 枚举值

```csharp
Trunk = 2
```

中继模式，使用本征 VLAN 和允许 VLAN 列表。

<a id="api-3d6d8d2d5dc3b748"></a>

##### Unknown · 枚举值

```csharp
Unknown = 0
```

尚未指定或识别模式。

<a id="api-cbf87948c5441cc2"></a>

### `VmApicMode` 枚举

APIC 模式（Msvm_ProcessorSettingData.ApicMode，u8）：自动 / 强制 xAPIC / 强制 x2APIC / Apic。改 guest CPUID 0x01 ECX[21] x2APIC 位。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorSettings.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Apic = 3 `](#api-c87518a9d0fa929b) | 使用 Hyper-V Apic 编码 3。 |
| [` Default = 0 `](#api-0e1fa5aa18c64e07) | 使用 Hyper-V 默认 APIC 模式。 |
| [` Legacy = 1 `](#api-daae61122965679a) | 请求传统 xAPIC 模式。 |
| [` X2Apic = 2 `](#api-2866f2e813e02fe3) | 请求 x2APIC 模式。 |

#### 详细说明

```csharp
public enum VmApicMode : byte
```

#### 枚举值说明

<a id="api-c87518a9d0fa929b"></a>

##### Apic · 枚举值

```csharp
Apic = 3
```

使用 Hyper-V Apic 编码 3。

<a id="api-0e1fa5aa18c64e07"></a>

##### Default · 枚举值

```csharp
Default = 0
```

使用 Hyper-V 默认 APIC 模式。

<a id="api-daae61122965679a"></a>

##### Legacy · 枚举值

```csharp
Legacy = 1
```

请求传统 xAPIC 模式。

<a id="api-2866f2e813e02fe3"></a>

##### X2Apic · 枚举值

```csharp
X2Apic = 2
```

请求 x2APIC 模式。

<a id="api-49f6c6fb73044013"></a>

### `VmCpuCoreMetric` 类

一次 CPU 核心采样；先检查 Sample.IsValid，再使用 Usage。虚拟机使用 GUID 归属，名称只用于显示。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmCpuCoreMetric.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int CoreId { get; init; } `](#api-f12437d79dbd8e37) | 虚拟处理器或宿主逻辑处理器编号；无法读取核心数量时为 -1，并返回无效样本。 |
| [` public bool IsHost { get; init; } `](#api-4eff842a37e31f59) | 是否为宿主逻辑处理器；不通过 VmName 的保留字判断。 |
| [` public PerformanceSampleInfo Sample { get; init; } = new(PerformanceSampleState.NotSampled, default) `](#api-d14f8ed722e82b54) | 本次采集的有效性、观测时间与问题说明；无效样本不代表当前负载为零。 |
| [` public float Usage { get; init; } `](#api-80b5706781a6f42c) | 使用率百分比；仅 Sample.IsValid 为 true 时有意义，缺测时不能作为零使用率。 |
| [` public Guid? VmId { get; init; } `](#api-3da8150f1cf56638) | 所属虚拟机的稳定 GUID；宿主核心为 null。 |
| [` public string VmName { get; init; } = string.Empty `](#api-eeec6580baee405b) | 该采样所属的虚拟机显示名称；不作为身份。 |

#### 详细说明

```csharp
public class VmCpuCoreMetric
```

#### 属性说明

<a id="api-f12437d79dbd8e37"></a>

##### CoreId · 属性

```csharp
public int CoreId { get; init; }
```

虚拟处理器或宿主逻辑处理器编号；无法读取核心数量时为 -1，并返回无效样本。

<a id="api-4eff842a37e31f59"></a>

##### IsHost · 属性

```csharp
public bool IsHost { get; init; }
```

是否为宿主逻辑处理器；不通过 VmName 的保留字判断。

<a id="api-d14f8ed722e82b54"></a>

##### Sample · 属性

```csharp
public PerformanceSampleInfo Sample { get; init; } = new(PerformanceSampleState.NotSampled, default);
```

本次采集的有效性、观测时间与问题说明；无效样本不代表当前负载为零。

<a id="api-80b5706781a6f42c"></a>

##### Usage · 属性

```csharp
public float Usage { get; init; }
```

使用率百分比；仅 Sample.IsValid 为 true 时有意义，缺测时不能作为零使用率。

<a id="api-3da8150f1cf56638"></a>

##### VmId · 属性

```csharp
public Guid? VmId { get; init; }
```

所属虚拟机的稳定 GUID；宿主核心为 null。

<a id="api-eeec6580baee405b"></a>

##### VmName · 属性

```csharp
public string VmName { get; init; } = string.Empty;
```

该采样所属的虚拟机显示名称；不作为身份。

<a id="api-20add229729c5226"></a>

### `VmCreationParameters` 类

创建虚拟机的可变输入。服务在开始时复制输入并按最终名称解析目录；设置此模型本身不会创建资源。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 创建 |
| 源文件 | ` Management/VirtualMachines/Instances/Creation/VmCreationParameters.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool CreateDifferencingDisk { get; set; } `](#api-e5e82ba7fa1d867e) | 使用已有磁盘时，是否基于该磁盘创建差分盘；由调用方明确选择，Core 不根据创建次数改变此选项。 |
| [` public string DifferencingDiskRoot { get; set; } = string.Empty `](#api-8ff89ad5c8032e53) | 新差分盘的根目录；空值按虚拟机目录解析，最终还会追加虚拟机名称子目录。 |
| [` public int DiskMode { get; set; } = 0 `](#api-94c1f8bcca0dea68) | 磁盘来源模式：0 新建、1 使用已有磁盘、2 稍后添加；默认 0。 |
| [` public long DiskSizeGiB { get; set; } = 128 `](#api-ccdf98a569c5caca) | 新建虚拟磁盘的容量，单位为 GiB（1024&#179; 字节），默认 128。 |
| [` public bool EnableDynamicMemory { get; set; } = true `](#api-a0be124258ed28f2) | 是否启用动态内存，默认 true。 |
| [` public bool EnableSecureBoot { get; set; } = true `](#api-1154a98755323cb8) | 是否请求启用安全启动，默认 true，实际适用性取决于代数和隔离模式。 |
| [` public bool EnableTpm { get; set; } = true `](#api-bce116ef86199bb3) | 是否请求启用虚拟 TPM，默认 true。 |
| [` public int Generation { get; set; } = 2 `](#api-5beb8806f7106e1a) | 虚拟机代数，1 或 2；默认 2。 |
| [` public string IsoPath { get; set; } = string.Empty `](#api-451c3a2ff570d2e5) | 要挂载的 ISO 文件路径；空字符串表示不挂载安装介质。 |
| [` public string IsolationType { get; set; } = "Disabled" `](#api-f6b25db0f3280368) | 隔离模式标识：Disabled、TrustedLaunch、VBS、SNP、TDX、RME 或 OpenHCL；默认 Disabled。 |
| [` public long MemoryMiB { get; set; } = 4096 `](#api-4281b02bfc9a8ab8) | 启动内存，单位为 MiB（1024&#178; 字节），默认 4096。 |
| [` public string Name { get; set; } = "NewVM" `](#api-47b9508292a9b919) | 调用方指定的虚拟机名称，默认 NewVM；名称或目录冲突时自动避让，实际名称和提示随创建结果返回。 |
| [` public string OpenHclIgvmPath { get; set; } = string.Empty `](#api-24b1854fd7682b20) | OpenHCL 模式使用的 IGVM 文件路径；其他模式可为空。 |
| [` public string Path { get; set; } = string.Empty `](#api-98f6bdf04b22465a) | 虚拟机配置文件的根目录，服务在其下创建最终虚拟机名称目录。 |
| [` public int ProcessorCount { get; set; } = 4 `](#api-acf2f392706c19b8) | 虚拟处理器数量，默认 4。 |
| [` public bool StoreNewDisksWithVm { get; set; } `](#api-47f79f6d49e730d2) | 是否将新磁盘放入虚拟机目录下的 Virtual Hard Disks；不会移动已有磁盘。 |
| [` public Guid? SwitchId { get; set; } `](#api-c2cf91f153d225d5) | 要连接的虚拟交换机 GUID；null 表示不连接，Guid.Empty 无效。 |
| [` public string Version { get; set; } = "8.0" `](#api-7751c8354294cf7b) | 请求的 Hyper-V 配置版本字符串，默认 8.0。 |
| [` public string VhdPath { get; set; } = string.Empty `](#api-40a1364a0769a4d5) | 新建模式下为磁盘目录，可留空以使用虚拟机目录；已有磁盘模式下为磁盘文件路径。 |

#### 详细说明

```csharp
public class VmCreationParameters
```

#### 属性说明

<a id="api-e5e82ba7fa1d867e"></a>

##### CreateDifferencingDisk · 属性

```csharp
public bool CreateDifferencingDisk { get; set; }
```

使用已有磁盘时，是否基于该磁盘创建差分盘；由调用方明确选择，Core 不根据创建次数改变此选项。

<a id="api-8ff89ad5c8032e53"></a>

##### DifferencingDiskRoot · 属性

```csharp
public string DifferencingDiskRoot { get; set; } = string.Empty;
```

新差分盘的根目录；空值按虚拟机目录解析，最终还会追加虚拟机名称子目录。

<a id="api-94c1f8bcca0dea68"></a>

##### DiskMode · 属性

```csharp
public int DiskMode { get; set; } = 0;
```

磁盘来源模式：0 新建、1 使用已有磁盘、2 稍后添加；默认 0。

<a id="api-ccdf98a569c5caca"></a>

##### DiskSizeGiB · 属性

```csharp
public long DiskSizeGiB { get; set; } = 128;
```

新建虚拟磁盘的容量，单位为 GiB（1024&#179; 字节），默认 128。

<a id="api-a0be124258ed28f2"></a>

##### EnableDynamicMemory · 属性

```csharp
public bool EnableDynamicMemory { get; set; } = true;
```

是否启用动态内存，默认 true。

<a id="api-1154a98755323cb8"></a>

##### EnableSecureBoot · 属性

```csharp
public bool EnableSecureBoot { get; set; } = true;
```

是否请求启用安全启动，默认 true，实际适用性取决于代数和隔离模式。

<a id="api-bce116ef86199bb3"></a>

##### EnableTpm · 属性

```csharp
public bool EnableTpm { get; set; } = true;
```

是否请求启用虚拟 TPM，默认 true。

<a id="api-5beb8806f7106e1a"></a>

##### Generation · 属性

```csharp
public int Generation { get; set; } = 2;
```

虚拟机代数，1 或 2；默认 2。

<a id="api-451c3a2ff570d2e5"></a>

##### IsoPath · 属性

```csharp
public string IsoPath { get; set; } = string.Empty;
```

要挂载的 ISO 文件路径；空字符串表示不挂载安装介质。

<a id="api-f6b25db0f3280368"></a>

##### IsolationType · 属性

```csharp
public string IsolationType { get; set; } = "Disabled";
```

隔离模式标识：Disabled、TrustedLaunch、VBS、SNP、TDX、RME 或 OpenHCL；默认 Disabled。

<a id="api-4281b02bfc9a8ab8"></a>

##### MemoryMiB · 属性

```csharp
public long MemoryMiB { get; set; } = 4096;
```

启动内存，单位为 MiB（1024&#178; 字节），默认 4096。

<a id="api-47b9508292a9b919"></a>

##### Name · 属性

```csharp
public string Name { get; set; } = "NewVM";
```

调用方指定的虚拟机名称，默认 NewVM；名称或目录冲突时自动避让，实际名称和提示随创建结果返回。

<a id="api-24b1854fd7682b20"></a>

##### OpenHclIgvmPath · 属性

```csharp
public string OpenHclIgvmPath { get; set; } = string.Empty;
```

OpenHCL 模式使用的 IGVM 文件路径；其他模式可为空。

<a id="api-98f6bdf04b22465a"></a>

##### Path · 属性

```csharp
public string Path { get; set; } = string.Empty;
```

虚拟机配置文件的根目录，服务在其下创建最终虚拟机名称目录。

<a id="api-acf2f392706c19b8"></a>

##### ProcessorCount · 属性

```csharp
public int ProcessorCount { get; set; } = 4;
```

虚拟处理器数量，默认 4。

<a id="api-47f79f6d49e730d2"></a>

##### StoreNewDisksWithVm · 属性

```csharp
public bool StoreNewDisksWithVm { get; set; }
```

是否将新磁盘放入虚拟机目录下的 Virtual Hard Disks；不会移动已有磁盘。

<a id="api-c2cf91f153d225d5"></a>

##### SwitchId · 属性

```csharp
public Guid? SwitchId { get; set; }
```

要连接的虚拟交换机 GUID；null 表示不连接，Guid.Empty 无效。名称只用于显示。

<a id="api-7751c8354294cf7b"></a>

##### Version · 属性

```csharp
public string Version { get; set; } = "8.0";
```

请求的 Hyper-V 配置版本字符串，默认 8.0。

<a id="api-40a1364a0769a4d5"></a>

##### VhdPath · 属性

```csharp
public string VhdPath { get; set; } = string.Empty;
```

新建模式下为磁盘目录，可留空以使用虚拟机目录；已有磁盘模式下为磁盘文件路径。

<a id="api-89d6aac76f2bf99b"></a>

### `VmDiskStatus` 类

虚拟机磁盘容量及性能采样。 属性更改通过 PropertyChanged 通知订阅方，不持有界面对象。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmDiskStatus.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public PerformanceSampleInfo CapacitySample { get; set; } = new(PerformanceSampleState.NotSampled, default) `](#api-6ba36d3f4f2c7df6) | 最大容量的本次读取状态；无效时不得将 MaxSize 的默认值解释为零容量。 |
| [` public long CurrentSize { get; set; } `](#api-9b57f46a83e7dc72) | 文件介质为文件长度，物理盘为整盘容量，单位字节；不是文件系统实际分配空间或来宾已用空间，须结合 SizeSample 判断有效性。 |
| [` public string DiskType { get; set; } = string.Empty `](#api-bf5a35356486ec44) | 磁盘来源类型标识，例如 Physical 或 Virtual。 |
| [` public long MaxSize { get; set; } `](#api-08f3acb4ba675dac) | 磁盘最大或虚拟容量，单位为字节。 |
| [` public string Name { get; set; } = string.Empty `](#api-1a3388f8c84b9b0f) | 磁盘显示名称。 |
| [` public string Path { get; set; } = string.Empty `](#api-e7c8ceae75c3ecd2) | 磁盘文件或设备路径。 |
| [` public PerformanceSampleInfo PerformanceSample { get; set; } = new(PerformanceSampleState.NotSampled, default) `](#api-6f20a3e050ef3e48) | 本次读写速率读取的有效性与时间；无效时速率字段不更新。 |
| [` public string PnpDeviceId { get; set; } = string.Empty `](#api-05f4aaf7da99296a) | 物理磁盘的 PnP 设备标识；虚拟磁盘可为空。 |
| [` public long ReadBytesPerSecond { get; set; } `](#api-5a25304e4e87cfd2) | 当前读取速率，单位为字节每秒。 |
| [` public PerformanceSampleInfo SizeSample { get; set; } = new(PerformanceSampleState.NotSampled, default) `](#api-8bdde416b1722a93) | 当前文件占用大小的本次读取状态；无效时不得将 CurrentSize 的默认值解释为空磁盘。 |
| [` public double UsagePercentage { get; } `](#api-0905dd0f46b003d4) | CurrentSize 相对 MaxSize 的百分比；文件介质可能超过 100%，物理盘通常为 100%，均不是来宾空间使用率。 |
| [` public long WriteBytesPerSecond { get; set; } `](#api-c3a36050a09c84cc) | 当前写入速率，单位为字节每秒。 |

#### 详细说明

```csharp
public partial class VmDiskStatus : ObservableObject
```

#### 属性说明

<a id="api-6ba36d3f4f2c7df6"></a>

##### CapacitySample · 生成属性

```csharp
public PerformanceSampleInfo CapacitySample { get; set; } = new(PerformanceSampleState.NotSampled, default);
```

最大容量的本次读取状态；无效时不得将 MaxSize 的默认值解释为零容量。

<a id="api-9b57f46a83e7dc72"></a>

##### CurrentSize · 生成属性

```csharp
public long CurrentSize { get; set; }
```

文件介质为文件长度，物理盘为整盘容量，单位字节；不是文件系统实际分配空间或来宾已用空间，须结合 SizeSample 判断有效性。

<a id="api-bf5a35356486ec44"></a>

##### DiskType · 生成属性

```csharp
public string DiskType { get; set; } = string.Empty;
```

磁盘来源类型标识，例如 Physical 或 Virtual。

<a id="api-08f3acb4ba675dac"></a>

##### MaxSize · 生成属性

```csharp
public long MaxSize { get; set; }
```

磁盘最大或虚拟容量，单位为字节。

<a id="api-1a3388f8c84b9b0f"></a>

##### Name · 生成属性

```csharp
public string Name { get; set; } = string.Empty;
```

磁盘显示名称。

<a id="api-e7c8ceae75c3ecd2"></a>

##### Path · 生成属性

```csharp
public string Path { get; set; } = string.Empty;
```

磁盘文件或设备路径。

<a id="api-6f20a3e050ef3e48"></a>

##### PerformanceSample · 生成属性

```csharp
public PerformanceSampleInfo PerformanceSample { get; set; } = new(PerformanceSampleState.NotSampled, default);
```

本次读写速率读取的有效性与时间；无效时速率字段不更新。

<a id="api-05f4aaf7da99296a"></a>

##### PnpDeviceId · 属性

```csharp
public string PnpDeviceId { get; set; } = string.Empty;
```

物理磁盘的 PnP 设备标识；虚拟磁盘可为空。

<a id="api-5a25304e4e87cfd2"></a>

##### ReadBytesPerSecond · 生成属性

```csharp
public long ReadBytesPerSecond { get; set; }
```

当前读取速率，单位为字节每秒。

<a id="api-8bdde416b1722a93"></a>

##### SizeSample · 生成属性

```csharp
public PerformanceSampleInfo SizeSample { get; set; } = new(PerformanceSampleState.NotSampled, default);
```

当前文件占用大小的本次读取状态；无效时不得将 CurrentSize 的默认值解释为空磁盘。

<a id="api-0905dd0f46b003d4"></a>

##### UsagePercentage · 属性

```csharp
public double UsagePercentage { get; }
```

CurrentSize 相对 MaxSize 的百分比；文件介质可能超过 100%，物理盘通常为 100%，均不是来宾空间使用率。最大容量不大于零时返回 0，使用前须确认 SizeSample 和 CapacitySample 有效。

<a id="api-c3a36050a09c84cc"></a>

##### WriteBytesPerSecond · 生成属性

```csharp
public long WriteBytesPerSecond { get; set; }
```

当前写入速率，单位为字节每秒。

<a id="api-51ff6610dbb72a26"></a>

### `VmGpuAssignment` 类

虚拟机已分配 GPU 的可变查询模型；修改字段不会自动改变 Hyper-V 分配。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Assignment/VmGpuAssignment.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DriverVersion { get; set; } = string.Empty `](#api-3b878cfd024f3d31) | 匹配到的宿主显卡驱动版本。 |
| [` public string HostDeviceInstanceId { get; } = string.Empty `](#api-2223ecbf264e9be2) | 匹配到的宿主显卡 PnP 实例 ID；无法匹配时为空。 |
| [` public string Manu { get; set; } = string.Empty `](#api-8bfcd446d3c01f0a) | GPU 芯片商名称，用于厂商分类。 |
| [` public ulong? MemoryBytes { get; set; } `](#api-fff875313738c476) | 匹配到的宿主 GPU 专用显存容量，单位为字节；null 表示未知，0 是有效容量，不是虚拟机分区配额。 |
| [` public string MemoryDisplay { get; } `](#api-4157002f358cbb28) | 显存显示文本，以 MiB 换算并标为 MB；未知容量返回 N/A。 |
| [` public string Name { get; set; } = string.Empty `](#api-9b4932329b8bfff9) | 宿主 GPU 型号的显示名称。 |
| [` public string Vendor { get; set; } = string.Empty `](#api-ea475ce24867a673) | 板卡制造商的显示名称。 |

#### 详细说明

```csharp
public partial class VmGpuAssignment : ObservableObject
```

#### 属性说明

<a id="api-3b878cfd024f3d31"></a>

##### DriverVersion · 生成属性

```csharp
public string DriverVersion { get; set; } = string.Empty;
```

匹配到的宿主显卡驱动版本。

<a id="api-2223ecbf264e9be2"></a>

##### HostDeviceInstanceId · 属性

```csharp
public string HostDeviceInstanceId { get; } = string.Empty;
```

匹配到的宿主显卡 PnP 实例 ID；无法匹配时为空。

<a id="api-8bfcd446d3c01f0a"></a>

##### Manu · 生成属性

```csharp
public string Manu { get; set; } = string.Empty;
```

GPU 芯片商名称，用于厂商分类。

<a id="api-fff875313738c476"></a>

##### MemoryBytes · 生成属性

```csharp
public ulong? MemoryBytes { get; set; }
```

匹配到的宿主 GPU 专用显存容量，单位为字节；null 表示未知，0 是有效容量，不是虚拟机分区配额。

<a id="api-4157002f358cbb28"></a>

##### MemoryDisplay · 属性

```csharp
public string MemoryDisplay { get; }
```

显存显示文本，以 MiB 换算并标为 MB；未知容量返回 N/A。

<a id="api-9b4932329b8bfff9"></a>

##### Name · 生成属性

```csharp
public string Name { get; set; } = string.Empty;
```

宿主 GPU 型号的显示名称。

<a id="api-ea475ce24867a673"></a>

##### Vendor · 生成属性

```csharp
public string Vendor { get; set; } = string.Empty;
```

板卡制造商的显示名称。

<a id="api-4eb8163b9d96a53e"></a>

### `VmGroup` record 类

Hyper-V 分组及其成员快照。成员集合仍可修改；修改模型集合不会写回 Hyper-V。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroup.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmGroup(Guid Id, string Name, VmGroupKind Kind) `](#api-b98c8aaa4bdf885e) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public HashSet<Guid> ChildIds { get; } = [] `](#api-8b6b9ca0a0ae084a) | 直接属于该组的子分组标识集合。 |
| [` public Guid Id { get; init; } `](#api-a7349d348f3ba4e8) | 分组 GUID。 |
| [` public bool IsEmpty { get; } `](#api-4819035fa448ce06) | 分组中是否没有虚拟机或子分组。 |
| [` public VmGroupKind Kind { get; init; } `](#api-26c25d04e4844b75) | 分组允许包含的成员类别。 |
| [` public string Name { get; init; } `](#api-dc227f7a8acbdc41) | 分组名称。 |
| [` public HashSet<Guid> VmIds { get; } = [] `](#api-542edef03aaba4b3) | 直接属于该组的虚拟机标识集合。 |

#### 详细说明

```csharp
public sealed record VmGroup(Guid Id, string Name, VmGroupKind Kind)
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 分组 GUID。 |
| `Name` | 分组名称。 |
| `Kind` | 分组允许包含的成员类别。 |

#### 构造函数说明

<a id="api-b98c8aaa4bdf885e"></a>

##### VmGroup · 主构造器

```csharp
public VmGroup(Guid Id, string Name, VmGroupKind Kind);
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 分组 GUID。 |
| `Name` | 分组名称。 |
| `Kind` | 分组允许包含的成员类别。 |

#### 属性说明

<a id="api-8b6b9ca0a0ae084a"></a>

##### ChildIds · 属性

```csharp
public HashSet<Guid> ChildIds { get; } = [];
```

直接属于该组的子分组标识集合。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 直接属于该组的子分组标识集合。 |
| 取值与默认 | GUID 集合；修改集合仅改变本地快照。 |

<a id="api-a7349d348f3ba4e8"></a>

##### Id · record 属性

```csharp
public Guid Id { get; init; }
```

分组 GUID。

<a id="api-4819035fa448ce06"></a>

##### IsEmpty · 属性

```csharp
public bool IsEmpty { get; }
```

分组中是否没有虚拟机或子分组。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 分组中是否没有虚拟机或子分组。 |
| 取值与默认 | true：两类成员均为空；false：至少包含一项成员。 |

<a id="api-26c25d04e4844b75"></a>

##### Kind · record 属性

```csharp
public VmGroupKind Kind { get; init; }
```

分组允许包含的成员类别。

<a id="api-dc227f7a8acbdc41"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

分组名称。

<a id="api-542edef03aaba4b3"></a>

##### VmIds · 属性

```csharp
public HashSet<Guid> VmIds { get; } = [];
```

直接属于该组的虚拟机标识集合。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 直接属于该组的虚拟机标识集合。 |
| 取值与默认 | GUID 集合；修改集合仅改变本地快照。 |

<a id="api-7009c707c715a19c"></a>

### `VmGroupKind` 枚举

Hyper-V 原生分组允许包含的成员类别。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroup.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Groups = 1 `](#api-f4de7d676db07b69) | 包含其他分组的分组。 |
| [` VirtualMachines = 0 `](#api-ff8305f29c38859c) | 包含虚拟机的分组。 |

#### 详细说明

```csharp
public enum VmGroupKind : ushort
```

#### 枚举值说明

<a id="api-f4de7d676db07b69"></a>

##### Groups · 枚举值

```csharp
Groups = 1
```

包含其他分组的分组。

<a id="api-ff8305f29c38859c"></a>

##### VirtualMachines · 枚举值

```csharp
VirtualMachines = 0
```

包含虚拟机的分组。

<a id="api-de5bea964cb743a6"></a>

### `VmGroupMove` record 类

一次分组成员移动所需的源成员及原父组信息。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 修改 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroup.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmGroupMove(Guid Id, bool IsGroup, Guid? SourceParentId) `](#api-1845b1c7c38169da) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public Guid Id { get; init; } `](#api-1ffa759b77a4dddf) | 待移动虚拟机或分组的 GUID。 |
| [` public bool IsGroup { get; init; } `](#api-6541a71bba398174) | true 表示分组，false 表示虚拟机。 |
| [` public Guid? SourceParentId { get; init; } `](#api-69628f009e548226) | 原父组 GUID；null 表示没有指定原父组。 |

#### 详细说明

```csharp
public sealed record VmGroupMove(Guid Id, bool IsGroup, Guid? SourceParentId)
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 待移动虚拟机或分组的 GUID。 |
| `IsGroup` | true 表示分组，false 表示虚拟机。 |
| `SourceParentId` | 原父组 GUID；null 表示没有指定原父组。 |

#### 构造函数说明

<a id="api-1845b1c7c38169da"></a>

##### VmGroupMove · 主构造器

```csharp
public VmGroupMove(Guid Id, bool IsGroup, Guid? SourceParentId);
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 待移动虚拟机或分组的 GUID。 |
| `IsGroup` | true 表示分组，false 表示虚拟机。 |
| `SourceParentId` | 原父组 GUID；null 表示没有指定原父组。 |

#### 属性说明

<a id="api-1ffa759b77a4dddf"></a>

##### Id · record 属性

```csharp
public Guid Id { get; init; }
```

待移动虚拟机或分组的 GUID。

<a id="api-6541a71bba398174"></a>

##### IsGroup · record 属性

```csharp
public bool IsGroup { get; init; }
```

true 表示分组，false 表示虚拟机。

<a id="api-69628f009e548226"></a>

##### SourceParentId · record 属性

```csharp
public Guid? SourceParentId { get; init; }
```

原父组 GUID；null 表示没有指定原父组。

<a id="api-5ae62ce278562650"></a>

### `VmGroupSnapshot` 类

原生分组的成员关系构成图结构；同一虚拟机或分组可以属于多个父分组。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 分组；管理层面 / 宿主 / 修改 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroup.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmGroupSnapshot(IReadOnlyList<VmGroup> groups) `](#api-d448bb645b5a77fc) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<VmGroup> Groups { get; } = groups `](#api-26d88b3a5b3c9ca7) | 构造时传入的分组列表引用；与调用方共享列表及元素。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public bool CanContain(Guid parentId, Guid childId) `](#api-c520f8ea80146951) | 检查父组能否包含指定子组，包括成员类型和循环关系检查。 |
| [` public HashSet<Guid> GetVmIds(Guid groupId) `](#api-3bdacb77ef96fa5a) | 沿子分组收集所有虚拟机 GUID，去重且避免循环遍历。 |

#### 详细说明

```csharp
public sealed class VmGroupSnapshot(IReadOnlyList<VmGroup> groups)
```

| 参数 | 说明 |
| --- | --- |
| `groups` | 分组列表；保留此引用而非深复制，分组 GUID 应唯一。 |

#### 构造函数说明

<a id="api-d448bb645b5a77fc"></a>

##### VmGroupSnapshot · 主构造器

```csharp
public VmGroupSnapshot(IReadOnlyList<VmGroup> groups);
```

功能归属：管理层面 / 宿主 / 修改 / 分组；管理层面 / 宿主 / 查询 / 分组。

| 参数 | 说明 |
| --- | --- |
| `groups` | 分组列表；保留此引用而非深复制，分组 GUID 应唯一。 |

#### 属性说明

<a id="api-26d88b3a5b3c9ca7"></a>

##### Groups · 属性

```csharp
public IReadOnlyList<VmGroup> Groups { get; } = groups;
```

功能归属：管理层面 / 宿主 / 修改 / 分组；管理层面 / 宿主 / 查询 / 分组。

构造时传入的分组列表引用；与调用方共享列表及元素。

#### 方法说明

<a id="api-c520f8ea80146951"></a>

##### CanContain · 方法

```csharp
public bool CanContain(Guid parentId, Guid childId);
```

功能归属：管理层面 / 宿主 / 修改 / 分组。

检查父组能否包含指定子组，包括成员类型和循环关系检查。

| 参数 | 说明 |
| --- | --- |
| `parentId` | 候选父分组 GUID。 |
| `childId` | 候选子分组 GUID。 |

返回：两个组均存在、父组允许包含分组且不会形成循环时为 true。

<a id="api-3bdacb77ef96fa5a"></a>

##### GetVmIds · 方法

```csharp
public HashSet<Guid> GetVmIds(Guid groupId);
```

功能归属：管理层面 / 宿主 / 查询 / 分组。

沿子分组收集所有虚拟机 GUID，去重且避免循环遍历。

| 参数 | 说明 |
| --- | --- |
| `groupId` | 起始分组 GUID。 |

返回：新建的 GUID 集合；找不到起始分组时为空。

<a id="api-4a13983a582f9896"></a>

### `VmImportDiskPreview` 类

导入预览中的磁盘文件信息。该对象只描述扫描结果，不挂载或导入磁盘。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportModels.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public long ActualSize { get; init; } `](#api-1cb9b8a57667f69c) | 源文件实际大小，单位为字节。 |
| [` public string ActualSizeText { get; } `](#api-6b9cfab8fab0e854) | 实际文件大小显示文本；负值按 0 处理，以 1024 为换算基数。 |
| [` public string Controller { get; init; } = string.Empty `](#api-b9052c36241bb04c) | 控制器及槽位的显示描述；空值表示未取得。 |
| [` public bool Exists { get; init; } `](#api-dad40075ae8d4573) | 扫描时源磁盘文件是否存在。 |
| [` public string Format { get; init; } = string.Empty `](#api-a753ff7a9632959b) | 虚拟磁盘格式标识，例如 VHD 或 VHDX。 |
| [` public string KindText { get; } `](#api-227d4a0472eab5fd) | 磁盘格式与分配类型组合的显示文本。 |
| [` public string Name { get; init; } = string.Empty `](#api-7673d388f78b1b84) | 磁盘显示名称。 |
| [` public string? ParentPath { get; init; } `](#api-da79a26be33179f7) | 差分磁盘的父盘路径；未取得或非差分盘时为 null。 |
| [` public string SlotText { get; } `](#api-3c1c70435bd86e8f) | 控制器槽位显示文本；无信息时显示破折号。 |
| [` public string SourcePath { get; init; } = string.Empty `](#api-783651d659da1598) | 源磁盘文件路径。 |
| [` public string Type { get; init; } = string.Empty `](#api-1189fa7562d6c569) | 虚拟磁盘分配类型的显示描述。 |
| [` public ulong VirtualSize { get; init; } `](#api-0d11b8d96517a744) | 虚拟磁盘的标称容量，单位为字节。 |
| [` public string VirtualSizeText { get; } `](#api-4dcf53842f99ae91) | 虚拟容量显示文本，以 1024 为换算基数。 |

#### 详细说明

```csharp
public sealed class VmImportDiskPreview
```

#### 属性说明

<a id="api-1cb9b8a57667f69c"></a>

##### ActualSize · 属性

```csharp
public long ActualSize { get; init; }
```

源文件实际大小，单位为字节。

<a id="api-6b9cfab8fab0e854"></a>

##### ActualSizeText · 属性

```csharp
public string ActualSizeText { get; }
```

实际文件大小显示文本；负值按 0 处理，以 1024 为换算基数。

<a id="api-b9052c36241bb04c"></a>

##### Controller · 属性

```csharp
public string Controller { get; init; } = string.Empty;
```

控制器及槽位的显示描述；空值表示未取得。

<a id="api-dad40075ae8d4573"></a>

##### Exists · 属性

```csharp
public bool Exists { get; init; }
```

扫描时源磁盘文件是否存在。

<a id="api-a753ff7a9632959b"></a>

##### Format · 属性

```csharp
public string Format { get; init; } = string.Empty;
```

虚拟磁盘格式标识，例如 VHD 或 VHDX。

<a id="api-227d4a0472eab5fd"></a>

##### KindText · 属性

```csharp
public string KindText { get; }
```

磁盘格式与分配类型组合的显示文本。

<a id="api-7673d388f78b1b84"></a>

##### Name · 属性

```csharp
public string Name { get; init; } = string.Empty;
```

磁盘显示名称。

<a id="api-da79a26be33179f7"></a>

##### ParentPath · 属性

```csharp
public string? ParentPath { get; init; }
```

差分磁盘的父盘路径；未取得或非差分盘时为 null。

<a id="api-3c1c70435bd86e8f"></a>

##### SlotText · 属性

```csharp
public string SlotText { get; }
```

控制器槽位显示文本；无信息时显示破折号。

<a id="api-783651d659da1598"></a>

##### SourcePath · 属性

```csharp
public string SourcePath { get; init; } = string.Empty;
```

源磁盘文件路径。

<a id="api-1189fa7562d6c569"></a>

##### Type · 属性

```csharp
public string Type { get; init; } = string.Empty;
```

虚拟磁盘分配类型的显示描述。

<a id="api-0d11b8d96517a744"></a>

##### VirtualSize · 属性

```csharp
public ulong VirtualSize { get; init; }
```

虚拟磁盘的标称容量，单位为字节。

<a id="api-4dcf53842f99ae91"></a>

##### VirtualSizeText · 属性

```csharp
public string VirtualSizeText { get; }
```

虚拟容量显示文本，以 1024 为换算基数。

<a id="api-d2925feb598f9ae9"></a>

### `VmImportGuidMode` 枚举

导入时对虚拟机唯一身份的处理方式；由调用方明确选择。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportOptions.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` GenerateNew = 1 `](#api-9cb9ff7dc829de77) | 让 Hyper-V 生成新 GUID，即使源 GUID 没有冲突也生成；仅适用于复制导入。 |
| [` Preserve = 0 `](#api-7b021089782ff88d) | 保留源 GUID；宿主已存在同一身份时失败，不自动改为新建。 |

#### 详细说明

```csharp
public enum VmImportGuidMode
```

#### 枚举值说明

<a id="api-9cb9ff7dc829de77"></a>

##### GenerateNew · 枚举值

```csharp
GenerateNew = 1
```

让 Hyper-V 生成新 GUID，即使源 GUID 没有冲突也生成；仅适用于复制导入。

<a id="api-7b021089782ff88d"></a>

##### Preserve · 枚举值

```csharp
Preserve = 0
```

保留源 GUID；宿主已存在同一身份时失败，不自动改为新建。

<a id="api-7dca25c06b2f128b"></a>

### `VmImportOptions` 类

一台虚拟机的导入选择。传给 ImportAsync；Core 在开始导入前复制并校验这些值。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportOptions.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmImportOptions(VmImportGuidMode guidMode) `](#api-f4ec72bd2222866b) | 创建一次导入的选项，并明确保留原身份还是生成新身份。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? ConfigurationDirectory { get; init; } `](#api-9edf4bd6dd62c8ce) | 完整的配置目标目录。 |
| [` public string? DiskDirectory { get; init; } `](#api-1d5d6c1dd4f16efe) | 完整的磁盘目标目录。 |
| [` public VmImportGuidMode GuidMode { get; } `](#api-0f71e26193f52b57) | GUID 处理方式；保留时遇到冲突返回失败，绝不自动切换为生成新 GUID。 |
| [` public string? Name { get; init; } `](#api-7c0bc31e860b189b) | 导入后的名称。 |
| [` public IReadOnlyDictionary<Guid, Guid?> SwitchMappings { get; init; } =  new ReadOnlyDictionary<Guid, Guid?>(new Dictionary<Guid, Guid?>()) `](#api-a63603106f461248) | 源交换机 GUID 到目标交换机 GUID 的映射；值为 null 表示明确断开该源交换机对应的连接。 |

#### 详细说明

```csharp
public sealed class VmImportOptions
```

调用约定：未指定名称、目录和交换机映射时保留原有导入规则。GUID 策略必须在构造时明确传入。 目录是该虚拟机的完整目标目录，不是再追加虚拟机名称的父目录。使用原目录注册时不能指定目标目录。

示例：

复制为新身份，并将预览中列出的连接映射到指定交换机。名称和目录由上层选择，不由 Core 自动命名。



```csharp
using ExHyperV.Models;
public static class ImportOptionsExample
{
    public static VmImportOptions Create(VmImportPreview preview, string name,
        string configurationDirectory, string diskDirectory, Guid targetSwitchId) =>
        new(VmImportGuidMode.GenerateNew)
        {
            Name = name,
            ConfigurationDirectory = configurationDirectory,
            DiskDirectory = diskDirectory,
            SwitchMappings = preview.SourceSwitchIds.ToDictionary(id => id, _ => (Guid?)targetSwitchId)
        };
}
```

#### 构造函数说明

<a id="api-f4ec72bd2222866b"></a>

##### VmImportOptions · 构造器

```csharp
public VmImportOptions(VmImportGuidMode guidMode);
```

创建一次导入的选项，并明确保留原身份还是生成新身份。

| 参数 | 说明 |
| --- | --- |
| `guidMode` | GUID 策略；使用原目录注册时必须为 Preserve。 |

#### 属性说明

<a id="api-9edf4bd6dd62c8ce"></a>

##### ConfigurationDirectory · 属性

```csharp
public string? ConfigurationDirectory { get; init; }
```

完整的配置目标目录。null 使用“宿主默认配置目录/导入名称”；必须为绝对路径，不能覆盖有内容的目录。

<a id="api-1d5d6c1dd4f16efe"></a>

##### DiskDirectory · 属性

```csharp
public string? DiskDirectory { get; init; }
```

完整的磁盘目标目录。null 使用“宿主默认磁盘目录/导入名称”；宿主未设磁盘目录时使用配置目标目录。

<a id="api-0f71e26193f52b57"></a>

##### GuidMode · 属性

```csharp
public VmImportGuidMode GuidMode { get; }
```

GUID 处理方式；保留时遇到冲突返回失败，绝不自动切换为生成新 GUID。

<a id="api-7c0bc31e860b189b"></a>

##### Name · 属性

```csharp
public string? Name { get; init; }
```

导入后的名称。null 保留来源名称；空白名称无效，不自动生成名称。

<a id="api-a63603106f461248"></a>

##### SwitchMappings · 属性

```csharp
public IReadOnlyDictionary<Guid, Guid?> SwitchMappings { get; init; } =
        new ReadOnlyDictionary<Guid, Guid?>(new Dictionary<Guid, Guid?>());
```

源交换机 GUID 到目标交换机 GUID 的映射；值为 null 表示明确断开该源交换机对应的连接。

调用约定：源 GUID 可从 Preview.SourceSwitchIds 取得，目标 GUID 从宿主交换机查询取得。同一源交换机上的连接使用同一映射。 没有映射的连接沿用原规则：原交换机仍存在则保留，不存在则断开。明确指定的目标不存在时失败，不改接同名交换机。 调用开始时复制字典；调用方不能在复制期间并发修改它。Guid.Empty 不是有效的源或目标身份。

<a id="api-6d79ba6e0fb88a01"></a>

### `VmImportPlacementMode` 枚举

导入虚拟机时配置和磁盘的放置方式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` ExistingDirectory = 1 `](#api-daaba4d9b7c8a326) | 使用现有目录中的文件位置。 |
| [` HostDirectories = 0 `](#api-b2469fbe9f75382d) | 复制到 VmImportOptions 指定的目录；未指定时在宿主默认目录下以导入名称建立子目录。 |

#### 详细说明

```csharp
public enum VmImportPlacementMode
```

#### 枚举值说明

<a id="api-daaba4d9b7c8a326"></a>

##### ExistingDirectory · 枚举值

```csharp
ExistingDirectory = 1
```

使用现有目录中的文件位置。

<a id="api-b2469fbe9f75382d"></a>

##### HostDirectories · 枚举值

```csharp
HostDirectories = 0
```

复制到 VmImportOptions 指定的目录；未指定时在宿主默认目录下以导入名称建立子目录。

<a id="api-db92ad11e50deda7"></a>

### `VmImportPreview` 类

来源配置及导入检查结果。由 Core 创建；修改导入选择请使用 VmImportOptions，此对象不承担配置输入。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportModels.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<string> CompatibilityIssues { get; } = Array.Empty<string>() `](#api-0b60069a33995733) | 来源文件的兼容性问题。 |
| [` public string ConfigSummary { get; } `](#api-b0665e1d363ed3bf) | 处理器、启动内存和各磁盘容量的组合摘要。 |
| [` public string ConfigurationVersion { get; init; } = string.Empty `](#api-fca5a5aac06e6c48) | 源虚拟机配置版本。 |
| [` public DateTime Created { get; init; } `](#api-a0c48bd5db21f159) | 源配置记录的创建时间；DateTime.MinValue 表示未知。 |
| [` public string CreatedText { get; } `](#api-feb27bf61ce4f58f) | 按当前界面区域格式化的创建时间；未知时显示破折号。 |
| [` public string DiskSummary { get; } `](#api-ea469193d0a26909) | 预览中的磁盘数量文本。 |
| [` public IReadOnlyList<VmImportDiskPreview> Disks { get; } = Array.Empty<VmImportDiskPreview>() `](#api-aa559a0adf54a465) | 源磁盘的只读预览集合。 |
| [` public bool? DynamicMemory { get; init; } `](#api-e0330616bd943f69) | 是否启用动态内存；null 表示未读取到。 |
| [` public int Generation { get; init; } `](#api-efefb66075ea1792) | 虚拟机代数。 |
| [` public string GenerationText { get; } `](#api-e2424a449fc5da2a) | 虚拟机代数的本地化显示文本。 |
| [` public string GuidText { get; } `](#api-93b07372eef74dae) | 源 GUID 的带连字符显示文本；新生成的身份在导入后通过 VmImportResult.VmId 取得。 |
| [` public bool? HasSavedState { get; init; } `](#api-299f1e0e2398fff2) | 源配置是否包含保存状态；null 表示未读取到。 |
| [` public ulong? MaximumMemoryMiB { get; init; } `](#api-ec0c0804e3554e97) | 动态内存上限，单位为 MiB；null 表示未读取到。 |
| [` public string MemoryText { get; } `](#api-5fcba828aa1b15b6) | 内存摘要；动态模式及上下限均已取得时显示范围，其余只显示启动内存。 |
| [` public ulong? MinimumMemoryMiB { get; init; } `](#api-b9b794ca181ed43f) | 动态内存下限，单位为 MiB；null 表示未读取到。 |
| [` public string Name { get; init; } = string.Empty `](#api-1e31872de6f8cfb1) | 来源配置中的名称；调用方可以通过 VmImportOptions.Name 指定导入后的名称。 |
| [` public Guid OriginalGuid { get; init; } `](#api-99bc2642b46f2929) | 源配置中的虚拟机 GUID；无法取得时为 Guid.Empty。 |
| [` public string OsType { get; init; } = "Windows" `](#api-5768f53331db46c9) | 源配置 HostOnlyItems 中的来宾系统类型；缺失或未知时为 Windows。 |
| [` public int ProcessorCount { get; init; } `](#api-b5dbc527a386e335) | 源配置的虚拟处理器数量。 |
| [` public string ProcessorMemorySummary { get; } `](#api-35888d64e5bbeb8a) | 处理器数量与启动内存的组合摘要。 |
| [` public string SavedStateText { get; } `](#api-8863cc2373dbb529) | 保存状态的本地化文本；未读取到时显示未知。 |
| [` public IReadOnlyList<Guid> SourceSwitchIds { get; } = Array.Empty<Guid>() `](#api-5bb1ec86a58bd6a0) | 主配置中识别到的源交换机 GUID，供 VmImportOptions.SwitchMappings 使用；未连接或未识别的记录不产生 GUID。 |
| [` public ulong StartupMemoryMiB { get; init; } `](#api-c2bce5ef8515b8e1) | 启动内存，单位为 MiB（1024&#178; 字节）。 |
| [` public string StartupMemoryText { get; } `](#api-046db5d20742b713) | 启动内存的本地化显示文本。 |

#### 详细说明

```csharp
public sealed class VmImportPreview
```

#### 属性说明

<a id="api-0b60069a33995733"></a>

##### CompatibilityIssues · 属性

```csharp
public IReadOnlyList<string> CompatibilityIssues { get; } = Array.Empty<string>();
```

来源文件的兼容性问题。目标目录、GUID 和网络映射在提交导入选项时另外校验。

<a id="api-b0665e1d363ed3bf"></a>

##### ConfigSummary · 属性

```csharp
public string ConfigSummary { get; }
```

处理器、启动内存和各磁盘容量的组合摘要。

<a id="api-fca5a5aac06e6c48"></a>

##### ConfigurationVersion · 属性

```csharp
public string ConfigurationVersion { get; init; } = string.Empty;
```

源虚拟机配置版本。

<a id="api-a0c48bd5db21f159"></a>

##### Created · 属性

```csharp
public DateTime Created { get; init; }
```

源配置记录的创建时间；DateTime.MinValue 表示未知。

<a id="api-feb27bf61ce4f58f"></a>

##### CreatedText · 属性

```csharp
public string CreatedText { get; }
```

按当前界面区域格式化的创建时间；未知时显示破折号。

<a id="api-ea469193d0a26909"></a>

##### DiskSummary · 属性

```csharp
public string DiskSummary { get; }
```

预览中的磁盘数量文本。

<a id="api-aa559a0adf54a465"></a>

##### Disks · 属性

```csharp
public IReadOnlyList<VmImportDiskPreview> Disks { get; } = Array.Empty<VmImportDiskPreview>();
```

源磁盘的只读预览集合。

<a id="api-e0330616bd943f69"></a>

##### DynamicMemory · 属性

```csharp
public bool? DynamicMemory { get; init; }
```

是否启用动态内存；null 表示未读取到。当前导入解析器不读取此项。

<a id="api-efefb66075ea1792"></a>

##### Generation · 属性

```csharp
public int Generation { get; init; }
```

虚拟机代数。

<a id="api-e2424a449fc5da2a"></a>

##### GenerationText · 属性

```csharp
public string GenerationText { get; }
```

虚拟机代数的本地化显示文本。

<a id="api-93b07372eef74dae"></a>

##### GuidText · 属性

```csharp
public string GuidText { get; }
```

源 GUID 的带连字符显示文本；新生成的身份在导入后通过 VmImportResult.VmId 取得。

<a id="api-299f1e0e2398fff2"></a>

##### HasSavedState · 属性

```csharp
public bool? HasSavedState { get; init; }
```

源配置是否包含保存状态；null 表示未读取到。当前导入解析器不读取此项。

<a id="api-ec0c0804e3554e97"></a>

##### MaximumMemoryMiB · 属性

```csharp
public ulong? MaximumMemoryMiB { get; init; }
```

动态内存上限，单位为 MiB；null 表示未读取到。当前导入解析器不读取此项。

<a id="api-5fcba828aa1b15b6"></a>

##### MemoryText · 属性

```csharp
public string MemoryText { get; }
```

内存摘要；动态模式及上下限均已取得时显示范围，其余只显示启动内存。

<a id="api-b9b794ca181ed43f"></a>

##### MinimumMemoryMiB · 属性

```csharp
public ulong? MinimumMemoryMiB { get; init; }
```

动态内存下限，单位为 MiB；null 表示未读取到。当前导入解析器不读取此项。

<a id="api-1e31872de6f8cfb1"></a>

##### Name · 属性

```csharp
public string Name { get; init; } = string.Empty;
```

来源配置中的名称；调用方可以通过 VmImportOptions.Name 指定导入后的名称。

<a id="api-99bc2642b46f2929"></a>

##### OriginalGuid · 属性

```csharp
public Guid OriginalGuid { get; init; }
```

源配置中的虚拟机 GUID；无法取得时为 Guid.Empty。

<a id="api-5768f53331db46c9"></a>

##### OsType · 属性

```csharp
public string OsType { get; init; } = "Windows";
```

源配置 HostOnlyItems 中的来宾系统类型；缺失或未知时为 Windows。

<a id="api-b5dbc527a386e335"></a>

##### ProcessorCount · 属性

```csharp
public int ProcessorCount { get; init; }
```

源配置的虚拟处理器数量。

<a id="api-35888d64e5bbeb8a"></a>

##### ProcessorMemorySummary · 属性

```csharp
public string ProcessorMemorySummary { get; }
```

处理器数量与启动内存的组合摘要。

<a id="api-8863cc2373dbb529"></a>

##### SavedStateText · 属性

```csharp
public string SavedStateText { get; }
```

保存状态的本地化文本；未读取到时显示未知。

<a id="api-5bb1ec86a58bd6a0"></a>

##### SourceSwitchIds · 属性

```csharp
public IReadOnlyList<Guid> SourceSwitchIds { get; } = Array.Empty<Guid>();
```

主配置中识别到的源交换机 GUID，供 VmImportOptions.SwitchMappings 使用；未连接或未识别的记录不产生 GUID。

<a id="api-c2bce5ef8515b8e1"></a>

##### StartupMemoryMiB · 属性

```csharp
public ulong StartupMemoryMiB { get; init; }
```

启动内存，单位为 MiB（1024&#178; 字节）。

<a id="api-046db5d20742b713"></a>

##### StartupMemoryText · 属性

```csharp
public string StartupMemoryText { get; }
```

启动内存的本地化显示文本。

<a id="api-e60536341907c3b7"></a>

### `VmImportSourceKind` 枚举

虚拟机导入来源的容器形式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Folder = 0 `](#api-1ae8ce5e9ff21e3c) | 从已有目录导入。 |
| [` Zip = 1 `](#api-d327bb226b74199e) | 从 ZIP 压缩包导入。 |

#### 详细说明

```csharp
public enum VmImportSourceKind
```

#### 枚举值说明

<a id="api-1ae8ce5e9ff21e3c"></a>

##### Folder · 枚举值

```csharp
Folder = 0
```

从已有目录导入。

<a id="api-d327bb226b74199e"></a>

##### Zip · 枚举值

```csharp
Zip = 1
```

从 ZIP 压缩包导入。

<a id="api-bcb2ffa2f3865a82"></a>

### `VmInstance` 类

虚拟机清单及详情的可变模型；包含配置、查询时的状态和可独立刷新的性能样本。 设备集合支持变更通知，是否已加载以及样本是否有效须按对应字段判断。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmInstance.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmInstance() `](#api-c354f64ecc36c3c9) | 创建尚未加载宿主数据的虚拟机模型。 |
| [` public VmInstance(Guid id, string name) `](#api-290574c9e8a82b45) | 创建带虚拟机身份的模型；不会读取 Hyper-V。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ObservableCollection<VmGpuAssignment> AssignedGpus { get; } = new() `](#api-f7bb00c795c6eeb3) | 已分配 GPU 适配器的可变通知集合。 |
| [` public double AssignedMemoryGiB { get; set; } `](#api-d394276ba471ab82) | 当前已分配内存，以 GiB 表示；本次读取是否有效见 AssignedMemorySample。 |
| [` public PerformanceSampleInfo? AssignedMemorySample { get; set; } `](#api-1727553a2fd34a47) | 已分配内存的本次读取状态；未采集时为 null。 |
| [` public ObservableCollection<BootOrderItem> BootOrderItems { get; } = new() `](#api-6ba39b1b26b50df9) | 启动顺序的可变通知集合。 |
| [` public int CpuCount { get; set; } `](#api-49c8da356f3c31c7) | 配置的虚拟处理器数量。 |
| [` public double DemandMemoryGiB { get; set; } `](#api-381102ba45b6b7b9) | 来宾报告的内存需求，以 GiB 表示；本次读取是否有效见 DemandMemorySample。 |
| [` public PerformanceSampleInfo? DemandMemorySample { get; set; } `](#api-f2cf44e13fdc1e66) | 内存需求的本次读取状态；普通清单不采集该指标，未采集时为 null。 |
| [` public ObservableCollection<VmDiskStatus> Disks { get; } = new() `](#api-57c5c2bd90ee8e4d) | 磁盘容量和速率的可变通知集合。 |
| [` public int Generation { get; set; } `](#api-b63a053ff4b529d8) | 虚拟机代数，1 或 2；未取得或无法识别时为 0。 |
| [` public string? GpuName { get; set; } `](#api-23f9e184629f302e) | 概要显示的 GPU 名称；未取得时可为 null。 |
| [` public bool HasGpu { get; } `](#api-ae0d9a5a576e3aa1) | 是否分配了 GPU；优先采用已确认的 HasGpuAssignment，未提供时以名称或分配集合推断。 |
| [` public bool? HasGpuAssignment { get; set; } `](#api-b71eac39bdc74dd4) | 是否确认分配了 GPU 分区；null 表示本次没有取得可靠分配清单。 |
| [` public Guid Id { get; init; } `](#api-60ebdd5a647f3973) | 虚拟机 GUID。 |
| [` public string IpAddress { get; set; } = string.Empty `](#api-3b0887682a9d8d62) | 按 Core 地址优先规则选出的 IPv4 地址；未取得时为空字符串。 |
| [` public bool IsGpuActive { get; set; } `](#api-03e74b12eabf46f3) | 查询结果中的 GPU 活动标记。 |
| [` public bool IsPoweredOff { get; } `](#api-10b0c8943b385721) | 是否已完全关机；保存、挂起或过渡状态均不属于完全关机。 |
| [` public bool IsRunning { get; } `](#api-d0efd84534f9e411) | 是否处于运行或选定的启动、关机、保存等过渡状态；暂停、已保存和已关机均为 false。 |
| [` public string MacAddress { get; set; } = "00:00:00:00:00:00" `](#api-e2c123ca04fda25c) | 用于概要显示的网卡 MAC 地址；未填充时为全零地址。 |
| [` public double MemoryGiB { get; set; } `](#api-90465da0eb5e0764) | 配置的启动内存，以 GiB（1024&#179; 字节）表示。 |
| [` public VmMemorySettings? MemorySettings { get; set; } `](#api-85bc544bc195eb0a) | 内存详细设置；尚未加载或查询不可用时为 null。 |
| [` public VmMmioSettings? MmioSettings { get; set; } `](#api-b64f3f5109df52ad) | MMIO 详细设置；尚未加载或查询不可用时为 null。 |
| [` public string Name { get; set; } = string.Empty `](#api-383ee85245f5e11f) | 虚拟机名称。 |
| [` public ObservableCollection<VmNetworkAdapter> NetworkAdapters { get; } = new() `](#api-263238b0642f7eca) | 当前虚拟网卡的可变通知集合。 |
| [` public string OsType { get; set; } = "Windows" `](#api-5967fc7516b523ea) | HostOnlyItems 保存的操作系统分类，缺少或无法识别时为 Windows；不是来宾上报的实际操作系统。 |
| [` public VmProcessorSettings? Processor { get; set; } `](#api-2f2d6c350073c37c) | 处理器详细设置；尚未加载或查询不可用时为 null。 |
| [` public TimeSpan RawUptime { get; set; } `](#api-8eb9034cc2978fef) | VM 已运行时长（来自 Msvm\_SummaryInformation.UpTime） |
| [` public VmState State { get; } `](#api-1e67a9d09b1b2a38) | 当前运行状态；业务判断不依赖本地化文本或后端状态码。 |
| [` public string StateText { get; set; } = string.Empty `](#api-c5c3425f27a94752) | 当前资源语言的状态显示文本；程序判断使用 State。 |
| [` public ObservableCollection<VmStorageAttachment> StorageItems { get; } = new() `](#api-2136b1eef14d153c) | 当前存储连接的可变通知集合。 |
| [` public double TotalDiskSizeGiB { get; set; } `](#api-62961cd1daf67c92) | Disks 中 MaxSize 的合计，单位 GiB，包含光驱镜像；容量缺测项的默认值仍参与计算，不能单凭总数判断完整容量。 |
| [` public string Version { get; set; } = "0.0" `](#api-3f80a0b157143130) | Hyper-V 配置版本字符串，未填充时为 0.0。 |

#### 详细说明

```csharp
public class VmInstance
```

调用约定：这是可变查询模型，不是持续连接或事务对象。修改其属性或集合不会自动提交 Hyper-V 配置；调用方负责刷新、服务提交和绑定线程调度。

#### 构造函数说明

<a id="api-c354f64ecc36c3c9"></a>

##### VmInstance · 构造器

```csharp
public VmInstance();
```

创建尚未加载宿主数据的虚拟机模型。

<a id="api-290574c9e8a82b45"></a>

##### VmInstance · 构造器

```csharp
public VmInstance(Guid id, string name);
```

创建带虚拟机身份的模型；不会读取 Hyper-V。

| 参数 | 说明 |
| --- | --- |
| `id` | 虚拟机 GUID。 |
| `name` | 虚拟机名称。 |

#### 属性说明

<a id="api-f7bb00c795c6eeb3"></a>

##### AssignedGpus · 属性

```csharp
public ObservableCollection<VmGpuAssignment> AssignedGpus { get; } = new();
```

已分配 GPU 适配器的可变通知集合。

<a id="api-d394276ba471ab82"></a>

##### AssignedMemoryGiB · 属性

```csharp
public double AssignedMemoryGiB { get; set; }
```

当前已分配内存，以 GiB 表示；本次读取是否有效见 AssignedMemorySample。

<a id="api-1727553a2fd34a47"></a>

##### AssignedMemorySample · 属性

```csharp
public PerformanceSampleInfo? AssignedMemorySample { get; set; }
```

已分配内存的本次读取状态；未采集时为 null。

<a id="api-6ba39b1b26b50df9"></a>

##### BootOrderItems · 属性

```csharp
public ObservableCollection<BootOrderItem> BootOrderItems { get; } = new();
```

启动顺序的可变通知集合。

<a id="api-49c8da356f3c31c7"></a>

##### CpuCount · 属性

```csharp
public int CpuCount { get; set; }
```

配置的虚拟处理器数量。

<a id="api-381102ba45b6b7b9"></a>

##### DemandMemoryGiB · 属性

```csharp
public double DemandMemoryGiB { get; set; }
```

来宾报告的内存需求，以 GiB 表示；本次读取是否有效见 DemandMemorySample。

<a id="api-f2cf44e13fdc1e66"></a>

##### DemandMemorySample · 属性

```csharp
public PerformanceSampleInfo? DemandMemorySample { get; set; }
```

内存需求的本次读取状态；普通清单不采集该指标，未采集时为 null。

<a id="api-57c5c2bd90ee8e4d"></a>

##### Disks · 属性

```csharp
public ObservableCollection<VmDiskStatus> Disks { get; } = new();
```

磁盘容量和速率的可变通知集合。

<a id="api-b63a053ff4b529d8"></a>

##### Generation · 属性

```csharp
public int Generation { get; set; }
```

虚拟机代数，1 或 2；未取得或无法识别时为 0。

<a id="api-23f9e184629f302e"></a>

##### GpuName · 属性

```csharp
public string? GpuName { get; set; }
```

概要显示的 GPU 名称；未取得时可为 null。

<a id="api-ae0d9a5a576e3aa1"></a>

##### HasGpu · 属性

```csharp
public bool HasGpu { get; }
```

是否分配了 GPU；优先采用已确认的 HasGpuAssignment，未提供时以名称或分配集合推断。

<a id="api-b71eac39bdc74dd4"></a>

##### HasGpuAssignment · 属性

```csharp
public bool? HasGpuAssignment { get; set; }
```

是否确认分配了 GPU 分区；null 表示本次没有取得可靠分配清单。此状态独立于设备显示名称是否可读。

<a id="api-60ebdd5a647f3973"></a>

##### Id · 属性

```csharp
public Guid Id { get; init; }
```

虚拟机 GUID。

<a id="api-3b0887682a9d8d62"></a>

##### IpAddress · 属性

```csharp
public string IpAddress { get; set; } = string.Empty;
```

按 Core 地址优先规则选出的 IPv4 地址；未取得时为空字符串。

<a id="api-03e74b12eabf46f3"></a>

##### IsGpuActive · 属性

```csharp
public bool IsGpuActive { get; set; }
```

查询结果中的 GPU 活动标记。

<a id="api-10b0c8943b385721"></a>

##### IsPoweredOff · 属性

```csharp
public bool IsPoweredOff { get; }
```

是否已完全关机；保存、挂起或过渡状态均不属于完全关机。

<a id="api-d0efd84534f9e411"></a>

##### IsRunning · 属性

```csharp
public bool IsRunning { get; }
```

是否处于运行或选定的启动、关机、保存等过渡状态；暂停、已保存和已关机均为 false。

<a id="api-e2c123ca04fda25c"></a>

##### MacAddress · 属性

```csharp
public string MacAddress { get; set; } = "00:00:00:00:00:00";
```

用于概要显示的网卡 MAC 地址；未填充时为全零地址。

<a id="api-90465da0eb5e0764"></a>

##### MemoryGiB · 属性

```csharp
public double MemoryGiB { get; set; }
```

配置的启动内存，以 GiB（1024&#179; 字节）表示。

<a id="api-85bc544bc195eb0a"></a>

##### MemorySettings · 属性

```csharp
public VmMemorySettings? MemorySettings { get; set; }
```

内存详细设置；尚未加载或查询不可用时为 null。

<a id="api-b64f3f5109df52ad"></a>

##### MmioSettings · 属性

```csharp
public VmMmioSettings? MmioSettings { get; set; }
```

MMIO 详细设置；尚未加载或查询不可用时为 null。

<a id="api-383ee85245f5e11f"></a>

##### Name · 属性

```csharp
public string Name { get; set; } = string.Empty;
```

虚拟机名称。

<a id="api-263238b0642f7eca"></a>

##### NetworkAdapters · 属性

```csharp
public ObservableCollection<VmNetworkAdapter> NetworkAdapters { get; } = new();
```

当前虚拟网卡的可变通知集合。

<a id="api-5967fc7516b523ea"></a>

##### OsType · 属性

```csharp
public string OsType { get; set; } = "Windows";
```

HostOnlyItems 保存的操作系统分类，缺少或无法识别时为 Windows；不是来宾上报的实际操作系统。

<a id="api-2f2d6c350073c37c"></a>

##### Processor · 属性

```csharp
public VmProcessorSettings? Processor { get; set; }
```

处理器详细设置；尚未加载或查询不可用时为 null。

<a id="api-8eb9034cc2978fef"></a>

##### RawUptime · 属性

```csharp
public TimeSpan RawUptime { get; set; }
```

VM 已运行时长（来自 Msvm_SummaryInformation.UpTime）

<a id="api-1e67a9d09b1b2a38"></a>

##### State · 属性

```csharp
public VmState State { get; }
```

当前运行状态；业务判断不依赖本地化文本或后端状态码。

<a id="api-c5c3425f27a94752"></a>

##### StateText · 属性

```csharp
public string StateText { get; set; } = string.Empty;
```

当前资源语言的状态显示文本；程序判断使用 State。

<a id="api-2136b1eef14d153c"></a>

##### StorageItems · 属性

```csharp
public ObservableCollection<VmStorageAttachment> StorageItems { get; } = new();
```

当前存储连接的可变通知集合。

<a id="api-62961cd1daf67c92"></a>

##### TotalDiskSizeGiB · 属性

```csharp
public double TotalDiskSizeGiB { get; set; }
```

Disks 中 MaxSize 的合计，单位 GiB，包含光驱镜像；容量缺测项的默认值仍参与计算，不能单凭总数判断完整容量。

<a id="api-3f80a0b157143130"></a>

##### Version · 属性

```csharp
public string Version { get; set; } = "0.0";
```

Hyper-V 配置版本字符串，未填充时为 0.0。

<a id="api-1245af70d4ef31dd"></a>

### `VmMemorySettings` 类

虚拟机内存的读取结果和修改请求值；通过 changedProperties 明确选择要提交的属性。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 内存 |
| 源文件 | ` Management/VirtualMachines/Memory/VmMemorySettings.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmMemorySettings() `](#api-579606f4b6f23f0b) | 创建内存设置模型，先提供标准页、大页、巨页三个选项；查询服务随后按实际宿主能力收窄。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public List<PageSizeItem> AvailablePageSizes { get; } = new() `](#api-af02e65849c242b0) | 根据宿主能力构造的可变页面大小选项列表。 |
| [` public byte? BackingPageSize { get; set; } `](#api-6baa115cb5f9e956) | 统一页面大小编码：0 标准页、1 大页、2 巨页；null 表示未取得值。 |
| [` public byte? BackingType { get; set; } `](#api-f8495c17b80ce6ea) | 实验性。 |
| [` public int Buffer { get; set; } `](#api-510742f09596571e) | 动态内存缓冲比例，单位为百分比。 |
| [` public bool? CxlEnabled { get; set; } `](#api-ee38e75812fb62b8) | 实验性 CXL 开关；已知部分 VMMS 版本在保存时可能崩溃。 |
| [` public uint? DynMemOperationAlignment { get; set; } `](#api-e43fd6f77436cca0) | 实验性。 |
| [` public bool DynamicMemoryEnabled { get; set; } `](#api-442a246088b76be7) | 是否启用动态内存。 |
| [` public bool? EnableColdHint { get; set; } `](#api-ac02c84608badb64) | 实验性。 |
| [` public bool? EnableEpf { get; set; } `](#api-aa368f622d1a2d34) | 实验性。 |
| [` public bool? EnableGpaPinning { get; set; } `](#api-18088a3375d5a0fc) | 实验性。 |
| [` public bool? EnableHotHint { get; set; } `](#api-d4750de9d4893c87) | 实验性。 |
| [` public bool? EnablePrivateCompressionStore { get; set; } `](#api-01c5aefe50932ed4) | 实验性。 |
| [` public ulong? MaxMemoryBlocksPerNumaNode { get; set; } `](#api-5bad0bbc763db8ab) | 实验性。 |
| [` public long Maximum { get; set; } `](#api-c4e18b2e657d1418) | 动态内存最大值，单位为 MiB。 |
| [` public byte? MemoryAccessTrackingPolicy { get; set; } `](#api-296adc74f1e302b9) | 实验性。 |
| [` public byte? MemoryAccessTrackingState { get; set; } `](#api-86d3a01cfd0ef1f0) | 实验性。 |
| [` public byte? MemoryEncryptionPolicy { get; set; } `](#api-d43fe310858a4b72) | 实验性。 |
| [` public long Minimum { get; set; } `](#api-79e84c05bd14bdec) | 动态内存最小值，单位为 MiB。 |
| [` public bool? SgxEnabled { get; set; } `](#api-137ad4fb24c64154) | 实验性。 |
| [` public string? SgxLaunchControlDefault { get; set; } `](#api-d5562561647805a6) | 实验性。 |
| [` public uint? SgxLaunchControlMode { get; set; } `](#api-5e378c729c2b356a) | 实验性。 |
| [` public double? SgxSize { get; set; } `](#api-487c14ee9e31a804) | 实验性。 |
| [` public long Startup { get; set; } `](#api-79f3ba7e128a5f73) | 启动内存，单位为 MiB（1024&#178; 字节）。 |
| [` public bool SupportsBackingPageSize { get; } `](#api-38ac08c146f6798f) | 宿主是否提供 BackingPageSize 属性；旧宿主通过 HugePagesEnabled 映射大页和巨页。 |
| [` public uint Weight { get; set; } `](#api-96864048d7a53d89) | 内存竞争时的原始权重，对应 Hyper-V Weight；不进行归一化换算，读取缺少值时沿用 5000。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public VmMemorySettings Clone() `](#api-61f2f7f6f95603b2) | 复制当前值为独立模型，不复制属性变化事件订阅者。 |
| [` public void ConfigurePageSizeSupport(bool supportsBackingPageSize) `](#api-0718401cd1847e63) | 更新本地页面大小选项，并发送 AvailablePageSizes 属性变化通知；不修改 Hyper-V。 |
| [` public void Restore(VmMemorySettings other) `](#api-94d0f03236a10a60) | 通过属性设置器恢复本地模型并触发相关通知；不提交 Hyper-V 配置。 |

#### 详细说明

```csharp
public partial class VmMemorySettings : ObservableObject
```

调用约定：标注“实验性”的字段继续支持读写，不需要额外的启用参数。已知编码、现有联动和未验证范围统一说明于 `ExHyperV.Services.VmMemoryFields` 的对应字段。



可空扩展属性未取得值时为 null；null 不表示关闭，也不是“重置默认”的请求。修改时忽略值为 null 的选择；非空选择对应的宿主属性不存在时，在任何修改前返回 Unsupported。修改前通过服务读取配置；只提交本次选中的字段。Clone/Restore 用于本地快照，不提交宿主。

#### 构造函数说明

<a id="api-579606f4b6f23f0b"></a>

##### VmMemorySettings · 构造器

```csharp
public VmMemorySettings();
```

创建内存设置模型，先提供标准页、大页、巨页三个选项；查询服务随后按实际宿主能力收窄。

#### 属性说明

<a id="api-af02e65849c242b0"></a>

##### AvailablePageSizes · 属性

```csharp
public List<PageSizeItem> AvailablePageSizes { get; } = new();
```

根据宿主能力构造的可变页面大小选项列表。

<a id="api-6baa115cb5f9e956"></a>

##### BackingPageSize · 生成属性

```csharp
public byte? BackingPageSize { get; set; }
```

统一页面大小编码：0 标准页、1 大页、2 巨页；null 表示未取得值。

<a id="api-f8495c17b80ce6ea"></a>

##### BackingType · 生成属性

```csharp
public byte? BackingType { get; set; }
```

实验性。选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。 |
| 单位与表示 | 类型编码，不是内存大小。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.BackingType`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.BackingType`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.BackingType；提交方式：明确选择字段。将 nameof(VmMemorySettings.BackingType) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.BackingType`。 |

<a id="api-510742f09596571e"></a>

##### Buffer · 生成属性

```csharp
public int Buffer { get; set; }
```

动态内存缓冲比例，单位为百分比。

<a id="api-ee38e75812fb62b8"></a>

##### CxlEnabled · 生成属性

```csharp
public bool? CxlEnabled { get; set; }
```

实验性 CXL 开关；已知部分 VMMS 版本在保存时可能崩溃。Core 不按研究风险拦截，由调用方提示，详见 VmMemoryFields.CxlEnabled。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 虚拟机内存配置中的 CXL 开关。此入口只修改 CxlEnabled，不枚举、分配或直通具体 CXL 设备。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.CxlEnabled`；该处为本字段的完整说明。 |
| 适用条件 | 保留显式写入，不以试写探测支持，也不按研究风险版本拦截。调用方应先展示 `ExHyperV.Services.VmMemoryFields.CxlEnabled` 中的服务崩溃风险、版本范围及关闭操作可能失败的限制。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.CxlEnabled；提交方式：明确选择字段。将 nameof(VmMemorySettings.CxlEnabled) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.CxlEnabled`。 |

<a id="api-e43fd6f77436cca0"></a>

##### DynMemOperationAlignment · 生成属性

```csharp
public uint? DynMemOperationAlignment { get; set; }
```

实验性。动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。 |
| 单位与表示 | 对齐选项编码，不是直接以字节传入的对齐量。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.DynMemOperationAlignment；提交方式：明确选择字段。将 nameof(VmMemorySettings.DynMemOperationAlignment) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment`。 |

<a id="api-442a246088b76be7"></a>

##### DynamicMemoryEnabled · 生成属性

```csharp
public bool DynamicMemoryEnabled { get; set; }
```

是否启用动态内存。

<a id="api-ac02c84608badb64"></a>

##### EnableColdHint · 生成属性

```csharp
public bool? EnableColdHint { get; set; }
```

实验性。控制内存配置中的冷提示开关。此入口不负责提交某段内存的冷热信息。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 控制内存配置中的冷提示开关。此入口不负责提交某段内存的冷热信息。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.EnableColdHint`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.EnableColdHint`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.EnableColdHint；提交方式：明确选择字段。将 nameof(VmMemorySettings.EnableColdHint) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.EnableColdHint`。 |

<a id="api-aa368f622d1a2d34"></a>

##### EnableEpf · 生成属性

```csharp
public bool? EnableEpf { get; set; }
```

实验性。保留 Hyper-V 的 EnableEpf 配置开关。缩写全称及功能用途尚未确认，因此不将它解释成某种已知内存优化。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 保留 Hyper-V 的 EnableEpf 配置开关。缩写全称及功能用途尚未确认，因此不将它解释成某种已知内存优化。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.EnableEpf`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.EnableEpf`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.EnableEpf；提交方式：明确选择字段。将 nameof(VmMemorySettings.EnableEpf) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.EnableEpf`。 |

<a id="api-18088a3375d5a0fc"></a>

##### EnableGpaPinning · 生成属性

```csharp
public bool? EnableGpaPinning { get; set; }
```

实验性。来宾物理地址（GPA）固定配置的开关。Core 目前读取并提交对应属性，不提供指定 GPA 范围的独立操作。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 来宾物理地址（GPA）固定配置的开关。Core 目前读取并提交对应属性，不提供指定 GPA 范围的独立操作。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.EnableGpaPinning`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.EnableGpaPinning`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.EnableGpaPinning；提交方式：明确选择字段。将 nameof(VmMemorySettings.EnableGpaPinning) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.EnableGpaPinning`。 |

<a id="api-d4750de9d4893c87"></a>

##### EnableHotHint · 生成属性

```csharp
public bool? EnableHotHint { get; set; }
```

实验性。控制内存配置中的热提示开关。此入口不负责提交某段内存的冷热信息。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 控制内存配置中的热提示开关。此入口不负责提交某段内存的冷热信息。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.EnableHotHint`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.EnableHotHint`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.EnableHotHint；提交方式：明确选择字段。将 nameof(VmMemorySettings.EnableHotHint) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.EnableHotHint`。 |

<a id="api-01c5aefe50932ed4"></a>

##### EnablePrivateCompressionStore · 生成属性

```csharp
public bool? EnablePrivateCompressionStore { get; set; }
```

实验性。内存配置中的专用压缩存储开关。当前入口仅传递配置，不返回压缩量或压缩率。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 内存配置中的专用压缩存储开关。当前入口仅传递配置，不返回压缩量或压缩率。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.EnablePrivateCompressionStore；提交方式：明确选择字段。将 nameof(VmMemorySettings.EnablePrivateCompressionStore) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore`。 |

<a id="api-5bad0bbc763db8ab"></a>

##### MaxMemoryBlocksPerNumaNode · 生成属性

```csharp
public ulong? MaxMemoryBlocksPerNumaNode { get; set; }
```

实验性。限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。 |
| 单位与表示 | MiB。表示容量，不是页面数，也不是 NUMA 节点数。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.MaxMemoryBlocksPerNumaNode；提交方式：明确选择字段。将 nameof(VmMemorySettings.MaxMemoryBlocksPerNumaNode) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode`。 |

<a id="api-c4e18b2e657d1418"></a>

##### Maximum · 生成属性

```csharp
public long Maximum { get; set; }
```

动态内存最大值，单位为 MiB。

<a id="api-296adc74f1e302b9"></a>

##### MemoryAccessTrackingPolicy · 生成属性

```csharp
public byte? MemoryAccessTrackingPolicy { get; set; }
```

实验性。配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。 |
| 单位与表示 | 跟踪策略编码；不能与 BackingPageSize 的编码互换。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.MemoryAccessTrackingPolicy；提交方式：明确选择字段。将 nameof(VmMemorySettings.MemoryAccessTrackingPolicy) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy`。 |

<a id="api-86d3a01cfd0ef1f0"></a>

##### MemoryAccessTrackingState · 生成属性

```csharp
public byte? MemoryAccessTrackingState { get; set; }
```

实验性。配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。此值是配置，不是一次访问采样结果。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。此值是配置，不是一次访问采样结果。 |
| 单位与表示 | 状态编码。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.MemoryAccessTrackingState；提交方式：明确选择字段。将 nameof(VmMemorySettings.MemoryAccessTrackingState) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState`。 |

<a id="api-d43fe310858a4b72"></a>

##### MemoryEncryptionPolicy · 生成属性

```csharp
public byte? MemoryEncryptionPolicy { get; set; }
```

实验性。请求虚拟机内存采用的加密策略。此字段表示配置，不能用它单独证明运行中的内存已经加密。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 请求虚拟机内存采用的加密策略。此字段表示配置，不能用它单独证明运行中的内存已经加密。 |
| 单位与表示 | 策略编码。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.MemoryEncryptionPolicy；提交方式：明确选择字段。将 nameof(VmMemorySettings.MemoryEncryptionPolicy) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy`。 |

<a id="api-79e84c05bd14bdec"></a>

##### Minimum · 生成属性

```csharp
public long Minimum { get; set; }
```

动态内存最小值，单位为 MiB。

<a id="api-137ad4fb24c64154"></a>

##### SgxEnabled · 生成属性

```csharp
public bool? SgxEnabled { get; set; }
```

实验性。请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置；启用配置本身不表示来宾已能使用 SGX。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置；启用配置本身不表示来宾已能使用 SGX。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.SgxEnabled`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.SgxEnabled`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.SgxEnabled；提交方式：明确选择字段。将 nameof(VmMemorySettings.SgxEnabled) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.SgxEnabled`。 |

<a id="api-d5562561647805a6"></a>

##### SgxLaunchControlDefault · 生成属性

```csharp
public string? SgxLaunchControlDefault { get; set; }
```

实验性。SGX 默认启动控制配置的原始文本。当前 Core 不解析、生成或改写其中的内容。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | SGX 默认启动控制配置的原始文本。当前 Core 不解析、生成或改写其中的内容。 |
| 单位与表示 | 原始字符串。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.SgxLaunchControlDefault；提交方式：明确选择字段。将 nameof(VmMemorySettings.SgxLaunchControlDefault) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault`。 |

<a id="api-5e378c729c2b356a"></a>

##### SgxLaunchControlMode · 生成属性

```csharp
public uint? SgxLaunchControlMode { get; set; }
```

实验性。SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。 |
| 单位与表示 | 模式编码。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode`。没有额外的实验功能总开关。 |
| 缺测与错误 | 读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。 |
| 修改对应 | 已提供修改入口；输入：newSettings.SgxLaunchControlMode；提交方式：明确选择字段。将 nameof(VmMemorySettings.SgxLaunchControlMode) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode`。 |

<a id="api-487c14ee9e31a804"></a>

##### SgxSize · 生成属性

```csharp
public double? SgxSize { get; set; }
```

实验性。为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。 |
| 单位与表示 | MiB；Core 的容量单位。底层属性为 UInt64，当前公开模型以 Double 表示，不提供小数 MiB 的写入精度。 |
| 取值与默认 | 取值和联动规则见 `ExHyperV.Services.VmMemoryFields.SgxSize`；该处为本字段的完整说明。 |
| 适用条件 | 实验性读写能力；宿主和功能条件见 `ExHyperV.Services.VmMemoryFields.SgxSize`。没有额外的实验功能总开关。 |
| 缺测与错误 | GetVmMemorySettingsAsync 未取得底层容量时保留 null，实际读到的 0 保留为 0；需要区分属性不存在、缺值与读取失败时，使用自由查询的对应字段状态。 |
| 修改对应 | 已提供修改入口；输入：newSettings.SgxSize；提交方式：明确选择字段。将 nameof(VmMemorySettings.SgxSize) 加入 changedProperties。具体规范化和必要联动见 `ExHyperV.Services.VmMemoryFields.SgxSize`。 |

<a id="api-79f3ba7e128a5f73"></a>

##### Startup · 生成属性

```csharp
public long Startup { get; set; }
```

启动内存，单位为 MiB（1024&#178; 字节）。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 虚拟机配置的启动内存。动态内存启用后，运行时分配量可以改变；此字段不表示当前已分配内存，也不表示来宾程序已经使用的内存。 |
| 单位与表示 | MiB，1 MiB = 1,048,576 字节。 |
| 取值与默认 | 读取当前配置值。可接受的设置范围及版本差异待核实；不能将模型初始值 0 解释为有效配置。 |
| 适用条件 | 这是配置量，不随动态内存的每次分配变化。具体版本、代数及运行状态组合的验证范围待补充。 |
| 缺测与错误 | 当前 MapMemory 在底层值为空时回退到 0；此设置模型的本属性没有独立有效性标记，读取失败由服务响应报告。需要字段级缺测状态时，使用 VmQueryService.QueryAsync 选择 VmMemoryFields.Startup。 |

<a id="api-38ac08c146f6798f"></a>

##### SupportsBackingPageSize · 属性

```csharp
public bool SupportsBackingPageSize { get; }
```

宿主是否提供 BackingPageSize 属性；旧宿主通过 HugePagesEnabled 映射大页和巨页。

<a id="api-96864048d7a53d89"></a>

##### Weight · 生成属性

```csharp
public uint Weight { get; set; }
```

内存竞争时的原始权重，对应 Hyper-V Weight；不进行归一化换算，读取缺少值时沿用 5000。

#### 方法说明

<a id="api-61f2f7f6f95603b2"></a>

##### Clone · 方法

```csharp
public VmMemorySettings Clone();
```

复制当前值为独立模型，不复制属性变化事件订阅者。

返回：新的设置模型；内存页面选项也会复制。

<a id="api-0718401cd1847e63"></a>

##### ConfigurePageSizeSupport · 方法

```csharp
public void ConfigurePageSizeSupport(bool supportsBackingPageSize);
```

更新本地页面大小选项，并发送 AvailablePageSizes 属性变化通知；不修改 Hyper-V。

| 参数 | 说明 |
| --- | --- |
| `supportsBackingPageSize` | true 提供 0、1、2 三项；false 只提供旧宿主可映射的 1、2。 |

<a id="api-94d0f03236a10a60"></a>

##### Restore · 方法

```csharp
public void Restore(VmMemorySettings other);
```

通过属性设置器恢复本地模型并触发相关通知；不提交 Hyper-V 配置。

| 参数 | 说明 |
| --- | --- |
| `other` | 要恢复的设置快照；null 时不做操作。 |

<a id="api-9565efeada622ee9"></a>

### `VmMigrationCompatibilityMode` 枚举

处理器迁移兼容模式（LimitProcessorFeaturesMode，u8）。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorSettings.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` CommonClusterFeatureSet = 1 `](#api-2a8970e8a043722b) | 使用群集共同处理器功能集的迁移兼容模式。 |
| [` MinimumFeatureSet = 0 `](#api-ec1b5a0c208622a3) | 使用最小处理器功能集的迁移兼容模式。 |

#### 详细说明

```csharp
public enum VmMigrationCompatibilityMode : byte
```

#### 枚举值说明

<a id="api-2a8970e8a043722b"></a>

##### CommonClusterFeatureSet · 枚举值

```csharp
CommonClusterFeatureSet = 1
```

使用群集共同处理器功能集的迁移兼容模式。

<a id="api-ec1b5a0c208622a3"></a>

##### MinimumFeatureSet · 枚举值

```csharp
MinimumFeatureSet = 0
```

使用最小处理器功能集的迁移兼容模式。

<a id="api-04e3b09953c73d67"></a>

### `VmMmioSettings` 类

虚拟机 MMIO 地址空间设置（Msvm_VirtualSystemSettingData，单位 MiB）。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / MMIO |
| 源文件 | ` Management/VirtualMachines/Mmio/VmMmioSettings.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ulong? HighBaseMiB { get; set; } `](#api-4144e28f2dcd10c7) | 高位 MMIO 地址基址，单位为 MiB（1024&#178; 字节）；null 表示未取得。 |
| [` public ulong? HighSizeMiB { get; set; } `](#api-6fb47b5dbeff2bce) | 高位 MMIO 空间大小，单位为 MiB（1024&#178; 字节）；null 表示未取得。 |
| [` public ulong? LowSizeMiB { get; set; } `](#api-807a8a6936300167) | 低位 MMIO 空间大小，单位为 MiB（1024&#178; 字节）；null 表示未取得。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public VmMmioSettings Clone() `](#api-8b7a42b6a79f2308) | 复制当前值为独立模型，不复制属性变化事件订阅者。 |
| [` public void Restore(VmMmioSettings other) `](#api-dd21b6803f5c007d) | 通过属性设置器恢复本地模型并触发相关通知；不提交 Hyper-V 配置。 |

#### 详细说明

```csharp
public partial class VmMmioSettings : ObservableObject
```

#### 属性说明

<a id="api-4144e28f2dcd10c7"></a>

##### HighBaseMiB · 生成属性

```csharp
public ulong? HighBaseMiB { get; set; }
```

高位 MMIO 地址基址，单位为 MiB（1024&#178; 字节）；null 表示未取得。

<a id="api-6fb47b5dbeff2bce"></a>

##### HighSizeMiB · 生成属性

```csharp
public ulong? HighSizeMiB { get; set; }
```

高位 MMIO 空间大小，单位为 MiB（1024&#178; 字节）；null 表示未取得。

<a id="api-807a8a6936300167"></a>

##### LowSizeMiB · 生成属性

```csharp
public ulong? LowSizeMiB { get; set; }
```

低位 MMIO 空间大小，单位为 MiB（1024&#178; 字节）；null 表示未取得。

#### 方法说明

<a id="api-8b7a42b6a79f2308"></a>

##### Clone · 方法

```csharp
public VmMmioSettings Clone();
```

复制当前值为独立模型，不复制属性变化事件订阅者。

返回：包含当前三个 MMIO 值的新模型。

<a id="api-dd21b6803f5c007d"></a>

##### Restore · 方法

```csharp
public void Restore(VmMmioSettings other);
```

通过属性设置器恢复本地模型并触发相关通知；不提交 Hyper-V 配置。

| 参数 | 说明 |
| --- | --- |
| `other` | 要恢复的设置快照；null 时不做操作。 |

<a id="api-0e0adcb5bf4c592f"></a>

### `VmNetworkAdapter` 类

聚合多个 Hyper-V 网络 WMI 类的网卡设置。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkAdapter.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int AccessVlanId { get; set; } `](#api-a1feb37c24214b53) | 接入模式下的 VLAN ID。 |
| [` public ulong BandwidthLimitBitsPerSecond { get; set; } `](#api-7dc1ee13848f7b45) | 最大带宽限制，单位为 bit/s；保留 Hyper-V 原始精度，不作整数 Mbit/s 换算。 |
| [` public ulong BandwidthReservationBitsPerSecond { get; set; } `](#api-bdaef9f42bc3f1f2) | 最低保留带宽，单位为 bit/s；保留 Hyper-V 原始精度，不作整数 Mbit/s 换算。 |
| [` public Guid DeviceId { get; } `](#api-2a225c09af726a7e) | 虚拟网卡的设备 GUID；尚未从服务取得有效身份时为 Guid.Empty。 |
| [` public bool DhcpGuardEnabled { get; set; } `](#api-d47c4056e2b0eb55) | 是否启用 DHCP 防护。 |
| [` public bool HasNetworkSpeedSample { get; set; } `](#api-a2be18e85de2fd47) | 是否已取得有效速率采样；false 时速率字段不能作为有效测量值。 |
| [` public List<string> IpAddresses { get; set; } = new List<string>() `](#api-751247edb8fb35c8) | 网卡 IP 地址的可变列表。 |
| [` public bool IpsecOffloadEnabled { get; set; } `](#api-53f5f84f043a7127) | 是否启用 IPsec 任务卸载。 |
| [` public bool IsConnected { get; set; } `](#api-1a825d032fde4cc3) | 网卡连接开关，表示模拟网线是否接通。 |
| [` public bool IsStaticMac { get; set; } `](#api-c9a5e50c2883f54d) | 是否使用静态 MAC 地址配置。 |
| [` public string MacAddress { get; set; } = string.Empty `](#api-554e306f108ff64a) | 虚拟网卡 MAC 地址。 |
| [` public bool MacSpoofingAllowed { get; set; } `](#api-a2034c3881e2ecb2) | 是否允许 MAC 地址欺骗。 |
| [` public PortMonitorMode MonitorMode { get; set; } `](#api-d8ad5d8a503cadaa) | 端口镜像的源、目标或禁用角色。 |
| [` public string Name { get; set; } = string.Empty `](#api-553f8b08c56a5ef9) | 虚拟网卡名称。 |
| [` public int NativeVlanId { get; set; } `](#api-caaaa2aaf66c80c6) | 中继模式下的本征 VLAN ID。 |
| [` public PerformanceSampleInfo PerformanceSample { get; set; } = new(PerformanceSampleState.NotSampled, default) `](#api-4131e1d7f5a701b0) | 本次收发速率采样状态与时间；无效时保留此前速率字段，但它们不是本次新样本。 |
| [` public PvlanMode PvlanMode { get; set; } `](#api-595d524b6ca54d7c) | 专用 VLAN 端口角色。 |
| [` public int PvlanPrimaryId { get; set; } `](#api-4cf52e95d96672de) | 专用 VLAN 的主 VLAN ID。 |
| [` public int PvlanSecondaryId { get; set; } `](#api-6c1cf0bdf2b8f5f5) | 专用 VLAN 的辅助 VLAN ID。 |
| [` public ulong ReceiveBytesPerSecond { get; set; } `](#api-1ccbf73b1018a867) | 当前接收速率，单位为字节每秒。 |
| [` public bool RouterGuardEnabled { get; set; } `](#api-faa73a409533de93) | 是否启用路由器通告防护。 |
| [` public ulong SendBytesPerSecond { get; set; } `](#api-d085f959775bcf67) | 当前发送速率，单位为字节每秒。 |
| [` public bool SriovEnabled { get; set; } `](#api-1d546df2fba1691c) | 是否启用单根 I/O 虚拟化 SR-IOV。 |
| [` public uint StormLimit { get; set; } `](#api-36a14d57652bbf94) | 广播、组播和未知单播的风暴限制，采用 Hyper-V StormLimit 原始值。 |
| [` public Guid? SwitchId { get; } `](#api-609e1b82df1cd7a2) | 查询得到的目标交换机 GUID；无连接目标或无法确认时为 null。 |
| [` public string SwitchName { get; set; } `](#api-801ac06b7251ea2b) | 交换机的显示名称；修改此值不改变连接目标，连接操作仅使用显式传入的交换机 GUID。 |
| [` public bool TeamingAllowed { get; set; } `](#api-ad988c19d88ab467) | 是否允许来宾对该网卡进行组队。 |
| [` public List<int> TrunkAllowedVlanIds { get; set; } = new List<int>() `](#api-9dc6dc7d08059185) | 中继模式下允许通过的 VLAN ID 可变列表。 |
| [` public VlanOperationMode VlanMode { get; set; } `](#api-41bd09d02b1eb300) | VLAN 工作模式。 |
| [` public bool VmqEnabled { get; set; } `](#api-632702ca0ab09eb2) | 是否启用虚拟机队列 VMQ。 |

#### 详细说明

```csharp
public partial class VmNetworkAdapter : ObservableObject
```

调用约定：包含网卡设置与采样数据，修改通过相应服务提交。设备和交换机分别用 DeviceId、SwitchId 标识，名称仅供显示。

#### 属性说明

<a id="api-a1feb37c24214b53"></a>

##### AccessVlanId · 生成属性

```csharp
public int AccessVlanId { get; set; }
```

接入模式下的 VLAN ID。

<a id="api-7dc1ee13848f7b45"></a>

##### BandwidthLimitBitsPerSecond · 生成属性

```csharp
public ulong BandwidthLimitBitsPerSecond { get; set; }
```

最大带宽限制，单位为 bit/s；保留 Hyper-V 原始精度，不作整数 Mbit/s 换算。

<a id="api-bdaef9f42bc3f1f2"></a>

##### BandwidthReservationBitsPerSecond · 生成属性

```csharp
public ulong BandwidthReservationBitsPerSecond { get; set; }
```

最低保留带宽，单位为 bit/s；保留 Hyper-V 原始精度，不作整数 Mbit/s 换算。

<a id="api-2a225c09af726a7e"></a>

##### DeviceId · 属性

```csharp
public Guid DeviceId { get; }
```

虚拟网卡的设备 GUID；尚未从服务取得有效身份时为 Guid.Empty。

<a id="api-d47c4056e2b0eb55"></a>

##### DhcpGuardEnabled · 生成属性

```csharp
public bool DhcpGuardEnabled { get; set; }
```

是否启用 DHCP 防护。

<a id="api-a2be18e85de2fd47"></a>

##### HasNetworkSpeedSample · 生成属性

```csharp
public bool HasNetworkSpeedSample { get; set; }
```

是否已取得有效速率采样；false 时速率字段不能作为有效测量值。

<a id="api-751247edb8fb35c8"></a>

##### IpAddresses · 生成属性

```csharp
public List<string> IpAddresses { get; set; } = new List<string>();
```

网卡 IP 地址的可变列表。

<a id="api-53f5f84f043a7127"></a>

##### IpsecOffloadEnabled · 生成属性

```csharp
public bool IpsecOffloadEnabled { get; set; }
```

是否启用 IPsec 任务卸载。

<a id="api-1a825d032fde4cc3"></a>

##### IsConnected · 生成属性

```csharp
public bool IsConnected { get; set; }
```

网卡连接开关，表示模拟网线是否接通。

<a id="api-c9a5e50c2883f54d"></a>

##### IsStaticMac · 生成属性

```csharp
public bool IsStaticMac { get; set; }
```

是否使用静态 MAC 地址配置。

<a id="api-554e306f108ff64a"></a>

##### MacAddress · 生成属性

```csharp
public string MacAddress { get; set; } = string.Empty;
```

虚拟网卡 MAC 地址。

<a id="api-a2034c3881e2ecb2"></a>

##### MacSpoofingAllowed · 生成属性

```csharp
public bool MacSpoofingAllowed { get; set; }
```

是否允许 MAC 地址欺骗。

<a id="api-d8ad5d8a503cadaa"></a>

##### MonitorMode · 生成属性

```csharp
public PortMonitorMode MonitorMode { get; set; }
```

端口镜像的源、目标或禁用角色。

<a id="api-553f8b08c56a5ef9"></a>

##### Name · 生成属性

```csharp
public string Name { get; set; } = string.Empty;
```

虚拟网卡名称。

<a id="api-caaaa2aaf66c80c6"></a>

##### NativeVlanId · 生成属性

```csharp
public int NativeVlanId { get; set; }
```

中继模式下的本征 VLAN ID。

<a id="api-4131e1d7f5a701b0"></a>

##### PerformanceSample · 生成属性

```csharp
public PerformanceSampleInfo PerformanceSample { get; set; } = new(PerformanceSampleState.NotSampled, default);
```

本次收发速率采样状态与时间；无效时保留此前速率字段，但它们不是本次新样本。

<a id="api-595d524b6ca54d7c"></a>

##### PvlanMode · 生成属性

```csharp
public PvlanMode PvlanMode { get; set; }
```

专用 VLAN 端口角色。

<a id="api-4cf52e95d96672de"></a>

##### PvlanPrimaryId · 生成属性

```csharp
public int PvlanPrimaryId { get; set; }
```

专用 VLAN 的主 VLAN ID。

<a id="api-6c1cf0bdf2b8f5f5"></a>

##### PvlanSecondaryId · 生成属性

```csharp
public int PvlanSecondaryId { get; set; }
```

专用 VLAN 的辅助 VLAN ID。

<a id="api-1ccbf73b1018a867"></a>

##### ReceiveBytesPerSecond · 生成属性

```csharp
public ulong ReceiveBytesPerSecond { get; set; }
```

当前接收速率，单位为字节每秒。

<a id="api-faa73a409533de93"></a>

##### RouterGuardEnabled · 生成属性

```csharp
public bool RouterGuardEnabled { get; set; }
```

是否启用路由器通告防护。

<a id="api-d085f959775bcf67"></a>

##### SendBytesPerSecond · 生成属性

```csharp
public ulong SendBytesPerSecond { get; set; }
```

当前发送速率，单位为字节每秒。

<a id="api-1d546df2fba1691c"></a>

##### SriovEnabled · 生成属性

```csharp
public bool SriovEnabled { get; set; }
```

是否启用单根 I/O 虚拟化 SR-IOV。

<a id="api-36a14d57652bbf94"></a>

##### StormLimit · 生成属性

```csharp
public uint StormLimit { get; set; }
```

广播、组播和未知单播的风暴限制，采用 Hyper-V StormLimit 原始值。

<a id="api-609e1b82df1cd7a2"></a>

##### SwitchId · 属性

```csharp
public Guid? SwitchId { get; }
```

查询得到的目标交换机 GUID；无连接目标或无法确认时为 null。修改连接时显式向 UpdateConnectionAsync 传入目标 GUID。

<a id="api-801ac06b7251ea2b"></a>

##### SwitchName · 属性

```csharp
public string SwitchName { get; set; }
```

交换机的显示名称；修改此值不改变连接目标，连接操作仅使用显式传入的交换机 GUID。

<a id="api-ad988c19d88ab467"></a>

##### TeamingAllowed · 生成属性

```csharp
public bool TeamingAllowed { get; set; }
```

是否允许来宾对该网卡进行组队。

<a id="api-9dc6dc7d08059185"></a>

##### TrunkAllowedVlanIds · 生成属性

```csharp
public List<int> TrunkAllowedVlanIds { get; set; } = new List<int>();
```

中继模式下允许通过的 VLAN ID 可变列表。

<a id="api-41bd09d02b1eb300"></a>

##### VlanMode · 生成属性

```csharp
public VlanOperationMode VlanMode { get; set; }
```

VLAN 工作模式。

<a id="api-632702ca0ab09eb2"></a>

##### VmqEnabled · 生成属性

```csharp
public bool VmqEnabled { get; set; }
```

是否启用虚拟机队列 VMQ。

<a id="api-75dcd2fafb7a6260"></a>

### `VmPcieDeviceSetting` 类

单个已分配 PCIe 设备的身份和来宾模式设置；编辑 GuestMode 后由服务应用，AppliedGuestMode 保存已应用值。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/VmPcieModels.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public VmPcieGuestMode AppliedGuestMode { get; set; } `](#api-3ed9a8e0839920b6) | 最后读取或成功应用的来宾模式，用于判断尚未提交的修改。 |
| [` public string ClassType { get; init; } = string.Empty `](#api-9685fb6ce7798b5d) | 设备 PnP 类别。 |
| [` public string DeviceInstanceId { get; init; } = string.Empty `](#api-b64207f86e1be5c8) | 宿主 PnP 设备实例 ID。 |
| [` public VmPcieGuestMode GuestMode { get; set; } `](#api-18b4515c49bf4192) | 当前编辑的来宾设备呈现模式，修改不会自动提交。 |
| [` public bool GuestModeAvailable { get; init; } `](#api-0b7a094015c92218) | 该设备是否提供来宾模式属性。 |
| [` public string Name { get; init; } = string.Empty `](#api-12234bf3a8624c7a) | 设备的显示名称。 |
| [` public string Path { get; init; } = string.Empty `](#api-a6bf0b5ed3bad1d1) | 宿主 PCIe 设备位置路径。 |
| [` public string Vendor { get; init; } = string.Empty `](#api-a897a59204e02a88) | 设备制造商的显示名称。 |

#### 详细说明

```csharp
public partial class VmPcieDeviceSetting : ObservableObject
```

#### 属性说明

<a id="api-3ed9a8e0839920b6"></a>

##### AppliedGuestMode · 属性

```csharp
public VmPcieGuestMode AppliedGuestMode { get; set; }
```

最后读取或成功应用的来宾模式，用于判断尚未提交的修改。

<a id="api-9685fb6ce7798b5d"></a>

##### ClassType · 属性

```csharp
public string ClassType { get; init; } = string.Empty;
```

设备 PnP 类别。

<a id="api-b64207f86e1be5c8"></a>

##### DeviceInstanceId · 属性

```csharp
public string DeviceInstanceId { get; init; } = string.Empty;
```

宿主 PnP 设备实例 ID。

<a id="api-18b4515c49bf4192"></a>

##### GuestMode · 生成属性

```csharp
public VmPcieGuestMode GuestMode { get; set; }
```

当前编辑的来宾设备呈现模式，修改不会自动提交。

<a id="api-0b7a094015c92218"></a>

##### GuestModeAvailable · 属性

```csharp
public bool GuestModeAvailable { get; init; }
```

该设备是否提供来宾模式属性。

<a id="api-12234bf3a8624c7a"></a>

##### Name · 属性

```csharp
public string Name { get; init; } = string.Empty;
```

设备的显示名称。

<a id="api-a6bf0b5ed3bad1d1"></a>

##### Path · 属性

```csharp
public string Path { get; init; } = string.Empty;
```

宿主 PCIe 设备位置路径。

<a id="api-a897a59204e02a88"></a>

##### Vendor · 属性

```csharp
public string Vendor { get; init; } = string.Empty;
```

设备制造商的显示名称。

<a id="api-6870c8b5d182ab07"></a>

### `VmPcieGuestMode` 枚举

来宾访问 PCIe 设备的呈现模式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/VmPcieModels.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Emulated = 1 `](#api-85e8bd9c04df3319) | 仿真设备模式。 |
| [` Paravirtualized = 0 `](#api-4ecd6d1c4afeb6d4) | 半虚拟化设备模式。 |

#### 详细说明

```csharp
public enum VmPcieGuestMode : byte
```

#### 枚举值说明

<a id="api-85e8bd9c04df3319"></a>

##### Emulated · 枚举值

```csharp
Emulated = 1
```

仿真设备模式。

<a id="api-4ecd6d1c4afeb6d4"></a>

##### Paravirtualized · 枚举值

```csharp
Paravirtualized = 0
```

半虚拟化设备模式。

<a id="api-0ee78ad274e05163"></a>

### `VmPcieSettings` 类

虚拟机 PCIe 系统设置及已分配设备的查询快照；Devices 列表仍可修改，修改不会自动写回 Hyper-V。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/VmPcieModels.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool BootPciExpress { get; init; } `](#api-377d2bf1dd588ebe) | 是否配置启用启动阶段 vPCI；固件与设备限制见 VmPcieService.SetBootPciExpressAsync，true 不表示已经引导成功。 |
| [` public bool BootPciExpressAvailable { get; init; } `](#api-ab87dfeefd5d90f6) | 宿主是否提供 BootPciExpress 属性；不表示已验证具体控制器可以引导。 |
| [` public List<VmPcieDeviceSetting> Devices { get; init; } = [] `](#api-d8c33143eddedaef) | 已分配 PCIe 设备设置的可变列表。 |
| [` public bool EmulationEnabled { get; init; } `](#api-d5157522dbacd10f) | 当前是否启用 PCIe 仿真。 |
| [` public bool SystemSettingsAvailable { get; init; } `](#api-2cdb3f68df1ae598) | 宿主是否提供 PCIe 系统级设置对象及 EmulationMode、Topology 属性；不表示当前值可读、已开启仿真或已建立拓扑。 |
| [` public ushort Topology { get; init; } `](#api-7833067f587a83e6) | Hyper-V 返回的 PCIe 拓扑编码。 |

#### 详细说明

```csharp
public sealed class VmPcieSettings
```

#### 属性说明

<a id="api-377d2bf1dd588ebe"></a>

##### BootPciExpress · 属性

```csharp
public bool BootPciExpress { get; init; }
```

是否配置启用启动阶段 vPCI；固件与设备限制见 VmPcieService.SetBootPciExpressAsync，true 不表示已经引导成功。

<a id="api-ab87dfeefd5d90f6"></a>

##### BootPciExpressAvailable · 属性

```csharp
public bool BootPciExpressAvailable { get; init; }
```

宿主是否提供 BootPciExpress 属性；不表示已验证具体控制器可以引导。

<a id="api-d8c33143eddedaef"></a>

##### Devices · 属性

```csharp
public List<VmPcieDeviceSetting> Devices { get; init; } = [];
```

已分配 PCIe 设备设置的可变列表。

<a id="api-d5157522dbacd10f"></a>

##### EmulationEnabled · 属性

```csharp
public bool EmulationEnabled { get; init; }
```

当前是否启用 PCIe 仿真。

<a id="api-2cdb3f68df1ae598"></a>

##### SystemSettingsAvailable · 属性

```csharp
public bool SystemSettingsAvailable { get; init; }
```

宿主是否提供 PCIe 系统级设置对象及 EmulationMode、Topology 属性；不表示当前值可读、已开启仿真或已建立拓扑。

<a id="api-7833067f587a83e6"></a>

##### Topology · 属性

```csharp
public ushort Topology { get; init; }
```

Hyper-V 返回的 PCIe 拓扑编码。

<a id="api-c38f561557cd3ed7"></a>

### `VmPowerAction` 枚举

虚拟机支持的电源动作；强制断电必须由调用方明确选择。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 电源 |
| 源文件 | ` Management/VirtualMachines/Power/VmPowerAction.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Restart = 3 `](#api-72841ded1ef5f7fd) | 请求来宾重启；失败时不会自动执行硬重置。 |
| [` Save = 4 `](#api-066a35875206bbf2) | 保存虚拟机运行状态。 |
| [` Start = 0 `](#api-fea99d68b1a48a20) | 启动虚拟机。 |
| [` Stop = 1 `](#api-5edc70694068deca) | 请求来宾正常关机；失败时不会自动强制断电。 |
| [` Suspend = 5 `](#api-a4d87305f41bc281) | 暂停虚拟机运行。 |
| [` TurnOff = 2 `](#api-46d8f8d9fc377430) | 强制关闭虚拟机；必要时可终止对应的工作进程。 |

#### 详细说明

```csharp
public enum VmPowerAction
```

#### 枚举值说明

<a id="api-72841ded1ef5f7fd"></a>

##### Restart · 枚举值

```csharp
Restart = 3
```

请求来宾重启；失败时不会自动执行硬重置。

<a id="api-066a35875206bbf2"></a>

##### Save · 枚举值

```csharp
Save = 4
```

保存虚拟机运行状态。

<a id="api-fea99d68b1a48a20"></a>

##### Start · 枚举值

```csharp
Start = 0
```

启动虚拟机。

<a id="api-5edc70694068deca"></a>

##### Stop · 枚举值

```csharp
Stop = 1
```

请求来宾正常关机；失败时不会自动强制断电。

<a id="api-a4d87305f41bc281"></a>

##### Suspend · 枚举值

```csharp
Suspend = 5
```

暂停虚拟机运行。

<a id="api-46d8f8d9fc377430"></a>

##### TurnOff · 枚举值

```csharp
TurnOff = 2
```

强制关闭虚拟机；必要时可终止对应的工作进程。

<a id="api-542e7663b47b4a87"></a>

### `VmProcessorCapability` 枚举

宿主为虚拟机提供的处理器设置项；可用不代表当前电源状态和硬件条件一定允许修改。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorCapability.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` ActivityCounters = 12 `](#api-b3a974df2533d529) | ACNT/MCNT 活动计数器。 |
| [` ArchitecturalPerformanceMonitoring = 11 `](#api-9ae4e1b6f5ccb289) | 体系结构性能监控。 |
| [` AutonomousActivityWindow = 22 `](#api-771601fa71d56f17) | 自主频率活动窗口。 |
| [` DesiredFrequency = 20 `](#api-ace74c13e64e7720) | 期望处理器频率。 |
| [` EnergyPerformancePreference = 21 `](#api-2363d217a30e8ccd) | 性能与能效偏好。 |
| [` FrequencyMaximum = 18 `](#api-1da869bb118b1198) | 处理器频率上限。 |
| [` FrequencyMinimum = 19 `](#api-19f9d9a961f53eb2) | 处理器频率下限。 |
| [` HardwareIsolatedGuestLimit = 29 `](#api-25f06c252e5f25a0) | 硬件隔离来宾数量上限。 |
| [` HardwareIsolation = 28 `](#api-be5ec343ac0eb30d) | 硬件隔离扩展。 |
| [` HostResourceProtection = 5 `](#api-c56635a7c50288a5) | 宿主资源保护。 |
| [` IgnoreHostFrequencyMaximum = 23 `](#api-38b1f164489bc1e2) | 忽略宿主最大频率限制。 |
| [` InstructionTracing = 27 `](#api-dabb21a6bdfe63d9) | 处理器指令跟踪。 |
| [` InterruptControllerMode = 14 `](#api-e5e0fc2823f72acc) | 来宾中断控制器模式。 |
| [` L3CacheAssociativity = 15 `](#api-26dc3f11ca4a0b98) | 来宾可见的 L3 缓存相联路数。 |
| [` L3ProcessorDistribution = 16 `](#api-2e7e739a8c63371e) | 虚拟处理器在 L3 域之间的分布策略。 |
| [` LargePageMitigation = 17 `](#api-f6ce977763f91185) | 大页拆分缓解策略。 |
| [` LastBranchRecording = 25 `](#api-874efc1edf5dc26e) | 最后分支记录。 |
| [` LegacyOperatingSystemCompatibility = 8 `](#api-0f3f0351e44a9dfe) | 旧操作系统兼容。 |
| [` LocalPeripheralInterruptMode = 35 `](#api-f02b25931ed16903) | 局部外设中断模式。 |
| [` MigrationCompatibility = 6 `](#api-ab7166dfe6724b52) | 处理器迁移兼容开关。 |
| [` MigrationFeatureSet = 7 `](#api-8c6251660521efc4) | 迁移兼容采用的功能集。 |
| [` NestedVirtualization = 4 `](#api-16eb1f645ff5e088) | 向来宾提供嵌套虚拟化。 |
| [` NumaNodesPerSocket = 33 `](#api-2b990c5c18900678) | 每插槽 NUMA 节点数量上限。 |
| [` PerformanceMonitoring = 24 `](#api-2b531b11aea9d540) | 性能监控单元透传。 |
| [` PhysicalAddressWidth = 34 `](#api-4a0cba29f11933f9) | 来宾物理地址位宽。 |
| [` PreciseEventSampling = 26 `](#api-e90a609e8de39d15) | 精确事件采样。 |
| [` ProcessorBrand = 13 `](#api-55fe8a3124cb7f87) | 来宾可见的处理器品牌字符串。 |
| [` ProcessorClustersPerSocket = 30 `](#api-fe8f963edbc46939) | 每插槽处理器簇数量上限。 |
| [` ProcessorCount = 0 `](#api-3e5688ca40186b7d) | 虚拟处理器数量。 |
| [` ProcessorsPerL3Domain = 31 `](#api-e2fd0419d55ea758) | 每个 L3 域的处理器数量上限。 |
| [` ProcessorsPerNumaNode = 32 `](#api-6de6862ba4eb064d) | 每个 NUMA 节点的处理器数量上限。 |
| [` ResourceLimit = 2 `](#api-b054bf14d6c6b999) | 处理器资源使用上限。 |
| [` ResourceReservation = 1 `](#api-4005993d0861735a) | 处理器资源保留比例。 |
| [` SchedulingWeight = 3 `](#api-e7f6defd633a95df) | 处理器资源竞争权重。 |
| [` SimultaneousMultithreading = 9 `](#api-562810faa3639f91) | 来宾同时多线程拓扑。 |
| [` SpeculationControlPolicy = 10 `](#api-24a1abd1a0ccccc8) | 推测执行控制策略。 |

#### 详细说明

```csharp
public enum VmProcessorCapability
```

#### 枚举值说明

<a id="api-b3a974df2533d529"></a>

##### ActivityCounters · 枚举值

```csharp
ActivityCounters = 12
```

ACNT/MCNT 活动计数器。

<a id="api-9ae4e1b6f5ccb289"></a>

##### ArchitecturalPerformanceMonitoring · 枚举值

```csharp
ArchitecturalPerformanceMonitoring = 11
```

体系结构性能监控。

<a id="api-771601fa71d56f17"></a>

##### AutonomousActivityWindow · 枚举值

```csharp
AutonomousActivityWindow = 22
```

自主频率活动窗口。

<a id="api-ace74c13e64e7720"></a>

##### DesiredFrequency · 枚举值

```csharp
DesiredFrequency = 20
```

期望处理器频率。

<a id="api-2363d217a30e8ccd"></a>

##### EnergyPerformancePreference · 枚举值

```csharp
EnergyPerformancePreference = 21
```

性能与能效偏好。

<a id="api-1da869bb118b1198"></a>

##### FrequencyMaximum · 枚举值

```csharp
FrequencyMaximum = 18
```

处理器频率上限。

<a id="api-19f9d9a961f53eb2"></a>

##### FrequencyMinimum · 枚举值

```csharp
FrequencyMinimum = 19
```

处理器频率下限。

<a id="api-25f06c252e5f25a0"></a>

##### HardwareIsolatedGuestLimit · 枚举值

```csharp
HardwareIsolatedGuestLimit = 29
```

硬件隔离来宾数量上限。

<a id="api-be5ec343ac0eb30d"></a>

##### HardwareIsolation · 枚举值

```csharp
HardwareIsolation = 28
```

硬件隔离扩展。

<a id="api-c56635a7c50288a5"></a>

##### HostResourceProtection · 枚举值

```csharp
HostResourceProtection = 5
```

宿主资源保护。

<a id="api-38b1f164489bc1e2"></a>

##### IgnoreHostFrequencyMaximum · 枚举值

```csharp
IgnoreHostFrequencyMaximum = 23
```

忽略宿主最大频率限制。

<a id="api-dabb21a6bdfe63d9"></a>

##### InstructionTracing · 枚举值

```csharp
InstructionTracing = 27
```

处理器指令跟踪。

<a id="api-e5e0fc2823f72acc"></a>

##### InterruptControllerMode · 枚举值

```csharp
InterruptControllerMode = 14
```

来宾中断控制器模式。

<a id="api-26dc3f11ca4a0b98"></a>

##### L3CacheAssociativity · 枚举值

```csharp
L3CacheAssociativity = 15
```

来宾可见的 L3 缓存相联路数。

<a id="api-2e7e739a8c63371e"></a>

##### L3ProcessorDistribution · 枚举值

```csharp
L3ProcessorDistribution = 16
```

虚拟处理器在 L3 域之间的分布策略。

<a id="api-f6ce977763f91185"></a>

##### LargePageMitigation · 枚举值

```csharp
LargePageMitigation = 17
```

大页拆分缓解策略。

<a id="api-874efc1edf5dc26e"></a>

##### LastBranchRecording · 枚举值

```csharp
LastBranchRecording = 25
```

最后分支记录。

<a id="api-0f3f0351e44a9dfe"></a>

##### LegacyOperatingSystemCompatibility · 枚举值

```csharp
LegacyOperatingSystemCompatibility = 8
```

旧操作系统兼容。

<a id="api-f02b25931ed16903"></a>

##### LocalPeripheralInterruptMode · 枚举值

```csharp
LocalPeripheralInterruptMode = 35
```

局部外设中断模式。

<a id="api-ab7166dfe6724b52"></a>

##### MigrationCompatibility · 枚举值

```csharp
MigrationCompatibility = 6
```

处理器迁移兼容开关。

<a id="api-8c6251660521efc4"></a>

##### MigrationFeatureSet · 枚举值

```csharp
MigrationFeatureSet = 7
```

迁移兼容采用的功能集。

<a id="api-16eb1f645ff5e088"></a>

##### NestedVirtualization · 枚举值

```csharp
NestedVirtualization = 4
```

向来宾提供嵌套虚拟化。

<a id="api-2b990c5c18900678"></a>

##### NumaNodesPerSocket · 枚举值

```csharp
NumaNodesPerSocket = 33
```

每插槽 NUMA 节点数量上限。

<a id="api-2b531b11aea9d540"></a>

##### PerformanceMonitoring · 枚举值

```csharp
PerformanceMonitoring = 24
```

性能监控单元透传。

<a id="api-4a0cba29f11933f9"></a>

##### PhysicalAddressWidth · 枚举值

```csharp
PhysicalAddressWidth = 34
```

来宾物理地址位宽。

<a id="api-e90a609e8de39d15"></a>

##### PreciseEventSampling · 枚举值

```csharp
PreciseEventSampling = 26
```

精确事件采样。

<a id="api-55fe8a3124cb7f87"></a>

##### ProcessorBrand · 枚举值

```csharp
ProcessorBrand = 13
```

来宾可见的处理器品牌字符串。

<a id="api-fe8f963edbc46939"></a>

##### ProcessorClustersPerSocket · 枚举值

```csharp
ProcessorClustersPerSocket = 30
```

每插槽处理器簇数量上限。

<a id="api-3e5688ca40186b7d"></a>

##### ProcessorCount · 枚举值

```csharp
ProcessorCount = 0
```

虚拟处理器数量。

<a id="api-e2fd0419d55ea758"></a>

##### ProcessorsPerL3Domain · 枚举值

```csharp
ProcessorsPerL3Domain = 31
```

每个 L3 域的处理器数量上限。

<a id="api-6de6862ba4eb064d"></a>

##### ProcessorsPerNumaNode · 枚举值

```csharp
ProcessorsPerNumaNode = 32
```

每个 NUMA 节点的处理器数量上限。

<a id="api-b054bf14d6c6b999"></a>

##### ResourceLimit · 枚举值

```csharp
ResourceLimit = 2
```

处理器资源使用上限。

<a id="api-4005993d0861735a"></a>

##### ResourceReservation · 枚举值

```csharp
ResourceReservation = 1
```

处理器资源保留比例。

<a id="api-e7f6defd633a95df"></a>

##### SchedulingWeight · 枚举值

```csharp
SchedulingWeight = 3
```

处理器资源竞争权重。

<a id="api-562810faa3639f91"></a>

##### SimultaneousMultithreading · 枚举值

```csharp
SimultaneousMultithreading = 9
```

来宾同时多线程拓扑。

<a id="api-24a1abd1a0ccccc8"></a>

##### SpeculationControlPolicy · 枚举值

```csharp
SpeculationControlPolicy = 10
```

推测执行控制策略。

<a id="api-1bd5c358d4233af8"></a>

### `VmProcessorSettings` 类

虚拟机处理器设置的可变模型。属性变化可用于绑定；提交配置由 VmProcessorService 执行。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorSettings.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool? AllowAcountMcount { get; set; } `](#api-e09bb47e09898e50) | 是否允许访问 ACNT/MCNT 计数器，对应 AllowACountMCount；null 表示未取得。 |
| [` public VmApicMode? ApicMode { get; set; } `](#api-06dff0eb27a8cc10) | 来宾 APIC 呈现模式；null 表示未取得。 |
| [` public bool CanTogglePerfmonIpt { get; } `](#api-6711df7cb8a3b5ca) | 按当前 IPT 和 PMU 状态判断是否允许切换 IPT。 |
| [` public bool CanTogglePerfmonPebs { get; } `](#api-c9986a8ce0f62756) | 按当前 PEBS 和 PMU 状态判断是否允许切换 PEBS。 |
| [` public bool CanTogglePerfmonPmu { get; } `](#api-2c47c4867989c0bd) | 按当前 PMU、PEBS 和 IPT 状态判断是否允许切换 PMU。 |
| [` public bool? CompatibilityForMigrationEnabled { get; set; } `](#api-7df699543bf40a8a) | 是否启用处理器迁移兼容；null 表示未取得。 |
| [` public VmMigrationCompatibilityMode? CompatibilityForMigrationMode { get; set; } `](#api-6b6ea0bce8cde6da) | 处理器迁移兼容所采用的功能集模式；null 表示未取得。 |
| [` public bool? CompatibilityForOlderOperatingSystemsEnabled { get; set; } `](#api-a5e68ba211e52460) | 是否启用旧操作系统兼容；null 表示未取得。 |
| [` public int Count { get; set; } `](#api-2ad9ef72ed6de310) | 虚拟处理器数量。 |
| [` public string? CpuBrandString { get; set; } `](#api-f078b42b57d3b839) | 向来宾公开的处理器品牌字符串；null 表示未取得。 |
| [` public bool? DisableSpeculationControls { get; set; } `](#api-713d74581e8f9c78) | 是否禁用推测执行控制；null 表示未取得。 |
| [` public bool? EnableHostResourceProtection { get; set; } `](#api-cfc4afd3ee752e7c) | 是否启用宿主资源保护；null 表示未取得。 |
| [` public bool? EnablePerfmonArchPmu { get; set; } `](#api-86367239158af1bb) | 是否启用体系结构性能监控单元；null 表示未取得。 |
| [` public bool? EnablePerfmonIpt { get; set; } `](#api-b53212a586247d9b) | 是否启用处理器跟踪 IPT，依赖 PMU；null 表示未取得。 |
| [` public bool? EnablePerfmonLbr { get; set; } `](#api-5ba3d69498b55ae4) | 是否透传最后分支记录 LBR；null 表示未取得。 |
| [` public bool? EnablePerfmonPebs { get; set; } `](#api-f19e510eb0f83218) | 是否启用精确事件采样 PEBS，依赖 PMU；null 表示未取得。 |
| [` public bool? EnablePerfmonPmu { get; set; } `](#api-cbe6d5886db27916) | 是否透传 PMU 性能监控；null 表示未取得，切换还需满足相关依赖规则。 |
| [` public bool? ExposeVirtualizationExtensions { get; set; } `](#api-80be917bee2bcf87) | 是否向来宾公开虚拟化扩展以支持嵌套虚拟化；null 表示未取得。 |
| [` public bool? HardwareIsolationExtensionsEnabled { get; set; } `](#api-57f5f594c218d8f1) | 是否启用硬件隔离扩展；null 表示未取得。 |
| [` public uint? L3CacheWays { get; set; } `](#api-50d097255d8f8f31) | 来宾看到的 L3 缓存相联路数，0 表示默认；不改变物理缓存，null 表示未取得。 |
| [` public L3DistributionPolicy? L3DistributionPolicy { get; set; } `](#api-f8b10ec44eb0e6bc) | 虚拟处理器在虚拟 L3 缓存域间的分布策略；null 表示未取得。 |
| [` public ulong Limit { get; set; } `](#api-bd8988e757bf36e7) | 处理器资源上限的原始值，单位为 0.001%；100000 表示 100%。 |
| [` public LpiMode? LpiMode { get; set; } `](#api-899ac1a52ff26036) | 局部外设中断模式；null 表示未取得。 |
| [` public uint? MaxClusterCountPerSocket { get; set; } `](#api-fda2f43a0b9fc90e) | 每个虚拟处理器插槽的最大簇数量；null 表示未取得。 |
| [` public uint? MaxHwIsolatedGuests { get; set; } `](#api-069a7d0f3dd8abce) | 允许的硬件隔离来宾最大数量；null 表示未取得。 |
| [` public ulong? MaxNumaNodesPerSocket { get; set; } `](#api-63406158130c178c) | 每个虚拟插槽的最大 NUMA 节点数量；null 表示未取得。 |
| [` public uint? MaxProcessorCountPerL3 { get; set; } `](#api-89caf851e7e8b313) | 每个虚拟 L3 缓存域的最大处理器数量；null 表示未取得。 |
| [` public ulong? MaxProcessorsPerNumaNode { get; set; } `](#api-1e6b72d3da610bc9) | 每个虚拟 NUMA 节点的最大处理器数量；null 表示未取得。 |
| [` public PageShatterMode? PageShatterMode { get; set; } `](#api-fe193c0b8ccbb9bd) | 二级地址转换的大页拆分缓解策略；null 表示未取得。 |
| [` public uint? PerfCpuAutonomousActivityWindow { get; set; } `](#api-b588b091ca89a2a5) | 宿主自主活动窗口的原始编码，不在模型中换算时间单位；null 表示未取得。 |
| [` public uint? PerfCpuEnergyPerformancePreference { get; set; } `](#api-e367485d17c3404c) | 宿主性能与能效偏好的原始编码；null 表示未取得。 |
| [` public uint? PerfCpuFreqCapMhz { get; set; } `](#api-58a607b2bc9bc394) | 每虚拟机处理器频率上限，单位为 MHz；null 表示未取得。 |
| [` public uint? PerfCpuFreqDesiredMhz { get; set; } `](#api-f5e33988371a6362) | 每虚拟机期望处理器频率，单位为 MHz；null 表示未取得。 |
| [` public uint? PerfCpuFreqMinMhz { get; set; } `](#api-87ef13670ae04326) | 每虚拟机处理器频率下限，单位为 MHz；null 表示未取得。 |
| [` public bool? PerfCpuIgnoreHostMaxFrequency { get; set; } `](#api-fa443a55db63001b) | 是否忽略宿主最大频率限制；null 表示未取得。 |
| [` public uint? PhysicalAddressWidth { get; set; } `](#api-6ee4358f8a53aaa4) | 来宾物理地址位宽，单位为位；null 表示未取得。 |
| [` public int RelativeWeight { get; set; } `](#api-2ce9d3e0067c7873) | 处理器竞争时的相对权重。 |
| [` public ulong Reservation { get; set; } `](#api-9774ba25ec4a2e4e) | 保留处理器资源的原始值，单位为 0.001%；100000 表示 100%。 |
| [` public SmtMode? SmtMode { get; set; } `](#api-3a53eedde34c1330) | 向来宾呈现的同时多线程模式；null 表示未取得。 |
| [` public IReadOnlySet<VmProcessorCapability> SupportedCapabilities { get; } `](#api-e013d390f93853f5) | 本次查询中宿主提供的处理器设置项，只读且不随设置值是否为 null 改变。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public bool Supports(VmProcessorCapability capability) `](#api-34be76ae19fd66ec) | 判断本次查询是否提供某项处理器设置字段；与当前值是否为 null 无关，不执行宿主查询或写入。 |

#### 详细说明

```csharp
public partial class VmProcessorSettings : ObservableObject
```

调用约定：可空字段未取得当前值时保留 null，不补为 false、0 或空字符串。字段是否存在由 SupportedCapabilities 或 Supports 单独表示：支持且值为 null 表示本次没有可用的当前值，不支持且值为 null 表示本次查询未提供该字段。未查询的模型也不包含支持信息。



频率上限、下限、期望频率、能效偏好、自主活动窗口、硬件隔离来宾上限、每插槽簇数量、每 L3 域处理器数量和物理地址位宽仍将底层 0xFFFFFFFF 未设置编码归一为 null；不能据 null 推断有效默认值。其他实际读到的 false、0 和空字符串保留原值。



字段存在不保证硬件支持或当前状态下可修改；修改仍由服务校验，并由底层返回执行结果。读取的 null 不等于写入请求，写入语义见 SetVmProcessorAsync 的 changedProperties 和空值约定。

#### 属性说明

<a id="api-e09bb47e09898e50"></a>

##### AllowAcountMcount · 生成属性

```csharp
public bool? AllowAcountMcount { get; set; }
```

是否允许访问 ACNT/MCNT 计数器，对应 AllowACountMCount；null 表示未取得。

<a id="api-06dff0eb27a8cc10"></a>

##### ApicMode · 生成属性

```csharp
public VmApicMode? ApicMode { get; set; }
```

来宾 APIC 呈现模式；null 表示未取得。

<a id="api-6711df7cb8a3b5ca"></a>

##### CanTogglePerfmonIpt · 属性

```csharp
public bool CanTogglePerfmonIpt { get; }
```

按当前 IPT 和 PMU 状态判断是否允许切换 IPT。

<a id="api-c9986a8ce0f62756"></a>

##### CanTogglePerfmonPebs · 属性

```csharp
public bool CanTogglePerfmonPebs { get; }
```

按当前 PEBS 和 PMU 状态判断是否允许切换 PEBS。

<a id="api-2c47c4867989c0bd"></a>

##### CanTogglePerfmonPmu · 属性

```csharp
public bool CanTogglePerfmonPmu { get; }
```

按当前 PMU、PEBS 和 IPT 状态判断是否允许切换 PMU。

<a id="api-7df699543bf40a8a"></a>

##### CompatibilityForMigrationEnabled · 生成属性

```csharp
public bool? CompatibilityForMigrationEnabled { get; set; }
```

是否启用处理器迁移兼容；null 表示未取得。

<a id="api-6b6ea0bce8cde6da"></a>

##### CompatibilityForMigrationMode · 生成属性

```csharp
public VmMigrationCompatibilityMode? CompatibilityForMigrationMode { get; set; }
```

处理器迁移兼容所采用的功能集模式；null 表示未取得。

<a id="api-a5e68ba211e52460"></a>

##### CompatibilityForOlderOperatingSystemsEnabled · 生成属性

```csharp
public bool? CompatibilityForOlderOperatingSystemsEnabled { get; set; }
```

是否启用旧操作系统兼容；null 表示未取得。

<a id="api-2ad9ef72ed6de310"></a>

##### Count · 生成属性

```csharp
public int Count { get; set; }
```

虚拟处理器数量。

<a id="api-f078b42b57d3b839"></a>

##### CpuBrandString · 生成属性

```csharp
public string? CpuBrandString { get; set; }
```

向来宾公开的处理器品牌字符串；null 表示未取得。

<a id="api-713d74581e8f9c78"></a>

##### DisableSpeculationControls · 生成属性

```csharp
public bool? DisableSpeculationControls { get; set; }
```

是否禁用推测执行控制；null 表示未取得。

<a id="api-cfc4afd3ee752e7c"></a>

##### EnableHostResourceProtection · 生成属性

```csharp
public bool? EnableHostResourceProtection { get; set; }
```

是否启用宿主资源保护；null 表示未取得。

<a id="api-86367239158af1bb"></a>

##### EnablePerfmonArchPmu · 生成属性

```csharp
public bool? EnablePerfmonArchPmu { get; set; }
```

是否启用体系结构性能监控单元；null 表示未取得。

<a id="api-b53212a586247d9b"></a>

##### EnablePerfmonIpt · 生成属性

```csharp
public bool? EnablePerfmonIpt { get; set; }
```

是否启用处理器跟踪 IPT，依赖 PMU；null 表示未取得。

<a id="api-5ba3d69498b55ae4"></a>

##### EnablePerfmonLbr · 生成属性

```csharp
public bool? EnablePerfmonLbr { get; set; }
```

是否透传最后分支记录 LBR；null 表示未取得。

<a id="api-f19e510eb0f83218"></a>

##### EnablePerfmonPebs · 生成属性

```csharp
public bool? EnablePerfmonPebs { get; set; }
```

是否启用精确事件采样 PEBS，依赖 PMU；null 表示未取得。

<a id="api-cbe6d5886db27916"></a>

##### EnablePerfmonPmu · 生成属性

```csharp
public bool? EnablePerfmonPmu { get; set; }
```

是否透传 PMU 性能监控；null 表示未取得，切换还需满足相关依赖规则。

<a id="api-80be917bee2bcf87"></a>

##### ExposeVirtualizationExtensions · 生成属性

```csharp
public bool? ExposeVirtualizationExtensions { get; set; }
```

是否向来宾公开虚拟化扩展以支持嵌套虚拟化；null 表示未取得。

<a id="api-57f5f594c218d8f1"></a>

##### HardwareIsolationExtensionsEnabled · 生成属性

```csharp
public bool? HardwareIsolationExtensionsEnabled { get; set; }
```

是否启用硬件隔离扩展；null 表示未取得。

<a id="api-50d097255d8f8f31"></a>

##### L3CacheWays · 生成属性

```csharp
public uint? L3CacheWays { get; set; }
```

来宾看到的 L3 缓存相联路数，0 表示默认；不改变物理缓存，null 表示未取得。

<a id="api-f8b10ec44eb0e6bc"></a>

##### L3DistributionPolicy · 生成属性

```csharp
public L3DistributionPolicy? L3DistributionPolicy { get; set; }
```

虚拟处理器在虚拟 L3 缓存域间的分布策略；null 表示未取得。

<a id="api-bd8988e757bf36e7"></a>

##### Limit · 生成属性

```csharp
public ulong Limit { get; set; }
```

处理器资源上限的原始值，单位为 0.001%；100000 表示 100%。

<a id="api-899ac1a52ff26036"></a>

##### LpiMode · 生成属性

```csharp
public LpiMode? LpiMode { get; set; }
```

局部外设中断模式；null 表示未取得。

<a id="api-fda2f43a0b9fc90e"></a>

##### MaxClusterCountPerSocket · 生成属性

```csharp
public uint? MaxClusterCountPerSocket { get; set; }
```

每个虚拟处理器插槽的最大簇数量；null 表示未取得。

<a id="api-069a7d0f3dd8abce"></a>

##### MaxHwIsolatedGuests · 生成属性

```csharp
public uint? MaxHwIsolatedGuests { get; set; }
```

允许的硬件隔离来宾最大数量；null 表示未取得。

<a id="api-63406158130c178c"></a>

##### MaxNumaNodesPerSocket · 生成属性

```csharp
public ulong? MaxNumaNodesPerSocket { get; set; }
```

每个虚拟插槽的最大 NUMA 节点数量；null 表示未取得。

<a id="api-89caf851e7e8b313"></a>

##### MaxProcessorCountPerL3 · 生成属性

```csharp
public uint? MaxProcessorCountPerL3 { get; set; }
```

每个虚拟 L3 缓存域的最大处理器数量；null 表示未取得。

<a id="api-1e6b72d3da610bc9"></a>

##### MaxProcessorsPerNumaNode · 生成属性

```csharp
public ulong? MaxProcessorsPerNumaNode { get; set; }
```

每个虚拟 NUMA 节点的最大处理器数量；null 表示未取得。

<a id="api-fe193c0b8ccbb9bd"></a>

##### PageShatterMode · 生成属性

```csharp
public PageShatterMode? PageShatterMode { get; set; }
```

二级地址转换的大页拆分缓解策略；null 表示未取得。

<a id="api-b588b091ca89a2a5"></a>

##### PerfCpuAutonomousActivityWindow · 生成属性

```csharp
public uint? PerfCpuAutonomousActivityWindow { get; set; }
```

宿主自主活动窗口的原始编码，不在模型中换算时间单位；null 表示未取得。

<a id="api-e367485d17c3404c"></a>

##### PerfCpuEnergyPerformancePreference · 生成属性

```csharp
public uint? PerfCpuEnergyPerformancePreference { get; set; }
```

宿主性能与能效偏好的原始编码；null 表示未取得。

<a id="api-58a607b2bc9bc394"></a>

##### PerfCpuFreqCapMhz · 生成属性

```csharp
public uint? PerfCpuFreqCapMhz { get; set; }
```

每虚拟机处理器频率上限，单位为 MHz；null 表示未取得。

<a id="api-f5e33988371a6362"></a>

##### PerfCpuFreqDesiredMhz · 生成属性

```csharp
public uint? PerfCpuFreqDesiredMhz { get; set; }
```

每虚拟机期望处理器频率，单位为 MHz；null 表示未取得。

<a id="api-87ef13670ae04326"></a>

##### PerfCpuFreqMinMhz · 生成属性

```csharp
public uint? PerfCpuFreqMinMhz { get; set; }
```

每虚拟机处理器频率下限，单位为 MHz；null 表示未取得。

<a id="api-fa443a55db63001b"></a>

##### PerfCpuIgnoreHostMaxFrequency · 生成属性

```csharp
public bool? PerfCpuIgnoreHostMaxFrequency { get; set; }
```

是否忽略宿主最大频率限制；null 表示未取得。

<a id="api-6ee4358f8a53aaa4"></a>

##### PhysicalAddressWidth · 生成属性

```csharp
public uint? PhysicalAddressWidth { get; set; }
```

来宾物理地址位宽，单位为位；null 表示未取得。

<a id="api-2ce9d3e0067c7873"></a>

##### RelativeWeight · 生成属性

```csharp
public int RelativeWeight { get; set; }
```

处理器竞争时的相对权重。

<a id="api-9774ba25ec4a2e4e"></a>

##### Reservation · 生成属性

```csharp
public ulong Reservation { get; set; }
```

保留处理器资源的原始值，单位为 0.001%；100000 表示 100%。

<a id="api-3a53eedde34c1330"></a>

##### SmtMode · 生成属性

```csharp
public SmtMode? SmtMode { get; set; }
```

向来宾呈现的同时多线程模式；null 表示未取得。

<a id="api-e013d390f93853f5"></a>

##### SupportedCapabilities · 属性

```csharp
public IReadOnlySet<VmProcessorCapability> SupportedCapabilities { get; }
```

本次查询中宿主提供的处理器设置项，只读且不随设置值是否为 null 改变。修改是否成功仍受运行状态、配置版本和硬件条件约束。

#### 方法说明

<a id="api-34be76ae19fd66ec"></a>

##### Supports · 方法

```csharp
public bool Supports(VmProcessorCapability capability);
```

判断本次查询是否提供某项处理器设置字段；与当前值是否为 null 无关，不执行宿主查询或写入。

| 参数 | 说明 |
| --- | --- |
| `capability` | 要判断的业务设置项。 |

返回：本次查询包含该设置项时为 true；未查询或未知能力值返回 false。

<a id="api-3580d41605c9185d"></a>

### `VmScreenshotImage` 类

一张独立保存的截图，包含尺寸和图像字节，可转换为 JPEG。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 视频 |
| 源文件 | ` Business/Video/VmScreenshotImage.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Height { get; } `](#api-303b9cfcb76974ee) | 截图的高度。 |
| [` public bool IsEncoded { get; } `](#api-b9ae04ef49079b14) | 图像字节是否保存为编码图片。 |
| [` public int Stride { get; } `](#api-bf981ee5c71326cf) | 原始像素图每行占用的字节数。 |
| [` public int Width { get; } `](#api-0b3eb7c06cf7b62d) | 截图的宽度。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static VmScreenshotImage FromEncoded(byte[] data) `](#api-34e11334a6e078f0) | 从已有图片数据创建图像，保留原来的编码格式。 |
| [` public static VmScreenshotImage FromRgb565(byte[] data, int width, int height) `](#api-48da9354b4692098) | 从 RGB565 原始像素创建图像，并复制输入数组。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public byte[] CopyBytes() `](#api-62587e9c550f3337) | 取得图像内容的一份独立字节副本。 |
| [` public byte[] EncodeJpeg() `](#api-94fead54aaa4b009) | 将这张图像转换为 JPEG，便于保存或发送给网页。 |

#### 详细说明

```csharp
public sealed class VmScreenshotImage
```

调用约定：CaptureAsync 返回的图像保存 RGB565 原始像素；FromEncoded 创建的图像保存已有的图片文件字节。 用 IsEncoded 区分两者，再用 CopyBytes 取得副本。对象不需要 Dispose，也不依赖控制台连接的生命周期。

#### 属性说明

<a id="api-303b9cfcb76974ee"></a>

##### Height · 属性

```csharp
public int Height { get; }
```

截图的高度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 截图的高度。 |
| 取值与默认 | 正整数，单位为像素。 |

<a id="api-b9ae04ef49079b14"></a>

##### IsEncoded · 属性

```csharp
public bool IsEncoded { get; }
```

图像字节是否保存为编码图片。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 图像字节是否保存为编码图片。 |
| 取值与默认 | true：图片文件字节；false：从上到下排列的小端 RGB565 像素，每像素 2 字节。 |

<a id="api-bf981ee5c71326cf"></a>

##### Stride · 属性

```csharp
public int Stride { get; }
```

原始像素图每行占用的字节数。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 原始像素图每行占用的字节数。 |
| 取值与默认 | RGB565 图像为 Width &#215; 2；编码图片为 0。 |

<a id="api-0b3eb7c06cf7b62d"></a>

##### Width · 属性

```csharp
public int Width { get; }
```

截图的宽度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 截图的宽度。 |
| 取值与默认 | 正整数，单位为像素。 |

#### 静态方法说明

<a id="api-34e11334a6e078f0"></a>

##### FromEncoded · 方法

```csharp
public static VmScreenshotImage FromEncoded(byte[] data);
```

从已有图片数据创建图像，保留原来的编码格式。

| 参数 | 说明 |
| --- | --- |
| `data` | Windows 图像解码器可读取的图片字节，例如 JPEG 或 PNG；本方法复制输入数组。 |

返回：保存原编码字节和图片尺寸的图像，IsEncoded 为 true。

调用约定：读取图片取得宽高，但不会把对象保存的内容替换为原始像素。不支持或损坏的图片会由解码器抛出异常。

<a id="api-48da9354b4692098"></a>

##### FromRgb565 · 方法

```csharp
public static VmScreenshotImage FromRgb565(byte[] data, int width, int height);
```

从 RGB565 原始像素创建图像，并复制输入数组。

| 参数 | 说明 |
| --- | --- |
| `data` | 从上到下排列的小端 RGB565 数据，每个像素 2 字节、每行无填充；长度至少为 width &#215; height &#215; 2。 |
| `width` | 图像宽度，正整数，单位为像素。 |
| `height` | 图像高度，正整数，单位为像素。 |

返回：拥有独立字节副本的图像，IsEncoded 为 false。

调用约定：构造后可复用原数组。整个数组都会被复制，多余的尾部字节也会由 CopyBytes 原样返回。

- 异常 `ArgumentException`：宽高不是正数，或数组长度不足。
- 异常 `OverflowException`：计算行长度或图像长度时超出整数范围。

#### 方法说明

<a id="api-62587e9c550f3337"></a>

##### CopyBytes · 方法

```csharp
public byte[] CopyBytes();
```

取得图像内容的一份独立字节副本。

返回：IsEncoded 为 false 时是 RGB565 像素，为 true 时是原来的编码图片字节。修改返回数组不会改变此图像。

<a id="api-94fead54aaa4b009"></a>

##### EncodeJpeg · 方法

```csharp
public byte[] EncodeJpeg();
```

将这张图像转换为 JPEG，便于保存或发送给网页。

返回：新建的 JPEG 字节数组。

调用约定：使用 Windows JPEG 编码器，质量参数固定为 80。JPEG 有损且不保留透明度； 即使输入已经是 JPEG，此方法也会重新编码。想保留原文件字节时使用 CopyBytes。

<a id="api-add6eece8fedcd7e"></a>

### `VmState` 枚举

虚拟机当前的运行状态；不包含后端状态码。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmState.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Deferred = 8 `](#api-5f46e0ae4911a33b) | 操作已延期。 |
| [` FastSaved = 18 `](#api-8b3af396226136f4) | 运行状态已经快速保存。 |
| [` FastSaving = 19 `](#api-6b065d760f2e0fad) | 正在快速保存运行状态。 |
| [` ForceRestarting = 21 `](#api-1ef92fe1710af277) | 正在强制重新启动。 |
| [` ForceShuttingDown = 20 `](#api-939192b02c931266) | 正在强制关机。 |
| [` Hibernated = 22 `](#api-8a8631f4ac91a9c6) | 已经休眠。 |
| [` InTest = 7 `](#api-54ecbcb9844735eb) | 正在测试。 |
| [` MergingDisks = 12 `](#api-68677115f012a0b7) | 正在合并磁盘。 |
| [` NotApplicable = 5 `](#api-67b6f28f9b4a14ad) | 运行状态不适用。 |
| [` Other = 1 `](#api-644dc2111a8076f2) | 后端报告的其他状态。 |
| [` PoweredOff = 3 `](#api-cd4724c649c2470b) | 已经完全关机。 |
| [` Processing = 15 `](#api-d50e1b6e5eec538f) | 正在处理操作。 |
| [` Resuming = 17 `](#api-b660a74402cadc5d) | 正在恢复执行。 |
| [` Running = 2 `](#api-bf70f12a545787fd) | 正在运行。 |
| [` Saved = 6 `](#api-f4ba21bc9601aa71) | 运行状态已经保存。 |
| [` Saving = 13 `](#api-2134cf154e8d104f) | 正在保存运行状态。 |
| [` ShuttingDown = 4 `](#api-925486150fea820e) | 正在正常关机。 |
| [` Starting = 10 `](#api-ec477a8600b4533b) | 正在启动。 |
| [` Stopping = 14 `](#api-3c9d41960f633386) | 正在停止。 |
| [` Suspended = 9 `](#api-e2048656b710e81e) | 执行已经挂起。 |
| [` Suspending = 16 `](#api-757da3de21ccbe4f) | 正在挂起执行。 |
| [` Unknown = 0 `](#api-235e1476c17ca53f) | 当前状态未知或无法识别。 |
| [` WaitingToStart = 11 `](#api-783318f7b53f6f8b) | 正在等待启动。 |

#### 详细说明

```csharp
public enum VmState
```

#### 枚举值说明

<a id="api-5f46e0ae4911a33b"></a>

##### Deferred · 枚举值

```csharp
Deferred = 8
```

操作已延期。

<a id="api-8b3af396226136f4"></a>

##### FastSaved · 枚举值

```csharp
FastSaved = 18
```

运行状态已经快速保存。

<a id="api-6b065d760f2e0fad"></a>

##### FastSaving · 枚举值

```csharp
FastSaving = 19
```

正在快速保存运行状态。

<a id="api-1ef92fe1710af277"></a>

##### ForceRestarting · 枚举值

```csharp
ForceRestarting = 21
```

正在强制重新启动。

<a id="api-939192b02c931266"></a>

##### ForceShuttingDown · 枚举值

```csharp
ForceShuttingDown = 20
```

正在强制关机。

<a id="api-8a8631f4ac91a9c6"></a>

##### Hibernated · 枚举值

```csharp
Hibernated = 22
```

已经休眠。

<a id="api-54ecbcb9844735eb"></a>

##### InTest · 枚举值

```csharp
InTest = 7
```

正在测试。

<a id="api-68677115f012a0b7"></a>

##### MergingDisks · 枚举值

```csharp
MergingDisks = 12
```

正在合并磁盘。

<a id="api-67b6f28f9b4a14ad"></a>

##### NotApplicable · 枚举值

```csharp
NotApplicable = 5
```

运行状态不适用。

<a id="api-644dc2111a8076f2"></a>

##### Other · 枚举值

```csharp
Other = 1
```

后端报告的其他状态。

<a id="api-cd4724c649c2470b"></a>

##### PoweredOff · 枚举值

```csharp
PoweredOff = 3
```

已经完全关机。

<a id="api-d50e1b6e5eec538f"></a>

##### Processing · 枚举值

```csharp
Processing = 15
```

正在处理操作。

<a id="api-b660a74402cadc5d"></a>

##### Resuming · 枚举值

```csharp
Resuming = 17
```

正在恢复执行。

<a id="api-bf70f12a545787fd"></a>

##### Running · 枚举值

```csharp
Running = 2
```

正在运行。

<a id="api-f4ba21bc9601aa71"></a>

##### Saved · 枚举值

```csharp
Saved = 6
```

运行状态已经保存。

<a id="api-2134cf154e8d104f"></a>

##### Saving · 枚举值

```csharp
Saving = 13
```

正在保存运行状态。

<a id="api-925486150fea820e"></a>

##### ShuttingDown · 枚举值

```csharp
ShuttingDown = 4
```

正在正常关机。

<a id="api-ec477a8600b4533b"></a>

##### Starting · 枚举值

```csharp
Starting = 10
```

正在启动。

<a id="api-3c9d41960f633386"></a>

##### Stopping · 枚举值

```csharp
Stopping = 14
```

正在停止。

<a id="api-e2048656b710e81e"></a>

##### Suspended · 枚举值

```csharp
Suspended = 9
```

执行已经挂起。

<a id="api-757da3de21ccbe4f"></a>

##### Suspending · 枚举值

```csharp
Suspending = 16
```

正在挂起执行。

<a id="api-235e1476c17ca53f"></a>

##### Unknown · 枚举值

```csharp
Unknown = 0
```

当前状态未知或无法识别。

<a id="api-783318f7b53f6f8b"></a>

##### WaitingToStart · 枚举值

```csharp
WaitingToStart = 11
```

正在等待启动。

<a id="api-5f84f3e0c5a86728"></a>

### `VmStorageAttachment` 类

VM 存储清单项：物理磁盘或虚拟磁盘均统一表达。 提供介质身份、容量、控制器位置与当前配置。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Models ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmStorageAttachment.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int ControllerLocation { get; set; } `](#api-c2bc67d033829454) | 磁盘在控制器上的槽位编号。 |
| [` public int ControllerNumber { get; set; } `](#api-b4ba7a82a21ca97c) | 控制器编号。 |
| [` public string ControllerType { get; set; } = string.Empty `](#api-eb632a38f93b026c) | 控制器类型标识，例如 IDE 或 SCSI。 |
| [` public string DeviceToken { get; init; } `](#api-d57671d37b442124) | VM、控制器和驱动器的身份摘要；修改介质路径时原样回传，可序列化。 |
| [` public string DiskModel { get; set; } = string.Empty `](#api-83ebea6f08e86fbd) | 物理磁盘型号的显示名称。 |
| [` public int DiskNumber { get; set; } `](#api-199e7a4545566f31) | 宿主物理磁盘编号，用于物理盘来源。 |
| [` public double DiskSizeGiB { get; set; } `](#api-3715fa6fea736708) | 物理设备为宿主磁盘容量，文件介质为文件长度，单位 GiB（1024&#179; 字节）；文件长度不是虚拟容量或来宾已用空间，默认 0 也可能表示未读到。 |
| [` public string DiskType { get; set; } = string.Empty `](#api-74f31f37020a544d) | 介质来源类别：Physical 为物理设备，Virtual 为文件介质，Empty 为未挂载介质。 |
| [` public string DriveType { get; set; } = string.Empty `](#api-0b2a42ad91c617db) | 驱动器类别：HardDisk 为硬盘，DvdDrive 为光驱。 |
| [` public bool HasIsoMedia { get; } `](#api-ba9f8086356c7996) | 是否为已设置非空介质路径的虚拟光驱。 |
| [` public bool IsPassthroughStale { get; set; } `](#api-6f4be459138d6cc7) | 物理盘直通引用是否失效，例如磁盘被联机后从可直通资源池消失。 |
| [` public string PathOrDiskNumber { get; set; } = string.Empty `](#api-770913173f000731) | 虚拟介质文件路径，或 PhysicalDisk 加宿主磁盘编号的文本；空光驱可为空。 |
| [` public string SelectionToken { get; init; } `](#api-472909aef47bc144) | 当前 VM、挂接和介质配置的不透明身份摘要；移除时原样回传，可随模型序列化，不得自行构造或修改。 |
| [` public string SerialNumber { get; set; } = string.Empty `](#api-865e2d3c0577d619) | 物理磁盘序列号。 |

#### 详细说明

```csharp
public partial class VmStorageAttachment : ObservableObject
```

#### 属性说明

<a id="api-c2bc67d033829454"></a>

##### ControllerLocation · 生成属性

```csharp
public int ControllerLocation { get; set; }
```

磁盘在控制器上的槽位编号。

<a id="api-b4ba7a82a21ca97c"></a>

##### ControllerNumber · 生成属性

```csharp
public int ControllerNumber { get; set; }
```

控制器编号。

<a id="api-eb632a38f93b026c"></a>

##### ControllerType · 生成属性

```csharp
public string ControllerType { get; set; } = string.Empty;
```

控制器类型标识，例如 IDE 或 SCSI。

<a id="api-d57671d37b442124"></a>

##### DeviceToken · 属性

```csharp
public string DeviceToken { get; init; }
```

VM、控制器和驱动器的身份摘要；修改介质路径时原样回传，可序列化。

调用约定：不包含当前介质路径，同一驱动器的路径修改仍按本次请求覆盖。不得自行构造；无法识别后端复用完全相同设备身份的重建。移除整个设备仍使用含介质配置的 SelectionToken。

<a id="api-83ebea6f08e86fbd"></a>

##### DiskModel · 生成属性

```csharp
public string DiskModel { get; set; } = string.Empty;
```

物理磁盘型号的显示名称。

<a id="api-199e7a4545566f31"></a>

##### DiskNumber · 生成属性

```csharp
public int DiskNumber { get; set; }
```

宿主物理磁盘编号，用于物理盘来源。

<a id="api-3715fa6fea736708"></a>

##### DiskSizeGiB · 生成属性

```csharp
public double DiskSizeGiB { get; set; }
```

物理设备为宿主磁盘容量，文件介质为文件长度，单位 GiB（1024&#179; 字节）；文件长度不是虚拟容量或来宾已用空间，默认 0 也可能表示未读到。

<a id="api-74f31f37020a544d"></a>

##### DiskType · 生成属性

```csharp
public string DiskType { get; set; } = string.Empty;
```

介质来源类别：Physical 为物理设备，Virtual 为文件介质，Empty 为未挂载介质。

<a id="api-0b2a42ad91c617db"></a>

##### DriveType · 生成属性

```csharp
public string DriveType { get; set; } = string.Empty;
```

驱动器类别：HardDisk 为硬盘，DvdDrive 为光驱。

<a id="api-ba9f8086356c7996"></a>

##### HasIsoMedia · 属性

```csharp
public bool HasIsoMedia { get; }
```

是否为已设置非空介质路径的虚拟光驱。

<a id="api-6f4be459138d6cc7"></a>

##### IsPassthroughStale · 生成属性

```csharp
public bool IsPassthroughStale { get; set; }
```

物理盘直通引用是否失效，例如磁盘被联机后从可直通资源池消失。

<a id="api-770913173f000731"></a>

##### PathOrDiskNumber · 生成属性

```csharp
public string PathOrDiskNumber { get; set; } = string.Empty;
```

虚拟介质文件路径，或 PhysicalDisk 加宿主磁盘编号的文本；空光驱可为空。数字编号另见 DiskNumber。

<a id="api-472909aef47bc144"></a>

##### SelectionToken · 属性

```csharp
public string SelectionToken { get; init; }
```

当前 VM、挂接和介质配置的不透明身份摘要；移除时原样回传，可随模型序列化，不得自行构造或修改。

调用约定：这不是授权凭证，也不包含可调用的 WMI 路径。配置改变后旧值失效；未查询而手工构造的模型不能用于移除。无法识别完全相同配置的外部重建。

<a id="api-865e2d3c0577d619"></a>

##### SerialNumber · 生成属性

```csharp
public string SerialNumber { get; set; } = string.Empty;
```

物理磁盘序列号。

<a id="api-ba368a7c161ab2aa"></a>

### `ArpSnoopService` 类

捕获本机 ARP 流量并维护 MAC 到 IPv4 的近期映射，作为来宾上报地址之外的辅助来源。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / ARP 嗅探 |
| 源文件 | ` Management/Host/Arp/ArpSnoopService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public static ArpSnoopService Instance { get; } = new() `](#api-adb15ea578d3fd20) | 本进程共享的 ARP 嗅探实例；读取不会启动捕获，任一使用者释放它都会终止该共享实例。 |
| [` public bool IsAvailable { get; } `](#api-c7c940e04990f14e) | 本实例的捕获流是否正在运行；初始化失败、流停止或最终关闭后为 false。 |
| [` public ApiResponse? LastFailure { get; } `](#api-9ad48d2131e55733) | 首次捕获或清理失败的诊断；尚未失败时为 null。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public void Dispose() `](#api-13435e0342a25628) | 最终停止捕获并清理本实例的独立会话和实时流，不修改其他程序的过滤器或会话；以后不能重新启动。 |
| [` public void Start() `](#api-6bbdff3f2ad28f04) | 启动本实例的独立 ARP 捕获；重复调用不会创建第二套资源。 |
| [` public bool TryGetIp(string? mac, out string ip) `](#api-ae18054ac4a66479) | 查询指定 MAC 尚未过期的 IPv4 缓存，不主动发包或等待新地址。 |

#### 详细说明

```csharp
public sealed unsafe class ArpSnoopService : IDisposable
```

调用约定：取得 `ArpSnoopService.Instance` 后调用 `ArpSnoopService.Start` 开始捕获，再通过 `ArpSnoopService.TryGetIp` 按 MAC 读取近期 IPv4 地址。需要持续更新地址的宿主应用应在整个使用期间保持捕获，结束时调用 `ArpSnoopService.Dispose`。



Start 可重复调用；启动失败或 Dispose 后，同一实例不能重新启动。状态见 `ArpSnoopService.IsAvailable`，错误见 `ArpSnoopService.LastFailure`。每条缓存记录有效期为 5 分钟。



使用微软公开 Packet Monitor API 的独立会话和实时流，需要管理员权限及支持该 API 的 Windows。不同进程可独立捕获，关闭只释放本实例的资源。公开 API 不可用时不回退到私有接口；其他 IP 来源不受影响。



缓存按 MAC 保存最近观察到的一个 IPv4 地址，跨交换机或 VLAN 使用相同 MAC 时会相互覆盖。

#### 属性说明

<a id="api-adb15ea578d3fd20"></a>

##### Instance · 属性

```csharp
public static ArpSnoopService Instance { get; } = new();
```

本进程共享的 ARP 嗅探实例；读取不会启动捕获，任一使用者释放它都会终止该共享实例。

<a id="api-c7c940e04990f14e"></a>

##### IsAvailable · 属性

```csharp
public bool IsAvailable { get; }
```

本实例的捕获流是否正在运行；初始化失败、流停止或最终关闭后为 false。

<a id="api-9ad48d2131e55733"></a>

##### LastFailure · 属性

```csharp
public ApiResponse? LastFailure { get; }
```

首次捕获或清理失败的诊断；尚未失败时为 null。Code 为 HRESULT，ErrorKind 区分权限不足、不支持和执行失败。

调用约定：Start 初始化失败通过此属性报告，正常 Dispose 不产生错误。

#### 方法说明

<a id="api-13435e0342a25628"></a>

##### Dispose · 方法

```csharp
public void Dispose();
```

最终停止捕获并清理本实例的独立会话和实时流，不修改其他程序的过滤器或会话；以后不能重新启动。

<a id="api-6bbdff3f2ad28f04"></a>

##### Start · 方法

```csharp
public void Start();
```

启动本实例的独立 ARP 捕获；重复调用不会创建第二套资源。

调用约定：同步准备捕获资源，不等待报文到达。初始化失败保留 IsAvailable=false 并设置 LastFailure；初始化失败或 Dispose 后不支持在同一实例重新启动。

<a id="api-ae18054ac4a66479"></a>

##### TryGetIp · 方法

```csharp
public bool TryGetIp(string? mac, out string ip);
```

查询指定 MAC 尚未过期的 IPv4 缓存，不主动发包或等待新地址。

| 参数 | 说明 |
| --- | --- |
| `mac` | 待查询的 MAC；允许空值，格式会归一化。 |
| `ip` | 成功时接收 IPv4 地址；失败时为空字符串。 |

返回：找到有效缓存时为 true；否则为 false 且 ip 为空。

<a id="api-34f8d5f444bc1540"></a>

### `ConsoleAudioFormat` record 类

一批音频采样的采样率、声道数和数值格式。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 音频 |
| 源文件 | ` Business/Audio/ConsoleAudioFormat.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleAudioFormat(int SampleRate, int Channels, ConsoleAudioSampleFormat SampleFormat) `](#api-2ad1caf2a831f077) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Channels { get; init; } `](#api-29daddd109f93a79) | 每个采样时刻的声道数量。 |
| [` public ConsoleAudioSampleFormat SampleFormat { get; init; } `](#api-dbde7700b3d73cf0) | PCM 中单个声道采样值的编码格式。 |
| [` public int SampleRate { get; init; } `](#api-18d9057a80f614d6) | 每个声道每秒的采样数。 |

#### 详细说明

```csharp
public sealed record ConsoleAudioFormat(int SampleRate, int Channels, ConsoleAudioSampleFormat SampleFormat)
```

| 参数 | 说明 |
| --- | --- |
| `SampleRate` | 每个声道每秒的采样数。 |
| `Channels` | 每个采样时刻的声道数量。 |
| `SampleFormat` | PCM 中单个声道采样值的编码格式。 |

调用约定：采样按声道交错排列：双声道依次保存“第 1 次采样的左、右声道，第 2 次采样的左、右声道……”。 同一采样时刻的全部声道组成一个采样帧。



例如 48 kHz、双声道、16 位整数，每个采样帧占 4 字节；10 毫秒包含 480 个采样帧，共 1920 字节。



此记录只描述格式，构造时不检查数值。ConsoleAudioPacket 会检查格式及数据长度。 当前没有可用的控制台音频后端；多声道的声道顺序仍需在接入后端时确定。

#### 构造函数说明

<a id="api-2ad1caf2a831f077"></a>

##### ConsoleAudioFormat · 主构造器

```csharp
public ConsoleAudioFormat(int SampleRate, int Channels, ConsoleAudioSampleFormat SampleFormat);
```

| 参数 | 说明 |
| --- | --- |
| `SampleRate` | 每个声道每秒的采样数。 |
| `Channels` | 每个采样时刻的声道数量。 |
| `SampleFormat` | PCM 中单个声道采样值的编码格式。 |

#### 属性说明

<a id="api-29daddd109f93a79"></a>

##### Channels · record 属性

```csharp
public int Channels { get; init; }
```

每个采样时刻的声道数量。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 每个采样时刻的声道数量。 |
| 取值与默认 | 正整数。例如 1 为单声道，2 为双声道。 |

<a id="api-dbde7700b3d73cf0"></a>

##### SampleFormat · record 属性

```csharp
public ConsoleAudioSampleFormat SampleFormat { get; init; }
```

PCM 中单个声道采样值的编码格式。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | PCM 中单个声道采样值的编码格式。 |

<a id="api-18d9057a80f614d6"></a>

##### SampleRate · record 属性

```csharp
public int SampleRate { get; init; }
```

每个声道每秒的采样数。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 每个声道每秒的采样数。 |
| 取值与默认 | 正整数，单位为 Hz。例如 48000 为 48 kHz。 |

<a id="api-dca91d63ad5a1a97"></a>

### `ConsoleAudioPacket` 类

一段音频采样及其格式、包序号和时间戳。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 音频 |
| 源文件 | ` Business/Audio/ConsoleAudioPacket.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleAudioPacket(ConsoleAudioFormat format, ReadOnlySpan<byte> samples, long sequence, TimeSpan timestamp) `](#api-7ced884f097119dd) | 复制一段完整的 PCM 采样，创建可独立保留的音频包。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleAudioFormat Format { get; } `](#api-13883892ef8a1bac) | 解释当前采样数据所使用的音频格式。 |
| [` public ReadOnlySpan<byte> Samples { get; } `](#api-e594e516c61cdf5a) | 音频包独立持有的原始 PCM 采样。 |
| [` public long Sequence { get; } `](#api-42025f5d02f86b6d) | 当前音频流中的包序号。 |
| [` public TimeSpan Timestamp { get; } `](#api-6938bca8c47d1496) | 当前音频流中的相对时间。 |

#### 详细说明

```csharp
public sealed class ConsoleAudioPacket
```

调用约定：构造时复制采样数据，因此可在回调返回或连接关闭后继续使用，不需要 Dispose。 当前此类型是音频接口的数据约定，内置控制台后端尚未输出音频包。

示例：

构造一段 10 毫秒的双声道静音，说明数据长度如何由格式计算。此例只构造数据，不代表已有音频连接。



```csharp
using ExHyperV.Services;
public static class AudioPacketExample
{
    public static ConsoleAudioPacket Silence()
    {
        var format = new ConsoleAudioFormat(48000, 2,
            ConsoleAudioSampleFormat.Signed16LittleEndian);
        byte[] samples = new byte[480 * 2 * 2];
        return new ConsoleAudioPacket(format, samples, 0, TimeSpan.Zero);
    }
}
```

#### 构造函数说明

<a id="api-7ced884f097119dd"></a>

##### ConsoleAudioPacket · 构造器

```csharp
public ConsoleAudioPacket(ConsoleAudioFormat format, ReadOnlySpan<byte> samples, long sequence, TimeSpan timestamp);
```

复制一段完整的 PCM 采样，创建可独立保留的音频包。

| 参数 | 说明 |
| --- | --- |
| `format` | 这段数据的采样率、声道数和数值格式。 |
| `samples` | 按声道交错排列的原始采样；长度必须为“声道数 &#215; 每个采样字节数”的整数倍，可为空。 |
| `sequence` | 当前音频流中的包序号，非负；由生产者安排顺序。 |
| `timestamp` | 当前音频流中的相对时间，非负；更具体的计时起点见 Timestamp。 |

调用约定：返回后可以复用原采样内存。构造器只检查本包的格式、长度、序号和时间是否合法，不检查与上一包的先后关系。

- 异常 `ArgumentNullException`：format 为 null。
- 异常 `ArgumentOutOfRangeException`：格式无效、声道数过大，或 sequence、timestamp 为负。
- 异常 `ArgumentException`：数据末尾缺少一个完整采样帧所需的字节。

#### 属性说明

<a id="api-13883892ef8a1bac"></a>

##### Format · 属性

```csharp
public ConsoleAudioFormat Format { get; }
```

解释当前采样数据所使用的音频格式。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 解释当前采样数据所使用的音频格式。 |

<a id="api-e594e516c61cdf5a"></a>

##### Samples · 属性

```csharp
public ReadOnlySpan<byte> Samples { get; }
```

音频包独立持有的原始 PCM 采样。

调用约定：构造时已复制，可在回调结束和连接关闭后继续读取。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 音频包独立持有的原始 PCM 采样。 |
| 取值与默认 | 按声道交错排列；字节数为“声道数 &#215; 每采样字节数”的整数倍，可为空。 |

<a id="api-42025f5d02f86b6d"></a>

##### Sequence · 属性

```csharp
public long Sequence { get; }
```

当前音频流中的包序号。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前音频流中的包序号。 |
| 取值与默认 | 非负整数，由生产者安排；与视频帧序号分别计数。 |

<a id="api-6938bca8c47d1496"></a>

##### Timestamp · 属性

```csharp
public TimeSpan Timestamp { get; }
```

当前音频流中的相对时间。

调用约定：音频后端尚未接入，时间零点和采样时刻的对应关系尚未约定，当前不能用于音画同步。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前音频流中的相对时间。 |
| 取值与默认 | 非负时间间隔。 |

<a id="api-bfefa55b473f5a20"></a>

### `ConsoleAudioSampleFormat` 枚举

PCM 音频中每个采样数值的编码方式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 音频 |
| 源文件 | ` Business/Audio/ConsoleAudioSampleFormat.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Float32LittleEndian = 1 `](#api-9e9803e9d467e9c6) | 每个采样占 4 字节，小端 IEEE 754 单精度浮点数，通常用 -1～1 表示完整振幅。 |
| [` Signed16LittleEndian = 0 `](#api-06aba26d855a7f5f) | 每个采样占 2 字节，小端有符号 16 位整数，数值范围 -32768～32767。 |

#### 详细说明

```csharp
public enum ConsoleAudioSampleFormat
```

调用约定：采样按小端字节序存储，以 ConsoleAudioFormat 指定的声道顺序交错排列。

#### 枚举值说明

<a id="api-9e9803e9d467e9c6"></a>

##### Float32LittleEndian · 枚举值

```csharp
Float32LittleEndian = 1
```

每个采样占 4 字节，小端 IEEE 754 单精度浮点数，通常用 -1～1 表示完整振幅。

<a id="api-06aba26d855a7f5f"></a>

##### Signed16LittleEndian · 枚举值

```csharp
Signed16LittleEndian = 0
```

每个采样占 2 字节，小端有符号 16 位整数，数值范围 -32768～32767。

<a id="api-0bbffb055adaf595"></a>

### `ConsoleBackendInfo` record 类

一个控制台后端的名称、实现状态和功能列表。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 连接 |
| 源文件 | ` Business/Connection/ConsoleBackendInfo.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleBackendInfo(string BackendId, string Name, bool IsImplemented, ConsoleCapabilities Capabilities, string? UnavailableReason) `](#api-b32c829aa23387d4) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string BackendId { get; init; } `](#api-871059250bcef465) | 创建连接时用于选择后端的标识。 |
| [` public ConsoleCapabilities Capabilities { get; init; } `](#api-e8cfb4ea6a1af103) | 后端已实现的交互能力，可按位组合。 |
| [` public bool IsImplemented { get; init; } `](#api-b5211152e6ae9611) | 当前 Core 是否实现了此后端。 |
| [` public string Name { get; init; } `](#api-afac97bdb3e82f59) | 后端的显示名称。 |
| [` public string? UnavailableReason { get; init; } `](#api-25f4b7669d43238c) | 后端尚未实现的原因。 |

#### 详细说明

```csharp
public sealed record ConsoleBackendInfo(string BackendId, string Name, bool IsImplemented,
    ConsoleCapabilities Capabilities, string? UnavailableReason)
```

| 参数 | 说明 |
| --- | --- |
| `BackendId` | 创建连接时用于选择后端的标识。 |
| `Name` | 后端的显示名称。 |
| `IsImplemented` | 当前 Core 是否实现了此后端。 |
| `Capabilities` | 后端已实现的交互能力，可按位组合。 |
| `UnavailableReason` | 后端尚未实现的原因。 |

调用约定：这些信息来自内置目录，适合用来展示连接方式。 剪贴板等功能是否已经可以使用，还要在创建连接后查看相应接口的 IsAvailable。

#### 构造函数说明

<a id="api-b32c829aa23387d4"></a>

##### ConsoleBackendInfo · 主构造器

```csharp
public ConsoleBackendInfo(string BackendId, string Name, bool IsImplemented, ConsoleCapabilities Capabilities, string? UnavailableReason);
```

| 参数 | 说明 |
| --- | --- |
| `BackendId` | 创建连接时用于选择后端的标识。 |
| `Name` | 后端的显示名称。 |
| `IsImplemented` | 当前 Core 是否实现了此后端。 |
| `Capabilities` | 后端已实现的交互能力，可按位组合。 |
| `UnavailableReason` | 后端尚未实现的原因。 |

#### 属性说明

<a id="api-871059250bcef465"></a>

##### BackendId · record 属性

```csharp
public string BackendId { get; init; }
```

创建连接时用于选择后端的标识。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 创建连接时用于选择后端的标识。 |
| 取值与默认 | `RdpBasic`、`RdpEnhanced`、`Nimue`；区分大小写。 |

<a id="api-e8cfb4ea6a1af103"></a>

##### Capabilities · record 属性

```csharp
public ConsoleCapabilities Capabilities { get; init; }
```

后端已实现的交互能力，可按位组合。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 后端已实现的交互能力，可按位组合。 |

<a id="api-b5211152e6ae9611"></a>

##### IsImplemented · record 属性

```csharp
public bool IsImplemented { get; init; }
```

当前 Core 是否实现了此后端。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前 Core 是否实现了此后端。 |
| 取值与默认 | true：可以创建连接；false：仅登记标识。实际连接结果由目标虚拟机和通道状态决定。 |

<a id="api-afac97bdb3e82f59"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

后端的显示名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 后端的显示名称。 |
| 取值与默认 | 例如 `RDP Basic`。 |

<a id="api-25f4b7669d43238c"></a>

##### UnavailableReason · record 属性

```csharp
public string? UnavailableReason { get; init; }
```

后端尚未实现的原因。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 后端尚未实现的原因。 |
| 取值与默认 | 文字说明；已实现时为 null。 |

<a id="api-a7effae4ff578e31"></a>

### `ConsoleCapabilities` 枚举

控制台后端实现的功能，可使用按位运算组合和检查。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 连接 |
| 源文件 | ` Business/Connection/ConsoleCapabilities.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` AudioInput = 32 `](#api-93a45b37927396bc) | 历史预留标志，不属于当前功能范围；内置后端不声明此能力。 |
| [` AudioOutput = 16 `](#api-913452124f8ec79c) | 预留的音频接收标志；当前内置后端不声明此能力。 |
| [` Clipboard = 4 `](#api-292c72e6710fb71b) | 与来宾交换剪贴板内容。 |
| [` DisplayResize = 8 `](#api-afcde72f260256e7) | 请求调整来宾桌面的分辨率和缩放比例。 |
| [` Input = 2 `](#api-7e464082a6488718) | 发送键鼠输入。 |
| [` None = 0 `](#api-d780f671659a09a0) | 没有已接入能力。 |
| [` Video = 1 `](#api-1b1b43f19d13ea4d) | 接收画面。 |

#### 详细说明

```csharp
public enum ConsoleCapabilities
```

调用约定：例如 (capabilities &amp; ConsoleCapabilities.Clipboard) != 0 表示实现了剪贴板。 这份列表描述后端能力；某次连接的通道是否就绪，由对应能力接口报告。

#### 枚举值说明

<a id="api-93a45b37927396bc"></a>

##### AudioInput · 枚举值

```csharp
AudioInput = 32
```

历史预留标志，不属于当前功能范围；内置后端不声明此能力。

<a id="api-913452124f8ec79c"></a>

##### AudioOutput · 枚举值

```csharp
AudioOutput = 16
```

预留的音频接收标志；当前内置后端不声明此能力。

<a id="api-292c72e6710fb71b"></a>

##### Clipboard · 枚举值

```csharp
Clipboard = 4
```

与来宾交换剪贴板内容。

<a id="api-afcde72f260256e7"></a>

##### DisplayResize · 枚举值

```csharp
DisplayResize = 8
```

请求调整来宾桌面的分辨率和缩放比例。

<a id="api-7e464082a6488718"></a>

##### Input · 枚举值

```csharp
Input = 2
```

发送键鼠输入。

<a id="api-d780f671659a09a0"></a>

##### None · 枚举值

```csharp
None = 0
```

没有已接入能力。

<a id="api-1b1b43f19d13ea4d"></a>

##### Video · 枚举值

```csharp
Video = 1
```

接收画面。

<a id="api-382082b2780091a0"></a>

### `ConsoleClipboardData` record 类

一次剪贴板内容，以及这份内容可供接收方选择的不同格式。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 剪贴板 |
| 源文件 | ` Business/Clipboard/ConsoleClipboardData.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleClipboardData(IReadOnlyList<ConsoleClipboardFormat> Formats) `](#api-4d93c6467d333027) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<ConsoleClipboardFormat> Formats { get; init; } `](#api-e1fcb60beaee51c4) | 同一份剪贴板内容提供的格式列表。 |

#### 详细说明

```csharp
public sealed record ConsoleClipboardData(IReadOnlyList<ConsoleClipboardFormat> Formats)
```

| 参数 | 说明 |
| --- | --- |
| `Formats` | 同一份剪贴板内容提供的格式列表。 |

调用约定：例如同一段网页内容可同时提供纯文本和 HTML，接收方选择能处理的格式。



构造记录时保存原集合；SetClipboard 在发送时检查并复制。提交期间保持集合及数据不变。



从 ClipboardReceived 收到的数据由托管内存持有，可在回调结束后继续使用，无需 Dispose。 接收列表只包含成功取得的受支持格式；空列表也可能表示格式不受支持或取数失败，不能据此认定来宾清空了剪贴板。

#### 构造函数说明

<a id="api-4d93c6467d333027"></a>

##### ConsoleClipboardData · 主构造器

```csharp
public ConsoleClipboardData(IReadOnlyList<ConsoleClipboardFormat> Formats);
```

| 参数 | 说明 |
| --- | --- |
| `Formats` | 同一份剪贴板内容提供的格式列表。 |

#### 属性说明

<a id="api-e1fcb60beaee51c4"></a>

##### Formats · record 属性

```csharp
public IReadOnlyList<ConsoleClipboardFormat> Formats { get; init; }
```

同一份剪贴板内容提供的格式列表。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 同一份剪贴板内容提供的格式列表。 |
| 取值与默认 | 0～3 种受支持且不重复的格式。每种数据最多 16 MiB，连同编号、名称、长度信息的传输总量最多 32 MiB。 |

<a id="api-30176205ed3087ed"></a>

### `ConsoleClipboardFormat` record 类

剪贴板内容的一种表示方式，由格式编号、名称和原始字节组成。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 剪贴板 |
| 源文件 | ` Business/Clipboard/ConsoleClipboardFormat.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleClipboardFormat(uint Id, string? Name, ReadOnlyMemory<byte> Data) `](#api-f8356eec4d7c44d8) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ReadOnlyMemory<byte> Data { get; init; } `](#api-48b6ea504a18d284) | 该格式的原始字节，只读视图保留调用方提供的内存。 |
| [` public uint Id { get; init; } `](#api-bdb8a6ee908a44c5) | 剪贴板格式的编号。 |
| [` public string? Name { get; init; } `](#api-30821a3c68534000) | 注册剪贴板格式的名称。 |

#### 详细说明

```csharp
public sealed record ConsoleClipboardFormat(uint Id, string? Name, ReadOnlyMemory<byte> Data)
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 剪贴板格式的编号。 |
| `Name` | 注册剪贴板格式的名称。 |
| `Data` | 该格式的原始字节，只读视图保留调用方提供的内存。 |

调用约定：CF_DIB 包含位图信息头和像素数据，不含 BMP 文件头。CF_HTML 包含片段范围头和 UTF-8 内容，头中的偏移量按字节计算。 HTML 布局见 [HTML Clipboard Format](https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format)。



SetClipboard 会复制数据并检查格式编号、名称、长度和文本结束符，不会完整验证 DIB 或 HTML 的内部结构。 调用方应提供由对应格式生成器或系统剪贴板取得的合法数据。

#### 构造函数说明

<a id="api-f8356eec4d7c44d8"></a>

##### ConsoleClipboardFormat · 主构造器

```csharp
public ConsoleClipboardFormat(uint Id, string? Name, ReadOnlyMemory<byte> Data);
```

| 参数 | 说明 |
| --- | --- |
| `Id` | 剪贴板格式的编号。 |
| `Name` | 注册剪贴板格式的名称。 |
| `Data` | 该格式的原始字节，只读视图保留调用方提供的内存。 |

#### 属性说明

<a id="api-48b6ea504a18d284"></a>

##### Data · record 属性

```csharp
public ReadOnlyMemory<byte> Data { get; init; }
```

该格式的原始字节，只读视图保留调用方提供的内存。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 该格式的原始字节，只读视图保留调用方提供的内存。 |
| 取值与默认 | Unicode 文本使用 UTF-16LE 并以两个零字节结束；位图使用 CF_DIB 布局；HTML 使用含头部的 CF_HTML 布局。单种数据最多 16 MiB。 |

<a id="api-bdb8a6ee908a44c5"></a>

##### Id · record 属性

```csharp
public uint Id { get; init; }
```

剪贴板格式的编号。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 剪贴板格式的编号。 |
| 取值与默认 | 13：Unicode 文本；8：DIB 位图；0xC000～0xFFFF：HTML 使用的注册格式编号。 |

<a id="api-30821a3c68534000"></a>

##### Name · record 属性

```csharp
public string? Name { get; init; }
```

注册剪贴板格式的名称。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 注册剪贴板格式的名称。 |
| 取值与默认 | HTML 使用 `HTML Format`，区分大小写；文本和 DIB 使用 null 或空字符串。 |

<a id="api-e62d45486843fafd"></a>

### `ConsoleConnectionOptions` record 类

连接目标、初始桌面尺寸、缩放比例和连接超时。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 连接 |
| 源文件 | ` Business/Connection/ConsoleConnectionOptions.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2) `](#api-7763df3391afb936) | 等待底层连接建立的最长时间。 |
| [` public uint DesktopScale { get; init; } = 100 `](#api-83de64b0ed6709f2) | 请求的来宾桌面缩放比例。 |
| [` public int Height { get; init; } = 768 `](#api-9588f922040c059c) | 请求的来宾桌面高度。 |
| [` public Guid VmId { get; init; } `](#api-30f96f6a247d12d1) | 要连接的本机虚拟机标识。 |
| [` public int Width { get; init; } = 1024 `](#api-8dd0fb25967f65e6) | 请求的来宾桌面宽度。 |

#### 详细说明

```csharp
public sealed record ConsoleConnectionOptions
```

调用约定：后端由 `ConsoleService.CreateConnection` 的 backendId 参数选择。 当前 RDP 后端连接本机 Hyper-V 虚拟机。



Width、Height、DesktopScale 是请求值，实际画面尺寸以收到的 `ConsoleFrame` 为准。 基本会话使用来宾已有的显示模式，不能通过这些参数保证改变来宾分辨率。

#### 属性说明

<a id="api-7763df3391afb936"></a>

##### ConnectTimeout · 属性

```csharp
public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
```

等待底层连接建立的最长时间。

调用约定：从原生连接对象创建完成后计时；不包含原生库初始化、来宾登录和断开清理。超时由 Disconnected 事件报告。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 等待底层连接建立的最长时间。 |
| 取值与默认 | 大于零、不超过 2 分钟，默认 2 秒。 |

<a id="api-83de64b0ed6709f2"></a>

##### DesktopScale · 属性

```csharp
public uint DesktopScale { get; init; } = 100;
```

请求的来宾桌面缩放比例。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 请求的来宾桌面缩放比例。 |
| 取值与默认 | 100～500%，默认 100%。 |

<a id="api-9588f922040c059c"></a>

##### Height · 属性

```csharp
public int Height { get; init; } = 768;
```

请求的来宾桌面高度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 请求的来宾桌面高度。 |
| 取值与默认 | 200～8192 像素，默认 768。Width &#215; Height &#215; 4 不超过 64 MiB 减 64 字节。 |

<a id="api-30f96f6a247d12d1"></a>

##### VmId · 属性

```csharp
public Guid VmId { get; init; }
```

要连接的本机虚拟机标识。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 要连接的本机虚拟机标识。 |
| 取值与默认 | 非空 GUID，例如 `6f0c2e91-8a45-4b37-9d60-c1257e3a8f42`。 |

<a id="api-8dd0fb25967f65e6"></a>

##### Width · 属性

```csharp
public int Width { get; init; } = 1024;
```

请求的来宾桌面宽度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 请求的来宾桌面宽度。 |
| 取值与默认 | 200～8192 像素，默认 1024。Width &#215; Height &#215; 4 不超过 64 MiB 减 64 字节。 |

<a id="api-ea5482a340e8774c"></a>

### `ConsoleConnectionState` 枚举

控制台连接当前处于连接中、已连接还是断开阶段。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 连接 |
| 源文件 | ` Business/Connection/ConsoleConnectionState.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Connected = 1 `](#api-0d4fe0b7f8d1efe0) | 底层已报告连接建立，不等同于用户登录已完成。 |
| [` Connecting = 2 `](#api-00a38bbabb7b7a57) | 已开始连接，尚未报告连接建立。 |
| [` Disconnected = 0 `](#api-d980797589ccf428) | 尚未启动，或已断开并完成底层清理；断开后的对象不能重启。 |
| [` Disconnecting = 3 `](#api-5356eea13a929ee1) | 已请求断开，底层仍在退出。 |

#### 详细说明

```csharp
public enum ConsoleConnectionState
```

调用约定：新对象的初始值和完成断开后的值都是 Disconnected。 每个对象只能启动一次；重新连接需要创建新对象。

#### 枚举值说明

<a id="api-0d4fe0b7f8d1efe0"></a>

##### Connected · 枚举值

```csharp
Connected = 1
```

底层已报告连接建立，不等同于用户登录已完成。

<a id="api-00a38bbabb7b7a57"></a>

##### Connecting · 枚举值

```csharp
Connecting = 2
```

已开始连接，尚未报告连接建立。

<a id="api-d980797589ccf428"></a>

##### Disconnected · 枚举值

```csharp
Disconnected = 0
```

尚未启动，或已断开并完成底层清理；断开后的对象不能重启。

<a id="api-5356eea13a929ee1"></a>

##### Disconnecting · 枚举值

```csharp
Disconnecting = 3
```

已请求断开，底层仍在退出。

<a id="api-29568228a14c6559"></a>

### `ConsoleDisconnectInfo` record 类

连接结束时提供的诊断信息，用于显示原因或记录日志。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 连接 |
| 源文件 | ` Business/Connection/ConsoleDisconnectInfo.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleDisconnectInfo(uint ErrorCode, string? Error) `](#api-af7a6bc07d65bbd8) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? Error { get; init; } `](#api-72416d12389513d4) | 连接结束时附带的错误说明。 |
| [` public uint ErrorCode { get; init; } `](#api-da32a13ed43b2bb4) | 连接结束时的后端诊断码。 |

#### 详细说明

```csharp
public sealed record ConsoleDisconnectInfo(uint ErrorCode, string? Error)
```

| 参数 | 说明 |
| --- | --- |
| `ErrorCode` | 连接结束时的后端诊断码。 |
| `Error` | 连接结束时附带的错误说明。 |

调用约定：诊断码按后端解释，记录时一并保留 BackendId 和 Error。主动关闭和远端正常结束都可能返回 0。

#### 构造函数说明

<a id="api-af7a6bc07d65bbd8"></a>

##### ConsoleDisconnectInfo · 主构造器

```csharp
public ConsoleDisconnectInfo(uint ErrorCode, string? Error);
```

| 参数 | 说明 |
| --- | --- |
| `ErrorCode` | 连接结束时的后端诊断码。 |
| `Error` | 连接结束时附带的错误说明。 |

#### 属性说明

<a id="api-72416d12389513d4"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

连接结束时附带的错误说明。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 连接结束时附带的错误说明。 |
| 取值与默认 | 文字说明；未提供时为 null。 |

<a id="api-da32a13ed43b2bb4"></a>

##### ErrorCode · record 属性

```csharp
public uint ErrorCode { get; init; }
```

连接结束时的后端诊断码。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 连接结束时的后端诊断码。 |
| 取值与默认 | 当前 RDP 后端使用 FreeRDP 或 Win32 错误码；`0xFFFFFFFF` 为托管异常，`0xFFFFFFFE` 为连接超时，0 为未报告错误。 |

<a id="api-972d5767e28038af"></a>

### `ConsoleFrame` 类

一帧虚拟机桌面画面，包含完整像素、尺寸、序号和本次变化区域。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 视频 |
| 源文件 | ` Business/Video/ConsoleFrame.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleFrame(int width, int height, int stride, ReadOnlyMemory<byte> pixels, long sequence) `](#api-c1824b67530e7d8f) | 使用调用方提供的像素内存创建租约；任何关联租约存活期间都不得修改该内存。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public long BaseSequence { get; } `](#api-ffd9b3afb8291670) | Damage 所依据的前一帧序号。 |
| [` public int ByteLength { get; } `](#api-0dd98558052c2c13) | 整帧像素缓冲区的长度。 |
| [` public ConsoleFrameRegion Damage { get; } `](#api-c0ca95b63fdbbdea) | 相对 BaseSequence 发生变化的桌面区域。 |
| [` public bool HasFullDamage { get; } `](#api-f3cf8f02d41f9263) | 本次画面是否需要全量刷新。 |
| [` public int Height { get; } `](#api-616a1e77beb8c421) | 完整来宾桌面的高度。 |
| [` public ReadOnlySpan<byte> Pixels { get; } `](#api-0f916becd52bb8d3) | 当前帧的只读像素内存。 |
| [` public long Sequence { get; } `](#api-7ed89a64cc88291b) | 本连接生成画面时递增的帧序号。 |
| [` public int Stride { get; } `](#api-15bad73304b82dfa) | 像素缓冲区每行占用的字节数。 |
| [` public int Width { get; } `](#api-d082eed74a45c63e) | 完整来宾桌面的宽度。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public byte[] CopyPixels() `](#api-e5049e309fda705d) | 复制整帧像素，取得可以独立保存和修改的字节数组。 |
| [` public void Dispose() `](#api-9636f7084707a2fd) | 释放当前这份帧租约；重复释放没有额外效果。 |
| [` public ConsoleFrame Retain() `](#api-276168d816a67152) | 保留这帧画面，供回调结束后继续使用，不复制像素。 |

#### 详细说明

```csharp
public sealed class ConsoleFrame : IDisposable
```

调用约定：Pixels 是 BGRA32 像素：每个像素依次为蓝、绿、红、Alpha 四个字节，图像从上到下排列。 当前控制台后端的帧已包含远端光标，Alpha 为 255；自行构造的帧保留调用方提供的像素。 按 Stride 计算下一行的位置，按 Width 和 Height 取得桌面尺寸。



这个对象也负责保持像素内存有效，称为“帧租约”。FrameReceived 回调中的帧由 Core 临时借出； 只在回调内使用时无需保留。要在回调后使用，请调用 Retain，并在使用完毕后 Dispose；需要独立数组则调用 CopyPixels。



局部刷新：Damage 描述相对 BaseSequence 那一帧的变化。 只有显示端当前保存的序号等于 BaseSequence，且尺寸一致时，才能直接只更新 Damage。 跳过了中间帧、尺寸改变或 HasFullDamage 为 true 时，最简单可靠的做法是刷新整帧。Pixels 始终含有完整画面。



保留的帧在连接关闭后仍可读取。读取期间必须保持该份租约有效，不要由另一线程同时释放。 长时间积压帧会占用缓冲池，应及时消费并释放。

示例：

将帧交给已有的工作队列：入队前保留，消费后释放。队列应有容量限制，避免显示落后时积压大量帧。



```csharp
using ExHyperV.Services;
using System.Threading.Channels;
public static class RetainedFrameExample
{
    public static void Enqueue(ChannelWriter<ConsoleFrame> queue, ConsoleFrame borrowed)
    {
        var retained = borrowed.Retain();
        // 使用 Wait 模式的有界队列，TryWrite 失败时由此处释放。
        if (!queue.TryWrite(retained)) retained.Dispose();
    }
    public static async Task ConsumeAsync(Channel<ConsoleFrame> queue, Action<ConsoleFrame> draw,
        Func<Task> stopProducer, CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var frame in queue.Reader.ReadAllAsync(cancellationToken))
                using (frame) draw(frame);
        }
        finally
        {
            // 先关闭入队入口；晚到的 Enqueue 会自行释放，不能再留下新帧。
            queue.Writer.TryComplete();
            try
            {
                // 此回调须停止生产，并等待已经进入的帧回调退出。
                await stopProducer();
            }
            finally
            {
                while (queue.Reader.TryRead(out var remaining)) remaining.Dispose();
            }
        }
    }
}
```

#### 构造函数说明

<a id="api-c1824b67530e7d8f"></a>

##### ConsoleFrame · 构造器

```csharp
public ConsoleFrame(int width, int height, int stride, ReadOnlyMemory<byte> pixels, long sequence);
```

使用调用方提供的像素内存创建租约；任何关联租约存活期间都不得修改该内存。

| 参数 | 说明 |
| --- | --- |
| `width` | 正数图像宽度，单位为像素。 |
| `height` | 正数图像高度，单位为像素。 |
| `stride` | 每行字节数；必须为 4 的倍数且不小于 width &#215; 4，可包含行尾填充。 |
| `pixels` | 长度必须等于 stride &#215; height 的完整像素内存，不复制；任何租约存活时都不得修改或释放其底层内存。 |
| `sequence` | 正数帧序号；此构造将 BaseSequence 设为 sequence - 1 并标记整帧变化。 |

调用约定：仅验证像素描述与内存长度，不复制或冻结调用方内存。长度运算使用 64 位整数，拒绝无法用托管内存长度表示的帧。

- 异常 `ArgumentOutOfRangeException`：尺寸、行长度或帧序号无效，或帧字节数超出可表示范围。
- 异常 `ArgumentException`：像素内存长度与图像描述不一致。

#### 属性说明

<a id="api-ffd9b3afb8291670"></a>

##### BaseSequence · 属性

```csharp
public long BaseSequence { get; }
```

Damage 所依据的前一帧序号。

调用约定：显示端缺少该帧或尺寸不一致时，应合并中间变化或刷新完整画面。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | Damage 所依据的前一帧序号。 |
| 取值与默认 | 非负整数。例如 BaseSequence 为 41、Sequence 为 42，Damage 描述第 41 帧到第 42 帧的变化。 |

<a id="api-0dd98558052c2c13"></a>

##### ByteLength · 属性

```csharp
public int ByteLength { get; }
```

整帧像素缓冲区的长度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 整帧像素缓冲区的长度。 |
| 取值与默认 | 字节数，等于 Stride &#215; Height。 |

<a id="api-c0ca95b63fdbbdea"></a>

##### Damage · 属性

```csharp
public ConsoleFrameRegion Damage { get; }
```

相对 BaseSequence 发生变化的桌面区域。

调用约定：用于局部刷新；Pixels 始终保存完整画面。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 相对 BaseSequence 发生变化的桌面区域。 |
| 取值与默认 | 以桌面左上角为原点的像素矩形。 |

<a id="api-f3cf8f02d41f9263"></a>

##### HasFullDamage · 属性

```csharp
public bool HasFullDamage { get; }
```

本次画面是否需要全量刷新。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 本次画面是否需要全量刷新。 |
| 取值与默认 | true：刷新整个桌面；false：可根据 BaseSequence 和 Damage 局部刷新。 |

<a id="api-616a1e77beb8c421"></a>

##### Height · 属性

```csharp
public int Height { get; }
```

完整来宾桌面的高度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 完整来宾桌面的高度。 |
| 取值与默认 | 正整数，单位为物理像素。 |

<a id="api-0f916becd52bb8d3"></a>

##### Pixels · 属性

```csharp
public ReadOnlySpan<byte> Pixels { get; }
```

当前帧的只读像素内存。

调用约定：只在当前租约未释放时有效。读取期间保持租约有效，不得同时由另一线程释放。

- 异常 `ObjectDisposedException`：此租约已经释放。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前帧的只读像素内存。 |
| 取值与默认 | 从上到下排列的 BGRA32：每像素依次为蓝、绿、红、Alpha 四个字节；行长度为 Stride，完整长度为 ByteLength。连接生成的帧包含远端光标，Alpha 为 255。 |

<a id="api-7ed89a64cc88291b"></a>

##### Sequence · 属性

```csharp
public long Sequence { get; }
```

本连接生成画面时递增的帧序号。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 本连接生成画面时递增的帧序号。 |
| 取值与默认 | 正整数；重复获取同一画面时序号不变。 |

<a id="api-15bad73304b82dfa"></a>

##### Stride · 属性

```csharp
public int Stride { get; }
```

像素缓冲区每行占用的字节数。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 像素缓冲区每行占用的字节数。 |
| 取值与默认 | 4 的倍数，不小于 Width &#215; 4；连接生成的帧为 Width &#215; 4，自行构造时可包含行尾填充。 |

<a id="api-d082eed74a45c63e"></a>

##### Width · 属性

```csharp
public int Width { get; }
```

完整来宾桌面的宽度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 完整来宾桌面的宽度。 |
| 取值与默认 | 正整数，单位为物理像素。 |

#### 方法说明

<a id="api-e5049e309fda705d"></a>

##### CopyPixels · 方法

```csharp
public byte[] CopyPixels();
```

复制整帧像素，取得可以独立保存和修改的字节数组。

返回：长度为 ByteLength 的新数组，按原来的 Stride 排列；同时保存 Width、Height、Stride 才能解释它。

调用约定：适合需要长期保留或交给会修改数据的组件。只想延后读取时可使用 Retain，避免这次复制。

- 异常 `ObjectDisposedException`：当前租约已经释放。

<a id="api-9636f7084707a2fd"></a>

##### Dispose · 方法

```csharp
public void Dispose();
```

释放当前这份帧租约；重复释放没有额外效果。

调用约定：释放后不能再读取此对象的 Pixels、调用 Retain 或 CopyPixels。 其他通过 Retain 取得的帧仍可使用。不要在读取此租约的同时由另一线程释放它。

<a id="api-276168d816a67152"></a>

##### Retain · 方法

```csharp
public ConsoleFrame Retain();
```

保留这帧画面，供回调结束后继续使用，不复制像素。

返回：一份新的帧租约，共享当前像素；调用方用完后必须 Dispose。

调用约定：每次 Retain 都取得独立的释放责任。释放原帧不会使保留的帧失效；连接关闭也不会。 不要把保留的帧无限积压，否则缓冲池可能耗尽并暂停输出新帧。

- 异常 `ObjectDisposedException`：当前租约已经释放。
- 异常 `InvalidOperationException`：同一缓冲区的租约数量已达整数上限。

<a id="api-323b953e417e6d27"></a>

### `ConsoleFrameRegion` record 结构

桌面上需要更新的一块矩形区域，位置和大小以像素表示。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 视频 |
| 源文件 | ` Business/Video/ConsoleFrameRegion.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleFrameRegion(int X, int Y, int Width, int Height) `](#api-e28cc59df781112f) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Height { get; init; } `](#api-1acbdb407b0a10e2) | 区域的高度。 |
| [` public bool IsEmpty { get; } `](#api-de55acb7c12efa21) | 区域是否不包含像素。 |
| [` public int Width { get; init; } `](#api-85e0631a66a0b36f) | 区域的宽度。 |
| [` public int X { get; init; } `](#api-88895926f6d0094f) | 区域左边界相对桌面左上角的横坐标。 |
| [` public int Y { get; init; } `](#api-6ed9dcdf84d2fd37) | 区域上边界相对桌面左上角的纵坐标。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleFrameRegion Union(ConsoleFrameRegion other) `](#api-b33a0b28d3c433a8) | 取得能同时覆盖本区域和另一区域的最小矩形。 |

#### 详细说明

```csharp
public readonly record struct ConsoleFrameRegion(int X, int Y, int Width, int Height)
```

| 参数 | 说明 |
| --- | --- |
| `X` | 区域左边界相对桌面左上角的横坐标。 |
| `Y` | 区域上边界相对桌面左上角的纵坐标。 |
| `Width` | 区域的宽度。 |
| `Height` | 区域的高度。 |

调用约定：右边界 X + Width 和下边界 Y + Height 不计入区域。 例如 (10, 20, 2, 1) 包含 (10, 20)、(11, 20) 两个像素。 自行构造时请提供非负坐标和尺寸；此记录的构造器不检查是否越出桌面。

#### 构造函数说明

<a id="api-e28cc59df781112f"></a>

##### ConsoleFrameRegion · 主构造器

```csharp
public ConsoleFrameRegion(int X, int Y, int Width, int Height);
```

| 参数 | 说明 |
| --- | --- |
| `X` | 区域左边界相对桌面左上角的横坐标。 |
| `Y` | 区域上边界相对桌面左上角的纵坐标。 |
| `Width` | 区域的宽度。 |
| `Height` | 区域的高度。 |

#### 属性说明

<a id="api-1acbdb407b0a10e2"></a>

##### Height · record 属性

```csharp
public int Height { get; init; }
```

区域的高度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 区域的高度。 |
| 取值与默认 | 非负整数，单位为像素；0 表示空区域。 |

<a id="api-de55acb7c12efa21"></a>

##### IsEmpty · 属性

```csharp
public bool IsEmpty { get; }
```

区域是否不包含像素。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 区域是否不包含像素。 |
| 取值与默认 | 宽度或高度为 0 时为 true，否则为 false。 |

<a id="api-85e0631a66a0b36f"></a>

##### Width · record 属性

```csharp
public int Width { get; init; }
```

区域的宽度。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 区域的宽度。 |
| 取值与默认 | 非负整数，单位为像素；0 表示空区域。 |

<a id="api-88895926f6d0094f"></a>

##### X · record 属性

```csharp
public int X { get; init; }
```

区域左边界相对桌面左上角的横坐标。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 区域左边界相对桌面左上角的横坐标。 |
| 取值与默认 | 非负整数，单位为像素。 |

<a id="api-6ed9dcdf84d2fd37"></a>

##### Y · record 属性

```csharp
public int Y { get; init; }
```

区域上边界相对桌面左上角的纵坐标。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 区域上边界相对桌面左上角的纵坐标。 |
| 取值与默认 | 非负整数，单位为像素。 |

#### 方法说明

<a id="api-b33a0b28d3c433a8"></a>

##### Union · 方法

```csharp
public ConsoleFrameRegion Union(ConsoleFrameRegion other);
```

取得能同时覆盖本区域和另一区域的最小矩形。

| 参数 | 说明 |
| --- | --- |
| `other` | 要合并的区域，与当前区域使用同一桌面坐标系。 |

返回：合并后的外接矩形；其中一项为空时直接返回另一项。

调用约定：合并不相邻区域时，返回的矩形也会包含两者之间的像素。

- 异常 `OverflowException`：计算矩形边界时超出整数范围。

<a id="api-fc9ac7bacf776039"></a>

### `ConsoleKeyEvent` record 结构

按键序列中的一个扫描码事件；按下和释放分别占一项。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 控制 / 键盘 |
| 源文件 | ` Business/Keyboard/ConsoleKeyEvent.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConsoleKeyEvent(ushort ScanCode, bool Extended, bool Down) `](#api-9c8ed46b0b6c72e0) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool Down { get; init; } `](#api-9dda0a7e66ef6c82) | 按键事件的动作。 |
| [` public bool Extended { get; init; } `](#api-7eb7c66facc0c4a1) | 扫描码是否带 E0 前缀。 |
| [` public ushort ScanCode { get; init; } `](#api-11ff40b969784eda) | 键盘按键的 PC 扫描码。 |

#### 详细说明

```csharp
public readonly record struct ConsoleKeyEvent(ushort ScanCode, bool Extended, bool Down)
```

| 参数 | 说明 |
| --- | --- |
| `ScanCode` | 键盘按键的 PC 扫描码。 |
| `Extended` | 扫描码是否带 E0 前缀。 |
| `Down` | 按键事件的动作。 |

#### 构造函数说明

<a id="api-9c8ed46b0b6c72e0"></a>

##### ConsoleKeyEvent · 主构造器

```csharp
public ConsoleKeyEvent(ushort ScanCode, bool Extended, bool Down);
```

| 参数 | 说明 |
| --- | --- |
| `ScanCode` | 键盘按键的 PC 扫描码。 |
| `Extended` | 扫描码是否带 E0 前缀。 |
| `Down` | 按键事件的动作。 |

#### 属性说明

<a id="api-9dda0a7e66ef6c82"></a>

##### Down · record 属性

```csharp
public bool Down { get; init; }
```

按键事件的动作。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 按键事件的动作。 |
| 取值与默认 | true：按下；false：释放。 |

<a id="api-7eb7c66facc0c4a1"></a>

##### Extended · record 属性

```csharp
public bool Extended { get; init; }
```

扫描码是否带 E0 前缀。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 扫描码是否带 E0 前缀。 |
| 取值与默认 | true：带 E0 前缀；false：普通扫描码。 |

<a id="api-11ff40b969784eda"></a>

##### ScanCode · record 属性

```csharp
public ushort ScanCode { get; init; }
```

键盘按键的 PC 扫描码。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 键盘按键的 PC 扫描码。 |
| 取值与默认 | 1～255。例如 A 键为 `0x1E`；实际字符由来宾键盘布局和修饰键决定。 |

<a id="api-2583c9fa0f39f88e"></a>

### `ConsoleKeyboardToggleState` 枚举

需要在来宾中开启的键盘锁定键，可用 | 组合。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 控制 / 键盘 |
| 源文件 | ` Business/Keyboard/ConsoleKeyboardToggleState.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` CapsLock = 4 `](#api-f79ff8268fee0221) | Caps Lock 开启。 |
| [` KanaLock = 8 `](#api-d0621aa00fc7e807) | Kana Lock 开启。 |
| [` None = 0 `](#api-889328ee9cddf476) | 所有锁定键均关闭。 |
| [` NumLock = 2 `](#api-ff76c2af1e8f94e2) | Num Lock 开启。 |
| [` ScrollLock = 1 `](#api-e690ea67ea02216a) | Scroll Lock 开启。 |

#### 详细说明

```csharp
public enum ConsoleKeyboardToggleState : uint
```

调用约定：由调用方读取锁定状态，再交给 SynchronizeKeyboard 同步到来宾。

#### 枚举值说明

<a id="api-f79ff8268fee0221"></a>

##### CapsLock · 枚举值

```csharp
CapsLock = 4
```

Caps Lock 开启。

<a id="api-d0621aa00fc7e807"></a>

##### KanaLock · 枚举值

```csharp
KanaLock = 8
```

Kana Lock 开启。

<a id="api-889328ee9cddf476"></a>

##### None · 枚举值

```csharp
None = 0
```

所有锁定键均关闭。

<a id="api-ff76c2af1e8f94e2"></a>

##### NumLock · 枚举值

```csharp
NumLock = 2
```

Num Lock 开启。

<a id="api-e690ea67ea02216a"></a>

##### ScrollLock · 枚举值

```csharp
ScrollLock = 1
```

Scroll Lock 开启。

<a id="api-ed483ee678cf01e1"></a>

### `ConsolePointerButton` 枚举

鼠标输入中要按下或释放的一个按键。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 控制 / 鼠标 |
| 源文件 | ` Business/Pointer/ConsolePointerButton.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Left = 1 `](#api-98ac0f558892e8a3) | 鼠标左键。 |
| [` Middle = 4 `](#api-7dee1fcca80261e6) | 鼠标中键。 |
| [` None = 0 `](#api-65443b8d47d780ae) | 没有鼠标按键，用于移动或滚轮事件。 |
| [` Right = 2 `](#api-b1cd3a8e9cc3272e) | 鼠标右键。 |

#### 详细说明

```csharp
public enum ConsolePointerButton
```

调用约定：SendPointer 每次只接收一个按键，不能用 | 组合。移动和滚轮事件使用 None。

#### 枚举值说明

<a id="api-98ac0f558892e8a3"></a>

##### Left · 枚举值

```csharp
Left = 1
```

鼠标左键。

<a id="api-7dee1fcca80261e6"></a>

##### Middle · 枚举值

```csharp
Middle = 4
```

鼠标中键。

<a id="api-65443b8d47d780ae"></a>

##### None · 枚举值

```csharp
None = 0
```

没有鼠标按键，用于移动或滚轮事件。

<a id="api-b1cd3a8e9cc3272e"></a>

##### Right · 枚举值

```csharp
Right = 2
```

鼠标右键。

<a id="api-78a7a0c2e5b1433b"></a>

### `ConsoleService` 类

选择连接方式，创建用于查看和操作虚拟机桌面的控制台连接。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 连接 |
| 源文件 | ` Business/Connection/ConsoleService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static IConsoleConnection CreateConnection(ConsoleConnectionOptions options, string backendId = RdpBasicBackendId) `](#api-0b4175a57cc83770) | 为指定虚拟机创建控制台连接。 |
| [` public static IReadOnlyList<ConsoleBackendInfo> GetBackends() `](#api-5fde38d41600601e) | 列出当前版本登记的控制台后端及其能力。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public const string NimueBackendId = "Nimue" `](#api-e3c713c57eff5656) | Nimue 的预留后端标识，当前尚未接入。 |
| [` public const string RdpBasicBackendId = "RdpBasic" `](#api-67820999b25792f3) | RDP 基本会话的后端标识，提供视频和键鼠输入。 |
| [` public const string RdpEnhancedBackendId = "RdpEnhanced" `](#api-f88e5239b9fd534c) | RDP 增强会话的后端标识，另提供剪贴板和分辨率调整，需要来宾支持增强会话。 |

#### 详细说明

```csharp
public static class ConsoleService
```

调用约定：先用 `ConsoleService.GetBackends` 查看可用后端，再用 `ConsoleService.CreateConnection` 创建连接。 创建后订阅画面、连接状态等事件，调用 `IConsoleConnection.Start` 开始连接。 连接的用法见 `IConsoleConnection`。



RdpBasic 提供基本画面和键鼠；RdpEnhanced 还提供剪贴板和分辨率调整，需要来宾支持增强会话。 两者目前均由 FreeRDP 实现。Nimue 仅登记了标识，尚未接入。

#### 静态方法说明

<a id="api-0b4175a57cc83770"></a>

##### CreateConnection · 方法

```csharp
public static IConsoleConnection CreateConnection(ConsoleConnectionOptions options, string backendId = RdpBasicBackendId);
```

为指定虚拟机创建控制台连接。

| 参数 | 说明 |
| --- | --- |
| `options` | 目标虚拟机、初始桌面尺寸、缩放比例和连接超时。 |
| `backendId` | 后端标识，默认 RdpBasic。使用本类的标识常量或 GetBackends 返回的 BackendId；区分大小写。 |

返回：尚未启动的连接。先订阅事件，再调用 Start；用完后由调用方释放。

调用约定：此时只创建连接对象并检查参数，Start 才开始连接虚拟机。 后端选择决定基本或增强会话；连接失败时不会自动切换后端。

- 异常 `ArgumentNullException`：options 或 backendId 为 null。
- 异常 `ArgumentException`：后端标识未知，或连接参数无效。
- 异常 `NotSupportedException`：所选后端尚未实现。

<a id="api-5fde38d41600601e"></a>

##### GetBackends · 方法

```csharp
public static IReadOnlyList<ConsoleBackendInfo> GetBackends();
```

列出当前版本登记的控制台后端及其能力。

返回：只读列表，包含 RdpBasic、RdpEnhanced 和尚未实现的 Nimue。

调用约定：此方法只读取内置目录，不连接虚拟机，也不检测宿主或来宾是否已经准备好。 可先按 IsImplemented 筛选，再按 Capabilities 判断需要的功能是否存在。

#### 字段与常量说明

<a id="api-e3c713c57eff5656"></a>

##### NimueBackendId · 字段

```csharp
public const string NimueBackendId = "Nimue";
```

Nimue 的预留后端标识，当前尚未接入。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | Nimue 的预留后端标识，当前尚未接入。 |
| 取值与默认 | `Nimue`。 |

<a id="api-67820999b25792f3"></a>

##### RdpBasicBackendId · 字段

```csharp
public const string RdpBasicBackendId = "RdpBasic";
```

RDP 基本会话的后端标识，提供视频和键鼠输入。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | RDP 基本会话的后端标识，提供视频和键鼠输入。 |
| 取值与默认 | `RdpBasic`。 |

<a id="api-f88e5239b9fd534c"></a>

##### RdpEnhancedBackendId · 字段

```csharp
public const string RdpEnhancedBackendId = "RdpEnhanced";
```

RDP 增强会话的后端标识，另提供剪贴板和分辨率调整，需要来宾支持增强会话。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | RDP 增强会话的后端标识，另提供剪贴板和分辨率调整，需要来宾支持增强会话。 |
| 取值与默认 | `RdpEnhanced`。 |

<a id="api-300a4de29fad713b"></a>

### `ControllerDisk` record 结构

PCIe 存储控制器名下的物理磁盘，用于直通前确认能否脱机。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / PCIe |
| 源文件 | ` Management/Host/Pcie/HostPcieService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ControllerDisk(int Number, string FriendlyName, bool IsSystem, bool IsBoot, bool IsOffline) `](#api-fa056ddd346ca9cc) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string FriendlyName { get; init; } `](#api-f4bc8369699b3649) | 磁盘显示名称。 |
| [` public bool IsBoot { get; init; } `](#api-c3d81943ccee9f57) | 是否为启动磁盘。 |
| [` public bool IsOffline { get; init; } `](#api-0bdc6ac27aff2f3c) | 是否已经脱机。 |
| [` public bool IsSystem { get; init; } `](#api-3fb60c52a213641b) | 是否包含系统分区。 |
| [` public int Number { get; init; } `](#api-15d6a3359f3b009a) | 宿主磁盘编号。 |

#### 详细说明

```csharp
public readonly record struct ControllerDisk(int Number, string FriendlyName, bool IsSystem, bool IsBoot, bool IsOffline)
```

| 参数 | 说明 |
| --- | --- |
| `Number` | 宿主磁盘编号。 |
| `FriendlyName` | 磁盘显示名称。 |
| `IsSystem` | 是否包含系统分区。 |
| `IsBoot` | 是否为启动磁盘。 |
| `IsOffline` | 是否已经脱机。 |

#### 构造函数说明

<a id="api-fa056ddd346ca9cc"></a>

##### ControllerDisk · 主构造器

```csharp
public ControllerDisk(int Number, string FriendlyName, bool IsSystem, bool IsBoot, bool IsOffline);
```

| 参数 | 说明 |
| --- | --- |
| `Number` | 宿主磁盘编号。 |
| `FriendlyName` | 磁盘显示名称。 |
| `IsSystem` | 是否包含系统分区。 |
| `IsBoot` | 是否为启动磁盘。 |
| `IsOffline` | 是否已经脱机。 |

#### 属性说明

<a id="api-f4bc8369699b3649"></a>

##### FriendlyName · record 属性

```csharp
public string FriendlyName { get; init; }
```

磁盘显示名称。

<a id="api-c3d81943ccee9f57"></a>

##### IsBoot · record 属性

```csharp
public bool IsBoot { get; init; }
```

是否为启动磁盘。

<a id="api-0bdc6ac27aff2f3c"></a>

##### IsOffline · record 属性

```csharp
public bool IsOffline { get; init; }
```

是否已经脱机。

<a id="api-3fb60c52a213641b"></a>

##### IsSystem · record 属性

```csharp
public bool IsSystem { get; init; }
```

是否包含系统分区。

<a id="api-15d6a3359f3b009a"></a>

##### Number · record 属性

```csharp
public int Number { get; init; }
```

宿主磁盘编号。

<a id="api-94857f108f5411f3"></a>

### `CpuAffinityService` 类

读取、应用和保存虚拟机 CPU 亲和性，按宿主调度器选择 CPU 组或进程接口。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 处理器；管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/CpuAffinityService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<StartupApplyStatus> ApplySavedAffinityAfterStartAsync(Guid vmId, CancellationToken cancellationToken = default) `](#api-ec0d3670d83200f9) | 核实保存环境后，在 Root 上恢复进程亲和性，最多等待五轮；Classic/Core 只检查偏好，不自动重建 CPU 组。 |
| [` public static async Task<List<int>> GetCpuAffinityAsync(Guid vmId) `](#api-e1b65193766f3eec) | 读取当前实际 CPU 组或运行进程的处理器选择，不用保存偏好代替实际状态。 |
| [` public static async Task<ApiResponse<AffinityState>> ReadStateAsync(Guid vmId) `](#api-7c551349ec1337cf) | 同时读取亲和性的实际状态和保存偏好；两者分别返回，供调用方明确区分。 |
| [` public static async Task<SaveResult> SaveAsync(Guid vmId, List<int> coreIndices) `](#api-b08a0a1e95f9305d) | 应用处理器亲和性，将选择、后端和宿主拓扑摘要一起保存到 HostOnlyItems。 |

#### 详细说明

```csharp
public static class CpuAffinityService
```

#### 静态方法说明

<a id="api-ec0d3670d83200f9"></a>

##### ApplySavedAffinityAfterStartAsync · 方法

```csharp
public static async Task<StartupApplyStatus> ApplySavedAffinityAfterStartAsync(Guid vmId, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

核实保存环境后，在 Root 上恢复进程亲和性，最多等待五轮；Classic/Core 只检查偏好，不自动重建 CPU 组。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；字符串形式也应使用 GUID，而非显示名称。 |
| `cancellationToken` | 协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。 |

返回：恢复结果。ReselectionRequired 表示环境不同或无法核实，需要重新选择并保存；ReadStateAsync 提供具体原因及原编号。旧数组与版本 1 非空记录同样需要重新保存。空选择无需恢复。取消等待抛出取消异常。

调用约定：由调用方在虚拟机启动后调用，不建立持续后台监督。整轮持有该 VM 的跨进程锁，重试等待合计九秒，另加查询与操作耗时；后续手动保存等待本轮结束，旧重试不会在其后反向覆盖。同一 Core 进程对同一 vmmem 实例复用先前应用结果，明确 SaveAsync 仍可修改；该去重记录不跨进程共享。只有明确尚未出现的进程会重试，结果未知不自动重放。



环境检查只决定是否应用旧偏好，不关闭虚拟机、不清除当前绑定，也不删除原元数据。SMBIOS UUID 与拓扑相同的整机克隆可能无法区分；这是编号适用性检查，不是机器身份认证。当前没有跨平台拓扑不变或原生写入与硬件变化之间的事务保证。

- 异常 `CoreOperationException`：拓扑不支持、查询失败、身份冲突或应用失败；Result 保留具体错误，未把失败当作虚拟机已停止。

<a id="api-e1b65193766f3eec"></a>

##### GetCpuAffinityAsync · 方法

```csharp
public static async Task<List<int>> GetCpuAffinityAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 处理器。

读取当前实际 CPU 组或运行进程的处理器选择，不用保存偏好代替实际状态。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；字符串形式也应使用 GUID，而非显示名称。 |

返回：当前后端的实际处理器编号；Classic/Core 只有确认未绑定时返回空列表。Root 无运行进程时应改用 ReadStateAsync 查看保存偏好。

- 异常 `CoreOperationException`：查询失败、Root 无运行进程、调度器未知、拓扑不支持或身份不一致；Result 保留具体原因。

<a id="api-7c551349ec1337cf"></a>

##### ReadStateAsync · 方法

```csharp
public static async Task<ApiResponse<AffinityState>> ReadStateAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 处理器。

同时读取亲和性的实际状态和保存偏好；两者分别返回，供调用方明确区分。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机稳定 GUID。 |

返回：Root 运行或暂停时包含实际处理器，关机或保存状态仅包含偏好；Classic/Core 返回实际配置的组选择及保存偏好。SavedProcessors 保留旧编号，SavedPreferenceStatus 与 SavedPreferenceIssue 说明是否还能复用。环境读取失败记为 EnvironmentUnverified；实际绑定或元数据查询失败仍返回失败，不以空选择代替。

<a id="api-b08a0a1e95f9305d"></a>

##### SaveAsync · 方法

```csharp
public static async Task<SaveResult> SaveAsync(Guid vmId, List<int> coreIndices);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

应用处理器亲和性，将选择、后端和宿主拓扑摘要一起保存到 HostOnlyItems。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；字符串形式也应使用 GUID，而非显示名称。 |
| `coreIndices` | HostProcessorService.GetAffinityProcessorsAsync 返回的处理器编号；空集合表示解除组绑定或 Root 进程限制。 |

返回：应用或排队状态及元数据是否成功保存；应用成功与持久化成功是两个独立结果。

调用约定：新偏好将处理器选择和环境摘要写入同一个 HostOnlyItems 值。宿主身份取自 SMBIOS UUID，拓扑包含编号到核心、封装和 NUMA 的关系；Root 另包含 Windows 处理器组和核心效率类别。保存前后环境不同或读取失败时，显式应用仍可完成，但 SavedPreferenceStatus 为 EnvironmentUnverified，不能自动恢复；空选择不受此限制。重新选择使用当前宿主枚举的编号，不把旧宿主编号直接传回。



Classic/Core 为每台虚拟机分别建立 CPU 组，不按相同处理器集合共享。当前已独享且处理器未变时保留原组；旧共享组在该 VM 本次保存时拆分，新组复制已报告的四项策略值，不自动分摊原共享配额。无法复制的策略或不兼容的新拓扑返回失败。成员核对限于 WMI 可见的 Hyper-V 虚拟机；外部工具仍可另行绑定其他对象，Core 不提供系统级排他占用。旧组不在本次创建范围内，换组或解绑后不自动删除。Root 只支持实际单 Windows 处理器组，使用已核验的进程句柄写入并回读；停机时仅保存偏好。同一 VM 的 Root 保存与启动恢复跨进程串行。

<a id="api-47fadfc26c8a17d2"></a>

### `CpuAffinityService.AffinityState` record 类

分别表示已确认的亲和性和保存的启动偏好，不把保存成功当成运行时已生效。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/CpuAffinityService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public AffinityState(VmState State, IReadOnlyList<int>? AppliedProcessors, IReadOnlyList<int>? SavedProcessors, int? ProcessId, long? ProcessCreationTime) `](#api-d8cd56052cce6dd3) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<int>? AppliedProcessors { get; init; } `](#api-603338e2ec710d82) | 实际处理器编号；Root 关机或保存状态为 null，Classic/Core 未绑定为非 null 空集合。 |
| [` public long? ProcessCreationTime { get; init; } `](#api-7f4413b67f82456f) | 同一进程句柄读取的创建时间，UTC FILETIME；与 ProcessId 一起辨别进程实例，为 null 时未提供。 |
| [` public int? ProcessId { get; init; } `](#api-5a062f1d73e8657c) | Root 已核验的 vmmem 进程 ID；无进程或其他后端为 null。 |
| [` public string? SavedPreferenceIssue { get; init; } `](#api-805329284fb65fcd) | 需要重新选择或无法核实的原因；无需说明时为 null。 |
| [` public PreferenceStatus SavedPreferenceStatus { get; init; } `](#api-d314493285506c0a) | 保存偏好与当前环境的匹配状态；不代表运行时亲和性已经生效。 |
| [` public IReadOnlyList<int>? SavedProcessors { get; init; } `](#api-1d54dadb1209594f) | 保存的处理器编号，包括旧环境或其他后端的选择；使用前检查 SavedPreferenceStatus。 |
| [` public VmState State { get; init; } `](#api-c91ca6434aab27d2) | 本次读取的虚拟机状态。 |

#### 详细说明

```csharp
public sealed record AffinityState(VmState State, IReadOnlyList<int>? AppliedProcessors,
            IReadOnlyList<int>? SavedProcessors, int? ProcessId, long? ProcessCreationTime)
```

| 参数 | 说明 |
| --- | --- |
| `State` | 本次读取的虚拟机状态。 |
| `AppliedProcessors` | 实际处理器编号；Root 关机或保存状态为 null，Classic/Core 未绑定为非 null 空集合。 |
| `SavedProcessors` | 保存的处理器编号，包括旧环境或其他后端的选择；使用前检查 SavedPreferenceStatus。未保存或无法解析时为 null，空集合表示不限制。 |
| `ProcessId` | Root 已核验的 vmmem 进程 ID；无进程或其他后端为 null。 |
| `ProcessCreationTime` | 同一进程句柄读取的创建时间，UTC FILETIME；与 ProcessId 一起辨别进程实例，为 null 时未提供。 |

#### 构造函数说明

<a id="api-d8cd56052cce6dd3"></a>

##### AffinityState · 主构造器

```csharp
public AffinityState(VmState State, IReadOnlyList<int>? AppliedProcessors, IReadOnlyList<int>? SavedProcessors, int? ProcessId, long? ProcessCreationTime);
```

| 参数 | 说明 |
| --- | --- |
| `State` | 本次读取的虚拟机状态。 |
| `AppliedProcessors` | 实际处理器编号；Root 关机或保存状态为 null，Classic/Core 未绑定为非 null 空集合。 |
| `SavedProcessors` | 保存的处理器编号，包括旧环境或其他后端的选择；使用前检查 SavedPreferenceStatus。未保存或无法解析时为 null，空集合表示不限制。 |
| `ProcessId` | Root 已核验的 vmmem 进程 ID；无进程或其他后端为 null。 |
| `ProcessCreationTime` | 同一进程句柄读取的创建时间，UTC FILETIME；与 ProcessId 一起辨别进程实例，为 null 时未提供。 |

#### 属性说明

<a id="api-603338e2ec710d82"></a>

##### AppliedProcessors · record 属性

```csharp
public IReadOnlyList<int>? AppliedProcessors { get; init; }
```

实际处理器编号；Root 关机或保存状态为 null，Classic/Core 未绑定为非 null 空集合。

<a id="api-7f4413b67f82456f"></a>

##### ProcessCreationTime · record 属性

```csharp
public long? ProcessCreationTime { get; init; }
```

同一进程句柄读取的创建时间，UTC FILETIME；与 ProcessId 一起辨别进程实例，为 null 时未提供。

<a id="api-5a062f1d73e8657c"></a>

##### ProcessId · record 属性

```csharp
public int? ProcessId { get; init; }
```

Root 已核验的 vmmem 进程 ID；无进程或其他后端为 null。

<a id="api-805329284fb65fcd"></a>

##### SavedPreferenceIssue · 属性

```csharp
public string? SavedPreferenceIssue { get; init; }
```

需要重新选择或无法核实的原因；无需说明时为 null。原保存内容不会自动删除。

<a id="api-d314493285506c0a"></a>

##### SavedPreferenceStatus · 属性

```csharp
public PreferenceStatus SavedPreferenceStatus { get; init; }
```

保存偏好与当前环境的匹配状态；不代表运行时亲和性已经生效。

<a id="api-1d54dadb1209594f"></a>

##### SavedProcessors · record 属性

```csharp
public IReadOnlyList<int>? SavedProcessors { get; init; }
```

保存的处理器编号，包括旧环境或其他后端的选择；使用前检查 SavedPreferenceStatus。未保存或无法解析时为 null，空集合表示不限制。

<a id="api-c91ca6434aab27d2"></a>

##### State · record 属性

```csharp
public VmState State { get; init; }
```

本次读取的虚拟机状态。

<a id="api-a86b242e8bfed0d3"></a>

### `CpuAffinityService.PreferenceStatus` 枚举

保存的处理器选择是否适用于当前宿主、后端和编号映射。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/CpuAffinityService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` EnvironmentChanged = 3 `](#api-fd4fcd71ab9a5107) | 宿主、后端或编号映射不匹配；保留原选择，重新选择并保存后才能复用。 |
| [` EnvironmentUnverified = 4 `](#api-adb2f95457442478) | 旧记录缺少环境信息、格式版本尚不支持或当前信息无法读取；未自动应用。 |
| [` InvalidConfiguration = 5 `](#api-41e9ad4343e71626) | 保存内容无法解析为处理器选择；原始元数据保留。 |
| [` MatchesEnvironment = 2 `](#api-07eb82c4e982d8db) | 宿主、后端和处理器拓扑摘要一致；实际生效状态另查 AppliedProcessors。 |
| [` None = 0 `](#api-151b08dd98c7e6df) | 没有保存亲和性选择。 |
| [` Unrestricted = 1 `](#api-b8f42d9fa5c8fde6) | 保存了空选择，表示不要求限制处理器；无需环境匹配。 |

#### 详细说明

```csharp
public enum PreferenceStatus
```

#### 枚举值说明

<a id="api-fd4fcd71ab9a5107"></a>

##### EnvironmentChanged · 枚举值

```csharp
EnvironmentChanged = 3
```

宿主、后端或编号映射不匹配；保留原选择，重新选择并保存后才能复用。

<a id="api-adb2f95457442478"></a>

##### EnvironmentUnverified · 枚举值

```csharp
EnvironmentUnverified = 4
```

旧记录缺少环境信息、格式版本尚不支持或当前信息无法读取；未自动应用。

<a id="api-41e9ad4343e71626"></a>

##### InvalidConfiguration · 枚举值

```csharp
InvalidConfiguration = 5
```

保存内容无法解析为处理器选择；原始元数据保留。

<a id="api-07eb82c4e982d8db"></a>

##### MatchesEnvironment · 枚举值

```csharp
MatchesEnvironment = 2
```

宿主、后端和处理器拓扑摘要一致；实际生效状态另查 AppliedProcessors。

<a id="api-151b08dd98c7e6df"></a>

##### None · 枚举值

```csharp
None = 0
```

没有保存亲和性选择。

<a id="api-b8f42d9fa5c8fde6"></a>

##### Unrestricted · 枚举值

```csharp
Unrestricted = 1
```

保存了空选择，表示不要求限制处理器；无需环境匹配。

<a id="api-d5c38afbcf66d0c3"></a>

### `CpuAffinityService.SaveResult` record 类

亲和性应用状态与 HostOnlyItems 元数据保存结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/CpuAffinityService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public SaveResult(SaveStatus Status, bool MetadataSaved) `](#api-7904623338803789) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? CleanupError { get; init; } `](#api-f6b7bfe7577288e7) | 恢复原绑定或清理本次新组的失败说明，不覆盖原始错误。 |
| [` public ApiErrorKind CleanupErrorKind { get; init; } `](#api-b49cd89e8b5ba84f) | 恢复或清理失败的类别；未发生此类失败时为 None。 |
| [` public Guid? CurrentGroupId { get; init; } `](#api-11cce6b5234ec0d7) | Classic/Core 下本次最后确认的绑定；全零为未绑定，null 为未确认或 Root 路径。 |
| [` public string? Error { get; init; } `](#api-0ad18dde6814b0d5) | 应用或元数据保存的失败说明；Status=Applied 时失败仅表示后续保存未成功。 |
| [` public int ErrorCode { get; init; } `](#api-e96576d5f8a96bc7) | 原始错误码，结合 ErrorSource 解释。 |
| [` public ApiErrorKind ErrorKind { get; init; } `](#api-656f1d4f2751aa46) | 原始失败类别；OutcomeUnknown 时不得直接重试或反向写入。 |
| [` public ApiErrorSource ErrorSource { get; init; } `](#api-5558b372707a4697) | 原始错误码的来源。 |
| [` public bool MetadataSaved { get; init; } `](#api-212f91fca19d5bc2) | 所选处理器是否已保存到虚拟机配置；不等同于亲和性应用状态。 |
| [` public Guid? RetainedGroupId { get; init; } `](#api-2c86effde94b1012) | 失败后保留或无法确认删除的本次新建组；既有组从不由本操作删除。 |
| [` public string? SavedPreferenceIssue { get; init; } `](#api-a136413053e94d50) | 环境无法核实或偏好未确认保存的说明；空选择无需环境核实。 |
| [` public PreferenceStatus SavedPreferenceStatus { get; init; } `](#api-7b29b28d2e9eb789) | 本次成功保存的偏好与环境是否匹配；保存失败时为 EnvironmentUnverified。 |
| [` public SaveStatus Status { get; init; } `](#api-0466b19c5caf1512) | 亲和性已应用、等待应用或失败的状态。 |

#### 详细说明

```csharp
public sealed record SaveResult(SaveStatus Status, bool MetadataSaved)
```

| 参数 | 说明 |
| --- | --- |
| `Status` | 亲和性已应用、等待应用或失败的状态。 |
| `MetadataSaved` | 所选处理器是否已保存到虚拟机配置；不等同于亲和性应用状态。 |

#### 构造函数说明

<a id="api-7904623338803789"></a>

##### SaveResult · 主构造器

```csharp
public SaveResult(SaveStatus Status, bool MetadataSaved);
```

| 参数 | 说明 |
| --- | --- |
| `Status` | 亲和性已应用、等待应用或失败的状态。 |
| `MetadataSaved` | 所选处理器是否已保存到虚拟机配置；不等同于亲和性应用状态。 |

#### 属性说明

<a id="api-f6b7bfe7577288e7"></a>

##### CleanupError · 属性

```csharp
public string? CleanupError { get; init; }
```

恢复原绑定或清理本次新组的失败说明，不覆盖原始错误。

<a id="api-b49cd89e8b5ba84f"></a>

##### CleanupErrorKind · 属性

```csharp
public ApiErrorKind CleanupErrorKind { get; init; }
```

恢复或清理失败的类别；未发生此类失败时为 None。

<a id="api-11cce6b5234ec0d7"></a>

##### CurrentGroupId · 属性

```csharp
public Guid? CurrentGroupId { get; init; }
```

Classic/Core 下本次最后确认的绑定；全零为未绑定，null 为未确认或 Root 路径。

<a id="api-0ad18dde6814b0d5"></a>

##### Error · 属性

```csharp
public string? Error { get; init; }
```

应用或元数据保存的失败说明；Status=Applied 时失败仅表示后续保存未成功。

<a id="api-e96576d5f8a96bc7"></a>

##### ErrorCode · 属性

```csharp
public int ErrorCode { get; init; }
```

原始错误码，结合 ErrorSource 解释。

<a id="api-656f1d4f2751aa46"></a>

##### ErrorKind · 属性

```csharp
public ApiErrorKind ErrorKind { get; init; }
```

原始失败类别；OutcomeUnknown 时不得直接重试或反向写入。

<a id="api-5558b372707a4697"></a>

##### ErrorSource · 属性

```csharp
public ApiErrorSource ErrorSource { get; init; }
```

原始错误码的来源。

<a id="api-212f91fca19d5bc2"></a>

##### MetadataSaved · record 属性

```csharp
public bool MetadataSaved { get; init; }
```

所选处理器是否已保存到虚拟机配置；不等同于亲和性应用状态。

<a id="api-2c86effde94b1012"></a>

##### RetainedGroupId · 属性

```csharp
public Guid? RetainedGroupId { get; init; }
```

失败后保留或无法确认删除的本次新建组；既有组从不由本操作删除。

<a id="api-a136413053e94d50"></a>

##### SavedPreferenceIssue · 属性

```csharp
public string? SavedPreferenceIssue { get; init; }
```

环境无法核实或偏好未确认保存的说明；空选择无需环境核实。

<a id="api-7b29b28d2e9eb789"></a>

##### SavedPreferenceStatus · 属性

```csharp
public PreferenceStatus SavedPreferenceStatus { get; init; }
```

本次成功保存的偏好与环境是否匹配；保存失败时为 EnvironmentUnverified。显式应用成功不等于以后可自动恢复。

<a id="api-0466b19c5caf1512"></a>

##### Status · record 属性

```csharp
public SaveStatus Status { get; init; }
```

亲和性已应用、等待应用或失败的状态。

<a id="api-3d34b02185eb00ea"></a>

### `CpuAffinityService.SaveStatus` 枚举

保存亲和性请求后的应用状态；元数据持久化另由 MetadataSaved 表示。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/CpuAffinityService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Applied = 0 `](#api-edb2ad6fdd7b5522) | 已向运行时或 CPU 组应用所选处理器。 |
| [` Failed = 2 `](#api-d53bcabf42e587ee) | 亲和性操作失败。 |
| [` Queued = 1 `](#api-13c6b6a652f43211) | 当前不能立即应用，选择供下次启动后恢复。 |

#### 详细说明

```csharp
public enum SaveStatus
```

#### 枚举值说明

<a id="api-edb2ad6fdd7b5522"></a>

##### Applied · 枚举值

```csharp
Applied = 0
```

已向运行时或 CPU 组应用所选处理器。

<a id="api-d53bcabf42e587ee"></a>

##### Failed · 枚举值

```csharp
Failed = 2
```

亲和性操作失败。

<a id="api-13c6b6a652f43211"></a>

##### Queued · 枚举值

```csharp
Queued = 1
```

当前不能立即应用，选择供下次启动后恢复。

<a id="api-f8d0e17de2ab6736"></a>

### `CpuAffinityService.StartupApplyStatus` 枚举

启动后恢复保存亲和性的结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/CpuAffinityService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Applied = 1 `](#api-9fa34aea06d9db2e) | 已成功恢复亲和性。 |
| [` Exhausted = 3 `](#api-e7c52e0d2a85da7d) | 已用尽重试次数，仍未成功应用。 |
| [` InvalidConfiguration = 4 `](#api-14e4db55aeb654d4) | 保存的处理器选择无法形成有效配置。 |
| [` NotRequired = 0 `](#api-ea1358e2ba400723) | 当前调度器或保存的偏好不要求恢复，或当前 Core 进程已处理过同一 vmmem 实例。 |
| [` ReselectionRequired = 6 `](#api-e25f9119d3285b00) | 保存环境不匹配或无法核实，需要重新选择并保存；不应用旧编号、不删除偏好、不阻止虚拟机启动。 |
| [` Superseded = 5 `](#api-eb0d337a0a018d5f) | 恢复期间保存的偏好已被其他操作改变，本轮停止应用旧选择。 |
| [` VmStopped = 2 `](#api-249c9251fc886551) | 等待或重试期间虚拟机已停止。 |

#### 详细说明

```csharp
public enum StartupApplyStatus
```

#### 枚举值说明

<a id="api-9fa34aea06d9db2e"></a>

##### Applied · 枚举值

```csharp
Applied = 1
```

已成功恢复亲和性。

<a id="api-e7c52e0d2a85da7d"></a>

##### Exhausted · 枚举值

```csharp
Exhausted = 3
```

已用尽重试次数，仍未成功应用。

<a id="api-14e4db55aeb654d4"></a>

##### InvalidConfiguration · 枚举值

```csharp
InvalidConfiguration = 4
```

保存的处理器选择无法形成有效配置。

<a id="api-ea1358e2ba400723"></a>

##### NotRequired · 枚举值

```csharp
NotRequired = 0
```

当前调度器或保存的偏好不要求恢复，或当前 Core 进程已处理过同一 vmmem 实例。

<a id="api-e25f9119d3285b00"></a>

##### ReselectionRequired · 枚举值

```csharp
ReselectionRequired = 6
```

保存环境不匹配或无法核实，需要重新选择并保存；不应用旧编号、不删除偏好、不阻止虚拟机启动。

<a id="api-eb0d337a0a018d5f"></a>

##### Superseded · 枚举值

```csharp
Superseded = 5
```

恢复期间保存的偏好已被其他操作改变，本轮停止应用旧选择。

<a id="api-249c9251fc886551"></a>

##### VmStopped · 枚举值

```csharp
VmStopped = 2
```

等待或重试期间虚拟机已停止。

<a id="api-daf38b34353e49be"></a>

### `GpuDeploymentSession` 类

管理一次进程内 GPU 部署；Core 推进各步骤，并在需要确认时暂停，等待带版本标识的明确决定。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/GpuDeploymentSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public GpuDeploymentSession(Guid vmId, string vmName, HostGpuInfo gpu, VmGpuService gpuService, VmQueryService queryService, GpuDeploymentMode mode = GpuDeploymentMode.AddWithDrivers) `](#api-f91ceb7e4df3e117) | 创建尚未开始执行的单次 GPU 部署会话。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public async Task<GpuDeploymentSnapshot> CancelAsync() `](#api-4ea1036d17491171) | 请求取消，等待当前操作退出后尝试清理本次新增的适配器。 |
| [` public GpuDeploymentSnapshot GetStatus() `](#api-8074253c3683a7d4) | 读取最近发布的状态，不等待正在进行的底层操作。 |
| [` public async Task<GpuDeploymentSnapshot> ReconcileAsync() `](#api-5b584dc638877ec1) | 只读核实本次部署和清理的原生任务、GPU 适配器及临时磁盘资源。 |
| [` public void RequestCancellation() `](#api-0f3fa40d86861ea2) | 仅发送协作式取消请求，立即返回；正在执行的不可取消底层调用仍需自行结束。 |
| [` public async Task<GpuDeploymentSnapshot> RetryCleanupAsync() `](#api-d5769efaf9f99126) | 在允许 RetryCleanup 且 OutcomeUnknown 为 false 时，恢复临时磁盘资源并清理本次新增的 GPU 适配器。 |
| [` public async Task<GpuDeploymentSnapshot> StartAsync(Action<GpuDeploymentSnapshot>? progress = null) `](#api-cbaf6ad49c80c7f4) | 开始部署并执行到等待用户决定或结束；同一会话只能从 Created 状态启动。 |
| [` public async Task<GpuDeploymentSnapshot> SubmitDecisionAsync(GpuDeploymentDecision decision) `](#api-b6e5e2765e6b0344) | 校验当前请求 ID 和输入种类后推进部署；过期或错误的提交不会替代正在等待的合法请求。 |

#### 详细说明

```csharp
public sealed class GpuDeploymentSession
```

调用约定：每个实例管理一次 GPU 部署，串行执行写操作；GetStatus 可随时读取已发布快照。执行失败返回 Failed 或 CleanupRequired；错误状态、过期决定和无效输入抛出异常，保留有效的待确认事项。失败或取消后保留已完成的策略、MMIO、文件和电源变化，恢复本次临时磁盘资源，并清理创建回执明确归属于本次操作的 GPU 适配器。原任务是否结束或资源归属无法确认时保留现场；按 AllowedActions 调用 ReconcileAsync 核实和 RetryCleanupAsync 清理。恢复仅限当前进程内的此实例，不提供驱动文件回滚或重启后的恢复。

#### 构造函数说明

<a id="api-f91ceb7e4df3e117"></a>

##### GpuDeploymentSession · 构造器

```csharp
public GpuDeploymentSession(Guid vmId, string vmName, HostGpuInfo gpu, VmGpuService gpuService, VmQueryService queryService, GpuDeploymentMode mode = GpuDeploymentMode.AddWithDrivers);
```

创建尚未开始执行的单次 GPU 部署会话。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID；后续会按身份核对目标。 |
| `vmName` | 目标虚拟机名称，应与 vmId 对应。 |
| `gpu` | 目标宿主 GPU 及可分区资源信息。 |
| `gpuService` | 执行 GPU 查询与配置的服务。 |
| `queryService` | 读取目标虚拟机信息的服务；由调用方持有和释放，须在部署及后续核实、清理期间保持可用。 |
| `mode` | 本次部署范围，默认新增 GPU 并安装驱动；未定义的枚举值会被拒绝。 |

- 异常 `ArgumentOutOfRangeException`：mode 不是定义的部署模式。

#### 方法说明

<a id="api-4ea1036d17491171"></a>

##### CancelAsync · 方法

```csharp
public async Task<GpuDeploymentSnapshot> CancelAsync();
```

请求取消，等待当前操作退出后尝试清理本次新增的适配器。

返回：取消或既有终态快照；清理未完成时返回 CleanupRequired。

调用约定：不是立即中断或完整回滚。已完成、已失败、已取消或已经等待清理的会话直接返回当前状态；仅当 AllowedActions 包含 RetryCleanup 时才能重试清理。OutcomeUnknown 为 true 时保留资源；允许 Reconcile 时可调用 ReconcileAsync 只读核实原操作。

<a id="api-8074253c3683a7d4"></a>

##### GetStatus · 方法

```csharp
public GpuDeploymentSnapshot GetStatus();
```

读取最近发布的状态，不等待正在进行的底层操作。

返回：当前快照；读取不会推进流程或修改会话。

<a id="api-5b584dc638877ec1"></a>

##### ReconcileAsync · 方法

```csharp
public async Task<GpuDeploymentSnapshot> ReconcileAsync();
```

只读核实本次部署和清理的原生任务、GPU 适配器及临时磁盘资源。

返回：更新后的快照。原任务结束且资源已恢复时进入原定的 Failed 或 Cancelled；仍需清理时停在 CleanupRequired，并更新 AllowedActions。

调用约定：覆盖适配器创建和移除、电源、MMIO、分区扫描与 Windows 驱动部署中的临时磁盘操作。只观察已保存的任务，不重新执行写入；当前状态恰好符合预期不能单独证明旧任务已结束。作业仍运行、无法读取或回执不足时保留 OutcomeUnknown。Recovery.Operations 提供任务结果，Resources 提供原状态和恢复项，ObservedValues 提供本次核实到的电源与 MMIO 值。核实后不自动继续部署或清理；尚需恢复时由调用方明确调用 RetryCleanupAsync。作业已回收且没有终态证据、进程重启、驱动文件回滚和 Linux 在线部署仍需另行处理。

- 异常 `InvalidOperationException`：当前 AllowedActions 不包含 Reconcile。

<a id="api-0f3fa40d86861ea2"></a>

##### RequestCancellation · 方法

```csharp
public void RequestCancellation();
```

仅发送协作式取消请求，立即返回；正在执行的不可取消底层调用仍需自行结束。

<a id="api-d5769efaf9f99126"></a>

##### RetryCleanupAsync · 方法

```csharp
public async Task<GpuDeploymentSnapshot> RetryCleanupAsync();
```

在允许 RetryCleanup 且 OutcomeUnknown 为 false 时，恢复临时磁盘资源并清理本次新增的 GPU 适配器。

返回：清理完成后返回原定的 Failed 或 Cancelled；仍需处理的资源保留在 Recovery.Resources，状态为 CleanupRequired。

调用约定：先再次核实原任务，再在原资源锁内按盘符、物理盘挂接、磁盘原状态或映像卸载的顺序恢复。只恢复本次改变的资源，借用的已有挂载不卸载；磁盘身份、分区或控制器变化时停止，不操作替代对象。磁盘恢复要求 VM 已关闭，本方法不会自动关机。原来的联机和只读状态会覆盖期间的外部状态修改。恢复失败后保留原始记录；恢复本身结果未知时，下一步只能先 Reconcile。不会重新分配 GPU、复制驱动、继续部署或撤回已完成的电源和 MMIO 修改。

- 异常 `InvalidOperationException`：会话不在 CleanupRequired 状态，或 OutcomeUnknown 为 true。

<a id="api-cbaf6ad49c80c7f4"></a>

##### StartAsync · 方法

```csharp
public async Task<GpuDeploymentSnapshot> StartAsync(Action<GpuDeploymentSnapshot>? progress = null);
```

开始部署并执行到等待用户决定或结束；同一会话只能从 Created 状态启动。

| 参数 | 说明 |
| --- | --- |
| `progress` | 可选的快照通知，在执行流程所在的上下文同步触发。回调应快速返回；界面更新需调度到界面线程。阻塞等待会话写操作会导致死锁；回调异常会被隔离。 |

返回：停止推进时的最新快照，可能仍需用户决定。

- 异常 `InvalidOperationException`：会话已经离开 Created 状态。

<a id="api-b6e5e2765e6b0344"></a>

##### SubmitDecisionAsync · 方法

```csharp
public async Task<GpuDeploymentSnapshot> SubmitDecisionAsync(GpuDeploymentDecision decision);
```

校验当前请求 ID 和输入种类后推进部署；过期或错误的提交不会替代正在等待的合法请求。

| 参数 | 说明 |
| --- | --- |
| `decision` | 与 PendingDecision 对应的确认、分区、Linux 输入或返回决定；不能为空。 |

返回：执行至下一次等待或结束后的快照。

调用约定：选择分区后会重新核对磁盘状态。确认 PowerOff 会直接 TurnOff；确认 Windows 安装会修改离线分区，Linux 部署可能启动或重启来宾。

- 异常 `ArgumentNullException`：decision 为 null。
- 异常 `InvalidOperationException`：没有待确认事项，或请求 ID、输入种类与当前事项不匹配。
- 异常 `ArgumentException`：分区不在当前列表、分区不受支持或已加密，或 Linux 脚本、地址、端口、凭据不符合要求。

<a id="api-671735ce8d9b8726"></a>

### `HostAffinityProcessor` record 类

当前亲和性后端提供的一个宿主逻辑处理器选项。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器 |
| 源文件 | ` Management/Host/Processor/HostAffinityProcessor.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostAffinityProcessor(int Index, uint? PackageId, uint? CoreId, uint? NumaNode, CpuCoreType CoreType) `](#api-5ad9e40bf9cb8bd2) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public uint? CoreId { get; init; } `](#api-7f19eabd3e42cdc9) | HCS 返回的物理核心标识；未提供时为 null。 |
| [` public CpuCoreType CoreType { get; init; } `](#api-31f7b62392a2eea0) | 核心性能类别；未识别时为 Unknown。 |
| [` public int Index { get; init; } `](#api-02974389d90ac361) | 原样传给 CpuAffinityService.SaveAsync 的编号；Classic/Core 为 HCS LpIndex，Root 为唯一 Windows 处理器组内的编号。 |
| [` public uint? NumaNode { get; init; } `](#api-8ca3741d2305ec4d) | HCS 返回的 NUMA 节点；未提供时为 null。 |
| [` public uint? PackageId { get; init; } `](#api-00af2ba1d9b9a2b4) | HCS 返回的插槽标识；Root 枚举未提供时为 null。 |

#### 详细说明

```csharp
public sealed record HostAffinityProcessor(int Index, uint? PackageId, uint? CoreId, uint? NumaNode, CpuCoreType CoreType)
```

| 参数 | 说明 |
| --- | --- |
| `Index` | 原样传给 CpuAffinityService.SaveAsync 的编号；Classic/Core 为 HCS LpIndex，Root 为唯一 Windows 处理器组内的编号。 |
| `PackageId` | HCS 返回的插槽标识；Root 枚举未提供时为 null。 |
| `CoreId` | HCS 返回的物理核心标识；未提供时为 null。 |
| `NumaNode` | HCS 返回的 NUMA 节点；未提供时为 null。 |
| `CoreType` | 核心性能类别；未识别时为 Unknown。 |

#### 构造函数说明

<a id="api-5ad9e40bf9cb8bd2"></a>

##### HostAffinityProcessor · 主构造器

```csharp
public HostAffinityProcessor(int Index, uint? PackageId, uint? CoreId, uint? NumaNode, CpuCoreType CoreType);
```

| 参数 | 说明 |
| --- | --- |
| `Index` | 原样传给 CpuAffinityService.SaveAsync 的编号；Classic/Core 为 HCS LpIndex，Root 为唯一 Windows 处理器组内的编号。 |
| `PackageId` | HCS 返回的插槽标识；Root 枚举未提供时为 null。 |
| `CoreId` | HCS 返回的物理核心标识；未提供时为 null。 |
| `NumaNode` | HCS 返回的 NUMA 节点；未提供时为 null。 |
| `CoreType` | 核心性能类别；未识别时为 Unknown。 |

#### 属性说明

<a id="api-7f19eabd3e42cdc9"></a>

##### CoreId · record 属性

```csharp
public uint? CoreId { get; init; }
```

HCS 返回的物理核心标识；未提供时为 null。

<a id="api-31f7b62392a2eea0"></a>

##### CoreType · record 属性

```csharp
public CpuCoreType CoreType { get; init; }
```

核心性能类别；未识别时为 Unknown。

<a id="api-02974389d90ac361"></a>

##### Index · record 属性

```csharp
public int Index { get; init; }
```

原样传给 CpuAffinityService.SaveAsync 的编号；Classic/Core 为 HCS LpIndex，Root 为唯一 Windows 处理器组内的编号。

<a id="api-8ca3741d2305ec4d"></a>

##### NumaNode · record 属性

```csharp
public uint? NumaNode { get; init; }
```

HCS 返回的 NUMA 节点；未提供时为 null。

<a id="api-00af2ba1d9b9a2b4"></a>

##### PackageId · record 属性

```csharp
public uint? PackageId { get; init; }
```

HCS 返回的插槽标识；Root 枚举未提供时为 null。

<a id="api-692d77f6af1a6a5b"></a>

### `HostConsoleService` 类

配置宿主的虚拟机控制台连接策略。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 修改 / 控制台 |
| 源文件 | ` Management/Host/Console/HostConsoleService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<ApiResponse> SetEnhancedSessionModeEnabledAsync(bool enabled) `](#api-e603f03cc47f8616) | 修改宿主允许增强会话的策略。 |

#### 详细说明

```csharp
public static class HostConsoleService
```

#### 静态方法说明

<a id="api-e603f03cc47f8616"></a>

##### SetEnhancedSessionModeEnabledAsync · 方法

```csharp
public static Task<ApiResponse> SetEnhancedSessionModeEnabledAsync(bool enabled);
```

修改宿主允许增强会话的策略。

| 参数 | 说明 |
| --- | --- |
| `enabled` | 是否允许增强会话；实际连接仍取决于来宾支持。 |

返回：Hyper-V 配置提交响应。

调用约定：当前配置见 HyperVHostSettingsService.GetAsync 返回的 EnhancedSessionModeEnabled；连接还需要来宾支持增强会话。

<a id="api-e73baff26ca9c0f9"></a>

### `HostCpuGroupInfo` 类

宿主 CPU 组的处理器、调度策略与成员快照。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器组 |
| 源文件 | ` Management/Host/ProcessorGroups/HostCpuGroupInfo.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public uint? CpuCap { get; } `](#api-6f83e15a54d26153) | CPU 组的总算力配额，由组内所有成员共同使用。 |
| [` public Guid GroupId { get; } `](#api-807408604f6ed511) | CPU 组的唯一标识，用于查询和修改该组。 |
| [` public uint? IdleProcessorReserve { get; } `](#api-8628048aeb43a221) | 组空闲时仍为本组保留、不出借给其他组的逻辑处理器数量。 |
| [` public IReadOnlyList<uint>? LogicalProcessors { get; } `](#api-84fac401947dbf57) | 该 CPU 组包含的宿主逻辑处理器。 |
| [` public IReadOnlyList<HostCpuGroupMember> Members { get; } `](#api-f55ef5b3c4d9e75a) | 本次已确认绑定到该组的 Hyper-V 虚拟机。 |
| [` public bool MembersComplete { get; } `](#api-40ee0b271a505f0c) | 是否已读全 WMI 可见虚拟机的 CPU 组绑定。 |
| [` public string? MembersError { get; } `](#api-f05a5a292ed5dbb8) | 成员列表未读全的原因。 |
| [` public DateTimeOffset ObservedAtUtc { get; } `](#api-dfa7c8acca704b08) | 本次 CPU 组采集完成的时间。 |
| [` public uint? Priority { get; } `](#api-25ad75dbd43bcd5f) | CPU 组在竞争调度时的优先级，数值越大优先级越高。 |
| [` public uint? RelativeWeight { get; } `](#api-04b452c65c1a651b) | CPU 组竞争处理器时间时使用的相对权重。 |
| [` public string? Revision { get; } `](#api-5f2b565866dd6d2f) | 组配置与成员的版本摘要，用于在修改前检测变化。 |

#### 详细说明

```csharp
public sealed class HostCpuGroupInfo
```

调用约定：组策略适用于 Classic/Core 调度器，修改会影响该组全部使用者。成员列表覆盖 WMI 可见的 Hyper-V 虚拟机；其他 HCS 使用者不在此列表中。修改时将最新快照的 Revision 原样传回 `HostCpuGroupService`。

#### 属性说明

<a id="api-6f83e15a54d26153"></a>

##### CpuCap · 属性

```csharp
public uint? CpuCap { get; }
```

CPU 组的总算力配额，由组内所有成员共同使用。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | CPU 组的总算力配额，由组内所有成员共同使用。 |
| 取值与默认 | 0 至 65536；65536 为满额，32768 为 50%。未取得时为 null。 |
| 修改对应 | 已提供修改入口；输入：cpuCap；提交方式：单字段修改并回读。`HostCpuGroupService.SetCpuCapAsync`。 |

<a id="api-807408604f6ed511"></a>

##### GroupId · 属性

```csharp
public Guid GroupId { get; }
```

CPU 组的唯一标识，用于查询和修改该组。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | CPU 组的唯一标识，用于查询和修改该组。 |
| 取值与默认 | GUID 文本由 32 个十六进制字符和 4 个连字符组成，按 8-4-4-4-12 分组。 |

<a id="api-8628048aeb43a221"></a>

##### IdleProcessorReserve · 属性

```csharp
public uint? IdleProcessorReserve { get; }
```

组空闲时仍为本组保留、不出借给其他组的逻辑处理器数量。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 组空闲时仍为本组保留、不出借给其他组的逻辑处理器数量。 |
| 取值与默认 | 单位为个；0 至组内逻辑处理器数量，最多 255。SMT 和资源域容量还可能限制可用数量。未取得时为 null。 |
| 修改对应 | 已提供修改入口；输入：count；提交方式：单字段修改并回读。`HostCpuGroupService.SetIdleProcessorReserveAsync`。 |

<a id="api-84fac401947dbf57"></a>

##### LogicalProcessors · 属性

```csharp
public IReadOnlyList<uint>? LogicalProcessors { get; }
```

该 CPU 组包含的宿主逻辑处理器。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 该 CPU 组包含的宿主逻辑处理器。 |
| 取值与默认 | HCS 逻辑处理器编号列表；未取得时为 null。 |

<a id="api-f55ef5b3c4d9e75a"></a>

##### Members · 属性

```csharp
public IReadOnlyList<HostCpuGroupMember> Members { get; }
```

本次已确认绑定到该组的 Hyper-V 虚拟机。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 本次已确认绑定到该组的 Hyper-V 虚拟机。 |
| 取值与默认 | 成员列表；MembersComplete 为 false 时可能不完整。 |

<a id="api-40ee0b271a505f0c"></a>

##### MembersComplete · 属性

```csharp
public bool MembersComplete { get; }
```

是否已读全 WMI 可见虚拟机的 CPU 组绑定。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 是否已读全 WMI 可见虚拟机的 CPU 组绑定。 |
| 取值与默认 | true：读取完整；false：读取不完整，原因见 MembersError。 |

<a id="api-f05a5a292ed5dbb8"></a>

##### MembersError · 属性

```csharp
public string? MembersError { get; }
```

成员列表未读全的原因。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 成员列表未读全的原因。 |
| 取值与默认 | 说明文本；列表完整时为 null。 |

<a id="api-dfa7c8acca704b08"></a>

##### ObservedAtUtc · 属性

```csharp
public DateTimeOffset ObservedAtUtc { get; }
```

本次 CPU 组采集完成的时间。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 本次 CPU 组采集完成的时间。 |
| 取值与默认 | UTC 时间，偏移量为 +00:00。 |

<a id="api-25ad75dbd43bcd5f"></a>

##### Priority · 属性

```csharp
public uint? Priority { get; }
```

CPU 组在竞争调度时的优先级，数值越大优先级越高。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | CPU 组在竞争调度时的优先级，数值越大优先级越高。 |
| 取值与默认 | 0 至 7；未取得时为 null。 |
| 修改对应 | 已提供修改入口；输入：priority；提交方式：单字段修改并回读。`HostCpuGroupService.SetPriorityAsync`。 |

<a id="api-04b452c65c1a651b"></a>

##### RelativeWeight · 属性

```csharp
public uint? RelativeWeight { get; }
```

CPU 组竞争处理器时间时使用的相对权重。此名称对应尚未公开命名的 HCS 属性 0x40000。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | CPU 组竞争处理器时间时使用的相对权重。此名称对应尚未公开命名的 HCS 属性 0x40000。 |
| 取值与默认 | 1 至 65535；未取得时为 null。 |
| 修改对应 | 已提供修改入口；输入：weight；提交方式：单字段修改并回读。`HostCpuGroupService.SetRelativeWeightAsync`。 |

<a id="api-5f2b565866dd6d2f"></a>

##### Revision · 属性

```csharp
public string? Revision { get; }
```

组配置与成员的版本摘要，用于在修改前检测变化。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 组配置与成员的版本摘要，用于在修改前检测变化。 |
| 取值与默认 | 修改时原样传回；无法确认组身份、属性或成员时为 null。 |

<a id="api-d87f4f08ed1246c8"></a>

### `HostCpuGroupMember` record 类

绑定到 CPU 组的 Hyper-V 虚拟机。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器组 |
| 源文件 | ` Management/Host/ProcessorGroups/HostCpuGroupInfo.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostCpuGroupMember(Guid VmId, string? Name, VmState State) `](#api-dbc364042b459bf2) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? Name { get; init; } `](#api-1e5e484a49dbfa84) | 虚拟机显示名称；未取得时为 null。 |
| [` public VmState State { get; init; } `](#api-3ea2cc1f76f079e1) | 本次查询的虚拟机运行状态。 |
| [` public Guid VmId { get; init; } `](#api-db38839d7c548906) | 虚拟机 GUID。 |

#### 详细说明

```csharp
public sealed record HostCpuGroupMember(Guid VmId, string? Name, VmState State)
```

| 参数 | 说明 |
| --- | --- |
| `VmId` | 虚拟机 GUID。 |
| `Name` | 虚拟机显示名称；未取得时为 null。 |
| `State` | 本次查询的虚拟机运行状态。 |

#### 构造函数说明

<a id="api-dbc364042b459bf2"></a>

##### HostCpuGroupMember · 主构造器

```csharp
public HostCpuGroupMember(Guid VmId, string? Name, VmState State);
```

| 参数 | 说明 |
| --- | --- |
| `VmId` | 虚拟机 GUID。 |
| `Name` | 虚拟机显示名称；未取得时为 null。 |
| `State` | 本次查询的虚拟机运行状态。 |

#### 属性说明

<a id="api-1e5e484a49dbfa84"></a>

##### Name · record 属性

```csharp
public string? Name { get; init; }
```

虚拟机显示名称；未取得时为 null。

<a id="api-3ea2cc1f76f079e1"></a>

##### State · record 属性

```csharp
public VmState State { get; init; }
```

本次查询的虚拟机运行状态。

<a id="api-db38839d7c548906"></a>

##### VmId · record 属性

```csharp
public Guid VmId { get; init; }
```

虚拟机 GUID。

<a id="api-0333bc67cb65a850"></a>

### `HostCpuGroupService` 类

查询宿主 CPU 组，修改配额、优先级、空闲处理器保留数量和相对权重。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器组；管理层面 / 宿主 / 修改 / 处理器组 |
| 源文件 | ` Management/Host/ProcessorGroups/HostCpuGroupService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse<IReadOnlyList<HostCpuGroupInfo>>> GetAllAsync(CancellationToken cancellationToken = default) `](#api-32c813c114a6b666) | 读取全部宿主 CPU 组及其当前成员；没有 CPU 组时返回成功空列表。 |
| [` public static async Task<ApiResponse<HostCpuGroupInfo>> GetAsync(Guid groupId, CancellationToken cancellationToken = default) `](#api-087122f4aea3c833) | 读取指定宿主 CPU 组的策略、成员及修改所需的版本摘要。 |
| [` public static Task<ApiResponse<HostCpuGroupInfo>> SetCpuCapAsync(Guid groupId, uint cpuCap, string expectedRevision, CancellationToken cancellationToken = default) `](#api-92b992cf83f14692) | 修改 CPU 组的总配额，并确认实际回读值；影响该组全部成员。 |
| [` public static Task<ApiResponse<HostCpuGroupInfo>> SetIdleProcessorReserveAsync(Guid groupId, uint count, string expectedRevision, CancellationToken cancellationToken = default) `](#api-5d2ba3e9d3e27fe2) | 修改 CPU 组在空闲时保留的逻辑处理器数量。 |
| [` public static Task<ApiResponse<HostCpuGroupInfo>> SetPriorityAsync(Guid groupId, uint priority, string expectedRevision, CancellationToken cancellationToken = default) `](#api-26ca8631196e09c1) | 修改 CPU 组的调度优先级，影响该组全部成员。 |
| [` public static Task<ApiResponse<HostCpuGroupInfo>> SetRelativeWeightAsync(Guid groupId, uint weight, string expectedRevision, CancellationToken cancellationToken = default) `](#api-5979acdc912b88eb) | 修改 CPU 组竞争处理器时间时的相对权重。 |

#### 详细说明

```csharp
public static class HostCpuGroupService
```

调用约定：修改适用于 Classic/Core 调度器。先读取组快照，将非空 Revision 原样提交；配置或成员已经变化时返回 Conflict。修改影响该组全部使用者，成功后返回新的快照与 Revision。



Core 调用之间按宿主排队。成员列表仅覆盖 WMI 可见虚拟机。原生提交后等待结果与回读；无法确认结果时返回 OutcomeUnknown，重新查询后再决定后续操作。

#### 静态方法说明

<a id="api-32c813c114a6b666"></a>

##### GetAllAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyList<HostCpuGroupInfo>>> GetAllAsync(CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 查询 / 处理器组。

读取全部宿主 CPU 组及其当前成员；没有 CPU 组时返回成功空列表。

| 参数 | 说明 |
| --- | --- |
| `cancellationToken` | 可取消排队及读取步骤；已进行的同步原生查询不会被强行中断。 |

返回：组的只读快照；逐组检查 MembersComplete 和 Revision 后才可提交修改。

<a id="api-087122f4aea3c833"></a>

##### GetAsync · 方法

```csharp
public static async Task<ApiResponse<HostCpuGroupInfo>> GetAsync(Guid groupId, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 查询 / 处理器组。

读取指定宿主 CPU 组的策略、成员及修改所需的版本摘要。

| 参数 | 说明 |
| --- | --- |
| `groupId` | 宿主 CPU 组 GUID，不能为全零。 |
| `cancellationToken` | 可取消排队及读取步骤，不强行中断同步原生查询。 |

返回：指定组快照；组不存在时返回 NotFound，不创建新组。

调用约定：组 GUID 可取自亲和性保存结果的 CurrentGroupId，或用 `HostCpuGroupService.GetAllAsync` 按 Members 中的 VmId 查找。读取后将非空 Revision 原样传给本组策略修改方法；每次成功修改返回的新快照包含新的 Revision。

<a id="api-92b992cf83f14692"></a>

##### SetCpuCapAsync · 方法

```csharp
public static Task<ApiResponse<HostCpuGroupInfo>> SetCpuCapAsync(Guid groupId, uint cpuCap, string expectedRevision, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 修改 / 处理器组。

修改 CPU 组的总配额，并确认实际回读值；影响该组全部成员。

| 参数 | 说明 |
| --- | --- |
| `groupId` | 待修改的宿主 CPU 组 GUID。 |
| `cpuCap` | 组总配额，0 至 65536；65536 为满额，32768 为 50%。 |
| `expectedRevision` | GetAsync 或 GetAllAsync 返回的非空 Revision；过期时在任何写入前返回 Conflict。 |
| `cancellationToken` | 仅在原生提交前取消；提交后等待返回及回读，不自动撤销请求。 |

返回：修改后的只读快照；无法确认结果时返回 OutcomeUnknown，调用方应重新读取，不能直接重试。

<a id="api-5d2ba3e9d3e27fe2"></a>

##### SetIdleProcessorReserveAsync · 方法

```csharp
public static Task<ApiResponse<HostCpuGroupInfo>> SetIdleProcessorReserveAsync(Guid groupId, uint count, string expectedRevision, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 修改 / 处理器组。

修改 CPU 组在空闲时保留的逻辑处理器数量。

| 参数 | 说明 |
| --- | --- |
| `groupId` | 待修改的宿主 CPU 组 GUID。 |
| `count` | Core 支持 0 至 min(255,组逻辑处理器数)；具体 Core 调度器、SMT 和拓扑条件仍由 HCS 校验。 |
| `expectedRevision` | 最近读取的非空 Revision；过期时不执行修改。 |
| `cancellationToken` | 仅在原生提交前取消；提交后完成返回和回读。 |

返回：修改后经确认的组快照；保留系统原始失败，不猜测或自动调整数量。

<a id="api-26ca8631196e09c1"></a>

##### SetPriorityAsync · 方法

```csharp
public static Task<ApiResponse<HostCpuGroupInfo>> SetPriorityAsync(Guid groupId, uint priority, string expectedRevision, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 修改 / 处理器组。

修改 CPU 组的调度优先级，影响该组全部成员。

| 参数 | 说明 |
| --- | --- |
| `groupId` | 待修改的宿主 CPU 组 GUID。 |
| `priority` | 调度优先级原始值，0 至 7；数值越大优先级越高，不表示固定时间份额。 |
| `expectedRevision` | 最近读取的非空 Revision；过期时不执行修改。 |
| `cancellationToken` | 仅在原生提交前取消；提交后完成返回和回读。 |

返回：修改后经确认的组快照；失败不自动回滚或重放。

<a id="api-5979acdc912b88eb"></a>

##### SetRelativeWeightAsync · 方法

```csharp
public static Task<ApiResponse<HostCpuGroupInfo>> SetRelativeWeightAsync(Guid groupId, uint weight, string expectedRevision, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 修改 / 处理器组。

修改 CPU 组竞争处理器时间时的相对权重。

| 参数 | 说明 |
| --- | --- |
| `groupId` | 待修改的宿主 CPU 组 GUID。 |
| `weight` | 相对权重原始值，1 至 65535。 |
| `expectedRevision` | 最近读取的非空 Revision；过期时不执行修改。 |
| `cancellationToken` | 仅在原生提交前取消；提交后完成返回和回读。 |

返回：修改后经确认的组快照；系统未提供该属性时返回 Unsupported。

调用约定：RelativeWeight 对应 HCS 属性 0x40000，其名称与语义尚未纳入微软公开接口约定。

<a id="api-f899a0c126136eb7"></a>

### `HostDiskMountLease` 类

一次宿主映像挂载的独占使用期。释放前，同一映像的其他 Core 挂载、卸载和临时使用请求等待。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 修改 / 存储 |
| 源文件 | ` Management/Host/Storage/HostDiskMountLease.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ApiResponse? CleanupResult { get; } `](#api-c9b251a1062cce1c) | 清理和资源锁释放完成后的结果；尚未完成时为 null。 |
| [` public HostDiskMountResult Result { get; } `](#api-8973d789bc47d16f) | 取得使用权后的挂载快照，包含实际访问模式和是否由本次建立。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public async ValueTask DisposeAsync() `](#api-4c48912ab7eb1c65) | 等待 ReleaseAsync 完成；适用于 await using。 |
| [` public Task<ApiResponse> ReleaseAsync() `](#api-550a527d41a1c368) | 结束使用，清理本次建立的挂载并归还使用权；原先已有的挂载保留。 |

#### 详细说明

```csharp
public sealed class HostDiskMountLease : IAsyncDisposable
```

调用约定：使用 await using 或在 finally 中等待 ReleaseAsync。先结束文件读写和相关任务，再释放；不要在持有期间调用同一映像的挂载或卸载入口，否则会等待自己。不同映像可以并行。此约定不阻止其他工具直接操作 Windows 存储。

#### 属性说明

<a id="api-c9b251a1062cce1c"></a>

##### CleanupResult · 属性

```csharp
public ApiResponse? CleanupResult { get; }
```

清理和资源锁释放完成后的结果；尚未完成时为 null。失败时可结合 Result.ImagePath 核对剩余挂载。

<a id="api-8973d789bc47d16f"></a>

##### Result · 属性

```csharp
public HostDiskMountResult Result { get; }
```

取得使用权后的挂载快照，包含实际访问模式和是否由本次建立。失败结果也需释放使用期。

#### 方法说明

<a id="api-4c48912ab7eb1c65"></a>

##### DisposeAsync · 方法

```csharp
public async ValueTask DisposeAsync();
```

等待 ReleaseAsync 完成；适用于 await using。清理失败时抛出异常，结构化结果仍保存在 CleanupResult。

返回：清理完成后的异步操作。

- 异常 `IOException`：挂载清理或资源锁释放失败。

<a id="api-550a527d41a1c368"></a>

##### ReleaseAsync · 方法

```csharp
public Task<ApiResponse> ReleaseAsync();
```

结束使用，清理本次建立的挂载并归还使用权；原先已有的挂载保留。重复调用等待同一次清理，不重复卸载。

返回：清理与资源锁释放结果；无法确认挂载归属或卸载失败时返回错误。

调用约定：调用前需等待使用此映像的任务结束。此方法不接受取消令牌，避免业务取消中断必要的收尾；失败后也会释放锁，后续调用必须重新读取实际状态。

<a id="api-2dc1cd9f66ce73d2"></a>

### `HostDiskMountResult` record 类

映像挂载结果，分别表达挂载状态、磁盘发现和清理责任。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储 |
| 源文件 | ` Management/Host/Storage/HostDiskMountResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostDiskMountResult(string ImagePath, bool? IsMounted, int? DiskNumber, bool CreatedByCall, string Error, bool? IsReadOnly = null, string AccessError = "") `](#api-5ac98dbfd10dbc2d) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string AccessError { get; init; } `](#api-5cad90cdbac309ba) | 未能取得实际访问模式的原因；查询成功时为空字符串。 |
| [` public bool CreatedByCall { get; init; } `](#api-cd92e28cb9158a3e) | 挂载是否由本次调用建立；公开挂载入口返回的使用期对象负责在释放时清理，原有挂载不会自动卸载。 |
| [` public int? DiskNumber { get; init; } `](#api-80c2e43b88f4eb89) | 已发现的宿主磁盘编号；未知为 null。 |
| [` public string Error { get; init; } `](#api-d1351a2f5399c02c) | 挂载或发现失败说明；为空表示完整成功。 |
| [` public string ImagePath { get; init; } `](#api-8d0c23b7f84ee6eb) | 映像完整路径。 |
| [` public bool? IsMounted { get; init; } `](#api-3ce6c2cbb067258d) | 已确认挂载为 true，已确认未挂载为 false，无法确认时为 null。 |
| [` public bool? IsReadOnly { get; init; } `](#api-05da7786b48066a8) | 宿主磁盘的实际访问模式：true 只读，false 读写，null 未取得。 |
| [` public bool RequiresInspection { get; } `](#api-457200a6fe554249) | 挂载状态是否未知；为 true 时需重新查询映像状态。 |
| [` public bool Success { get; } `](#api-c43e0ce425dcf08c) | 挂载及宿主磁盘发现均成功；实际访问模式独立由 IsReadOnly 表示，未知模式不会改写此状态。 |

#### 详细说明

```csharp
public sealed record HostDiskMountResult(string ImagePath, bool? IsMounted, int? DiskNumber,
    bool CreatedByCall, string Error, bool? IsReadOnly = null, string AccessError = "")
```

| 参数 | 说明 |
| --- | --- |
| `ImagePath` | 映像完整路径。 |
| `IsMounted` | 已确认挂载为 true，已确认未挂载为 false，无法确认时为 null。 |
| `DiskNumber` | 已发现的宿主磁盘编号；未知为 null。 |
| `CreatedByCall` | 挂载是否由本次调用建立；公开挂载入口返回的使用期对象负责在释放时清理，原有挂载不会自动卸载。 |
| `Error` | 挂载或发现失败说明；为空表示完整成功。 |
| `IsReadOnly` | 宿主磁盘的实际访问模式：true 只读，false 读写，null 未取得。已有挂载沿用原模式，可能与请求不同。 |
| `AccessError` | 未能取得实际访问模式的原因；查询成功时为空字符串。 |

#### 构造函数说明

<a id="api-5ac98dbfd10dbc2d"></a>

##### HostDiskMountResult · 主构造器

```csharp
public HostDiskMountResult(string ImagePath, bool? IsMounted, int? DiskNumber, bool CreatedByCall, string Error, bool? IsReadOnly = null, string AccessError = "");
```

| 参数 | 说明 |
| --- | --- |
| `ImagePath` | 映像完整路径。 |
| `IsMounted` | 已确认挂载为 true，已确认未挂载为 false，无法确认时为 null。 |
| `DiskNumber` | 已发现的宿主磁盘编号；未知为 null。 |
| `CreatedByCall` | 挂载是否由本次调用建立；公开挂载入口返回的使用期对象负责在释放时清理，原有挂载不会自动卸载。 |
| `Error` | 挂载或发现失败说明；为空表示完整成功。 |
| `IsReadOnly` | 宿主磁盘的实际访问模式：true 只读，false 读写，null 未取得。已有挂载沿用原模式，可能与请求不同。 |
| `AccessError` | 未能取得实际访问模式的原因；查询成功时为空字符串。 |

#### 属性说明

<a id="api-5cad90cdbac309ba"></a>

##### AccessError · record 属性

```csharp
public string AccessError { get; init; }
```

未能取得实际访问模式的原因；查询成功时为空字符串。

<a id="api-cd92e28cb9158a3e"></a>

##### CreatedByCall · record 属性

```csharp
public bool CreatedByCall { get; init; }
```

挂载是否由本次调用建立；公开挂载入口返回的使用期对象负责在释放时清理，原有挂载不会自动卸载。

<a id="api-80c2e43b88f4eb89"></a>

##### DiskNumber · record 属性

```csharp
public int? DiskNumber { get; init; }
```

已发现的宿主磁盘编号；未知为 null。

<a id="api-d1351a2f5399c02c"></a>

##### Error · record 属性

```csharp
public string Error { get; init; }
```

挂载或发现失败说明；为空表示完整成功。

<a id="api-8d0c23b7f84ee6eb"></a>

##### ImagePath · record 属性

```csharp
public string ImagePath { get; init; }
```

映像完整路径。

<a id="api-3ce6c2cbb067258d"></a>

##### IsMounted · record 属性

```csharp
public bool? IsMounted { get; init; }
```

已确认挂载为 true，已确认未挂载为 false，无法确认时为 null。

<a id="api-05da7786b48066a8"></a>

##### IsReadOnly · record 属性

```csharp
public bool? IsReadOnly { get; init; }
```

宿主磁盘的实际访问模式：true 只读，false 读写，null 未取得。已有挂载沿用原模式，可能与请求不同。

<a id="api-457200a6fe554249"></a>

##### RequiresInspection · 属性

```csharp
public bool RequiresInspection { get; }
```

挂载状态是否未知；为 true 时需重新查询映像状态。

<a id="api-c43e0ce425dcf08c"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

挂载及宿主磁盘发现均成功；实际访问模式独立由 IsReadOnly 表示，未知模式不会改写此状态。

<a id="api-b7abecd5b9d2180e"></a>

### `HostDiskReference` record 类

查询时选中的宿主磁盘身份；修改时原样回传，不以磁盘编号单独确认设备。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储 |
| 源文件 | ` Management/Host/Storage/HostDiskReference.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostDiskReference(int Number, string UniqueId, string SerialNumber, long SizeBytes) `](#api-4985a1511d5c8a5d) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Number { get; init; } `](#api-780d9edca739e2f6) | 查询时的宿主磁盘编号；编号变化后需要重新查询。 |
| [` public string SerialNumber { get; init; } `](#api-bfeb59019e16d5fa) | 磁盘序列号，未提供时为空。 |
| [` public long SizeBytes { get; init; } `](#api-64aa5e468c40342e) | 查询时的容量，单位为字节。 |
| [` public string UniqueId { get; init; } `](#api-e0d8ac0b7593f00c) | 存储提供程序返回的磁盘唯一标识，未提供时为空。 |

#### 详细说明

```csharp
public sealed record HostDiskReference(int Number, string UniqueId, string SerialNumber, long SizeBytes)
```

| 参数 | 说明 |
| --- | --- |
| `Number` | 查询时的宿主磁盘编号；编号变化后需要重新查询。 |
| `UniqueId` | 存储提供程序返回的磁盘唯一标识，未提供时为空。 |
| `SerialNumber` | 磁盘序列号，未提供时为空。 |
| `SizeBytes` | 查询时的容量，单位为字节。 |

调用约定：由 HostDiskInfo.Reference 或 GetDiskReferenceAsync 取得，修改时原样传回。设备标识或容量已变化时拒绝修改；联机、只读状态的变化不影响身份核对。

#### 构造函数说明

<a id="api-4985a1511d5c8a5d"></a>

##### HostDiskReference · 主构造器

```csharp
public HostDiskReference(int Number, string UniqueId, string SerialNumber, long SizeBytes);
```

| 参数 | 说明 |
| --- | --- |
| `Number` | 查询时的宿主磁盘编号；编号变化后需要重新查询。 |
| `UniqueId` | 存储提供程序返回的磁盘唯一标识，未提供时为空。 |
| `SerialNumber` | 磁盘序列号，未提供时为空。 |
| `SizeBytes` | 查询时的容量，单位为字节。 |

#### 属性说明

<a id="api-780d9edca739e2f6"></a>

##### Number · record 属性

```csharp
public int Number { get; init; }
```

查询时的宿主磁盘编号；编号变化后需要重新查询。

<a id="api-bfeb59019e16d5fa"></a>

##### SerialNumber · record 属性

```csharp
public string SerialNumber { get; init; }
```

磁盘序列号，未提供时为空。

<a id="api-64aa5e468c40342e"></a>

##### SizeBytes · record 属性

```csharp
public long SizeBytes { get; init; }
```

查询时的容量，单位为字节。

<a id="api-e0d8ac0b7593f00c"></a>

##### UniqueId · record 属性

```csharp
public string UniqueId { get; init; }
```

存储提供程序返回的磁盘唯一标识，未提供时为空。

<a id="api-e3330b9fc8413dee"></a>

### `HostDiskService` 类

查询宿主物理磁盘、光驱和分区，并执行磁盘挂载、脱机、只读及盘符操作。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储；管理层面 / 宿主 / 修改 / 存储 |
| 源文件 | ` Management/Host/Storage/HostDiskService.Identity.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse<char>> AssignPartitionDriveLetterAsync(HostPartitionReference partition, char driveLetter) `](#api-d8111a87ffddf2a8) | 为宿主分区添加指定盘符访问路径。 |
| [` public static async Task<ApiResponse> DismountVhdxAsync(string imagePath) `](#api-b59d381fa5a58060) | 显式卸载宿主上的虚拟磁盘，并重新查询确认状态；等待其他 Core 使用者结束使用和清理。 |
| [` public static async Task<ApiResponse<HostDiskReference>> GetDiskReferenceAsync(int diskNumber) `](#api-10985885e3f822cc) | 读取当前编号对应的磁盘身份，供调用方选择并在后续修改时原样回传。 |
| [` public static async Task<ApiResponse<List<HostDiskInfo>>> GetHostDisksAsync() `](#api-5a5ea3d7d36f9c7c) | 读取宿主磁盘身份、容量及物理盘直通候选状态。 |
| [` public static async Task<ApiResponse<List<HostOpticalDriveInfo>>> GetHostOpticalDrivesAsync() `](#api-b1850ace8b2b1346) | 读取具有 PnP 设备身份的宿主光驱。 |
| [` public static async Task<ApiResponse<HostPartitionReference>> GetPartitionReferenceAsync(HostDiskReference disk, int partitionNumber) `](#api-76a9aa8a22314f9f) | 读取所选磁盘上的分区身份，供添加或移除盘符使用。 |
| [` public static Task<HostDiskMountLease> MountDiskImageAsync(string imagePath, CancellationToken cancellationToken = default) `](#api-492a94e1ef00d54d) | 挂载宿主磁盘映像，并取得独占使用期。 |
| [` public static Task<HostDiskMountLease> MountVhdxAsync(string imagePath, bool readOnly = false, CancellationToken cancellationToken = default) `](#api-21c619753396b0cf) | 通过 Hyper-V 挂载虚拟磁盘，并取得独占使用期。 |
| [` public static async Task<ApiResponse> RemovePartitionAccessPathAsync(HostPartitionReference partition, char driveLetter) `](#api-7a8872188e88f69d) | 移除宿主分区的指定盘符访问路径，不删除分区。 |
| [` public static Task<ApiResponse> SetDefaultVirtualHardDiskPathAsync(string path) `](#api-c920f159d7c59109) | 修改新建虚拟磁盘的宿主默认目录，不移动已有磁盘。 |
| [` public static async Task<ApiResponse> SetDiskOfflineStatusAsync(HostDiskReference disk, bool isOffline) `](#api-6d08c05be488f81a) | 将宿主磁盘置为脱机或联机，立即影响该磁盘在宿主的可访问性。 |
| [` public static async Task<ApiResponse> SetDiskReadOnlyAsync(HostDiskReference disk, bool isReadOnly) `](#api-f64d9536a93ddb5b) | 修改宿主磁盘的只读属性。 |

#### 详细说明

```csharp
public static partial class HostDiskService
```

调用约定：修改需要宿主存储管理权限。Core 对同一磁盘或映像的写操作跨进程排队，不同资源可并行；映像通过卷和文件身份匹配。实际完成情况见各操作结果。

#### 静态方法说明

<a id="api-d8111a87ffddf2a8"></a>

##### AssignPartitionDriveLetterAsync · 方法

```csharp
public static async Task<ApiResponse<char>> AssignPartitionDriveLetterAsync(HostPartitionReference partition, char driveLetter);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

为宿主分区添加指定盘符访问路径。

| 参数 | 说明 |
| --- | --- |
| `partition` | 查询时选中的分区身份，含所属磁盘；身份不匹配时拒绝修改。 |
| `driveLetter` | 单个盘符字母 A 至 Z，不带冒号或反斜杠。 |

返回：成功响应包含盘符；查询和修改失败保留错误。

<a id="api-b59d381fa5a58060"></a>

##### DismountVhdxAsync · 方法

```csharp
public static async Task<ApiResponse> DismountVhdxAsync(string imagePath);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

显式卸载宿主上的虚拟磁盘，并重新查询确认状态；等待其他 Core 使用者结束使用和清理。

| 参数 | 说明 |
| --- | --- |
| `imagePath` | 待卸载的虚拟磁盘完整路径。 |

返回：已确认未挂载时成功；查询、分离或状态确认失败均明确报告。

调用约定：用于有意结束现存挂载。临时使用请释放 HostDiskMountLease，不要在持有同一映像使用期时调用此入口。

<a id="api-10985885e3f822cc"></a>

##### GetDiskReferenceAsync · 方法

```csharp
public static async Task<ApiResponse<HostDiskReference>> GetDiskReferenceAsync(int diskNumber);
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

读取当前编号对应的磁盘身份，供调用方选择并在后续修改时原样回传。

| 参数 | 说明 |
| --- | --- |
| `diskNumber` | 当前宿主磁盘编号，例如 0 表示磁盘 0。 |

返回：磁盘身份；不存在或无法取得有效标识时失败。

<a id="api-5a5ea3d7d36f9c7c"></a>

##### GetHostDisksAsync · 方法

```csharp
public static async Task<ApiResponse<List<HostDiskInfo>>> GetHostDisksAsync();
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

读取宿主磁盘身份、容量及物理盘直通候选状态。

返回：按磁盘编号排序的磁盘清单；存储或归属查询失败时返回错误。

<a id="api-b1850ace8b2b1346"></a>

##### GetHostOpticalDrivesAsync · 方法

```csharp
public static async Task<ApiResponse<List<HostOpticalDriveInfo>>> GetHostOpticalDrivesAsync();
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

读取具有 PnP 设备身份的宿主光驱。

返回：光驱列表响应；没有匹配光驱时列表为空。

<a id="api-76a9aa8a22314f9f"></a>

##### GetPartitionReferenceAsync · 方法

```csharp
public static async Task<ApiResponse<HostPartitionReference>> GetPartitionReferenceAsync(HostDiskReference disk, int partitionNumber);
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

读取所选磁盘上的分区身份，供添加或移除盘符使用。

| 参数 | 说明 |
| --- | --- |
| `disk` | 已经选中的磁盘身份；读取前后均确认仍是同一设备。 |
| `partitionNumber` | 该磁盘上的分区编号。 |

返回：分区身份；查询失败、身份变化或缺失时不返回可修改的引用。

<a id="api-492a94e1ef00d54d"></a>

##### MountDiskImageAsync · 方法

```csharp
public static Task<HostDiskMountLease> MountDiskImageAsync(string imagePath, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

挂载宿主磁盘映像，并取得独占使用期。

| 参数 | 说明 |
| --- | --- |
| `imagePath` | 宿主映像完整路径。 |
| `cancellationToken` | 等待使用权、提交前及磁盘发现期间取消；取得使用期后，取消不会替调用方结束文件访问或释放使用权。 |

返回：需释放的使用期对象；先检查 Result，使用结束后等待 ReleaseAsync 或 DisposeAsync。

调用约定：新挂载请求读写访问且不自动分配盘符；已有挂载沿用实际模式，见 Result.IsReadOnly，无法取得时为 null 并提供 AccessError。同一映像的其他 Core 使用者全程排队。取得使用权前的文件身份、锁或取消错误直接传播，不返回使用期对象。

<a id="api-21c619753396b0cf"></a>

##### MountVhdxAsync · 方法

```csharp
public static Task<HostDiskMountLease> MountVhdxAsync(string imagePath, bool readOnly = false, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

通过 Hyper-V 挂载虚拟磁盘，并取得独占使用期。

| 参数 | 说明 |
| --- | --- |
| `imagePath` | 宿主虚拟磁盘完整路径。 |
| `readOnly` | 新挂载是否只读；已有挂载保留原访问模式。 |
| `cancellationToken` | 在等待资源锁、提交前及磁盘发现期间取消；不会终止原生作业，也不会自动释放已返回的使用期。 |

返回：需释放的使用期对象。Result 失败也需释放；本次建立的挂载自动清理，已有挂载保留。

调用约定：已有挂载沿用原访问模式，见 Result.IsReadOnly。完成文件访问后释放使用期：本次建立的挂载自动卸载，已有挂载保留；其他 Core 调用者等待到清理完成。取得使用权前发生的文件身份、锁或取消异常直接抛出。

示例：

```csharp
using System.IO;
using ExHyperV.Services;
public static class MountedDiskExample
{
    public static async Task UseAsync(string path, Func<int, bool?, Task> use)
    {
        await using var lease = await HostDiskService.MountVhdxAsync(path, readOnly: true);
        var result = lease.Result;
        if (!result.Success) throw new IOException(result.Error);
        // 只读是新挂载的请求。已有挂载可能是读写；null 表示模式未知。
        await use(result.DiskNumber!.Value, result.IsReadOnly);
        // use 必须等待它启动的全部文件操作结束，随后才由 await using 清理。
    }
}
```

<a id="api-7a8872188e88f69d"></a>

##### RemovePartitionAccessPathAsync · 方法

```csharp
public static async Task<ApiResponse> RemovePartitionAccessPathAsync(HostPartitionReference partition, char driveLetter);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

移除宿主分区的指定盘符访问路径，不删除分区。

| 参数 | 说明 |
| --- | --- |
| `partition` | 查询时选中的分区身份，含所属磁盘；身份不匹配时拒绝修改。 |
| `driveLetter` | 要移除的盘符字母。 |

返回：盘符移除结果；原分区不存在或已被替换时失败，不修改替代分区。

<a id="api-c920f159d7c59109"></a>

##### SetDefaultVirtualHardDiskPathAsync · 方法

```csharp
public static Task<ApiResponse> SetDefaultVirtualHardDiskPathAsync(string path);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

修改新建虚拟磁盘的宿主默认目录，不移动已有磁盘。

| 参数 | 说明 |
| --- | --- |
| `path` | 宿主上的默认磁盘目录路径。 |

返回：Hyper-V 配置提交响应。

<a id="api-6d08c05be488f81a"></a>

##### SetDiskOfflineStatusAsync · 方法

```csharp
public static async Task<ApiResponse> SetDiskOfflineStatusAsync(HostDiskReference disk, bool isOffline);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

将宿主磁盘置为脱机或联机，立即影响该磁盘在宿主的可访问性。

| 参数 | 说明 |
| --- | --- |
| `disk` | 查询时选中的磁盘身份；身份不匹配时返回 Conflict，不修改同编号的替代设备。 |
| `isOffline` | true 脱机，false 联机。 |

返回：存储接口的操作响应；找不到目标磁盘时失败。

<a id="api-f64d9536a93ddb5b"></a>

##### SetDiskReadOnlyAsync · 方法

```csharp
public static async Task<ApiResponse> SetDiskReadOnlyAsync(HostDiskReference disk, bool isReadOnly);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

修改宿主磁盘的只读属性。

| 参数 | 说明 |
| --- | --- |
| `disk` | 查询时选中的磁盘身份；只核对设备，不比较原只读状态。 |
| `isReadOnly` | true 设置只读，false 取消只读。 |

返回：存储接口的操作响应；找不到目标磁盘时失败。

<a id="api-9579b5f504d1fb87"></a>

### `HostGpuService` 类

读取宿主 GPU 及分区能力，并配置宿主 GPU 分配策略。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / GPU；管理层面 / 宿主 / 修改 / GPU |
| 源文件 | ` Management/Host/Gpu/HostGpuService.Policy.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static ApiResponse<bool> GetAssignmentRestrictionsRelaxed() `](#api-f98b9400716fd8af) | 读取宿主是否已放宽 GPU 分配限制。 |
| [` public static async Task<ApiResponse<IReadOnlyList<HostGpuInfo>>> GetHostGpusAsync() `](#api-e3d65bd68b3e9065) | 读取当前在位的 PCI/ACPI 显示适配器，并关联 Hyper-V 可分区资源。 |
| [` public static bool IsGpuPartitioningVersionSupported(int? buildNumber = null) `](#api-9d62ac5622edf619) | 按应用规则检查 Windows 构建号是否达到 GPU 分区功能的版本门槛。 |
| [` public static ApiResponse SetAssignmentRestrictionsRelaxed(bool relaxed) `](#api-b627bf14aaea7eda) | 设置是否放宽宿主 GPU 分配限制。 |

#### 详细说明

```csharp
public static partial class HostGpuService
```

#### 静态方法说明

<a id="api-f98b9400716fd8af"></a>

##### GetAssignmentRestrictionsRelaxed · 方法

```csharp
public static ApiResponse<bool> GetAssignmentRestrictionsRelaxed();
```

功能归属：管理层面 / 宿主 / 查询 / GPU。

读取宿主是否已放宽 GPU 分配限制。

返回：成功时两个策略均为 DWORD 0 才返回 true；未配置或其他值返回 false，读取异常返回失败。

<a id="api-e3d65bd68b3e9065"></a>

##### GetHostGpusAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyList<HostGpuInfo>>> GetHostGpusAsync();
```

功能归属：管理层面 / 宿主 / 查询 / GPU。

读取当前在位的 PCI/ACPI 显示适配器，并关联 Hyper-V 可分区资源。

返回：GPU 清单或查询错误；专用显存为 null 表示未取得，0 是有效容量。原生设备枚举的部分失败可能表现为缺项或空清单。

调用约定：GPU 分区要求 Windows build ≥ 17134，并由设备的 IsPartitionable 表示当前是否存在可分区资源。宿主分配限制通过 SetAssignmentRestrictionsRelaxed 配置。

<a id="api-9d62ac5622edf619"></a>

##### IsGpuPartitioningVersionSupported · 方法

```csharp
public static bool IsGpuPartitioningVersionSupported(int? buildNumber = null);
```

功能归属：管理层面 / 宿主 / 查询 / GPU。

按应用规则检查 Windows 构建号是否达到 GPU 分区功能的版本门槛。

| 参数 | 说明 |
| --- | --- |
| `buildNumber` | Windows 构建号；null 使用本机版本。 |

返回：构建号至少为 17134 时为 true。

<a id="api-b627bf14aaea7eda"></a>

##### SetAssignmentRestrictionsRelaxed · 方法

```csharp
public static ApiResponse SetAssignmentRestrictionsRelaxed(bool relaxed);
```

功能归属：管理层面 / 宿主 / 修改 / GPU。

设置是否放宽宿主 GPU 分配限制。

| 参数 | 说明 |
| --- | --- |
| `relaxed` | true 放宽安全和设备支持限制；false 删除这两个覆盖值，恢复系统默认策略。 |

返回：两个策略值均完成修改时成功；否则返回失败原因。

调用约定：写入或删除可能部分完成；第二个值失败时不撤销第一个值。false 表示恢复默认，不恢复此前自定义的值。通过 `HostGpuService.GetAssignmentRestrictionsRelaxed` 读取是否已放宽限制。

<a id="api-90a99e60c4861486"></a>

### `HostNetworkService` 类

校验并修改宿主动态 MAC 地址池。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 修改 / 网络 |
| 源文件 | ` Management/Host/Network/HostNetworkService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static string? NormalizeDynamicMacAddress(string? value) `](#api-4693ff8c8ecb8744) | 将支持的 MAC 输入规范为无分隔符的大写十六进制。 |
| [` public static Task<ApiResponse> SetDynamicMacAddressRangeAsync(string minimum, string maximum) `](#api-c0cf8a1df8f18e8b) | 校验后提交宿主动态 MAC 地址池上下界。 |
| [` public static (string? Minimum, string? Maximum, string? Error) ValidateDynamicMacAddressRange(string? minimum, string? maximum) `](#api-0556bbc16b8a997d) | 规范化动态 MAC 地址池，并校验输入格式及上下界顺序。 |

#### 详细说明

```csharp
public static class HostNetworkService
```

#### 静态方法说明

<a id="api-4693ff8c8ecb8744"></a>

##### NormalizeDynamicMacAddress · 方法

```csharp
public static string? NormalizeDynamicMacAddress(string? value);
```

将支持的 MAC 输入规范为无分隔符的大写十六进制。

| 参数 | 说明 |
| --- | --- |
| `value` | 12 位十六进制，或由冒号/短横线分隔的六组地址。 |

返回：12 位规范 MAC；空值或格式不合法时为 null。

<a id="api-c0cf8a1df8f18e8b"></a>

##### SetDynamicMacAddressRangeAsync · 方法

```csharp
public static Task<ApiResponse> SetDynamicMacAddressRangeAsync(string minimum, string maximum);
```

校验后提交宿主动态 MAC 地址池上下界。

| 参数 | 说明 |
| --- | --- |
| `minimum` | 动态 MAC 池下界，支持规范化输入格式。 |
| `maximum` | 动态 MAC 池上界，必须不小于下界。 |

返回：校验或 Hyper-V 提交响应。

<a id="api-0556bbc16b8a997d"></a>

##### ValidateDynamicMacAddressRange · 方法

```csharp
public static (string? Minimum, string? Maximum, string? Error) ValidateDynamicMacAddressRange(string? minimum, string? maximum);
```

规范化动态 MAC 地址池，并校验输入格式及上下界顺序。

| 参数 | 说明 |
| --- | --- |
| `minimum` | 动态 MAC 池下界，支持规范化输入格式。 |
| `maximum` | 动态 MAC 池上界，必须不小于下界。 |

返回：规范化上下界及错误；Error=null 表示校验通过。

调用约定：检查范围为输入格式与上下界顺序；地址分配限制由 Hyper-V 在提交时校验。

<a id="api-d11d60064e518797"></a>

### `HostNvmeService` 类

管理主机原生 NVMe 功能的注册表覆盖开关；配置需要管理员权限并在重启后由系统采用。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储；管理层面 / 宿主 / 修改 / 存储 |
| 源文件 | ` Management/Host/Storage/HostNvmeService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static ApiResponse<bool> IsNativeNvmeEnabled() `](#api-fbe1257344400750) | 读取原生 NVMe 的启用配置；修改此开关要求 Windows build ≥ 26100。 |
| [` public static bool IsNativeNvmeSupported(int? buildNumber = null) `](#api-2d5d02a14a51e2e8) | 判断原生 NVMe 开关是否满足 Windows build ≥ 26100 的版本条件。 |
| [` public static ApiResponse SetNativeNvmeEnabled(bool enabled) `](#api-a688764490341628) | 设置原生 NVMe 开关，供系统重启后采用。 |

#### 详细说明

```csharp
public static class HostNvmeService
```

#### 静态方法说明

<a id="api-fbe1257344400750"></a>

##### IsNativeNvmeEnabled · 方法

```csharp
public static ApiResponse<bool> IsNativeNvmeEnabled();
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

读取原生 NVMe 的启用配置；修改此开关要求 Windows build ≥ 26100。

返回：成功时覆盖 DWORD 为 1 返回 true，不存在或其他值返回 false；读取失败返回错误。

调用约定：读取保存的启用配置；修改通过 SetNativeNvmeEnabled 提交，重启后由系统采用。

<a id="api-2d5d02a14a51e2e8"></a>

##### IsNativeNvmeSupported · 方法

```csharp
public static bool IsNativeNvmeSupported(int? buildNumber = null);
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

判断原生 NVMe 开关是否满足 Windows build ≥ 26100 的版本条件。

| 参数 | 说明 |
| --- | --- |
| `buildNumber` | 待判断的 Windows 构建号；null 使用当前系统构建号。 |

返回：构建号至少为 26100 时为 true。

调用约定：此结果只检查构建号；驱动实际采用情况由 Windows 决定。

<a id="api-a688764490341628"></a>

##### SetNativeNvmeEnabled · 方法

```csharp
public static ApiResponse SetNativeNvmeEnabled(bool enabled);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

设置原生 NVMe 开关，供系统重启后采用。

| 参数 | 说明 |
| --- | --- |
| `enabled` | true 写入启用配置；false 删除启用配置并恢复系统默认，不强制关闭系统可能默认启用的功能。 |

返回：配置写入结果；修改后需要重启。

调用约定：可用条件为 Windows build ≥ 26100；Core 在访问注册表前执行检查。不满足时抛出 PlatformNotSupportedException；注册表操作失败通过响应返回。配置需重启后由系统采用。

- 异常 `PlatformNotSupportedException`：当前系统构建号不满足应用提供开关的规则。

<a id="api-1c6d8e37aafe2c57"></a>

### `HostOpenHclService` 类

读取和设置宿主从文件加载 OpenHCL 固件的策略。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / OpenHCL；管理层面 / 宿主 / 修改 / OpenHCL |
| 源文件 | ` Management/Host/OpenHcl/HostOpenHclService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool IsFirmwareLoadFromFileEnabled() `](#api-50b66a2c7cae66d4) | 读取主机从文件加载 OpenHCL 固件的策略。 |
| [` public static (bool Success, string Error) SetFirmwareLoadFromFileEnabled(bool enabled) `](#api-1426863ff67c6d36) | 写入或删除主机从文件加载固件的策略。 |

#### 详细说明

```csharp
public static class HostOpenHclService
```

#### 静态方法说明

<a id="api-50b66a2c7cae66d4"></a>

##### IsFirmwareLoadFromFileEnabled · 方法

```csharp
public static bool IsFirmwareLoadFromFileEnabled();
```

功能归属：管理层面 / 宿主 / 查询 / OpenHCL。

读取主机从文件加载 OpenHCL 固件的策略。

返回：64 位注册表视图中的策略 DWORD 为 1 时为 true，未配置或其他值为 false；注册表读取异常直接传播。

<a id="api-1426863ff67c6d36"></a>

##### SetFirmwareLoadFromFileEnabled · 方法

```csharp
public static (bool Success, string Error) SetFirmwareLoadFromFileEnabled(bool enabled);
```

功能归属：管理层面 / 宿主 / 修改 / OpenHCL。

写入或删除主机从文件加载固件的策略。

| 参数 | 说明 |
| --- | --- |
| `enabled` | true 启用从文件加载，false 删除启用策略。 |

返回：成功标志及失败时的错误消息。

调用约定：这是宿主策略。关闭时删除启用配置，恢复系统默认；虚拟机的固件文件路径需另行配置。

<a id="api-7c77af8af6b14c8a"></a>

### `HostPartitionReference` record 类

查询时选中的宿主分区身份，包含所属磁盘；添加或移除盘符时原样回传。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储 |
| 源文件 | ` Management/Host/Storage/HostDiskReference.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostPartitionReference(HostDiskReference Disk, int PartitionNumber, string ObjectId, Guid? PartitionGuid) `](#api-67c768acef5b7b55) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public HostDiskReference Disk { get; init; } `](#api-9ae1f8e0cf1b659e) | 所属磁盘的查询身份。 |
| [` public string ObjectId { get; init; } `](#api-0fcb9bb70c5a0ffe) | 存储系统提供的分区标识，查询后原样保留。 |
| [` public Guid? PartitionGuid { get; init; } `](#api-9014b2928a6bf591) | GPT 分区 GUID；MBR 等不提供 GUID 的分区为 null。 |
| [` public int PartitionNumber { get; init; } `](#api-c9d685e3e8b28aba) | 查询时的分区编号。 |

#### 详细说明

```csharp
public sealed record HostPartitionReference(HostDiskReference Disk, int PartitionNumber, string ObjectId, Guid? PartitionGuid)
```

| 参数 | 说明 |
| --- | --- |
| `Disk` | 所属磁盘的查询身份。 |
| `PartitionNumber` | 查询时的分区编号。 |
| `ObjectId` | 存储系统提供的分区标识，查询后原样保留。 |
| `PartitionGuid` | GPT 分区 GUID；MBR 等不提供 GUID 的分区为 null。 |

调用约定：通过 GetPartitionReferenceAsync 取得，修改时原样传回。盘符和访问路径的变化不影响分区身份核对。

#### 构造函数说明

<a id="api-67c768acef5b7b55"></a>

##### HostPartitionReference · 主构造器

```csharp
public HostPartitionReference(HostDiskReference Disk, int PartitionNumber, string ObjectId, Guid? PartitionGuid);
```

| 参数 | 说明 |
| --- | --- |
| `Disk` | 所属磁盘的查询身份。 |
| `PartitionNumber` | 查询时的分区编号。 |
| `ObjectId` | 存储系统提供的分区标识，查询后原样保留。 |
| `PartitionGuid` | GPT 分区 GUID；MBR 等不提供 GUID 的分区为 null。 |

#### 属性说明

<a id="api-9ae1f8e0cf1b659e"></a>

##### Disk · record 属性

```csharp
public HostDiskReference Disk { get; init; }
```

所属磁盘的查询身份。

<a id="api-0fcb9bb70c5a0ffe"></a>

##### ObjectId · record 属性

```csharp
public string ObjectId { get; init; }
```

存储系统提供的分区标识，查询后原样保留。

<a id="api-9014b2928a6bf591"></a>

##### PartitionGuid · record 属性

```csharp
public Guid? PartitionGuid { get; init; }
```

GPT 分区 GUID；MBR 等不提供 GUID 的分区为 null。

<a id="api-c9d685e3e8b28aba"></a>

##### PartitionNumber · record 属性

```csharp
public int PartitionNumber { get; init; }
```

查询时的分区编号。

<a id="api-35590b81f811c4b0"></a>

### `HostPcieService` 类

读取宿主 PCIe/DDA 分配状态并执行设备转移。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / PCIe；管理层面 / 宿主 / 修改 / PCIe |
| 源文件 | ` Management/Host/Pcie/HostPcieService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<ApiResponse> ExecutePCIeOperationAsync(PcieAssignmentTarget target, PcieAssignmentTarget current, string instanceId, string path, IProgress<string>? progress = null) `](#api-eb34f8cd58d15275) | 按当前归属和稳定目标执行 PCIe 设备卸除、分配或归还。 |
| [` public static async Task<ApiResponse<List<ControllerDisk>>> GetControllerDisksAsync(string controllerInstanceId) `](#api-88c7ff45292a6a7b) | 查询指定 PCIe 存储控制器关联的宿主物理磁盘。 |
| [` public static Task<ApiResponse<PcieHostInfo>> GetPCIeInfoAsync() `](#api-0a177d811cc97019) | 读取宿主 PCIe 设备、当前归属与可选虚拟机目标。 |

#### 详细说明

```csharp
public static class HostPcieService
```

调用约定：变更需要管理员、Hyper-V 和设备管理权限，可能关闭 VM、禁用宿主设备或改变设备归属。交互式操作优先使用 PcieAssignmentSession 获取确认步骤。

#### 静态方法说明

<a id="api-eb34f8cd58d15275"></a>

##### ExecutePCIeOperationAsync · 方法

```csharp
public static Task<ApiResponse> ExecutePCIeOperationAsync(PcieAssignmentTarget target, PcieAssignmentTarget current, string instanceId, string path, IProgress<string>? progress = null);
```

功能归属：管理层面 / 宿主 / 修改 / PCIe。

按当前归属和稳定目标执行 PCIe 设备卸除、分配或归还。

| 参数 | 说明 |
| --- | --- |
| `target` | 目标宿主或带非空 GUID 的虚拟机；不能使用 Unassigned。 |
| `current` | 查询得到的设备归属；执行时重新核对，已经变化则返回 Conflict。 |
| `instanceId` | 设备的完整 PnP InstanceId，应与 path 来自同一设备查询结果。 |
| `path` | 同一设备的 DDA LocationPath；不得与另一设备的 InstanceId 拼接。 |
| `progress` | 可选的人类可读进度接收器。 |

返回：步骤执行结果，失败可能发生在部分步骤已完成后；应刷新设备实际归属。

调用约定：分配给 VM 时将其宿主关机动作设为 TurnOff；尚未启用 GuestControlledCacheTypes 时先强制关闭目标 VM，再启用该设置。失败不恢复这两项 VM 配置，也不重新启动 VM。宿主设备卸除请求将 RequireAcsSupport 和 RequireDeviceMitigations 均设为 false，未要求通过相应隔离与缓解条件。



同一控制器及其已枚举关联盘跨进程排队，锁覆盖检查、转移和失败恢复；直接入口不会自动将联机数据盘脱机。失败且原归属或目标为宿主时尝试恢复到宿主，并恢复同一磁盘的原联机、只读状态，覆盖期间外部状态修改。OutcomeUnknown 时停止补救；无法恢复时报告 CleanupRequired。归还兼容路径可忽略明确的 Mount 失败，成功响应仍应通过重新查询确认最终设备归属。

<a id="api-88c7ff45292a6a7b"></a>

##### GetControllerDisksAsync · 方法

```csharp
public static async Task<ApiResponse<List<ControllerDisk>>> GetControllerDisksAsync(string controllerInstanceId);
```

功能归属：管理层面 / 宿主 / 查询 / PCIe。

查询指定 PCIe 存储控制器关联的宿主物理磁盘。

| 参数 | 说明 |
| --- | --- |
| `controllerInstanceId` | 控制器的完整 PnP InstanceId。 |

返回：直接连接到控制器的磁盘及系统、启动、脱机标志；不包含间接下属磁盘。没有匹配时列表为空，查询失败返回错误。

<a id="api-0a177d811cc97019"></a>

##### GetPCIeInfoAsync · 方法

```csharp
public static Task<ApiResponse<PcieHostInfo>> GetPCIeInfoAsync();
```

功能归属：管理层面 / 宿主 / 查询 / PCIe。

读取宿主 PCIe 设备、当前归属与可选虚拟机目标。

返回：设备与稳定 GUID 目标快照；显式 WMI 查询失败返回错误，原生 PnP 枚举的部分失败仍可能表现为设备缺项或空清单。

<a id="api-a57b694781de3497"></a>

### `HostPowerPlan` record 类

宿主电源计划的显示名称与稳定 GUID。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 电源 |
| 源文件 | ` Management/Host/Power/HostPowerPlanService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostPowerPlan(string Name, Guid Id) `](#api-49ccd94d1b1c68ff) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public Guid Id { get; init; } `](#api-02183b7b9e62b945) | 电源计划 GUID。 |
| [` public string Name { get; init; } `](#api-f804418859e9c669) | 计划显示名称；读取名称失败时可为 GUID 文本。 |

#### 详细说明

```csharp
public sealed record HostPowerPlan(string Name, Guid Id)
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 计划显示名称；读取名称失败时可为 GUID 文本。 |
| `Id` | 电源计划 GUID。 |

#### 构造函数说明

<a id="api-49ccd94d1b1c68ff"></a>

##### HostPowerPlan · 主构造器

```csharp
public HostPowerPlan(string Name, Guid Id);
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 计划显示名称；读取名称失败时可为 GUID 文本。 |
| `Id` | 电源计划 GUID。 |

#### 属性说明

<a id="api-02183b7b9e62b945"></a>

##### Id · record 属性

```csharp
public Guid Id { get; init; }
```

电源计划 GUID。

<a id="api-f804418859e9c669"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

计划显示名称；读取名称失败时可为 GUID 文本。

<a id="api-79f9ca4dbc8029c4"></a>

### `HostPowerPlanService` 类

枚举宿主电源计划并设置当前活动计划；设置影响整个宿主。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 电源；管理层面 / 宿主 / 修改 / 电源 |
| 源文件 | ` Management/Host/Power/HostPowerPlanService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Guid? GetActivePowerPlanId() `](#api-0d0751d7762c8633) | 读取宿主当前活动电源计划的 GUID。 |
| [` public static IReadOnlyList<HostPowerPlan> GetPowerPlans() `](#api-acc29645411c8b3d) | 枚举系统电源计划及可读取的名称。 |
| [` public static void SetActivePowerPlan(Guid id) `](#api-8a6806d53be998d9) | 激活指定宿主电源计划。 |

#### 详细说明

```csharp
public static class HostPowerPlanService
```

#### 静态方法说明

<a id="api-0d0751d7762c8633"></a>

##### GetActivePowerPlanId · 方法

```csharp
public static Guid? GetActivePowerPlanId();
```

功能归属：管理层面 / 宿主 / 查询 / 电源。

读取宿主当前活动电源计划的 GUID。

返回：活动计划的 GUID；未能取得时为 null。

<a id="api-acc29645411c8b3d"></a>

##### GetPowerPlans · 方法

```csharp
public static IReadOnlyList<HostPowerPlan> GetPowerPlans();
```

功能归属：管理层面 / 宿主 / 查询 / 电源。

枚举系统电源计划及可读取的名称。

返回：已枚举到的计划列表；枚举结束或遇到接口错误时停止，可能返回部分列表。

<a id="api-8a6806d53be998d9"></a>

##### SetActivePowerPlan · 方法

```csharp
public static void SetActivePowerPlan(Guid id);
```

功能归属：管理层面 / 宿主 / 修改 / 电源。

激活指定宿主电源计划。

| 参数 | 说明 |
| --- | --- |
| `id` | 已安装电源计划的 GUID。 |

调用约定：作用于宿主全局；原生接口失败时抛出 Win32Exception。

- 异常 `Win32Exception`：系统拒绝激活指定计划，错误码保留在 NativeErrorCode 中。

<a id="api-be843791603a448d"></a>

### `HostProcessorPlatform` 枚举

虚拟处理器设置规则使用的宿主平台分类；由架构和厂商信息组合得到，不是独立的宿主查询字段。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/HostProcessorPlatform.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Amd = 1 `](#api-e28fb95483e5de32) | AMD 处理器平台。 |
| [` Arm64 = 3 `](#api-a6ff4649ec45a2b0) | 宿主原生 ARM64 平台。 |
| [` Intel = 2 `](#api-52d01cba459edc04) | Intel 处理器平台。 |
| [` Unknown = 0 `](#api-53c22e6ebadf9934) | 未能归入已知平台，也可能是厂商查询失败。 |

#### 详细说明

```csharp
public enum HostProcessorPlatform
```

#### 枚举值说明

<a id="api-e28fb95483e5de32"></a>

##### Amd · 枚举值

```csharp
Amd = 1
```

AMD 处理器平台。

<a id="api-a6ff4649ec45a2b0"></a>

##### Arm64 · 枚举值

```csharp
Arm64 = 3
```

宿主原生 ARM64 平台。

<a id="api-52d01cba459edc04"></a>

##### Intel · 枚举值

```csharp
Intel = 2
```

Intel 处理器平台。

<a id="api-53c22e6ebadf9934"></a>

##### Unknown · 枚举值

```csharp
Unknown = 0
```

未能归入已知平台，也可能是厂商查询失败。

<a id="api-1d2842d5ce9c4c63"></a>

### `HostProcessorPlatformService` 类

分别查询宿主架构和处理器厂商；两项可以独立读取。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器 |
| 源文件 | ` Management/Host/Processor/HostProcessorPlatformService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Architecture GetArchitecture() `](#api-90dabe8c67dc82a3) | 读取宿主操作系统的原生架构。 |
| [` public static async Task<ApiResponse<IReadOnlyList<string>>> GetManufacturersAsync(CancellationToken cancellationToken = default) `](#api-d217e6dd6337030b) | 读取宿主处理器的厂商标识，例如 GenuineIntel、AuthenticAMD 或系统报告的其他厂商。 |

#### 详细说明

```csharp
public static class HostProcessorPlatformService
```

#### 静态方法说明

<a id="api-90dabe8c67dc82a3"></a>

##### GetArchitecture · 方法

```csharp
public static Architecture GetArchitecture();
```

读取宿主操作系统的原生架构。

返回：Architecture 枚举值，例如 X64、Arm64。ARM64 Windows 中的 x64 模拟进程仍返回 Arm64。

<a id="api-d217e6dd6337030b"></a>

##### GetManufacturersAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyList<string>>> GetManufacturersAsync(CancellationToken cancellationToken = default);
```

读取宿主处理器的厂商标识，例如 GenuineIntel、AuthenticAMD 或系统报告的其他厂商。

| 参数 | 说明 |
| --- | --- |
| `cancellationToken` | 在等待查询锁、开始查询前及查询返回后检查取消；不强行中断已开始的 WMI 调用。 |

返回：成功时返回只读厂商清单；多个处理器的相同标识只保留一项。没有处理器记录时返回成功空结果，读取失败或记录缺少厂商时返回失败。

调用约定：厂商标识去除首尾空白，忽略大小写去重并排序。完整的非空结果在进程内缓存；失败和空结果在下次调用时重新查询。

- 异常 `OperationCanceledException`：调用方已请求取消。

<a id="api-abc371c87161ae39"></a>

### `HostProcessorService` 类

读取宿主逻辑处理器及可用于虚拟机亲和性的处理器集合。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器 |
| 源文件 | ` Management/Host/Processor/HostProcessorService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse<IReadOnlyList<HostAffinityProcessor>>> GetAffinityProcessorsAsync() `](#api-9698a6201d8a37b8) | 读取当前亲和性后端允许选择的宿主逻辑处理器。 |

#### 详细说明

```csharp
public static class HostProcessorService
```

#### 静态方法说明

<a id="api-9698a6201d8a37b8"></a>

##### GetAffinityProcessorsAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyList<HostAffinityProcessor>>> GetAffinityProcessorsAsync();
```

读取当前亲和性后端允许选择的宿主逻辑处理器。

返回：Classic/Core 返回 HCS 真实编号与拓扑；Root 返回唯一 Windows 处理器组的活动编号，多组宿主返回 Unsupported。查询失败不生成替代编号。

调用约定：保存亲和性时使用返回项的 Index：Classic/Core 为 HCS 逻辑处理器编号，Root 为 Windows 处理器组内编号。

<a id="api-5e4d7f06de4c8c7b"></a>

### `HostProductTypePendingSwitch` record 类

此前产品类型切换的挂起记录。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 系统产品类型 |
| 源文件 | ` Management/Host/ProductType/HostProductTypeService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostProductTypePendingSwitch(string TargetType, bool IsConfirmed, IReadOnlyList<string> RecoveryPaths) `](#api-84efe4d4e8e2943f) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool IsConfirmed { get; init; } `](#api-d48347487fb9526b) | 是否有成功替换后写入的挂起标记；仅文件占用不能确认。 |
| [` public IReadOnlyList<string> RecoveryPaths { get; init; } `](#api-2dd228b86f34d6fd) | 已记录或检测到的恢复材料路径。 |
| [` public string TargetType { get; init; } `](#api-acd0fe85855afae5) | 已知目标 WinNT/ServerNT；未知时为空字符串。 |

#### 详细说明

```csharp
public sealed record HostProductTypePendingSwitch(string TargetType, bool IsConfirmed, IReadOnlyList<string> RecoveryPaths)
```

| 参数 | 说明 |
| --- | --- |
| `TargetType` | 已知目标 WinNT/ServerNT；未知时为空字符串。 |
| `IsConfirmed` | 是否有成功替换后写入的挂起标记；仅文件占用不能确认。 |
| `RecoveryPaths` | 已记录或检测到的恢复材料路径。 |

#### 构造函数说明

<a id="api-84efe4d4e8e2943f"></a>

##### HostProductTypePendingSwitch · 主构造器

```csharp
public HostProductTypePendingSwitch(string TargetType, bool IsConfirmed, IReadOnlyList<string> RecoveryPaths);
```

| 参数 | 说明 |
| --- | --- |
| `TargetType` | 已知目标 WinNT/ServerNT；未知时为空字符串。 |
| `IsConfirmed` | 是否有成功替换后写入的挂起标记；仅文件占用不能确认。 |
| `RecoveryPaths` | 已记录或检测到的恢复材料路径。 |

#### 属性说明

<a id="api-d48347487fb9526b"></a>

##### IsConfirmed · record 属性

```csharp
public bool IsConfirmed { get; init; }
```

是否有成功替换后写入的挂起标记；仅文件占用不能确认。

<a id="api-2dd228b86f34d6fd"></a>

##### RecoveryPaths · record 属性

```csharp
public IReadOnlyList<string> RecoveryPaths { get; init; }
```

已记录或检测到的恢复材料路径。

<a id="api-acd0fe85855afae5"></a>

##### TargetType · record 属性

```csharp
public string TargetType { get; init; }
```

已知目标 WinNT/ServerNT；未知时为空字符串。

<a id="api-b3bf26b1b466b9c0"></a>

### `HostProductTypeService` 类

读取和实验性修改宿主 Windows 产品类型，通过 SYSTEM hive 替换安排重启后生效。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 系统产品类型；管理层面 / 宿主 / 修改 / 系统产品类型 |
| 源文件 | ` Management/Host/ProductType/HostProductTypeService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static HostProductTypeSwitchResult ApplySwitch(bool toServer) `](#api-b6b86ecb2adae1f9) | 准备并提交实验性 SYSTEM hive 产品类型替换，不重启宿主。 |
| [` public static bool CanApplySwitch(HostProductTypePendingSwitch? pending) `](#api-6a0f2cd3f00229e6) | 仅根据已经成功读取的挂起信息判断是否允许尝试提交；执行仍会重新检查并获取跨进程锁。 |
| [` public static ApiResponse<HostProductTypePendingSwitch> GetPendingSwitch() `](#api-46651c2ef821b281) | 读取此前提交的挂起标记，并检查遗留恢复文件是否仍被占用。 |
| [` public static bool IsServerSystem() `](#api-dc41fee6c57a4513) | 读取宿主是否属于服务器产品类型。 |

#### 详细说明

```csharp
public static class HostProductTypeService
```

调用约定：此功能为实验性 Windows 产品类型修改，需要管理员及备份、还原特权。每次使用独立工作目录，恢复材料保存在当前用户的 LocalApplicationData/ExHyperV/Recovery 下。挂起替换所用文件需保留到重启完成并核对结果；同一宿主禁止并行提交。

#### 静态方法说明

<a id="api-b6b86ecb2adae1f9"></a>

##### ApplySwitch · 方法

```csharp
public static HostProductTypeSwitchResult ApplySwitch(bool toServer);
```

功能归属：管理层面 / 宿主 / 修改 / 系统产品类型。

准备并提交实验性 SYSTEM hive 产品类型替换，不重启宿主。

| 参数 | 说明 |
| --- | --- |
| `toServer` | true 请求 ServerNT，false 请求 WinNT。 |

返回：结构化提交状态；失败仍携带恢复路径和已知挂载残留，不删除旧备份。

调用约定：其他进程正在提交时立即失败。已有确认的挂起替换时返回其目标，保留原请求。切换为 ServerNT 时同时禁用关机事件跟踪，切换为 WinNT 时删除该策略覆盖。替换提交、策略写入和挂起标记全部完成才报告成功；部分完成或清理失败时，结果保留已提交状态和恢复材料。

<a id="api-6a0f2cd3f00229e6"></a>

##### CanApplySwitch · 方法

```csharp
public static bool CanApplySwitch(HostProductTypePendingSwitch? pending);
```

功能归属：管理层面 / 宿主 / 修改 / 系统产品类型。

仅根据已经成功读取的挂起信息判断是否允许尝试提交；执行仍会重新检查并获取跨进程锁。

| 参数 | 说明 |
| --- | --- |
| `pending` | 成功查询得到的挂起信息；null 表示没有发现挂起线索。 |

返回：不存在挂起线索时为 true；查询失败时不应调用此判断。

<a id="api-46651c2ef821b281"></a>

##### GetPendingSwitch · 方法

```csharp
public static ApiResponse<HostProductTypePendingSwitch> GetPendingSwitch();
```

功能归属：管理层面 / 宿主 / 查询 / 系统产品类型。

读取此前提交的挂起标记，并检查遗留恢复文件是否仍被占用。

返回：成功空结果表示未发现挂起线索；有数据时需检查 IsConfirmed。读取错误单独返回失败。

<a id="api-dc41fee6c57a4513"></a>

##### IsServerSystem · 方法

```csharp
public static bool IsServerSystem();
```

功能归属：管理层面 / 宿主 / 查询 / 系统产品类型。

读取宿主是否属于服务器产品类型。

返回：ProductType 为非 WinNT 值时返回 true；工作站、未取得或读取失败时返回 false。

<a id="api-9f55bd406a2d2ac3"></a>

### `HostProductTypeSwitchResult` 类

产品类型切换结果；即使失败也保留恢复材料和未卸载注册表键。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 系统产品类型 |
| 源文件 | ` Management/Host/ProductType/HostProductTypeService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? MountedHiveKey { get; } `](#api-fcc1530392390b11) | 清理时未能卸载的临时注册表键。 |
| [` public IReadOnlyList<string> RecoveryPaths { get; } `](#api-2ecd2417589a3f45) | 切换使用的恢复目录、工作注册表文件和备份路径。 |
| [` public bool ReplacementSubmitted { get; } `](#api-2f3613c080792b7a) | 本次调用是否已成功提交系统注册表替换。 |
| [` public bool RequiresRestart { get; } `](#api-e52876918ffe7147) | 是否已有等待宿主重启的产品类型替换。 |
| [` public ApiResponse Response { get; } `](#api-c940dca16f656ccc) | 切换操作的结果及错误信息。 |
| [` public HostProductTypeSwitchState State { get; } `](#api-5d45638fa89b519d) | 产品类型切换的提交与清理状态。 |
| [` public bool Success { get; } `](#api-a3c0dadf47882c7e) | 产品类型替换是否完整提交，或已确认此前有待重启的替换。 |
| [` public string TargetType { get; } `](#api-747a596aac716aa2) | 本次切换的目标产品类型；已有挂起替换时为其目标类型。 |

#### 详细说明

```csharp
public sealed class HostProductTypeSwitchResult
```

#### 属性说明

<a id="api-fcc1530392390b11"></a>

##### MountedHiveKey · 属性

```csharp
public string? MountedHiveKey { get; }
```

清理时未能卸载的临时注册表键。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 清理时未能卸载的临时注册表键。 |
| 取值与默认 | HKLM 下的键名；无已知残留时为 null。 |

<a id="api-2ecd2417589a3f45"></a>

##### RecoveryPaths · 属性

```csharp
public IReadOnlyList<string> RecoveryPaths { get; }
```

切换使用的恢复目录、工作注册表文件和备份路径。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 切换使用的恢复目录、工作注册表文件和备份路径。 |
| 取值与默认 | 宿主路径列表；在早期失败时，部分路径对应的文件可能尚未创建。 |

<a id="api-2f3613c080792b7a"></a>

##### ReplacementSubmitted · 属性

```csharp
public bool ReplacementSubmitted { get; }
```

本次调用是否已成功提交系统注册表替换。后续步骤失败时仍保留提交结果。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 本次调用是否已成功提交系统注册表替换。后续步骤失败时仍保留提交结果。 |
| 取值与默认 | true：已提交；false：未确认提交成功。 |

<a id="api-e52876918ffe7147"></a>

##### RequiresRestart · 属性

```csharp
public bool RequiresRestart { get; }
```

是否已有等待宿主重启的产品类型替换。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 是否已有等待宿主重启的产品类型替换。 |
| 取值与默认 | true：本次或此前已提交替换；false：尚未确认提交。 |

<a id="api-c940dca16f656ccc"></a>

##### Response · 属性

```csharp
public ApiResponse Response { get; }
```

切换操作的结果及错误信息。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 切换操作的结果及错误信息。 |

<a id="api-5d45638fa89b519d"></a>

##### State · 属性

```csharp
public HostProductTypeSwitchState State { get; }
```

产品类型切换的提交与清理状态。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 产品类型切换的提交与清理状态。 |

<a id="api-a3c0dadf47882c7e"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

产品类型替换是否完整提交，或已确认此前有待重启的替换。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 产品类型替换是否完整提交，或已确认此前有待重启的替换。 |
| 取值与默认 | true：完整提交或此前已挂起；false：提交失败或仍有清理事项。 |

<a id="api-747a596aac716aa2"></a>

##### TargetType · 属性

```csharp
public string TargetType { get; }
```

本次切换的目标产品类型；已有挂起替换时为其目标类型。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 本次切换的目标产品类型；已有挂起替换时为其目标类型。 |
| 取值与默认 | WinNT：工作站；ServerNT：服务器。 |

<a id="api-2ced745c4ac2dd94"></a>

### `HostProductTypeSwitchState` 枚举

实验性系统产品类型切换的提交状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 系统产品类型 |
| 源文件 | ` Management/Host/ProductType/HostProductTypeService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` AlreadyPending = 1 `](#api-f62b7c02ac060179) | 已发现此前成功提交的待重启替换，本次未再次提交。 |
| [` CleanupRequired = 3 `](#api-fc4079af747ddf03) | 挂载清理或提交后的步骤未完全完成，须保留并核对恢复材料。 |
| [` Failed = 2 `](#api-410b9ee6d012fc71) | 本次未确认替换提交成功。 |
| [` SubmittedAwaitingRestart = 0 `](#api-d7ad74074e53f37f) | 本次替换已提交，关联步骤也完成，等待宿主重启。 |

#### 详细说明

```csharp
public enum HostProductTypeSwitchState
```

#### 枚举值说明

<a id="api-f62b7c02ac060179"></a>

##### AlreadyPending · 枚举值

```csharp
AlreadyPending = 1
```

已发现此前成功提交的待重启替换，本次未再次提交。

<a id="api-fc4079af747ddf03"></a>

##### CleanupRequired · 枚举值

```csharp
CleanupRequired = 3
```

挂载清理或提交后的步骤未完全完成，须保留并核对恢复材料。

<a id="api-410b9ee6d012fc71"></a>

##### Failed · 枚举值

```csharp
Failed = 2
```

本次未确认替换提交成功。

<a id="api-d7ad74074e53f37f"></a>

##### SubmittedAwaitingRestart · 枚举值

```csharp
SubmittedAwaitingRestart = 0
```

本次替换已提交，关联步骤也完成，等待宿主重启。

<a id="api-c97822da1d5f0c88"></a>

### `HostSystemInfoService` 类

读取用于显示的宿主系统概况，并在进程内缓存首次查询任务。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 基本信息 |
| 源文件 | ` Management/Host/System/HostSystemInfoService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<SystemInfo> GetSystemInfoAsync() `](#api-c93daf231c3d1b8f) | 一次读取系统名称与构建号、系统位数、处理器型号说明和已安装物理内存容量。 |

#### 详细说明

```csharp
public static class HostSystemInfoService
```

#### 静态方法说明

<a id="api-c93daf231c3d1b8f"></a>

##### GetSystemInfoAsync · 方法

```csharp
public static Task<SystemInfo> GetSystemInfoAsync();
```

一次读取系统名称与构建号、系统位数、处理器型号说明和已安装物理内存容量。

返回：包含四项显示文本的基本信息汇总，对应 Caption、OSArchitecture、CpuModel 和 MemCap。

调用约定：首次调用执行实际查询，后续调用复用进程内缓存。各项不可用时返回 N/A 等回退文本，首次回退结果也会被缓存。

<a id="api-0f0119c09ea17f4b"></a>

### `HostSystemInfoService.SystemInfo` record 类

宿主基本信息汇总，包含系统名称与构建号、系统位数、处理器型号说明和内存容量文本。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 基本信息；管理层面 / 宿主 / 查询 / 处理器；管理层面 / 宿主 / 查询 / 内存 |
| 源文件 | ` Management/Host/System/HostSystemInfoService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public SystemInfo(string Caption, string OSArchitecture, string CpuModel, string MemCap) `](#api-808754dfb1d7e952) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Caption { get; init; } `](#api-73e472fbb2e84308) | 操作系统名称及构建号，例如 Windows 11 Pro (Build 26100)。 |
| [` public string CpuModel { get; init; } `](#api-a092b8e0d2442c5f) | 处理器型号的显示文本；可能附带频率，多处理器时还附带数量。 |
| [` public string MemCap { get; init; } `](#api-119f8498a4982334) | 已安装物理内存的容量文本，按 1,073,741,824 字节换算，标签为 GB。 |
| [` public string OSArchitecture { get; init; } `](#api-56eebd10b2654851) | 操作系统的位数文本，例如 64 位。 |

#### 详细说明

```csharp
public record SystemInfo(
            string Caption,
            string OSArchitecture,
            string CpuModel,
            string MemCap)
```

| 参数 | 说明 |
| --- | --- |
| `Caption` | 操作系统名称及构建号，例如 Windows 11 Pro (Build 26100)。 |
| `OSArchitecture` | 操作系统的位数文本，例如 64 位。处理器指令集见 HostProcessorPlatformService.GetArchitecture。 |
| `CpuModel` | 处理器型号的显示文本；可能附带频率，多处理器时还附带数量。 |
| `MemCap` | 已安装物理内存的容量文本，按 1,073,741,824 字节换算，标签为 GB。 |

#### 构造函数说明

<a id="api-808754dfb1d7e952"></a>

##### SystemInfo · 主构造器

```csharp
public SystemInfo(string Caption, string OSArchitecture, string CpuModel, string MemCap);
```

功能归属：管理层面 / 宿主 / 查询 / 基本信息；管理层面 / 宿主 / 查询 / 处理器；管理层面 / 宿主 / 查询 / 内存。

| 参数 | 说明 |
| --- | --- |
| `Caption` | 操作系统名称及构建号，例如 Windows 11 Pro (Build 26100)。 |
| `OSArchitecture` | 操作系统的位数文本，例如 64 位。处理器指令集见 HostProcessorPlatformService.GetArchitecture。 |
| `CpuModel` | 处理器型号的显示文本；可能附带频率，多处理器时还附带数量。 |
| `MemCap` | 已安装物理内存的容量文本，按 1,073,741,824 字节换算，标签为 GB。 |

#### 属性说明

<a id="api-73e472fbb2e84308"></a>

##### Caption · record 属性

```csharp
public string Caption { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 基本信息。

操作系统名称及构建号，例如 Windows 11 Pro (Build 26100)。

<a id="api-a092b8e0d2442c5f"></a>

##### CpuModel · record 属性

```csharp
public string CpuModel { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 处理器。

处理器型号的显示文本；可能附带频率，多处理器时还附带数量。

<a id="api-119f8498a4982334"></a>

##### MemCap · record 属性

```csharp
public string MemCap { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 内存。

已安装物理内存的容量文本，按 1,073,741,824 字节换算，标签为 GB。

<a id="api-56eebd10b2654851"></a>

##### OSArchitecture · record 属性

```csharp
public string OSArchitecture { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 基本信息。

操作系统的位数文本，例如 64 位。处理器指令集见 HostProcessorPlatformService.GetArchitecture。

<a id="api-4d32cc978f36178b"></a>

### `HostVolumeAutoMountService` 类

管理 Windows Mount Manager 的宿主全局卷自动挂载策略。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储；管理层面 / 宿主 / 修改 / 存储 |
| 源文件 | ` Management/Host/Storage/HostVolumeAutoMountService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static ApiResponse<bool> GetState() `](#api-6e13cb9b828103f1) | 读取宿主是否自动挂载新卷。 |
| [` public static async Task<ApiResponse> SetEnabledAsync(bool enabled) `](#api-f2bf468124100c28) | 直接设置 Mount Manager 的宿主全局自动挂载策略，并重新查询确认。 |

#### 详细说明

```csharp
public static class HostVolumeAutoMountService
```

#### 静态方法说明

<a id="api-6e13cb9b828103f1"></a>

##### GetState · 方法

```csharp
public static ApiResponse<bool> GetState();
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

读取宿主是否自动挂载新卷。

返回：true 已启用，false 已禁用；读取失败返回 Win32 错误。

<a id="api-f2bf468124100c28"></a>

##### SetEnabledAsync · 方法

```csharp
public static async Task<ApiResponse> SetEnabledAsync(bool enabled);
```

功能归属：管理层面 / 宿主 / 修改 / 存储。

直接设置 Mount Manager 的宿主全局自动挂载策略，并重新查询确认。

| 参数 | 说明 |
| --- | --- |
| `enabled` | true 启用新卷自动挂载，false 禁用。 |

返回：成功表示设置后查询值与请求一致；原生失败保留 Win32 错误码。已写入但复核失败时为 OutcomeUnknown，复核值不一致时为 Conflict；失败不保证设置未改变。

调用约定：这是宿主全局的持久设置，退出程序后仍生效。Core 的写入和复核按宿主排队。

<a id="api-3df2cf54f9233c21"></a>

### `HyperVCheckResult` record 类

一个检查阶段的状态及失败信息，不包含修改系统的动作。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HyperVCheckResult(HyperVCheckStage Stage, HyperVCheckState State, HyperVDiagnosticKind DiagnosticKind, string Message, ApiFailure? Failure = null) `](#api-936388a5aefbffb0) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public HyperVDiagnosticKind DiagnosticKind { get; init; } `](#api-4a7361d12f813ad0) | 可供程序分支判断的诊断类别。 |
| [` public ApiFailure? Failure { get; init; } `](#api-1334b348163eb5da) | 原始接口错误的类别、来源和数值；没有原始错误时为 null。 |
| [` public string Message { get; init; } `](#api-2f585e2547fc334c) | 本阶段检查结论的文字说明。 |
| [` public HyperVCheckStage Stage { get; init; } `](#api-7dfe815332359fc5) | 检查阶段。 |
| [` public HyperVCheckState State { get; init; } `](#api-fcd4f1cc9cf30149) | 通过、未就绪、未知或未检查。 |

#### 详细说明

```csharp
public sealed record HyperVCheckResult(HyperVCheckStage Stage, HyperVCheckState State,
    HyperVDiagnosticKind DiagnosticKind, string Message, ApiFailure? Failure = null)
```

| 参数 | 说明 |
| --- | --- |
| `Stage` | 检查阶段。 |
| `State` | 通过、未就绪、未知或未检查。 |
| `DiagnosticKind` | 可供程序分支判断的诊断类别。 |
| `Message` | 本阶段检查结论的文字说明。 |
| `Failure` | 原始接口错误的类别、来源和数值；没有原始错误时为 null。 |

#### 构造函数说明

<a id="api-936388a5aefbffb0"></a>

##### HyperVCheckResult · 主构造器

```csharp
public HyperVCheckResult(HyperVCheckStage Stage, HyperVCheckState State, HyperVDiagnosticKind DiagnosticKind, string Message, ApiFailure? Failure = null);
```

| 参数 | 说明 |
| --- | --- |
| `Stage` | 检查阶段。 |
| `State` | 通过、未就绪、未知或未检查。 |
| `DiagnosticKind` | 可供程序分支判断的诊断类别。 |
| `Message` | 本阶段检查结论的文字说明。 |
| `Failure` | 原始接口错误的类别、来源和数值；没有原始错误时为 null。 |

#### 属性说明

<a id="api-4a7361d12f813ad0"></a>

##### DiagnosticKind · record 属性

```csharp
public HyperVDiagnosticKind DiagnosticKind { get; init; }
```

可供程序分支判断的诊断类别。

<a id="api-1334b348163eb5da"></a>

##### Failure · record 属性

```csharp
public ApiFailure? Failure { get; init; }
```

原始接口错误的类别、来源和数值；没有原始错误时为 null。

<a id="api-2f585e2547fc334c"></a>

##### Message · record 属性

```csharp
public string Message { get; init; }
```

本阶段检查结论的文字说明。

<a id="api-7dfe815332359fc5"></a>

##### Stage · record 属性

```csharp
public HyperVCheckStage Stage { get; init; }
```

检查阶段。

<a id="api-fcd4f1cc9cf30149"></a>

##### State · record 属性

```csharp
public HyperVCheckState State { get; init; }
```

通过、未就绪、未知或未检查。

<a id="api-5a39edf418e8defa"></a>

### `HyperVCheckStage` 枚举

Hyper-V 运行检查的阶段，顺序也是首个问题的判定优先级。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Hypervisor = 0 `](#api-3a892d81a1e40cde) | 当前系统的监控程序运行情况。 |
| [` Vmms = 1 `](#api-f876d09dcb00981e) | VMMS Windows 服务及其诊断信息。 |
| [` Wmi = 2 `](#api-0a94b10797388892) | Hyper-V WMI 管理服务实例是否可查询。 |

#### 详细说明

```csharp
public enum HyperVCheckStage
```

#### 枚举值说明

<a id="api-3a892d81a1e40cde"></a>

##### Hypervisor · 枚举值

```csharp
Hypervisor = 0
```

当前系统的监控程序运行情况。

<a id="api-f876d09dcb00981e"></a>

##### Vmms · 枚举值

```csharp
Vmms = 1
```

VMMS Windows 服务及其诊断信息。

<a id="api-0a94b10797388892"></a>

##### Wmi · 枚举值

```csharp
Wmi = 2
```

Hyper-V WMI 管理服务实例是否可查询。

<a id="api-15466f3ac21ed151"></a>

### `HyperVCheckState` 枚举

一个运行检查的结论；无法确认不等于明确不可用。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` NotChecked = 0 `](#api-4b73e342a0a6f400) | 此前阶段已遇到问题，本次未执行此检查。 |
| [` NotReady = 2 `](#api-2c1c9889bca71a16) | 查询已完成，明确不满足本阶段要求。 |
| [` Passed = 1 `](#api-87a59a7058014cf3) | 已确认满足本阶段要求。 |
| [` Unknown = 3 `](#api-b6e188cbcccf5771) | 查询失败、超时或信息不足，无法确定。 |

#### 详细说明

```csharp
public enum HyperVCheckState
```

#### 枚举值说明

<a id="api-4b73e342a0a6f400"></a>

##### NotChecked · 枚举值

```csharp
NotChecked = 0
```

此前阶段已遇到问题，本次未执行此检查。

<a id="api-2c1c9889bca71a16"></a>

##### NotReady · 枚举值

```csharp
NotReady = 2
```

查询已完成，明确不满足本阶段要求。

<a id="api-87a59a7058014cf3"></a>

##### Passed · 枚举值

```csharp
Passed = 1
```

已确认满足本阶段要求。

<a id="api-b6e188cbcccf5771"></a>

##### Unknown · 枚举值

```csharp
Unknown = 3
```

查询失败、超时或信息不足，无法确定。

<a id="api-3d00ec7a4b276e23"></a>

### `HyperVCpuMonitorService` 类

维护宿主及虚拟机 CPU 性能计数器；每个实例拥有自己的后台维护任务和释放生命周期。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 处理器 |
| 源文件 | ` Management/Host/Processor/HyperVCpuMonitorService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HyperVCpuMonitorService() `](#api-c6db9b28d9775b80) | 创建 CPU 采样器并立即启动后台计数器维护；使用结束后应释放实例。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public PerformanceSampleInfo VirtualMachineIdentitySample { get; } `](#api-dc2b76bf911dccac) | 最近一次虚拟机身份与处理器配置查询的采样状态。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static CpuCoreType GetCoreType(int coreId) `](#api-6a5b872c7488e8f8) | 读取首次探测宿主拓扑时缓存的核心性能分类。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public void Dispose() `](#api-f3baefcdcb63dc1c) | 永久停止本实例并同步等待计数器及后台任务释放。 |
| [` public ValueTask DisposeAsync() `](#api-f3a4e6eb19995624) | 永久停止本实例并异步等待计数器及后台任务释放；重复调用等待同一次清理。 |
| [` public List<VmCpuCoreMetric> GetCpuUsage() `](#api-1281772452a54367) | 读取每个宿主逻辑处理器及虚拟 CPU 的当前性能计数器样本。 |

#### 详细说明

```csharp
public class HyperVCpuMonitorService : IDisposable, IAsyncDisposable
```

调用约定：创建 HyperVCpuMonitorService 实例即启动后台计数器维护。按需要调用 `HyperVCpuMonitorService.GetCpuUsage` 采样；结果用 IsHost 区分宿主，用 VmId 区分虚拟机。宿主行对应逻辑处理器，虚拟机行对应虚拟处理器。



首次样本可能处于预热阶段；检查每项 Sample.IsValid 后再更新显示或参与计算。虚拟机身份读取情况可通过 `HyperVCpuMonitorService.VirtualMachineIdentitySample` 查看。



用完调用 `HyperVCpuMonitorService.DisposeAsync`，或用 await using 管理实例。释放会停止后台维护并释放计数器，之后不能继续采样。

#### 构造函数说明

<a id="api-c6db9b28d9775b80"></a>

##### HyperVCpuMonitorService · 构造器

```csharp
public HyperVCpuMonitorService();
```

创建 CPU 采样器并立即启动后台计数器维护；使用结束后应释放实例。

#### 属性说明

<a id="api-dc2b76bf911dccac"></a>

##### VirtualMachineIdentitySample · 属性

```csharp
public PerformanceSampleInfo VirtualMachineIdentitySample { get; }
```

最近一次虚拟机身份与处理器配置查询的采样状态。

调用约定：首次查询前为 NotSampled。未能取得身份的虚拟机不会出现在 GetCpuUsage 中；本状态用于说明身份查询是否完成或失败。

#### 静态方法说明

<a id="api-6a5b872c7488e8f8"></a>

##### GetCoreType · 方法

```csharp
public static CpuCoreType GetCoreType(int coreId);
```

读取首次探测宿主拓扑时缓存的核心性能分类。

| 参数 | 说明 |
| --- | --- |
| `coreId` | Windows 处理器组编号乘以 64 后加组内位号；不是 HCS LpIndex 或虚拟 CPU 编号。 |

返回：存在不同 EfficiencyClass 时，最高类别为 Performance，其余为 Efficient；同类核心、编号未找到或探测失败时为 Unknown。

调用约定：根据 Windows EfficiencyClass 分为性能和能效两类；首次探测结果在进程内缓存。

#### 方法说明

<a id="api-f3baefcdcb63dc1c"></a>

##### Dispose · 方法

```csharp
public void Dispose();
```

永久停止本实例并同步等待计数器及后台任务释放。

<a id="api-f3a4e6eb19995624"></a>

##### DisposeAsync · 方法

```csharp
public ValueTask DisposeAsync();
```

永久停止本实例并异步等待计数器及后台任务释放；重复调用等待同一次清理。

<a id="api-1281772452a54367"></a>

##### GetCpuUsage · 方法

```csharp
public List<VmCpuCoreMetric> GetCpuUsage();
```

读取每个宿主逻辑处理器及虚拟 CPU 的当前性能计数器样本。

返回：按核心列出的数值及 Sample 状态；仅 IsValid 样本可用于更新显示或计算平均值。宿主由 IsHost 区分，虚拟机由 VmId 区分。尚未取得任何虚拟机身份时，不返回无法归属的虚拟机样本。

调用约定：使用率按相邻两次调用之间的处理器运行时间计算，同一实例的调用者共享采样间隔。后台每两秒维护计数器清单。预热、身份查询失败或采样失败时 Sample.IsValid 为 false，此次数值不参与显示更新或计算。

- 异常 `ObjectDisposedException`：本实例已经开始最终释放。

<a id="api-2f2bab5dd6082806"></a>

### `HyperVDiagnosticKind` 枚举

运行诊断的机器可读分类；原始错误另见检查结果的 Failure。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` AccessDenied = 4 `](#api-e88cd3faae5062e6) | 读取被拒绝或缺少所需特权。 |
| [` ConnectionFailure = 8 `](#api-9767b5d8b9afbc24) | RPC 或 COM 连接失败。 |
| [` ExecutableMissing = 12 `](#api-d800526aa6b30c96) | VMMS 没有运行，且已确认其程序文件路径不存在。 |
| [` InvalidClass = 6 `](#api-b4c30e636f2a7047) | WMI 类不存在或不可用。 |
| [` InvalidData = 9 `](#api-de60b95e5e63f90a) | 查询数据缺失、格式异常或无法可靠解释。 |
| [` InvalidNamespace = 5 `](#api-865181b0e24295fc) | WMI 命名空间不存在或不可用。 |
| [` None = 0 `](#api-6121405e4747dfc4) | 没有诊断问题，或此阶段尚未检查。 |
| [` NotFound = 2 `](#api-bdb0767903f87137) | 成功查询后没有找到目标服务或实例。 |
| [` NotRunning = 1 `](#api-b44008ba01d3e33b) | 监控程序或服务没有运行。 |
| [` ProviderFailure = 7 `](#api-1b7f3ef75dba686f) | WMI Provider 报告执行失败。 |
| [` QueryFailed = 10 `](#api-7848960083879fed) | 没有归入其他类别的读取失败，需查看原始错误。 |
| [` ServiceDisabled = 11 `](#api-ddc456a603ad3627) | VMMS 没有运行，且服务启动类型为 Disabled。 |
| [` Timeout = 3 `](#api-9058510f283dabc9) | 本次等待读取结果已超时，不表示底层调用已取消。 |

#### 详细说明

```csharp
public enum HyperVDiagnosticKind
```

#### 枚举值说明

<a id="api-e88cd3faae5062e6"></a>

##### AccessDenied · 枚举值

```csharp
AccessDenied = 4
```

读取被拒绝或缺少所需特权。

<a id="api-9767b5d8b9afbc24"></a>

##### ConnectionFailure · 枚举值

```csharp
ConnectionFailure = 8
```

RPC 或 COM 连接失败。

<a id="api-d800526aa6b30c96"></a>

##### ExecutableMissing · 枚举值

```csharp
ExecutableMissing = 12
```

VMMS 没有运行，且已确认其程序文件路径不存在。

<a id="api-b4c30e636f2a7047"></a>

##### InvalidClass · 枚举值

```csharp
InvalidClass = 6
```

WMI 类不存在或不可用。

<a id="api-de60b95e5e63f90a"></a>

##### InvalidData · 枚举值

```csharp
InvalidData = 9
```

查询数据缺失、格式异常或无法可靠解释。

<a id="api-865181b0e24295fc"></a>

##### InvalidNamespace · 枚举值

```csharp
InvalidNamespace = 5
```

WMI 命名空间不存在或不可用。

<a id="api-6121405e4747dfc4"></a>

##### None · 枚举值

```csharp
None = 0
```

没有诊断问题，或此阶段尚未检查。

<a id="api-bdb0767903f87137"></a>

##### NotFound · 枚举值

```csharp
NotFound = 2
```

成功查询后没有找到目标服务或实例。

<a id="api-b44008ba01d3e33b"></a>

##### NotRunning · 枚举值

```csharp
NotRunning = 1
```

监控程序或服务没有运行。

<a id="api-1b7f3ef75dba686f"></a>

##### ProviderFailure · 枚举值

```csharp
ProviderFailure = 7
```

WMI Provider 报告执行失败。

<a id="api-7848960083879fed"></a>

##### QueryFailed · 枚举值

```csharp
QueryFailed = 10
```

没有归入其他类别的读取失败，需查看原始错误。

<a id="api-ddc456a603ad3627"></a>

##### ServiceDisabled · 枚举值

```csharp
ServiceDisabled = 11
```

VMMS 没有运行，且服务启动类型为 Disabled。

<a id="api-9058510f283dabc9"></a>

##### Timeout · 枚举值

```csharp
Timeout = 3
```

本次等待读取结果已超时，不表示底层调用已取消。

<a id="api-ea63144f831629dc"></a>

### `HyperVEnvironmentStatus` 类

Hyper-V 运行环境的结构化检查结果；按阶段顺序遇到首个问题后停止后续检查。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public HyperVCheckResult? FirstIssue { get; } `](#api-11035cbb2f7efdca) | 按监控程序、VMMS、WMI 顺序遇到的首个未通过结果；全部通过时为 null。 |
| [` public HyperVCheckResult Hypervisor { get; } `](#api-ba82d55a46e226f5) | 本系统监控程序的检查结果。 |
| [` public bool? IsInstalled { get; } `](#api-6a8fe1b5ea30a7ab) | 是否已查到宿主的 VMMS 服务。 |
| [` public bool IsReady { get; } `](#api-061fa6b7bc324734) | 监控程序、VMMS 服务和 WMI 管理接口是否均已通过运行检查。 |
| [` public string StatusText { get; } `](#api-cb44fa30cf482a5e) | 首个问题的显示说明；全部通过时为空。 |
| [` public HyperVCheckResult Vmms { get; } `](#api-26c01bbec370913b) | VMMS 服务检查结果。 |
| [` public VmmsServiceInfo? VmmsService { get; } `](#api-313b359863164567) | 成功查到 VMMS 时的服务信息；未检查、查询失败或不存在时为 null。 |
| [` public HyperVCheckResult Wmi { get; } `](#api-67f261ae75b436c9) | Hyper-V WMI 管理服务查询结果。 |

#### 详细说明

```csharp
public sealed class HyperVEnvironmentStatus
```

调用约定：检查顺序为监控程序、VMMS 服务、WMI 管理接口；遇到首个问题后，剩余阶段标为 NotChecked。

#### 属性说明

<a id="api-11035cbb2f7efdca"></a>

##### FirstIssue · 属性

```csharp
public HyperVCheckResult? FirstIssue { get; }
```

按监控程序、VMMS、WMI 顺序遇到的首个未通过结果；全部通过时为 null。

<a id="api-ba82d55a46e226f5"></a>

##### Hypervisor · 属性

```csharp
public HyperVCheckResult Hypervisor { get; }
```

本系统监控程序的检查结果。

<a id="api-6a8fe1b5ea30a7ab"></a>

##### IsInstalled · 属性

```csharp
public bool? IsInstalled { get; }
```

是否已查到宿主的 VMMS 服务。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 是否已查到宿主的 VMMS 服务。 |
| 取值与默认 | true：服务存在；false：已确认服务不存在；null：未检查或查询失败。 |

<a id="api-061fa6b7bc324734"></a>

##### IsReady · 属性

```csharp
public bool IsReady { get; }
```

监控程序、VMMS 服务和 WMI 管理接口是否均已通过运行检查。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 监控程序、VMMS 服务和 WMI 管理接口是否均已通过运行检查。 |
| 取值与默认 | true：三项全部通过；false：至少一项未通过或未检查。 |

<a id="api-cb44fa30cf482a5e"></a>

##### StatusText · 属性

```csharp
public string StatusText { get; }
```

首个问题的显示说明；全部通过时为空。

<a id="api-26c01bbec370913b"></a>

##### Vmms · 属性

```csharp
public HyperVCheckResult Vmms { get; }
```

VMMS 服务检查结果。

<a id="api-313b359863164567"></a>

##### VmmsService · 属性

```csharp
public VmmsServiceInfo? VmmsService { get; }
```

成功查到 VMMS 时的服务信息；未检查、查询失败或不存在时为 null。

<a id="api-67f261ae75b436c9"></a>

##### Wmi · 属性

```csharp
public HyperVCheckResult Wmi { get; }
```

Hyper-V WMI 管理服务查询结果。

<a id="api-d765f6dc004e22df"></a>

### `HyperVHostService` 类

检测本机 Hyper-V 环境，并提交 Windows 功能及引导配置变更。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V；管理层面 / 宿主 / 修改 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVHostService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<HyperVEnvironmentStatus> GetHyperVDiagnosticsAsync() `](#api-6e83f00a27983f48) | 取得 Hyper-V 运行环境的结构化检查结果，按监控程序、VMMS、WMI 顺序报告首个问题。 |
| [` public static async Task<(bool IsReady, bool IsInstalled, string StatusText)> GetHyperVStatusAsync() `](#api-eb7d67479cd7d470) | 读取 Hyper-V 环境是否就绪、是否查到 VMMS 及首个问题说明。 |
| [` public static int GetVmmsStatus() `](#api-850ddcb6b6c173ca) | 读取宿主 VMMS 服务的运行状态代码。 |
| [` public static bool IsHyperVWmiNamespaceAvailable() `](#api-71d57df0e8be1547) | 检测本机 Hyper-V WMI 管理服务是否可连接且存在。 |
| [` public static bool IsHypervisorPresent() `](#api-12b38ef2a7a8f6a3) | 检测系统是否上报存在监控程序。 |
| [` public static bool IsIommuEnabled() `](#api-606e6a9775f1bea2) | 检查 Device Guard 的可用安全属性中是否包含 DMA 保护。 |
| [` public static bool IsVirtualizationEnabled() `](#api-620a3d50b8f9a3f1) | 检查宿主是否具备已启用的硬件虚拟化扩展。 |
| [` public static async Task<ApiResponse> SetHyperVEnabledAsync(bool enabled) `](#api-fc59d5a50ad2e8a7) | 启用或停用宿主 Hyper-V。 |

#### 详细说明

```csharp
public static class HyperVHostService
```

#### 静态方法说明

<a id="api-6e83f00a27983f48"></a>

##### GetHyperVDiagnosticsAsync · 方法

```csharp
public static Task<HyperVEnvironmentStatus> GetHyperVDiagnosticsAsync();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

取得 Hyper-V 运行环境的结构化检查结果，按监控程序、VMMS、WMI 顺序报告首个问题。

返回：各阶段状态、首个问题、原始错误和 VMMS 服务详情；未执行的阶段为 NotChecked。

调用约定：每阶段最多等待 10 秒，前一阶段未通过时停止后续检查。超时结束等待，底层查询继续；重复调用共用仍在进行的查询。

<a id="api-eb7d67479cd7d470"></a>

##### GetHyperVStatusAsync · 方法

```csharp
public static async Task<(bool IsReady, bool IsInstalled, string StatusText)> GetHyperVStatusAsync();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

读取 Hyper-V 环境是否就绪、是否查到 VMMS 及首个问题说明。

返回：IsReady 表示三项均通过；IsInstalled 只在本次确认 VMMS 存在时为 true，未检查、查询失败和不存在均为 false。StatusText 只报告首个问题，正常就绪时为空。

调用约定：需要区分未检查、查询失败与服务不存在时，使用 GetHyperVDiagnosticsAsync。

<a id="api-850ddcb6b6c173ca"></a>

##### GetVmmsStatus · 方法

```csharp
public static int GetVmmsStatus();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

读取宿主 VMMS 服务的运行状态代码。

返回：0：未找到或无法查询；1：Running；2：服务存在但未运行。详细诊断见 GetHyperVDiagnosticsAsync。

<a id="api-71d57df0e8be1547"></a>

##### IsHyperVWmiNamespaceAvailable · 方法

```csharp
public static bool IsHyperVWmiNamespaceAvailable();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

检测本机 Hyper-V WMI 管理服务是否可连接且存在。

返回：检测为可用时为 true；未通过或超时为 false。等待有界，详细错误由 GetHyperVDiagnosticsAsync 提供。

<a id="api-12b38ef2a7a8f6a3"></a>

##### IsHypervisorPresent · 方法

```csharp
public static bool IsHypervisorPresent();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

检测系统是否上报存在监控程序。

返回：检测为可用时为 true；不满足条件或读取异常时为 false。

<a id="api-606e6a9775f1bea2"></a>

##### IsIommuEnabled · 方法

```csharp
public static bool IsIommuEnabled();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

检查 Device Guard 的可用安全属性中是否包含 DMA 保护。

返回：检测为可用时为 true；不满足条件或读取异常时为 false。

调用约定：以 Windows Device Guard 上报的 DMA 保护能力判断。

<a id="api-620a3d50b8f9a3f1"></a>

##### IsVirtualizationEnabled · 方法

```csharp
public static bool IsVirtualizationEnabled();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

检查宿主是否具备已启用的硬件虚拟化扩展。

返回：检测为可用时为 true；不满足条件或读取异常时为 false。

<a id="api-fc59d5a50ad2e8a7"></a>

##### SetHyperVEnabledAsync · 方法

```csharp
public static async Task<ApiResponse> SetHyperVEnabledAsync(bool enabled);
```

功能归属：管理层面 / 宿主 / 修改 / Hyper-V。

启用或停用宿主 Hyper-V。

| 参数 | 说明 |
| --- | --- |
| `enabled` | true 启用所需 Windows 功能并允许监控程序启动；false 停用监控程序启动，保留已安装组件。 |

返回：配置与运行检查的结果。RequiresRestart 和 RestartReasons 表示重启要求；Steps 保留各步骤结果。后续步骤失败时，已完成的变更和重启要求仍保留。

调用约定：开启时先启用必要组件，再修改引导配置；组件失败或尚待完成时停止后续修改。确认家庭版缺少功能时，尝试登记适用的本机 Hyper-V 安装包。无需重启时继续检查监控程序、VMMS 和 WMI 是否就绪。



需要管理员权限，同一宿主的 Core 请求串行执行。此方法不关闭虚拟机、不自动重启；调用方根据 RequiresRestart 安排重启。

<a id="api-4ad42a943b9e96dd"></a>

### `HyperVHostSettings` record 类

宿主 Hyper-V 管理服务的配置快照。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 存储；管理层面 / 宿主 / 查询 / 网络；管理层面 / 宿主 / 查询 / Hyper-V；管理层面 / 宿主 / 查询 / 控制台 |
| 源文件 | ` Management/Host/HyperV/HyperVHostSettingsService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HyperVHostSettings(bool EnhancedSessionModeEnabled, string DefaultVirtualMachinePath, string DefaultVirtualHardDiskPath, string MinimumMacAddress, string MaximumMacAddress) `](#api-7551c619f42b8fa1) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DefaultVirtualHardDiskPath { get; init; } `](#api-2d5cdc4b60e14dc2) | 创建虚拟硬盘时使用的宿主默认目录；未取得时为空字符串。 |
| [` public string DefaultVirtualMachinePath { get; init; } `](#api-e8d144acf92447f6) | 创建虚拟机时使用的默认配置目录，位于宿主文件系统；未取得时为空字符串。 |
| [` public bool EnhancedSessionModeEnabled { get; init; } `](#api-d0cf76593d92c868) | 宿主是否允许虚拟机增强会话。 |
| [` public string MaximumMacAddress { get; init; } `](#api-3a451f5dfea52c5c) | 宿主分配动态 MAC 地址的范围上界；未取得时为空字符串。 |
| [` public string MinimumMacAddress { get; init; } `](#api-83e60d0ccfb563fc) | 宿主分配动态 MAC 地址的范围下界；未取得时为空字符串。 |

#### 详细说明

```csharp
public sealed record HyperVHostSettings(
    bool EnhancedSessionModeEnabled,
    string DefaultVirtualMachinePath,
    string DefaultVirtualHardDiskPath,
    string MinimumMacAddress,
    string MaximumMacAddress)
```

| 参数 | 说明 |
| --- | --- |
| `EnhancedSessionModeEnabled` | 宿主是否允许虚拟机增强会话。true 允许；false 不允许或系统未提供此项。 |
| `DefaultVirtualMachinePath` | 创建虚拟机时使用的默认配置目录，位于宿主文件系统；未取得时为空字符串。 |
| `DefaultVirtualHardDiskPath` | 创建虚拟硬盘时使用的宿主默认目录；未取得时为空字符串。 |
| `MinimumMacAddress` | 宿主分配动态 MAC 地址的范围下界；未取得时为空字符串。 |
| `MaximumMacAddress` | 宿主分配动态 MAC 地址的范围上界；未取得时为空字符串。 |

#### 构造函数说明

<a id="api-7551c619f42b8fa1"></a>

##### HyperVHostSettings · 主构造器

```csharp
public HyperVHostSettings(bool EnhancedSessionModeEnabled, string DefaultVirtualMachinePath, string DefaultVirtualHardDiskPath, string MinimumMacAddress, string MaximumMacAddress);
```

功能归属：管理层面 / 宿主 / 查询 / 存储；管理层面 / 宿主 / 查询 / 网络；管理层面 / 宿主 / 查询 / Hyper-V；管理层面 / 宿主 / 查询 / 控制台。

| 参数 | 说明 |
| --- | --- |
| `EnhancedSessionModeEnabled` | 宿主是否允许虚拟机增强会话。true 允许；false 不允许或系统未提供此项。 |
| `DefaultVirtualMachinePath` | 创建虚拟机时使用的默认配置目录，位于宿主文件系统；未取得时为空字符串。 |
| `DefaultVirtualHardDiskPath` | 创建虚拟硬盘时使用的宿主默认目录；未取得时为空字符串。 |
| `MinimumMacAddress` | 宿主分配动态 MAC 地址的范围下界；未取得时为空字符串。 |
| `MaximumMacAddress` | 宿主分配动态 MAC 地址的范围上界；未取得时为空字符串。 |

#### 属性说明

<a id="api-2d5cdc4b60e14dc2"></a>

##### DefaultVirtualHardDiskPath · record 属性

```csharp
public string DefaultVirtualHardDiskPath { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 存储。

创建虚拟硬盘时使用的宿主默认目录；未取得时为空字符串。

<a id="api-e8d144acf92447f6"></a>

##### DefaultVirtualMachinePath · record 属性

```csharp
public string DefaultVirtualMachinePath { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

创建虚拟机时使用的默认配置目录，位于宿主文件系统；未取得时为空字符串。

<a id="api-d0cf76593d92c868"></a>

##### EnhancedSessionModeEnabled · record 属性

```csharp
public bool EnhancedSessionModeEnabled { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 控制台。

宿主是否允许虚拟机增强会话。true 允许；false 不允许或系统未提供此项。

<a id="api-3a451f5dfea52c5c"></a>

##### MaximumMacAddress · record 属性

```csharp
public string MaximumMacAddress { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 网络。

宿主分配动态 MAC 地址的范围上界；未取得时为空字符串。

<a id="api-83e60d0ccfb563fc"></a>

##### MinimumMacAddress · record 属性

```csharp
public string MinimumMacAddress { get; init; }
```

功能归属：管理层面 / 宿主 / 查询 / 网络。

宿主分配动态 MAC 地址的范围下界；未取得时为空字符串。

<a id="api-7d8a2f197b518ae9"></a>

### `HyperVHostSettingsService` 类

读取宿主 Hyper-V 配置快照，修改新建虚拟机的默认配置目录。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V；管理层面 / 宿主 / 修改 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVHostSettingsService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<HyperVHostSettings?> GetAsync() `](#api-7dc3a7ae4ba02bcc) | 读取宿主 Hyper-V 默认目录、增强会话及动态 MAC 地址池。 |
| [` public static Task<ApiResponse> SetDefaultVirtualMachinePathAsync(string path) `](#api-90ff6ff5c6d89e12) | 修改新建虚拟机的宿主默认配置目录，不移动已有虚拟机。 |

#### 详细说明

```csharp
public static class HyperVHostSettingsService
```

#### 静态方法说明

<a id="api-7dc3a7ae4ba02bcc"></a>

##### GetAsync · 方法

```csharp
public static async Task<HyperVHostSettings?> GetAsync();
```

功能归属：管理层面 / 宿主 / 查询 / Hyper-V。

读取宿主 Hyper-V 默认目录、增强会话及动态 MAC 地址池。

返回：配置快照；未查到配置或查询失败时为 null。

<a id="api-90ff6ff5c6d89e12"></a>

##### SetDefaultVirtualMachinePathAsync · 方法

```csharp
public static Task<ApiResponse> SetDefaultVirtualMachinePathAsync(string path);
```

功能归属：管理层面 / 宿主 / 修改 / Hyper-V。

修改新建虚拟机的宿主默认配置目录，不移动已有虚拟机。

| 参数 | 说明 |
| --- | --- |
| `path` | 宿主上的默认目录路径。 |

返回：Hyper-V 配置提交响应。

<a id="api-8469acca0f2d44da"></a>

### `HyperVNumaService` 类

读取和修改宿主 Hyper-V 跨 NUMA 节点分配内存策略。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 内存；管理层面 / 宿主 / 修改 / 内存 |
| 源文件 | ` Management/Host/Memory/HyperVNumaService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse<bool>> GetNumaSpanningEnabledAsync() `](#api-b1e7de3eb3d4ce6d) | 读取宿主是否允许跨 NUMA 节点分配。 |
| [` public static async Task<ApiResponse> SetNumaSpanningEnabledAsync(bool enabled) `](#api-e07705a213e3134c) | 提交宿主跨 NUMA 节点分配策略。 |

#### 详细说明

```csharp
public static class HyperVNumaService
```

#### 静态方法说明

<a id="api-b1e7de3eb3d4ce6d"></a>

##### GetNumaSpanningEnabledAsync · 方法

```csharp
public static async Task<ApiResponse<bool>> GetNumaSpanningEnabledAsync();
```

功能归属：管理层面 / 宿主 / 查询 / 内存。

读取宿主是否允许跨 NUMA 节点分配。

返回：true 允许跨 NUMA 节点分配内存；false 不允许或系统未提供有效设置值。查询失败时返回错误。

<a id="api-e07705a213e3134c"></a>

##### SetNumaSpanningEnabledAsync · 方法

```csharp
public static async Task<ApiResponse> SetNumaSpanningEnabledAsync(bool enabled);
```

功能归属：管理层面 / 宿主 / 修改 / 内存。

提交宿主跨 NUMA 节点分配策略。

| 参数 | 说明 |
| --- | --- |
| `enabled` | 是否允许 NUMA 跨节点分配。 |

返回：成功标志与失败说明。

调用约定：设置宿主后续的内存分配策略，已有虚拟机的内存保持原位置。

<a id="api-631237297addc003"></a>

### `HyperVSchedulerService` 类

分别读取宿主当前运行的调度器和引导配置，并设置后续启动的调度器选择。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 调度器；管理层面 / 宿主 / 修改 / 调度器 |
| 源文件 | ` Management/Host/Scheduler/HyperVSchedulerService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static HyperVSchedulerType GetConfiguredSchedulerType() `](#api-bb62581e3974b990) | 读取后续启动采用的调度器配置，不表示当前已经切换。 |
| [` public static HyperVSchedulerType GetSchedulerType() `](#api-e4d7f01e1b9f7594) | 读取宿主当前运行的 Hyper-V 调度器。 |
| [` public static Task<ApiResponse> SetSchedulerTypeAsync(HyperVSchedulerType type) `](#api-86bcce3a5339cc53) | 写入宿主引导配置中的 Hyper-V 调度器选择。 |

#### 详细说明

```csharp
public static class HyperVSchedulerService
```

#### 静态方法说明

<a id="api-bb62581e3974b990"></a>

##### GetConfiguredSchedulerType · 方法

```csharp
public static HyperVSchedulerType GetConfiguredSchedulerType();
```

功能归属：管理层面 / 宿主 / 查询 / 调度器。

读取后续启动采用的调度器配置，不表示当前已经切换。

返回：明确配置或按当前系统推定的启动调度器；无法确定时为 Unknown。

调用约定：读取当前启动项的 BCD 配置；未配置时按当前系统产品类型和构建号确定默认值，无法确定时返回 Unknown。此配置可能与正在运行的调度器不同。

<a id="api-e4d7f01e1b9f7594"></a>

##### GetSchedulerType · 方法

```csharp
public static HyperVSchedulerType GetSchedulerType();
```

功能归属：管理层面 / 宿主 / 查询 / 调度器。

读取宿主当前运行的 Hyper-V 调度器。

返回：当前识别出的调度器；未运行或无法确定时为 Unknown。

调用约定：优先查询内核运行信息，无法识别时读取本次启动的事件日志。内核调度器字段未文档化；两种来源均无法确认时返回 Unknown。下次启动配置见 GetConfiguredSchedulerType。

<a id="api-86bcce3a5339cc53"></a>

##### SetSchedulerTypeAsync · 方法

```csharp
public static Task<ApiResponse> SetSchedulerTypeAsync(HyperVSchedulerType type);
```

功能归属：管理层面 / 宿主 / 修改 / 调度器。

写入宿主引导配置中的 Hyper-V 调度器选择。

| 参数 | 说明 |
| --- | --- |
| `type` | 要写入的调度器模式。 |

返回：引导配置写入及读回结果；Unknown 或未定义的枚举值不会提交。失败信息区分写入失败、结果未知和写入成功后读回失败。

调用约定：通过 BCD WMI Provider 写入并回读，需要管理员及备份、还原特权。修改供后续启动采用，不自动重启宿主。

<a id="api-0e00c4ef25825f2b"></a>

### `HyperVSchedulerType` 枚举

Hyper-V 调度器模式；Unknown 表示未识别的结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 调度器 |
| 源文件 | ` Management/Host/Scheduler/HyperVSchedulerService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Classic = 0 `](#api-0dcc75cd2837d16f) | Classic 传统调度器，由监控程序调度来宾虚拟处理器。 |
| [` Core = 1 `](#api-91e68ab8b78df316) | Core 核心调度器，按物理核心的 SMT 拓扑协调同一虚拟机的虚拟处理器。 |
| [` Root = 2 `](#api-ce539d9420c4bbc9) | Root 根分区调度器，由根分区的 Windows 调度器安排处理器工作。 |
| [` Unknown = 3 `](#api-1e67a7a078254d3a) | 无法确定调度器类型。 |

#### 详细说明

```csharp
public enum HyperVSchedulerType
```

#### 枚举值说明

<a id="api-0dcc75cd2837d16f"></a>

##### Classic · 枚举值

```csharp
Classic = 0
```

Classic 传统调度器，由监控程序调度来宾虚拟处理器。

<a id="api-91e68ab8b78df316"></a>

##### Core · 枚举值

```csharp
Core = 1
```

Core 核心调度器，按物理核心的 SMT 拓扑协调同一虚拟机的虚拟处理器。

<a id="api-ce539d9420c4bbc9"></a>

##### Root · 枚举值

```csharp
Root = 2
```

Root 根分区调度器，由根分区的 Windows 调度器安排处理器工作。

<a id="api-1e67a7a078254d3a"></a>

##### Unknown · 枚举值

```csharp
Unknown = 3
```

无法确定调度器类型。

<a id="api-0f66c11c037b51ce"></a>

### `HyperVSwitchService` 类

读取和配置本机 Hyper-V 虚拟交换机及 ICS 上游共享关系。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 网络；管理层面 / 宿主 / 修改 / 网络 |
| 源文件 | ` Management/Host/Network/HyperVSwitchService.Operations.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task CreateSwitchAsync(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream) `](#api-8e882037c098524e) | 校验当前主机状态后创建交换机，并按模式建立上游或共享。 |
| [` public static Task DeleteSwitchAsync(Guid switchId) `](#api-531ce12c38ad235d) | 按稳定 GUID 关闭相关 ICS 并删除交换机，默认交换机受保护。 |
| [` public static Task DeleteSwitchAsync(string switchName) `](#api-e5862bc792ac1326) | 关闭相关 ICS 后删除指定交换机；默认交换机受规则保护。 |
| [` public static async Task<List<SwitchUpstreamAdapter>> GetBridgeableAdaptersAsync() `](#api-0bbb050d21026f1b) | 筛选已找到 Hyper-V 外部端口的宿主物理网卡。 |
| [` public static async Task<List<SwitchEndpointInfo>> GetFullSwitchNetworkStateAsync(string switchName) `](#api-e555b97da5c034ee) | 读取接入指定交换机的宿主和虚拟机网卡。 |
| [` public static Task<(List<SwitchInfo> Switches, List<SwitchUpstreamAdapter> PhysicalAdapters)> GetNetworkInfoAsync() `](#api-42c59a83cabc1075) | 读取交换机、连接模式和可用物理上游网卡快照。 |
| [` public static Task UpdateSwitchConfigurationAsync(Guid switchId, SwitchMode mode, SwitchUpstreamAdapter? upstream, bool allowManagementOS) `](#api-a7b99da342b381d5) | 按稳定 GUID 修改交换机端口和 ICS 共享；在宿主网络锁内重新校验配置。 |
| [` public static Task UpdateSwitchConfigurationAsync(string switchName, SwitchMode mode, SwitchUpstreamAdapter? upstream, bool allowManagementOS) `](#api-e456b7c7d3550d57) | 按唯一名称解析交换机身份，再切换连接模式。 |

#### 详细说明

```csharp
public static partial class HyperVSwitchService
```

调用约定：变更需要 Hyper-V 与网络管理权限，并可能短暂中断宿主/VM 网络。交换机及 ICS 配置在同一宿主跨 Core 进程串行；不阻止外部工具修改。底层结果未知时停止后续修改及恢复，调用方必须先核对实际状态。

#### 静态方法说明

<a id="api-8e882037c098524e"></a>

##### CreateSwitchAsync · 方法

```csharp
public static Task CreateSwitchAsync(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

校验当前主机状态后创建交换机，并按模式建立上游或共享。

| 参数 | 说明 |
| --- | --- |
| `name` | 新交换机显示名称。 |
| `mode` | 目标连接模式：桥接、ICS NAT 或隔离。 |
| `upstream` | 桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。 |

调用约定：在宿主网络锁内重新读取配置。新建时三种模式均保留宿主连接；NAT 使用 ICS，可能替换既有共享并改变私有侧 IPv4/DNS。明确失败才恢复共享并删除本次新交换机；结果未知时保留现场。CoreOperationException.Result 保留错误类别，Steps 的 ResourceId 提供失败时已确认的新交换机 GUID；成功调用不返回该 GUID，需重新查询。

<a id="api-531ce12c38ad235d"></a>

##### DeleteSwitchAsync · 方法

```csharp
public static Task DeleteSwitchAsync(Guid switchId);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

按稳定 GUID 关闭相关 ICS 并删除交换机，默认交换机受保护。

| 参数 | 说明 |
| --- | --- |
| `switchId` | 查询返回的交换机 GUID，不受显示名称修改影响。 |

调用约定：跨 Core 进程串行执行。失败抛出 CoreOperationException，Result 保留失败类别；OutcomeUnknown 时不得直接重试或清理。先行 ICS 修改不保证撤销。

<a id="api-e5862bc792ac1326"></a>

##### DeleteSwitchAsync · 方法

```csharp
public static Task DeleteSwitchAsync(string switchName);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

关闭相关 ICS 后删除指定交换机；默认交换机受规则保护。

| 参数 | 说明 |
| --- | --- |
| `switchName` | 目标交换机的唯一显示名称。 |

调用约定：名称在宿主网络锁内解析一次；重名返回 AmbiguousTarget，不选第一项。删除可能影响已连接 VM；失败由 CoreOperationException.Result 表达，不能推定先行步骤已撤销。

<a id="api-0bbb050d21026f1b"></a>

##### GetBridgeableAdaptersAsync · 方法

```csharp
public static async Task<List<SwitchUpstreamAdapter>> GetBridgeableAdaptersAsync();
```

功能归属：管理层面 / 宿主 / 查询 / 网络。

筛选已找到 Hyper-V 外部端口的宿主物理网卡。

返回：桥接候选列表；未在此排除其他交换机占用或断开链路，提交前仍需校验。

<a id="api-e555b97da5c034ee"></a>

##### GetFullSwitchNetworkStateAsync · 方法

```csharp
public static async Task<List<SwitchEndpointInfo>> GetFullSwitchNetworkStateAsync(string switchName);
```

功能归属：管理层面 / 宿主 / 查询 / 网络。

读取接入指定交换机的宿主和虚拟机网卡。

| 参数 | 说明 |
| --- | --- |
| `switchName` | 交换机显示名称；当前实现遇到同名对象时取查询首项，调用方须避免歧义。 |

返回：已读取的端点列表；失败可能被省略或转为空列表，不能将空列表当作已确认没有端点。

调用约定：每个端点返回显示名称、MAC 和一个择优 IPv4 地址。

<a id="api-42c59a83cabc1075"></a>

##### GetNetworkInfoAsync · 方法

```csharp
public static Task<(List<SwitchInfo> Switches, List<SwitchUpstreamAdapter> PhysicalAdapters)> GetNetworkInfoAsync();
```

功能归属：管理层面 / 宿主 / 查询 / 网络。

读取交换机、连接模式和可用物理上游网卡快照。

返回：交换机和物理网卡列表；网卡等读取失败可抛出异常，交换机主查询失败当前返回空交换机列表，不能与确实没有交换机区分。逐交换机识别问题见 StateError。

<a id="api-a7b99da342b381d5"></a>

##### UpdateSwitchConfigurationAsync · 方法

```csharp
public static Task UpdateSwitchConfigurationAsync(Guid switchId, SwitchMode mode, SwitchUpstreamAdapter? upstream, bool allowManagementOS);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

按稳定 GUID 修改交换机端口和 ICS 共享；在宿主网络锁内重新校验配置。

| 参数 | 说明 |
| --- | --- |
| `switchId` | 查询返回的交换机 GUID。 |
| `mode` | 目标桥接、ICS NAT 或隔离模式。 |
| `upstream` | 桥接或 NAT 的宿主上游网卡；隔离可为 null。 |
| `allowManagementOS` | 桥接或隔离时是否保留宿主连接；NAT 按共享要求处理。 |

调用约定：明确失败时尝试恢复原配置；结果未知时不恢复、不重试。CoreOperationException.Result 保留 OutcomeUnknown、CleanupRequired 等类别和原始失败。锁覆盖修改与恢复，但不阻止未遵循 Core 锁协议的工具。

<a id="api-e456b7c7d3550d57"></a>

##### UpdateSwitchConfigurationAsync · 方法

```csharp
public static Task UpdateSwitchConfigurationAsync(string switchName, SwitchMode mode, SwitchUpstreamAdapter? upstream, bool allowManagementOS);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

按唯一名称解析交换机身份，再切换连接模式。

| 参数 | 说明 |
| --- | --- |
| `switchName` | 目标交换机的唯一显示名称；可优先使用 GUID 重载。 |
| `mode` | 目标连接模式：桥接、ICS NAT 或隔离。 |
| `upstream` | 桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。 |
| `allowManagementOS` | 桥接或隔离模式下是否保留宿主连接；NAT 按共享要求处理。 |

调用约定：名称在宿主网络锁内解析一次，重名返回 AmbiguousTarget。明确失败才尝试恢复；结果未知时停止后续修改及恢复。CoreOperationException.Result 保留失败类别、原始错误和恢复结果，调用方应重新读取实际状态。

<a id="api-d949c0cb1a4fd072"></a>

### `IConsoleAudioInput` 接口

音频输入的历史预留接口，未实现，不属于当前 Core 的功能范围。

| 项目 | 内容 |
| --- | --- |
| 类型 | 接口 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 音频 |
| 源文件 | ` Business/Audio/Input/IConsoleAudioInput.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` ConsoleAudioFormat? Format { get; } `](#api-324de9ff89aa2d92) | 发送通道当前接受的采样格式。 |
| [` bool IsAvailable { get; } `](#api-1c918d04411dba90) | 音频发送通道是否已就绪。 |

**事件**

| 成员 | 说明 |
| --- | --- |
| [` event Action<bool>? AvailabilityChanged `](#api-71bffcbcad7505b8) | 发送通道的可用状态或采样格式变化时通知。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` bool SendAudio(ConsoleAudioFormat format, ReadOnlySpan<byte> samples) `](#api-4b650c40d5b6382b) | 把一段按当前格式准备好的 PCM 音频交给来宾。 |

#### 详细说明

```csharp
public interface IConsoleAudioInput
```

调用约定：当前内置后端的 AudioInput 为 null。以下成员保留采样格式、就绪通知和提交 PCM 的约定。

#### 属性说明

<a id="api-324de9ff89aa2d92"></a>

##### Format · 属性

```csharp
ConsoleAudioFormat? Format { get; }
```

发送通道当前接受的采样格式。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 发送通道当前接受的采样格式。 |
| 取值与默认 | 尚未协商或通道不可用时为 null。 |

<a id="api-1c918d04411dba90"></a>

##### IsAvailable · 属性

```csharp
bool IsAvailable { get; }
```

音频发送通道是否已就绪。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 音频发送通道是否已就绪。 |
| 取值与默认 | true：就绪；false：未就绪或已断开。 |

#### 事件说明

<a id="api-71bffcbcad7505b8"></a>

##### AvailabilityChanged · 事件

```csharp
event Action<bool>? AvailabilityChanged;
```

发送通道的可用状态或采样格式变化时通知。

调用约定：参数为变化后的 IsAvailable。即使仍为 true，也应重新读取 Format，确认采样格式是否改变。

#### 方法说明

<a id="api-4b650c40d5b6382b"></a>

##### SendAudio · 方法

```csharp
bool SendAudio(ConsoleAudioFormat format, ReadOnlySpan<byte> samples);
```

把一段按当前格式准备好的 PCM 音频交给来宾。

| 参数 | 说明 |
| --- | --- |
| `format` | 这批 samples 实际使用的格式，应与当前 Format 相同。 |
| `samples` | 原始 PCM 字节，包含完整的采样帧；本次调用返回后可复用该内存。 |

返回：true 表示数据已加入发送队列；通道不可用、格式不匹配或队列无法接收时为 false。

调用约定：格式可能在采集期间改变。发送时仍传入这批数据原来的格式；如果被拒绝，重新读取 Format 后为下一批数据调整格式。 返回 true 不表示来宾已经播放或处理了这段声音。

<a id="api-8982aaf97ec63492"></a>

### `IConsoleAudioOutput` 接口

来宾声音接收的预留接口；当前内置后端尚未实现。

| 项目 | 内容 |
| --- | --- |
| 类型 | 接口 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 音频 |
| 源文件 | ` Business/Audio/Output/IConsoleAudioOutput.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` bool IsAvailable { get; } `](#api-761e37d9a061b591) | 音频接收通道是否已就绪。 |

**事件**

| 成员 | 说明 |
| --- | --- |
| [` event Action<ConsoleAudioPacket>? AudioReceived `](#api-91ed73ff857079e0) | 收到一段来宾声音时通知，参数包含采样字节和格式。 |
| [` event Action<bool>? AvailabilityChanged `](#api-1ddd3617040ad708) | 音频接收通道变为可用或不可用时通知，参数为变化后的 IsAvailable。 |

#### 详细说明

```csharp
public interface IConsoleAudioOutput
```

调用约定：连接的 AudioOutput 当前为 null。接口约定通过 AudioReceived 交付 PCM 音频包，由调用方播放或编码。

#### 属性说明

<a id="api-761e37d9a061b591"></a>

##### IsAvailable · 属性

```csharp
bool IsAvailable { get; }
```

音频接收通道是否已就绪。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 音频接收通道是否已就绪。 |
| 取值与默认 | true：就绪；false：未就绪或已断开。 |

#### 事件说明

<a id="api-91ed73ff857079e0"></a>

##### AudioReceived · 事件

```csharp
event Action<ConsoleAudioPacket>? AudioReceived;
```

收到一段来宾声音时通知，参数包含采样字节和格式。

调用约定：回调在后台线程执行，应尽快返回。数据由音频包独立持有，可直接交给后续处理，无需另行复制或保留租约。

<a id="api-1ddd3617040ad708"></a>

##### AvailabilityChanged · 事件

```csharp
event Action<bool>? AvailabilityChanged;
```

音频接收通道变为可用或不可用时通知，参数为变化后的 IsAvailable。

<a id="api-199dbda02346a96d"></a>

### `IConsoleClipboard` 接口

与来宾交换剪贴板内容，例如双向复制粘贴文字。

| 项目 | 内容 |
| --- | --- |
| 类型 | 接口 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 剪贴板 |
| 源文件 | ` Business/Clipboard/IConsoleClipboard.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` bool IsAvailable { get; } `](#api-0df8d1c1e7561dfc) | 连接与剪贴板通道是否已就绪。 |

**事件**

| 成员 | 说明 |
| --- | --- |
| [` event Action<bool>? ClipboardAvailabilityChanged `](#api-15c6e3cb9d02431e) | 剪贴板通道变为可用或不可用时通知，参数为变化后的 IsAvailable。 |
| [` event Action<ConsoleClipboardData>? ClipboardReceived `](#api-f7b88fa31b137c0c) | 取得来宾剪贴板中受支持格式的数据时通知。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` bool SetClipboard(ConsoleClipboardData clipboard) `](#api-59566efd7e5c5a5c) | 把调用方准备好的剪贴板内容发送给来宾。 |

#### 详细说明

```csharp
public interface IConsoleClipboard
```

调用约定：当前 RdpEnhanced 提供此接口，RdpBasic 不提供。接口存在后还需要等待 IsAvailable 为 true。



向来宾发送时调用 SetClipboard；来宾提供新内容时，通过 ClipboardReceived 接收。 Core 不自动读取或写入宿主系统剪贴板，桌面程序或 Web 应用自行决定何时取出、显示或粘贴这些数据。



事件可能在后台线程触发；需要操作界面或宿主剪贴板时，调度到调用方要求的线程。 双向同步时应识别自己刚写入的内容，避免两端反复回传。

示例：

发送纯文本，并从收到的内容中选取纯文本格式。收到的文字交给调用方处理，不直接操作系统剪贴板。



```csharp
using ExHyperV.Services;
using System.Text;
public static class ClipboardExample
{
    public static bool SendText(IConsoleClipboard clipboard, string text)
    {
        if (!clipboard.IsAvailable) return false;
        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        return clipboard.SetClipboard(new ConsoleClipboardData(
            new[] { new ConsoleClipboardFormat(13, null, bytes) }));
    }
    public static string? ReadText(ConsoleClipboardData clipboard)
    {
        var text = clipboard.Formats.FirstOrDefault(format => format.Id == 13);
        if (text is null) return null;
        string value = Encoding.Unicode.GetString(text.Data.Span);
        return value.EndsWith('\0') ? value[..^1] : value;
    }
    public static void Subscribe(IConsoleClipboard clipboard, Action<string> receive)
    {
        clipboard.ClipboardReceived += data =>
        {
            if (ReadText(data) is { } text) receive(text);
        };
    }
}
```

#### 属性说明

<a id="api-0df8d1c1e7561dfc"></a>

##### IsAvailable · 属性

```csharp
bool IsAvailable { get; }
```

连接与剪贴板通道是否已就绪。

调用约定：RdpEnhanced 读取远端格式超过 30 秒未获应答时暂停剪贴板，视频和控制继续运行。迟到应答被丢弃后恢复读取最新内容；始终没有应答时需要重建连接。提交时以 SetClipboard 返回值为准。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 连接与剪贴板通道是否已就绪。 |
| 取值与默认 | true：可以提交内容；false：通道尚未就绪、已暂停或连接已断开。 |

#### 事件说明

<a id="api-15c6e3cb9d02431e"></a>

##### ClipboardAvailabilityChanged · 事件

```csharp
event Action<bool>? ClipboardAvailabilityChanged;
```

剪贴板通道变为可用或不可用时通知，参数为变化后的 IsAvailable。

<a id="api-f7b88fa31b137c0c"></a>

##### ClipboardReceived · 事件

```csharp
event Action<ConsoleClipboardData>? ClipboardReceived;
```

取得来宾剪贴板中受支持格式的数据时通知。

调用约定：从 Formats 中选择能处理的格式。空列表不一定表示来宾剪贴板为空，见 ConsoleClipboardData。 快速变化的内容可能被较新的内容替代，不保证逐次通知每次复制操作。 数据可在回调返回后继续使用，保存它不需要 Retain 或 Dispose。

#### 方法说明

<a id="api-59566efd7e5c5a5c"></a>

##### SetClipboard · 方法

```csharp
bool SetClipboard(ConsoleClipboardData clipboard);
```

把调用方准备好的剪贴板内容发送给来宾。

| 参数 | 说明 |
| --- | --- |
| `clipboard` | 同一份内容的文本、DIB 或 HTML 表示；具体格式布局见 ConsoleClipboardFormat。 |

返回：true 表示内容已复制并加入发送队列；通道不可用、数据格式无效、超出大小限制或队列无法接收时为 false。

调用约定：调用返回后可以复用输入数据。true 不表示来宾已经执行了粘贴；来宾程序何时使用内容由它自身决定。

<a id="api-53f629596085b5c9"></a>

### `IConsoleConnection` 接口

一条虚拟机桌面连接，提供画面、输入和其他受支持的交互能力。

| 项目 | 内容 |
| --- | --- |
| 类型 | 接口 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 连接 |
| 源文件 | ` Business/Connection/IConsoleConnection.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` IConsoleAudioInput? AudioInput { get; } `](#api-6d2ef8efd8158d4f) | 历史预留的音频输入接口，不属于当前功能范围。 |
| [` IConsoleAudioOutput? AudioOutput { get; } `](#api-b533977aa46e1380) | 接收来宾声音的预留接口。 |
| [` string BackendId { get; } `](#api-f5c787e1964eb9c0) | 当前连接使用的后端标识。 |
| [` IConsoleClipboard? Clipboard { get; } `](#api-83526b9be88d1f35) | 收发来宾剪贴板内容的接口。 |
| [` IConsoleInput? Input { get; } `](#api-6c119e309cccccce) | 向来宾发送键盘和鼠标输入的接口。 |
| [` ConsoleConnectionState State { get; } `](#api-7d05ee4a9643ebbf) | 当前连接所处的阶段。 |
| [` IConsoleVideoSource? Video { get; } `](#api-2b97e161fb617c87) | 接收来宾桌面画面的接口。 |

**事件**

| 成员 | 说明 |
| --- | --- |
| [` event Action? Connected `](#api-cd146ba118244997) | 底层连接建立时通知，不等同于用户登录完成。 |
| [` event Action<ConsoleDisconnectInfo>? Disconnected `](#api-f0481c70925621b5) | 连接结束并完成底层清理时通知，参数包含诊断码和可选的错误说明。 |
| [` event Action? LoginCompleted `](#api-941ce9a5aace08cd) | 后端报告用户登录完成时通知；并非所有后端都会报告此事件。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` Task DisconnectAsync() `](#api-81d62663c6d442aa) | 断开连接，并等待底层资源释放完成。 |
| [` void Start() `](#api-3ffa317c0bdfecc8) | 开始连接虚拟机，立即返回；连接结果由事件通知。 |

#### 详细说明

```csharp
public interface IConsoleConnection : IDisposable, IAsyncDisposable
```

调用约定：先订阅事件，再调用 Start。Connected 表示连接已建立；LoginCompleted 表示后端报告来宾登录完成； 剪贴板和音频等通道还各有自己的 IsAvailable。按实际需要等待相应状态。



Video、Input 等属性在此对象存活期间保持不变。null 表示这个后端没有提供该能力，等待连接也不会变为非 null。 例如 RdpBasic 的 Clipboard 为 null；RdpEnhanced 的 Clipboard 非 null，但需要等待通道就绪。



关闭时优先使用 await DisconnectAsync() 或 await DisposeAsync()，等待底层退出。 Dispose() 只发出停止请求。断开后若要重连，请新建对象。



不同事件可能在不同后台线程触发，不保证都在同一线程执行；界面更新需切换到界面线程，耗时工作应移出回调。 回调中不要同步等待本连接退出，也不要等待一个需要调用本连接的其他线程；这可能互相阻塞。 Disconnected 在底层清理完成后触发，可以在该通知中完成释放。各订阅者抛出的异常不会继续向外传播。

示例：

连接并监听状态，调用方决定何时结束。此例不显示画面；接收画面的示例见 IConsoleVideoSource。



```csharp
using ExHyperV.Services;
public static class ConnectionExample
{
    public static async Task RunAsync(Guid vmId, CancellationToken stop)
    {
        await using var connection = ConsoleService.CreateConnection(
            new ConsoleConnectionOptions { VmId = vmId },
            ConsoleService.RdpEnhancedBackendId);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Connected += () => Console.WriteLine("控制台已连接");
        connection.Disconnected += info =>
        {
            Console.WriteLine($"连接结束：{info.ErrorCode:X8} {info.Error}");
            ended.TrySetResult();
        };
        connection.Start();
        try { await ended.Task.WaitAsync(stop); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        await connection.DisconnectAsync();
    }
}
```

#### 属性说明

<a id="api-6d2ef8efd8158d4f"></a>

##### AudioInput · 属性

```csharp
IConsoleAudioInput? AudioInput { get; }
```

历史预留的音频输入接口，不属于当前功能范围。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 历史预留的音频输入接口，不属于当前功能范围。 |
| 取值与默认 | 当前内置后端均返回 null。 |

<a id="api-b533977aa46e1380"></a>

##### AudioOutput · 属性

```csharp
IConsoleAudioOutput? AudioOutput { get; }
```

接收来宾声音的预留接口。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 接收来宾声音的预留接口。 |
| 取值与默认 | 当前内置后端均返回 null，尚未实现。 |

<a id="api-f5c787e1964eb9c0"></a>

##### BackendId · 属性

```csharp
string BackendId { get; }
```

当前连接使用的后端标识。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前连接使用的后端标识。 |
| 取值与默认 | `RdpBasic` 或 `RdpEnhanced`；与 ConsoleService.GetBackends 返回的 BackendId 对应。 |

<a id="api-83526b9be88d1f35"></a>

##### Clipboard · 属性

```csharp
IConsoleClipboard? Clipboard { get; }
```

收发来宾剪贴板内容的接口。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 收发来宾剪贴板内容的接口。 |
| 取值与默认 | RdpBasic 为 null；RdpEnhanced 提供接口，通道就绪后 IsAvailable 为 true。 |

<a id="api-6c119e309cccccce"></a>

##### Input · 属性

```csharp
IConsoleInput? Input { get; }
```

向来宾发送键盘和鼠标输入的接口。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 向来宾发送键盘和鼠标输入的接口。 |
| 取值与默认 | 未提供输入能力时为 null。 |

<a id="api-7d05ee4a9643ebbf"></a>

##### State · 属性

```csharp
ConsoleConnectionState State { get; }
```

当前连接所处的阶段。

调用约定：Connected 表示底层连接建立；来宾登录和剪贴板通道就绪分别由相应事件通知。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前连接所处的阶段。 |

<a id="api-2b97e161fb617c87"></a>

##### Video · 属性

```csharp
IConsoleVideoSource? Video { get; }
```

接收来宾桌面画面的接口。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 接收来宾桌面画面的接口。 |
| 取值与默认 | 未提供视频能力时为 null。 |

#### 事件说明

<a id="api-cd146ba118244997"></a>

##### Connected · 事件

```csharp
event Action? Connected;
```

底层连接建立时通知，不等同于用户登录完成。

<a id="api-f0481c70925621b5"></a>

##### Disconnected · 事件

```csharp
event Action<ConsoleDisconnectInfo>? Disconnected;
```

连接结束并完成底层清理时通知，参数包含诊断码和可选的错误说明。

调用约定：仅对已经启动的连接通知。DisconnectAsync 等待的是底层清理完成，不等待此事件的订阅者执行完毕。

<a id="api-941ce9a5aace08cd"></a>

##### LoginCompleted · 事件

```csharp
event Action? LoginCompleted;
```

后端报告用户登录完成时通知；并非所有后端都会报告此事件。

#### 方法说明

<a id="api-81d62663c6d442aa"></a>

##### DisconnectAsync · 方法

```csharp
Task DisconnectAsync();
```

断开连接，并等待底层资源释放完成。

返回：在底层清理完成时结束的任务；不等待 Disconnected 事件处理完毕。

调用约定：可重复调用。尚未 Start 的对象也可以关闭，但关闭后不能再启动。

<a id="api-3ffa317c0bdfecc8"></a>

##### Start · 方法

```csharp
void Start();
```

开始连接虚拟机，立即返回；连接结果由事件通知。

调用约定：此方法不负责启动虚拟机，也不自动重连。每个对象只能调用一次。 启动后发生的连接失败通过 Disconnected 报告。

- 异常 `InvalidOperationException`：已经启动过，或对象已请求关闭。

<a id="api-ab4762a60b6762dc"></a>

### `IConsoleInput` 接口

向已经建立的控制台连接发送键盘和鼠标输入。

| 项目 | 内容 |
| --- | --- |
| 类型 | 接口 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 控制 / 键盘；业务层面 / 控制 / 鼠标 |
| 源文件 | ` Business/Keyboard/IConsoleInput.Keyboard.cs ` |

#### 成员概览

**方法**

| 成员 | 说明 |
| --- | --- |
| [` bool ReleaseAllInputs() `](#api-c8aa9f0307a29a7d) | 释放本连接记录的全部按键和鼠标按住状态，用于失焦或结束输入。 |
| [` bool SendClick(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.Left) `](#api-373036b38bdb9c13) | 一次提交一个鼠标按键的按下和释放，两项一起进入 Core 队列。 |
| [` bool SendKey(ushort scanCode, bool extended, bool down) `](#api-c68d794c15be61cb) | 发送一个按键的按下或释放事件。 |
| [` bool SendKeys(IReadOnlyList<ConsoleKeyEvent> events) `](#api-639a7aa82964b0c0) | 一次提交整组按键事件，按顺序发送。 |
| [` bool SendPause() `](#api-0a7414405be7d2d5) | 发送 Pause 键所需的特殊扫描码序列。 |
| [` bool SendPointer(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.None, bool down = false, short wheel = 0) `](#api-34b94c2751265ca5) | 发送一次鼠标移动、按下、释放或垂直滚轮输入。 |
| [` bool SynchronizeKeyboard(ConsoleKeyboardToggleState flags) `](#api-b63d7c0166496c27) | 让来宾的键盘锁定状态与调用方提供的状态一致。 |

#### 详细说明

```csharp
public partial interface IConsoleInput
```

调用约定：从连接的 Input 属性取得此接口。键盘使用扫描码并分别发送按下、释放；鼠标使用来宾桌面的像素坐标。



所有方法的 bool 返回值表示是否接受到发送队列，不能用来确认来宾已经处理了输入。 未连接、参数无效或队列无法接收时通常返回 false；SendKeys 的集合为 null 时抛出异常。

#### 方法说明

<a id="api-c8aa9f0307a29a7d"></a>

##### ReleaseAllInputs · 方法

```csharp
bool ReleaseAllInputs();
```

功能归属：业务层面 / 控制 / 键盘。

释放本连接记录的全部按键和鼠标按住状态，用于失焦或结束输入。

返回：连接可用且 Core 已接收释放请求时为 true；未连接或正在断开时为 false。

调用约定：清空托管待重试队列，为底层已接受的按下安排释放；底层拒绝释放时保留重试。 已进入底层队列的输入不会撤回，仍可能先于释放到达来宾。重复调用安全，只依据本连接记录的按住状态发送释放， 不查询来宾当前键盘状态，也不能保证断连后的释放送达。

<a id="api-373036b38bdb9c13"></a>

##### SendClick · 方法

```csharp
bool SendClick(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.Left);
```

功能归属：业务层面 / 控制 / 鼠标。

一次提交一个鼠标按键的按下和释放，两项一起进入 Core 队列。

| 参数 | 说明 |
| --- | --- |
| `x` | 最近桌面范围内的横坐标，单位为像素。 |
| `y` | 最近桌面范围内的纵坐标，单位为像素。 |
| `button` | Left、Right 或 Middle，默认左键。 |

返回：true 表示已接受完整点击；false 时本次按下与释放均未入队。底层拥塞时 Core 继续发送；断连仍可能中止尚未发出的事件。

<a id="api-c68d794c15be61cb"></a>

##### SendKey · 方法

```csharp
bool SendKey(ushort scanCode, bool extended, bool down);
```

功能归属：业务层面 / 控制 / 键盘。

发送一个按键的按下或释放事件。

| 参数 | 说明 |
| --- | --- |
| `scanCode` | PC 键盘扫描码，1～255。例如 A 键为 0x1E；它表示按键位置，实际字符由来宾键盘布局和修饰键决定。 |
| `extended` | 扫描码带 E0 前缀时为 true，例如右 Ctrl 使用 scanCode=0x1D、extended=true；普通左 Ctrl 为 false。Pause 请用 SendPause。 |
| `down` | true 表示按下，false 表示释放。同一个按键的两次事件应使用相同扫描码和 extended。 |

返回：Core 接受输入时为 true；未连接、参数无效或暂不能接受新的按下事件时为 false。

调用约定：连接存续期间，有效的释放事件会在底层队列拒绝时保留重试；后来的按下事件不会越过待发事件。返回 true 不等于来宾已处理。 失去焦点时调用 ReleaseAllInputs；断连后无法保证释放到达来宾，新连接不重放旧输入。整组按键可用 SendKeys 一次提交。

示例：

发送 A 键的按下和释放。这里只演示按键位置，不保证产生大写字母 A。



```csharp
using ExHyperV.Services;
public static class KeyboardExample
{
    public static bool PressA(IConsoleInput input)
    {
        return input.SendKeys([new(0x1E, false, true), new(0x1E, false, false)]);
    }
}
```

<a id="api-639a7aa82964b0c0"></a>

##### SendKeys · 方法

```csharp
bool SendKeys(IReadOnlyList<ConsoleKeyEvent> events);
```

功能归属：业务层面 / 控制 / 键盘。

一次提交整组按键事件，按顺序发送。

| 参数 | 说明 |
| --- | --- |
| `events` | 1～128 项扫描码事件，调用时复制；所有事件须有效。调用方应提供所需的释放事件。 |

返回：true 表示整组进入 Core 队列；false 时本组没有任何事件入队。底层暂时无法接收时由 Core 继续发送，不需重交整组。

调用约定：这里的整体性指 Core 接受请求，不是来宾原子执行；断连可能发生在序列中途。 托管待发序列尚未排空时拒绝新的按下和移动，仍接收释放。 失焦调用 ReleaseAllInputs 会取消尚未交给底层的事件，并为底层已接受的按下安排释放。

- 异常 `ArgumentNullException`：events 为 null。

<a id="api-0a7414405be7d2d5"></a>

##### SendPause · 方法

```csharp
bool SendPause();
```

功能归属：业务层面 / 控制 / 键盘。

发送 Pause 键所需的特殊扫描码序列。

返回：序列被接受到发送队列时为 true；未连接或队列无法接收时为 false。

调用约定：一次调用发送完整的 Pause 特殊扫描码序列。

<a id="api-34b94c2751265ca5"></a>

##### SendPointer · 方法

```csharp
bool SendPointer(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.None, bool down = false, short wheel = 0);
```

功能归属：业务层面 / 控制 / 鼠标。

发送一次鼠标移动、按下、释放或垂直滚轮输入。

| 参数 | 说明 |
| --- | --- |
| `x` | 相对来宾桌面左上角的横坐标，单位为像素，必须小于最近一帧的 Width。 |
| `y` | 相对来宾桌面左上角的纵坐标，单位为像素，必须小于最近一帧的 Height。 |
| `button` | 本次按下或释放的一个按键；移动和滚轮使用 None。 |
| `down` | true 为按下，false 为释放；button 为 None 时必须为 false。 |
| `wheel` | 垂直滚轮增量，-255～255。通常一格为 120，正值向前滚、负值向后滚；非零时 button 必须为 None，down 为 false。 |

返回：Core 接受输入时为 true。尚未收到首帧、按下或移动坐标越界、参数组合无效、未连接或暂不能接受新输入时为 false。有效释放事件在底层拒绝时保留重试。

调用约定：移动时只传 x、y；点击时在同一位置先 down=true 再 down=false；拖动时先按下，再连续发送移动，最后释放。



释放坐标越界时自动限制到最新桌面边界，分辨率缩小不会丢掉释放；其他事件仍需提交有效坐标。失焦使用 ReleaseAllInputs，普通点击可用 SendClick 一次提交。



显示区域经过缩放时，先减去画面的显示起点，再按来宾尺寸与显示尺寸的比例换算坐标。 黑边和画面外的输入不应发送。滚轮事件也需要合法的 x、y；较大的累计增量拆成多次发送。



滚轮单位沿用 Windows 的 WHEEL_DELTA，120 表示通常的一格，不表示滚动 120 行。 来宾应用决定实际滚动多少内容。参见 [WM_MOUSEWHEEL](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel)。

示例：

在一个已经换算好的来宾坐标上点击左键。



```csharp
using ExHyperV.Services;
public static class PointerExample
{
    public static bool Click(IConsoleInput input, ushort guestX, ushort guestY)
    {
        return input.SendClick(guestX, guestY);
    }
}
```

<a id="api-b63d7c0166496c27"></a>

##### SynchronizeKeyboard · 方法

```csharp
bool SynchronizeKeyboard(ConsoleKeyboardToggleState flags);
```

功能归属：业务层面 / 控制 / 键盘。

让来宾的键盘锁定状态与调用方提供的状态一致。

| 参数 | 说明 |
| --- | --- |
| `flags` | 当前开启的锁定键，可用 \| 组合。None 表示全部关闭。 |

返回：同步请求被接受到发送队列时为 true；未连接、标志无效或队列无法接收时为 false。

调用约定：适合在控制台获得输入焦点时调用，例如传入 NumLock | CapsLock。 Core 不读取宿主键盘状态，调用方自行取得并传入当前值。

<a id="api-f1ecb47435b21fa3"></a>

### `IConsoleVideoSource` 接口

从同一连接获取单帧或持续接收画面，并在后端支持时请求调整分辨率。

| 项目 | 内容 |
| --- | --- |
| 类型 | 接口 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 视频 |
| 源文件 | ` Business/Video/IConsoleVideoSource.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` bool SupportsResize { get; } `](#api-d113bc976ef6bc1b) | 后端是否提供桌面分辨率和缩放调整能力。 |

**事件**

| 成员 | 说明 |
| --- | --- |
| [` event Action<ConsoleFrame>? FrameReceived `](#api-87b9df2906679e18) | 收到新画面时通知；参数中的帧在本次回调期间可用。 |
| [` event Action<uint>? ResizeRejected `](#api-f2f5ecb7b595c286) | 已入队的尺寸调整请求在显示通道发送失败时通知。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` ConsoleFrame? CaptureFrame() `](#api-af19990c766a5aa8) | 取得此连接最近生成的完整画面，返回一份由调用方负责释放的帧租约。 |
| [` bool Resize(int width, int height, uint desktopScale = 100) `](#api-d3b8251b0e40130c) | 请求调整来宾桌面的分辨率和缩放比例。 |

#### 详细说明

```csharp
public interface IConsoleVideoSource
```

调用约定：从连接的 Video 属性取得此接口，在 Start 前订阅 FrameReceived。 每帧包含完整画面，可整帧绘制，也可根据 Damage 局部刷新。



画面以 BGRA32 原始像素交付，包含远端光标；按画面更新触发事件，帧率随更新频率变化。



回调返回后，借出的帧会被释放。在回调内完成读取即可；需要延后处理时，用 Retain 保留并在用完后 Dispose。 需要与连接完全独立的字节副本时用 CopyPixels。

示例：

下面同步消费一帧，避免额外复制。consume 应快速读取或绘制，不能在返回后继续使用传入的帧。



```csharp
using ExHyperV.Services;
public static class VideoExample
{
    public static void Subscribe(IConsoleConnection connection, Action<ConsoleFrame> consume)
    {
        if (connection.Video is { } video)
            video.FrameReceived += frame => consume(frame);
    }
    public static bool Capture(IConsoleConnection connection, Action<ConsoleFrame> consume)
    {
        using var frame = connection.Video?.CaptureFrame();
        if (frame is null) return false;
        consume(frame); // 完整画面；使用结束自动释放，不等待下一次 FrameReceived。
        return true;
    }
}
```

#### 属性说明

<a id="api-d113bc976ef6bc1b"></a>

##### SupportsResize · 属性

```csharp
bool SupportsResize { get; }
```

后端是否提供桌面分辨率和缩放调整能力。

调用约定：此值表示功能支持；连接及显示通道就绪后才能提交 Resize 请求。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 后端是否提供桌面分辨率和缩放调整能力。 |
| 取值与默认 | RdpEnhanced 为 true，RdpBasic 为 false。 |

#### 事件说明

<a id="api-87b9df2906679e18"></a>

##### FrameReceived · 事件

```csharp
event Action<ConsoleFrame>? FrameReceived;
```

收到新画面时通知；参数中的帧在本次回调期间可用。

调用约定：回调通常在后台线程执行。可直接读取 frame.Pixels；要交给其他线程，先调用 frame.Retain() 并负责释放保留的帧。 显示缩放由调用方处理，帧中的尺寸始终是来宾桌面像素。

<a id="api-f2f5ecb7b595c286"></a>

##### ResizeRejected · 事件

```csharp
event Action<uint>? ResizeRejected;
```

已入队的尺寸调整请求在显示通道发送失败时通知。

调用约定：参数是后端返回的诊断码，可与连接的 BackendId 一起记录。 当前 RDP 后端报告的是本地通道发送结果，不是来宾拒绝调整的回执。 连续失败可能合并通知，事件也不携带原请求尺寸；实际分辨率请查看后续帧的 Width 和 Height。

#### 方法说明

<a id="api-af19990c766a5aa8"></a>

##### CaptureFrame · 方法

```csharp
ConsoleFrame? CaptureFrame();
```

取得此连接最近生成的完整画面，返回一份由调用方负责释放的帧租约。

返回：完整 BGRA32 画面的独立租约，包含远端光标；尚未连接、尚无画面或已请求断开时返回 null。

调用约定：立即返回此连接缓存的最近画面，静止桌面也可获取。增强会话取增强桌面，基本会话取基本控制台。



此租约不复制像素，取得后像素不再变化，连接关闭后仍可读取。用 using 或 finally 及时 Dispose； 长期保留多张帧可能耗尽缓冲池并暂停新帧输出。保存独立副本时可调用 CopyPixels，再释放租约。



画面可能尚未被界面绘制。截图读取完整 Pixels，尺寸和格式保持原样；Sequence 仅在本连接内有效。

<a id="api-d3b8251b0e40130c"></a>

##### Resize · 方法

```csharp
bool Resize(int width, int height, uint desktopScale = 100);
```

请求调整来宾桌面的分辨率和缩放比例。

| 参数 | 说明 |
| --- | --- |
| `width` | 桌面宽度，200～8192 像素；当前 RDP 实现会将奇数向下取为偶数。 |
| `height` | 桌面高度，200～8192 像素。 |
| `desktopScale` | 来宾桌面缩放百分比，100～500，默认 100；不是调用方窗口的缩放比例。 |

返回：true 表示请求已加入发送队列。参数无效、未连接、不支持调整或队列无法接收时返回 false。

调用约定：宽 &#215; 高 &#215; 4 不得超过 64 MiB 减 64 字节。该限制在宽度取偶数前检查。



返回 true 不代表尺寸已经改变。显示通道发送失败时可收到 ResizeRejected；没有失败通知也不表示来宾已应用请求。 显示端应按后续 ConsoleFrame 的实际尺寸创建或调整图像。

<a id="api-b3d6436c4464b7b7"></a>

### `IsoBuilderService` 类

使用 Windows IMAPI 将目录构建为包含 ISO 9660 与 UDF 文件系统的光盘映像。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / ISO 构建 |
| 源文件 | ` Management/Tools/Iso/IsoBuilderService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static void BuildUdfIso(string sourceDirectory, string targetIsoPath, string volumeLabel) `](#api-458525b357c0a06f) | 将源目录内容写成 ISO 9660/UDF 映像，当前线程等待写入完成。 |

#### 详细说明

```csharp
public static class IsoBuilderService
```

#### 静态方法说明

<a id="api-458525b357c0a06f"></a>

##### BuildUdfIso · 方法

```csharp
public static void BuildUdfIso(string sourceDirectory, string targetIsoPath, string volumeLabel);
```

将源目录内容写成 ISO 9660/UDF 映像，当前线程等待写入完成。

| 参数 | 说明 |
| --- | --- |
| `sourceDirectory` | 包含要写入光盘内容的宿主目录。 |
| `targetIsoPath` | 目标 ISO 文件路径。 |
| `volumeLabel` | 光盘卷标。Core 保留字母、数字、下划线和连字符，其他字符替换为下划线，最多 32 个字符；空值使用 NewISO。 |

调用约定：输出只构建文件，不挂载映像。已有目标文件会被覆盖并截断至新映像长度；调用方负责选择输出路径。数据流提前结束时抛出异常，失败时可能留下部分文件。源目录内容放在映像根目录下。

<a id="api-9e6f4f1e1c48db2a"></a>

### `MmioCheckResultType` 枚举

虚拟机 MMIO 空间与当前主机分配要求的比较结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / MMIO |
| 源文件 | ` Management/VirtualMachines/Mmio/MmioCheckResultType.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` NeedsExpansion = 1 `](#api-c5851f3514586a28) | 当前空间不足，需要扩展。 |
| [` Ok = 0 `](#api-75becbee8d8495de) | 当前空间达到要求。 |

#### 详细说明

```csharp
public enum MmioCheckResultType
```

#### 枚举值说明

<a id="api-c5851f3514586a28"></a>

##### NeedsExpansion · 枚举值

```csharp
NeedsExpansion = 1
```

当前空间不足，需要扩展。

<a id="api-75becbee8d8495de"></a>

##### Ok · 枚举值

```csharp
Ok = 0
```

当前空间达到要求。

<a id="api-c531bbc4afdd18f2"></a>

### `PcieAssignmentDecision` record 类

对一次特定 PCIe 确认请求的决定，不能重用到其他步骤。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/PcieAssignmentSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public PcieAssignmentDecision(Guid RequestId, bool Approve) `](#api-40eda60b9b12b83d) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool Approve { get; init; } `](#api-146282bf9a69e0c8) | 是否同意该请求；伴随设备步骤中拒绝仅跳过伴随设备。 |
| [` public Guid RequestId { get; init; } `](#api-af047e47f0858946) | 从待确认状态读取的请求身份。 |

#### 详细说明

```csharp
public sealed record PcieAssignmentDecision(Guid RequestId, bool Approve)
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 从待确认状态读取的请求身份。 |
| `Approve` | 是否同意该请求；伴随设备步骤中拒绝仅跳过伴随设备。 |

#### 构造函数说明

<a id="api-40eda60b9b12b83d"></a>

##### PcieAssignmentDecision · 主构造器

```csharp
public PcieAssignmentDecision(Guid RequestId, bool Approve);
```

| 参数 | 说明 |
| --- | --- |
| `RequestId` | 从待确认状态读取的请求身份。 |
| `Approve` | 是否同意该请求；伴随设备步骤中拒绝仅跳过伴随设备。 |

#### 属性说明

<a id="api-146282bf9a69e0c8"></a>

##### Approve · record 属性

```csharp
public bool Approve { get; init; }
```

是否同意该请求；伴随设备步骤中拒绝仅跳过伴随设备。

<a id="api-af047e47f0858946"></a>

##### RequestId · record 属性

```csharp
public Guid RequestId { get; init; }
```

从待确认状态读取的请求身份。

<a id="api-d16e33c86a21492c"></a>

### `PcieAssignmentError` 枚举

PCIe 分配流程失败所在的阶段；具体细节见状态消息。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/PcieAssignmentSession.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Execution = 4 `](#api-b8f1eb7d45c554d0) | 设备转移或伴随操作失败。 |
| [` MmioCheck = 1 `](#api-49662c9ad3f69aa8) | 无法检查 MMIO 配置。 |
| [` MmioUpdate = 2 `](#api-03978ea90db8a3fa) | MMIO 扩展未成功。 |
| [` None = 0 `](#api-6e9d134748eb994b) | 没有记录失败阶段。 |
| [` SystemDisk = 3 `](#api-09f80251ece87af8) | 存储控制器关联系统或启动磁盘，禁止继续。 |

#### 详细说明

```csharp
public enum PcieAssignmentError
```

#### 枚举值说明

<a id="api-b8f1eb7d45c554d0"></a>

##### Execution · 枚举值

```csharp
Execution = 4
```

设备转移或伴随操作失败。

<a id="api-49662c9ad3f69aa8"></a>

##### MmioCheck · 枚举值

```csharp
MmioCheck = 1
```

无法检查 MMIO 配置。

<a id="api-03978ea90db8a3fa"></a>

##### MmioUpdate · 枚举值

```csharp
MmioUpdate = 2
```

MMIO 扩展未成功。

<a id="api-6e9d134748eb994b"></a>

##### None · 枚举值

```csharp
None = 0
```

没有记录失败阶段。

<a id="api-09f80251ece87af8"></a>

##### SystemDisk · 枚举值

```csharp
SystemDisk = 3
```

存储控制器关联系统或启动磁盘，禁止继续。

<a id="api-a51fd65e89867236"></a>

### `PcieAssignmentKind` 枚举

PCIe 设备当前归属或分配目标的种类。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/PcieAssignmentTarget.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Host = 0 `](#api-4c6aa9bf769e26df) | 设备由宿主使用。 |
| [` Unassigned = 1 `](#api-997c8e50ba24daac) | 设备已从宿主卸除，尚未分配给虚拟机；只能表示当前状态。 |
| [` VirtualMachine = 2 `](#api-03b823b9f400d41b) | 设备属于指定 GUID 的虚拟机。 |

#### 详细说明

```csharp
public enum PcieAssignmentKind
```

#### 枚举值说明

<a id="api-4c6aa9bf769e26df"></a>

##### Host · 枚举值

```csharp
Host = 0
```

设备由宿主使用。

<a id="api-997c8e50ba24daac"></a>

##### Unassigned · 枚举值

```csharp
Unassigned = 1
```

设备已从宿主卸除，尚未分配给虚拟机；只能表示当前状态。

<a id="api-03b823b9f400d41b"></a>

##### VirtualMachine · 枚举值

```csharp
VirtualMachine = 2
```

设备属于指定 GUID 的虚拟机。

<a id="api-0bba6ca7be99b19a"></a>

### `PcieAssignmentSession` 类

保存一次 PCIe 分配的检查、确认和执行状态，供界面或其他调用方逐步驱动。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/PcieAssignmentSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public PcieAssignmentSession(PcieDeviceInfo device, IEnumerable<PcieDeviceInfo> devices, PcieAssignmentTarget target) `](#api-335da65658106f0d) | 为指定设备及目标创建分配流程，复制当前设备列表用于规划确认。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public PcieAssignmentState State { get; } = new(PcieAssignmentStep.Ready) `](#api-45c0a5ccdd74afe1) | 最近一次推进产生的状态快照。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public async Task<PcieAssignmentState> AdvanceAsync(PcieAssignmentDecision? decision = null) `](#api-e77b48657194947b) | 推进到下一次需要确认或结束的位置，必要时执行已获决定的操作。 |

#### 详细说明

```csharp
public sealed class PcieAssignmentSession
```

调用约定：同一实例不能并发推进。创建实例不执行分配；推进可修改 MMIO、关闭 VM、脱机磁盘和转移设备，不是可整体回滚的事务。等待用户确认时不持有宿主资源锁；获准执行时，在控制器及关联磁盘锁内重新校验设备身份和系统盘标志，再连续执行脱机、转移及失败恢复。失败恢复以本次执行前的联机、只读状态为准，不保留期间外部状态修改；不能确认同一设备时停止恢复并报告失败。

#### 构造函数说明

<a id="api-335da65658106f0d"></a>

##### PcieAssignmentSession · 构造器

```csharp
public PcieAssignmentSession(PcieDeviceInfo device, IEnumerable<PcieDeviceInfo> devices, PcieAssignmentTarget target);
```

为指定设备及目标创建分配流程，复制当前设备列表用于规划确认。

| 参数 | 说明 |
| --- | --- |
| `device` | 选中的设备及当前归属。 |
| `devices` | 用于识别伴随设备、最后一块 GPU 等条件的当前设备快照。 |
| `target` | 目标宿主或虚拟机；未分配状态不能作为目标。 |

- 异常 `ArgumentNullException`：目标为 null。
- 异常 `ArgumentException`：目标为 Unassigned。

#### 属性说明

<a id="api-45c0a5ccdd74afe1"></a>

##### State · 属性

```csharp
public PcieAssignmentState State { get; } = new(PcieAssignmentStep.Ready);
```

最近一次推进产生的状态快照。

#### 方法说明

<a id="api-e77b48657194947b"></a>

##### AdvanceAsync · 方法

```csharp
public async Task<PcieAssignmentState> AdvanceAsync(PcieAssignmentDecision? decision = null);
```

推进到下一次需要确认或结束的位置，必要时执行已获决定的操作。

| 参数 | 说明 |
| --- | --- |
| `decision` | 包含当前 RequestId 的用户决定；ConfirmCompanions 的拒绝只跳过伴随设备，其他拒绝会取消流程。启动或只读取等待状态时传 null。 |

返回：最新状态；已结束时仍返回原状态，等待决定但未传 decision 时保持等待。

调用约定：MMIO 准备仅在本步骤获准后执行，可能强制关闭 VM、启停探测并修改配置；之后取消其他步骤不会撤销已完成的准备。拒绝过期或重复决定不会改变当前状态。取消或失败后按 RefreshRequired 重新查询实际状态，并检查 Steps、Failure 和 OutcomeUnknown。任一设备分配结果未知时，不再执行后续伴随设备。

- 异常 `InvalidOperationException`：同一实例上另一次推进尚未完成，或决定不属于当前待确认请求。

示例：

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Services;

public static class PcieAssignmentExample
{
    // decide 由宿主提供：展示当前状态并取得用户决定，不能默认全部同意。
    // 该方法会执行实际分配，调用前须具备权限并告知用户可能强制关闭 VM。
    public static async Task<PcieAssignmentState> AssignAsync(
        PcieDeviceInfo device,
        IEnumerable<PcieDeviceInfo> snapshot,
        PcieAssignmentTarget target,
        Func<PcieAssignmentState, Task<bool>> decide)
    {
        var session = new PcieAssignmentSession(device, snapshot, target);
        while (!session.State.IsFinished)
        {
            var pending = session.State;
            PcieAssignmentDecision? answer = pending.RequiresDecision
                ? new(pending.RequestId!.Value, await decide(pending))
                : null;
            await session.AdvanceAsync(answer);
        }
        // 调用方仍需按 Error 和 RefreshRequired 处理失败及部分变更。
        return session.State;
    }
}
```

<a id="api-56f294436caf98bb"></a>

### `PcieAssignmentState` record 类

PCIe 分配流程的不可变状态快照。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/PcieAssignmentSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public PcieAssignmentState(PcieAssignmentStep Step, string Title = "", string Message = "", PcieAssignmentError Error = PcieAssignmentError.None, bool RefreshRequired = false, Guid? RequestId = null) `](#api-36ac9b8b6943fefc) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public PcieAssignmentError Error { get; init; } `](#api-5cb2c7fcdff365bc) | 失败阶段；正常状态为 None。 |
| [` public ApiFailure? Failure { get; init; } `](#api-d056b5caf0db384e) | 需要优先处理的失败；结果未知优先于清理失败，其余取首个失败。 |
| [` public bool IsFinished { get; } `](#api-8e56ceb906e1d0e8) | 当前是否已完成、取消或失败，不能再推进业务步骤。 |
| [` public string Message { get; init; } `](#api-3dfb56e1f551dcce) | 本地化说明或失败详情。 |
| [` public bool OutcomeUnknown { get; } `](#api-b45094a857212b31) | 任一步骤的底层结果尚未确认；不得直接重试分配或反向清理，应先核实实际状态。 |
| [` public bool RefreshRequired { get; init; } `](#api-b70b11a444b88017) | 是否应重新读取设备/VM 状态；取消或失败也可能要求刷新。 |
| [` public Guid? RequestId { get; init; } `](#api-e7353760b2fe1986) | 当前确认请求的唯一身份；非待确认状态为 null。 |
| [` public bool RequiresDecision { get; } `](#api-5c29af7c5b50151e) | 当前是否正在等待调用方提交用户决定。 |
| [` public PcieAssignmentStep Step { get; init; } `](#api-5832db15cad30135) | 当前步骤。 |
| [` public IReadOnlyList<OperationStepResult> Steps { get; init; } = Array.Empty<OperationStepResult>() `](#api-9e1be126d0de1eac) | 本次流程已执行步骤的只读快照；ResourceId 区分主设备、伴随设备和磁盘，Failure 保留底层类别、来源及错误码。 |
| [` public string Title { get; init; } `](#api-eb5abfa1fdef18df) | 适合展示的本地化标题。 |

#### 详细说明

```csharp
public sealed record PcieAssignmentState(
    PcieAssignmentStep Step, string Title = "", string Message = "",
    PcieAssignmentError Error = PcieAssignmentError.None, bool RefreshRequired = false, Guid? RequestId = null)
```

| 参数 | 说明 |
| --- | --- |
| `Step` | 当前步骤。 |
| `Title` | 适合展示的本地化标题。 |
| `Message` | 本地化说明或失败详情。 |
| `Error` | 失败阶段；正常状态为 None。 |
| `RefreshRequired` | 是否应重新读取设备/VM 状态；取消或失败也可能要求刷新。 |
| `RequestId` | 当前确认请求的唯一身份；非待确认状态为 null。 |

#### 构造函数说明

<a id="api-36ac9b8b6943fefc"></a>

##### PcieAssignmentState · 主构造器

```csharp
public PcieAssignmentState(PcieAssignmentStep Step, string Title = "", string Message = "", PcieAssignmentError Error = PcieAssignmentError.None, bool RefreshRequired = false, Guid? RequestId = null);
```

| 参数 | 说明 |
| --- | --- |
| `Step` | 当前步骤。 |
| `Title` | 适合展示的本地化标题。 |
| `Message` | 本地化说明或失败详情。 |
| `Error` | 失败阶段；正常状态为 None。 |
| `RefreshRequired` | 是否应重新读取设备/VM 状态；取消或失败也可能要求刷新。 |
| `RequestId` | 当前确认请求的唯一身份；非待确认状态为 null。 |

#### 属性说明

<a id="api-5cb2c7fcdff365bc"></a>

##### Error · record 属性

```csharp
public PcieAssignmentError Error { get; init; }
```

失败阶段；正常状态为 None。

<a id="api-d056b5caf0db384e"></a>

##### Failure · 属性

```csharp
public ApiFailure? Failure { get; init; }
```

需要优先处理的失败；结果未知优先于清理失败，其余取首个失败。全部失败详情仍保留在 Steps 中。

<a id="api-8e56ceb906e1d0e8"></a>

##### IsFinished · 属性

```csharp
public bool IsFinished { get; }
```

当前是否已完成、取消或失败，不能再推进业务步骤。

<a id="api-3dfb56e1f551dcce"></a>

##### Message · record 属性

```csharp
public string Message { get; init; }
```

本地化说明或失败详情。

<a id="api-b45094a857212b31"></a>

##### OutcomeUnknown · 属性

```csharp
public bool OutcomeUnknown { get; }
```

任一步骤的底层结果尚未确认；不得直接重试分配或反向清理，应先核实实际状态。

<a id="api-b70b11a444b88017"></a>

##### RefreshRequired · record 属性

```csharp
public bool RefreshRequired { get; init; }
```

是否应重新读取设备/VM 状态；取消或失败也可能要求刷新。

<a id="api-e7353760b2fe1986"></a>

##### RequestId · record 属性

```csharp
public Guid? RequestId { get; init; }
```

当前确认请求的唯一身份；非待确认状态为 null。

<a id="api-5c29af7c5b50151e"></a>

##### RequiresDecision · 属性

```csharp
public bool RequiresDecision { get; }
```

当前是否正在等待调用方提交用户决定。

<a id="api-5832db15cad30135"></a>

##### Step · record 属性

```csharp
public PcieAssignmentStep Step { get; init; }
```

当前步骤。

<a id="api-9e1be126d0de1eac"></a>

##### Steps · 属性

```csharp
public IReadOnlyList<OperationStepResult> Steps { get; init; } = Array.Empty<OperationStepResult>();
```

本次流程已执行步骤的只读快照；ResourceId 区分主设备、伴随设备和磁盘，Failure 保留底层类别、来源及错误码。

调用约定：步骤名为 CheckMmio、ExpandMmio、ReadControllerDisks、AssignDevice、ReadReturnedDisks、RestoreDiskOnline 或 UnhandledFailure。未执行的步骤不列出，不以空列表推断没有副作用。

<a id="api-eb5abfa1fdef18df"></a>

##### Title · record 属性

```csharp
public string Title { get; init; }
```

适合展示的本地化标题。

<a id="api-aeb7200a2284716c"></a>

### `PcieAssignmentStep` 枚举

PCIe 分配流程当前执行或确认步骤。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/PcieAssignmentSession.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Cancelled = 5 `](#api-a19696e52bc45ae1) | 用户取消了需要确认的操作。 |
| [` Completed = 4 `](#api-07b6e90bf39b5db5) | 主分配流程完成。 |
| [` ConfirmCompanions = 3 `](#api-e3ffdd4c3ff2ad1d) | 等待选择是否连同同卡伴随设备一起分配。 |
| [` ConfirmLastGpu = 1 `](#api-882b471bf27e70d5) | 等待确认是否移出宿主最后一块 GPU。 |
| [` ConfirmMmio = 7 `](#api-bfcf096559b00e95) | 等待确认关闭目标虚拟机、按需启停探测并修改 MMIO 配置。 |
| [` ConfirmOfflineDisks = 2 `](#api-33965c43de094e4a) | 等待确认将控制器关联的数据盘脱机。 |
| [` Failed = 6 `](#api-bf1d3e2d8854a697) | 检查或执行失败，可能已经产生部分变更。 |
| [` Ready = 0 `](#api-2e368425096a7a22) | 尚未推进的起始状态。 |

#### 详细说明

```csharp
public enum PcieAssignmentStep
```

#### 枚举值说明

<a id="api-a19696e52bc45ae1"></a>

##### Cancelled · 枚举值

```csharp
Cancelled = 5
```

用户取消了需要确认的操作。

<a id="api-07b6e90bf39b5db5"></a>

##### Completed · 枚举值

```csharp
Completed = 4
```

主分配流程完成。

<a id="api-e3ffdd4c3ff2ad1d"></a>

##### ConfirmCompanions · 枚举值

```csharp
ConfirmCompanions = 3
```

等待选择是否连同同卡伴随设备一起分配。

<a id="api-882b471bf27e70d5"></a>

##### ConfirmLastGpu · 枚举值

```csharp
ConfirmLastGpu = 1
```

等待确认是否移出宿主最后一块 GPU。

<a id="api-bfcf096559b00e95"></a>

##### ConfirmMmio · 枚举值

```csharp
ConfirmMmio = 7
```

等待确认关闭目标虚拟机、按需启停探测并修改 MMIO 配置。

<a id="api-33965c43de094e4a"></a>

##### ConfirmOfflineDisks · 枚举值

```csharp
ConfirmOfflineDisks = 2
```

等待确认将控制器关联的数据盘脱机。

<a id="api-bf1d3e2d8854a697"></a>

##### Failed · 枚举值

```csharp
Failed = 6
```

检查或执行失败，可能已经产生部分变更。

<a id="api-2e368425096a7a22"></a>

##### Ready · 枚举值

```csharp
Ready = 0
```

尚未推进的起始状态。

<a id="api-dbd5c750f9191760"></a>

### `PcieAssignmentTarget` record 类

PCIe 设备的归属身份，不使用本地化文字或内部字符串标记表示宿主与未分配状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/PcieAssignmentTarget.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public static PcieAssignmentTarget Host { get; } = new(PcieAssignmentKind.Host) `](#api-c38bfc4ed1fb8d54) | 表示设备由宿主使用，也可用于请求将设备归还宿主。 |
| [` public PcieAssignmentKind Kind { get; } `](#api-0411059344195213) | 宿主、未分配或虚拟机。 |
| [` public static PcieAssignmentTarget Unassigned { get; } = new(PcieAssignmentKind.Unassigned) `](#api-ea25c249ab09e966) | 表示设备已从宿主卸除且尚未分配；不能作为分配操作的目标。 |
| [` public Guid? VirtualMachineId { get; } `](#api-37c609c006e9b4e0) | 虚拟机稳定 GUID；只有 Kind 为 VirtualMachine 时有值，操作不按显示名称重新定位。 |
| [` public string? VirtualMachineName { get; } `](#api-7a133ddad76b19d6) | 虚拟机显示名称快照，仅用于呈现；重命名不改变目标身份。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static PcieAssignmentTarget ForVirtualMachine(Guid virtualMachineId, string? displayName = null) `](#api-13915b51bdd1f128) | 按稳定 GUID 创建虚拟机归属或目标，显示名称不参与定位。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public bool Equals(PcieAssignmentTarget? other) `](#api-2fe1beb64a38b293) | 比较归属身份；虚拟机显示名称变化不改变相等性。 |
| [` public override int GetHashCode() `](#api-c6663a5aefc7c2c3) | 按归属种类和虚拟机 GUID 生成哈希值，不包含显示名称。 |

#### 详细说明

```csharp
public sealed record PcieAssignmentTarget
```

调用约定：当前分配操作支持归还宿主或转移至虚拟机。Unassigned 用于查询结果，不能作为分配操作的目标。

#### 属性说明

<a id="api-c38bfc4ed1fb8d54"></a>

##### Host · 属性

```csharp
public static PcieAssignmentTarget Host { get; } = new(PcieAssignmentKind.Host);
```

表示设备由宿主使用，也可用于请求将设备归还宿主。

<a id="api-0411059344195213"></a>

##### Kind · 属性

```csharp
public PcieAssignmentKind Kind { get; }
```

宿主、未分配或虚拟机。

<a id="api-ea25c249ab09e966"></a>

##### Unassigned · 属性

```csharp
public static PcieAssignmentTarget Unassigned { get; } = new(PcieAssignmentKind.Unassigned);
```

表示设备已从宿主卸除且尚未分配；不能作为分配操作的目标。

<a id="api-37c609c006e9b4e0"></a>

##### VirtualMachineId · 属性

```csharp
public Guid? VirtualMachineId { get; }
```

虚拟机稳定 GUID；只有 Kind 为 VirtualMachine 时有值，操作不按显示名称重新定位。

<a id="api-7a133ddad76b19d6"></a>

##### VirtualMachineName · 属性

```csharp
public string? VirtualMachineName { get; }
```

虚拟机显示名称快照，仅用于呈现；重命名不改变目标身份。

#### 静态方法说明

<a id="api-13915b51bdd1f128"></a>

##### ForVirtualMachine · 方法

```csharp
public static PcieAssignmentTarget ForVirtualMachine(Guid virtualMachineId, string? displayName = null);
```

按稳定 GUID 创建虚拟机归属或目标，显示名称不参与定位。

| 参数 | 说明 |
| --- | --- |
| `virtualMachineId` | 目标虚拟机的非空 GUID。 |
| `displayName` | 可选显示名称快照；未提供时以 GUID 展示。 |

返回：表示该虚拟机的不可变目标。

- 异常 `ArgumentException`：GUID 为空。

#### 方法说明

<a id="api-2fe1beb64a38b293"></a>

##### Equals · 方法

```csharp
public bool Equals(PcieAssignmentTarget? other);
```

比较归属身份；虚拟机显示名称变化不改变相等性。

| 参数 | 说明 |
| --- | --- |
| `other` | 另一归属对象，可为 null。 |

返回：种类与虚拟机 GUID 均相同才为 true。

<a id="api-c6663a5aefc7c2c3"></a>

##### GetHashCode · 方法

```csharp
public override int GetHashCode();
```

按归属种类和虚拟机 GUID 生成哈希值，不包含显示名称。

返回：稳定身份的哈希值。

<a id="api-8c62d8d2a672175b"></a>

### `PcieHostInfo` record 类

宿主 PCIe 设备归属和可分配虚拟机的同次查询快照。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / PCIe |
| 源文件 | ` Management/Host/Pcie/PcieHostInfo.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public PcieHostInfo(IReadOnlyList<PcieDeviceInfo> Devices, IReadOnlyList<PcieAssignmentTarget> VirtualMachines) `](#api-50931d5dcd03aae0) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<PcieDeviceInfo> Devices { get; init; } `](#api-15a835731b4ee664) | PCIe 设备快照及稳定归属身份。 |
| [` public IReadOnlyList<PcieAssignmentTarget> VirtualMachines { get; init; } `](#api-a34e22743b52b47e) | 带 GUID 与显示名称的虚拟机目标；同名虚拟机作为不同目标保留。 |

#### 详细说明

```csharp
public sealed record PcieHostInfo(IReadOnlyList<PcieDeviceInfo> Devices,
    IReadOnlyList<PcieAssignmentTarget> VirtualMachines)
```

| 参数 | 说明 |
| --- | --- |
| `Devices` | PCIe 设备快照及稳定归属身份。 |
| `VirtualMachines` | 带 GUID 与显示名称的虚拟机目标；同名虚拟机作为不同目标保留。 |

调用约定：设备与归属分步采集；执行转移时会重新核对设备身份和实际归属。

#### 构造函数说明

<a id="api-50931d5dcd03aae0"></a>

##### PcieHostInfo · 主构造器

```csharp
public PcieHostInfo(IReadOnlyList<PcieDeviceInfo> Devices, IReadOnlyList<PcieAssignmentTarget> VirtualMachines);
```

| 参数 | 说明 |
| --- | --- |
| `Devices` | PCIe 设备快照及稳定归属身份。 |
| `VirtualMachines` | 带 GUID 与显示名称的虚拟机目标；同名虚拟机作为不同目标保留。 |

#### 属性说明

<a id="api-15a835731b4ee664"></a>

##### Devices · record 属性

```csharp
public IReadOnlyList<PcieDeviceInfo> Devices { get; init; }
```

PCIe 设备快照及稳定归属身份。

<a id="api-a34e22743b52b47e"></a>

##### VirtualMachines · record 属性

```csharp
public IReadOnlyList<PcieAssignmentTarget> VirtualMachines { get; init; }
```

带 GUID 与显示名称的虚拟机目标；同名虚拟机作为不同目标保留。

<a id="api-978775a028711fbf"></a>

### `SharedBackgroundServices` 类

集中执行 Core 进程级 ARP 后台资源的最终清理。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Lifetime/SharedBackgroundServices.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task ShutdownAsync() `](#api-db8598581b9373e3) | 释放共享 ARP 捕获；用于宿主程序最终退出。 |

#### 详细说明

```csharp
public static class SharedBackgroundServices
```

#### 静态方法说明

<a id="api-db8598581b9373e3"></a>

##### ShutdownAsync · 方法

```csharp
public static Task ShutdownAsync();
```

释放共享 ARP 捕获；用于宿主程序最终退出。

调用约定：清理后不支持在同一进程重新启动捕获；不关闭虚拟机。

<a id="api-c8f4e6a6deee535d"></a>

### `SwitchConfigurationConflictException` 类

表示交换机变更违反当前配置保护或冲突规则。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 网络 |
| 源文件 | ` Management/Host/Network/SwitchConfigurationRules.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public SwitchConfigurationConflictException(string message) `](#api-6ac97287ef6bffb9) |  |

#### 详细说明

```csharp
public sealed class SwitchConfigurationConflictException(string message) : ExHyperV.Tools.CoreOperationException(
    ExHyperV.Tools.ApiResponse.Fail(message, kind: ExHyperV.Tools.ApiErrorKind.Conflict))
```

| 参数 | 说明 |
| --- | --- |
| `message` | 可供调用方展示的冲突说明。 |

#### 构造函数说明

<a id="api-6ac97287ef6bffb9"></a>

##### SwitchConfigurationConflictException · 主构造器

```csharp
public SwitchConfigurationConflictException(string message);
```

| 参数 | 说明 |
| --- | --- |
| `message` | 可供调用方展示的冲突说明。 |

<a id="api-942b8beb34283a23"></a>

### `SwitchConfigurationRules` 类

对交换机配置快照进行规则判断，不直接修改主机网络。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 网络；管理层面 / 宿主 / 修改 / 网络 |
| 源文件 | ` Management/Host/Network/SwitchConfigurationRules.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static List<SwitchUpstreamAdapter> GetAvailableCreationAdapters(SwitchMode mode, IEnumerable<SwitchInfo> existing, IEnumerable<SwitchUpstreamAdapter> physical, IEnumerable<SwitchUpstreamAdapter> bridgeable) `](#api-0919ed6c4a07d270) | 结合模式与已占用网卡筛选新建交换机可选的上游。 |
| [` public static string? GetCreationError(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream, IReadOnlyList<SwitchInfo> existing, IEnumerable<SwitchUpstreamAdapter> physical, IEnumerable<SwitchUpstreamAdapter> bridgeable) `](#api-86b9a8b86ae6d6c4) | 检查名称、模式和上游是否满足新建交换机规则。 |
| [` public static string? GetDeletionError(string targetId) `](#api-d610723ddaa1a298) | 检查交换机身份是否受到删除保护。 |
| [` public static string? GetUpdateConflict(string targetId, SwitchMode mode, SwitchUpstreamAdapter? upstream, IEnumerable<SwitchInfo> existing) `](#api-e1a3bc56fd101212) | 检查现有交换机修改是否违反默认交换机或上游占用规则。 |
| [` public static bool IsConfigurationComplete(SwitchMode mode, SwitchUpstreamAdapter? upstream) `](#api-9078e206f38624b8) | 判断所选模式与上游信息是否已满足基本配置条件。 |
| [` public static bool IsDefaultSwitch(string id) `](#api-5c324f1f89e0f556) | 根据固定交换机身份判断是否为 Hyper-V 默认交换机。 |
| [` public static bool RequiresUpstream(SwitchMode mode) `](#api-e2fade47fbbeffb4) | 判断指定连接模式是否要求上游网卡。 |

#### 详细说明

```csharp
public static class SwitchConfigurationRules
```

#### 静态方法说明

<a id="api-0919ed6c4a07d270"></a>

##### GetAvailableCreationAdapters · 方法

```csharp
public static List<SwitchUpstreamAdapter> GetAvailableCreationAdapters(SwitchMode mode, IEnumerable<SwitchInfo> existing, IEnumerable<SwitchUpstreamAdapter> physical, IEnumerable<SwitchUpstreamAdapter> bridgeable);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

结合模式与已占用网卡筛选新建交换机可选的上游。

| 参数 | 说明 |
| --- | --- |
| `mode` | 目标连接模式：桥接、ICS NAT 或隔离。 |
| `existing` | 当前交换机快照。 |
| `physical` | 当前物理上游网卡列表。 |
| `bridgeable` | 当前可桥接网卡列表。 |

返回：供调用方选择的网卡列表。

<a id="api-86b9a8b86ae6d6c4"></a>

##### GetCreationError · 方法

```csharp
public static string? GetCreationError(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream, IReadOnlyList<SwitchInfo> existing, IEnumerable<SwitchUpstreamAdapter> physical, IEnumerable<SwitchUpstreamAdapter> bridgeable);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

检查名称、模式和上游是否满足新建交换机规则。

| 参数 | 说明 |
| --- | --- |
| `name` | 新交换机显示名称。 |
| `mode` | 目标连接模式：桥接、ICS NAT 或隔离。 |
| `upstream` | 桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。 |
| `existing` | 当前交换机快照。 |
| `physical` | 当前物理上游网卡列表。 |
| `bridgeable` | 当前可桥接网卡列表。 |

返回：本地化错误；null 表示当前快照下规则通过。

<a id="api-d610723ddaa1a298"></a>

##### GetDeletionError · 方法

```csharp
public static string? GetDeletionError(string targetId);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

检查交换机身份是否受到删除保护。

| 参数 | 说明 |
| --- | --- |
| `targetId` | 待删除交换机 ID。 |

返回：受保护时的本地化错误，否则为 null。

<a id="api-e1a3bc56fd101212"></a>

##### GetUpdateConflict · 方法

```csharp
public static string? GetUpdateConflict(string targetId, SwitchMode mode, SwitchUpstreamAdapter? upstream, IEnumerable<SwitchInfo> existing);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

检查现有交换机修改是否违反默认交换机或上游占用规则。

| 参数 | 说明 |
| --- | --- |
| `targetId` | 待修改交换机 ID。 |
| `mode` | 目标连接模式：桥接、ICS NAT 或隔离。 |
| `upstream` | 桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。 |
| `existing` | 当前交换机快照。 |

返回：本地化冲突说明；null 表示未发现冲突。

<a id="api-9078e206f38624b8"></a>

##### IsConfigurationComplete · 方法

```csharp
public static bool IsConfigurationComplete(SwitchMode mode, SwitchUpstreamAdapter? upstream);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

判断所选模式与上游信息是否已满足基本配置条件。

| 参数 | 说明 |
| --- | --- |
| `mode` | 目标连接模式：桥接、ICS NAT 或隔离。 |
| `upstream` | 桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。 |

返回：基本输入完整时为 true；提交前还需校验主机状态。

<a id="api-5c324f1f89e0f556"></a>

##### IsDefaultSwitch · 方法

```csharp
public static bool IsDefaultSwitch(string id);
```

功能归属：管理层面 / 宿主 / 查询 / 网络。

根据固定交换机身份判断是否为 Hyper-V 默认交换机。

| 参数 | 说明 |
| --- | --- |
| `id` | 交换机 ID，非显示名称。 |

返回：匹配默认交换机 ID 时为 true。

<a id="api-e2fade47fbbeffb4"></a>

##### RequiresUpstream · 方法

```csharp
public static bool RequiresUpstream(SwitchMode mode);
```

功能归属：管理层面 / 宿主 / 修改 / 网络。

判断指定连接模式是否要求上游网卡。

| 参数 | 说明 |
| --- | --- |
| `mode` | 交换机连接模式。 |

返回：桥接或 NAT 需要上游时为 true。

<a id="api-a600131c0e0a9ab6"></a>

### `VmAffinityFields` 类

处理器亲和性的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Processor/VmAffinityFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<IReadOnlyList<int>> AppliedProcessors `](#api-1b589b84d0b4061c) | 实际处理器选择：亲和性后端当前应用的宿主逻辑处理器编号列表。 |
| [` public static readonly VmQueryField<long> ProcessCreationTime `](#api-bd9a9c07c9f65ed3) | 关联进程创建时间：关联工作进程的创建时间，与进程编号共同标识该进程实例。 |
| [` public static readonly VmQueryField<int> ProcessId `](#api-649932c3dca959f0) | 关联进程编号：Root 亲和性后端关联的虚拟机工作进程编号。 |
| [` public static readonly VmQueryField<string> SavedPreferenceIssue `](#api-e23d641142c50dab) | 亲和性环境说明：无法复用保存的处理器选择时的具体原因。 |
| [` public static readonly VmQueryField<CpuAffinityService.PreferenceStatus> SavedPreferenceStatus `](#api-395165dc75c45eca) | 亲和性环境匹配：保存的处理器选择是否仍属于当前宿主、后端和编号映射。 |
| [` public static readonly VmQueryField<IReadOnlyList<int>> SavedProcessors `](#api-4ca7e94106e13c41) | 保存的处理器选择：为虚拟机保存的宿主逻辑处理器选择列表。 |

#### 详细说明

```csharp
public static class VmAffinityFields
```

#### 字段与常量说明

<a id="api-1b589b84d0b4061c"></a>

##### AppliedProcessors · 字段

```csharp
public static readonly VmQueryField<IReadOnlyList<int>> AppliedProcessors;
```

亲和性后端当前应用的宿主逻辑处理器编号列表。

**单位与格式：** 处理器编号列表

**取值说明：** 编号采用当前亲和性后端的编号约定；HCS 处理器编号与 Windows 处理器组中的掩码位应分别解释。

<a id="api-bd9a9c07c9f65ed3"></a>

##### ProcessCreationTime · 字段

```csharp
public static readonly VmQueryField<long> ProcessCreationTime;
```

关联工作进程的创建时间，与进程编号共同标识该进程实例。

**单位与格式：** UTC FILETIME，100 纳秒为一单位

**取值说明：** 自 1601-01-01 UTC 起累计的 100 纳秒间隔数。

**适用条件：** 适用于 Root 亲和性后端。

<a id="api-649932c3dca959f0"></a>

##### ProcessId · 字段

```csharp
public static readonly VmQueryField<int> ProcessId;
```

Root 亲和性后端关联的虚拟机工作进程编号。

**取值说明：** Windows 进程编号（PID），正整数。

**适用条件：** 适用于 Root 亲和性后端，与 ProcessCreationTime 一起标识工作进程。

**缺测与错误：** 未关联工作进程时返回 Unavailable。

<a id="api-e23d641142c50dab"></a>

##### SavedPreferenceIssue · 字段

```csharp
public static readonly VmQueryField<string> SavedPreferenceIssue;
```

说明缺少保存环境、后端不同、宿主或编号映射变化等情况，供调用方提示重新选择。

**单位与格式：** 说明文本；程序判断使用 SavedPreferenceStatus。

**缺测与错误：** 无需说明时为空，自由查询返回 Unavailable；结合 SavedPreferenceStatus 判断是否需要处理。

<a id="api-395165dc75c45eca"></a>

##### SavedPreferenceStatus · 字段

```csharp
public static readonly VmQueryField<CpuAffinityService.PreferenceStatus> SavedPreferenceStatus;
```

判断保存偏好能否复用；与实际是否已经应用亲和性分开。

**适用条件：** 旧编号数组和版本 1 非空记录缺少环境摘要，需要重新选择并保存。当前查询不自动应用或删除选择。

<a id="api-4ca7e94106e13c41"></a>

##### SavedProcessors · 字段

```csharp
public static readonly VmQueryField<IReadOnlyList<int>> SavedProcessors;
```

为虚拟机保存的宿主逻辑处理器选择列表。

**取值说明：** 宿主逻辑处理器编号的只读列表，例如 `[0, 1, 2, 3]`。

**适用条件：** 两种后端均返回保存的选择。旧宿主或旧后端的编号也保留，使用前检查 SavedPreferenceStatus；不能把它直接当成当前宿主的可选编号。当前应用状态由 AppliedProcessors 提供。

<a id="api-90baee76b1294fcc"></a>

### `VmBatteryState` record 结构

虚拟电池在当前虚拟机上的可用性和启用状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 电源 |
| 源文件 | ` Management/VirtualMachines/Power/VmBatteryState.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmBatteryState(bool Available, bool Enabled) `](#api-9391c2ffb2364ec4) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool Available { get; init; } `](#api-90a994c7cc17f149) | 当前主机和虚拟机是否支持此功能。 |
| [` public bool Enabled { get; init; } `](#api-e6701e5dba639938) | 此功能当前是否启用；应先检查 Available。 |

#### 详细说明

```csharp
public readonly record struct VmBatteryState(bool Available, bool Enabled)
```

| 参数 | 说明 |
| --- | --- |
| `Available` | 当前主机和虚拟机是否支持此功能。 |
| `Enabled` | 此功能当前是否启用；应先检查 Available。 |

#### 构造函数说明

<a id="api-9391c2ffb2364ec4"></a>

##### VmBatteryState · 主构造器

```csharp
public VmBatteryState(bool Available, bool Enabled);
```

| 参数 | 说明 |
| --- | --- |
| `Available` | 当前主机和虚拟机是否支持此功能。 |
| `Enabled` | 此功能当前是否启用；应先检查 Available。 |

#### 属性说明

<a id="api-90a994c7cc17f149"></a>

##### Available · record 属性

```csharp
public bool Available { get; init; }
```

当前主机和虚拟机是否支持此功能。

<a id="api-e6701e5dba639938"></a>

##### Enabled · record 属性

```csharp
public bool Enabled { get; init; }
```

此功能当前是否启用；应先检查 Available。

<a id="api-3749e73e66ae55f3"></a>

### `VmBootFields` 类

引导的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 引导 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Boot/VmBootFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<string> Description `](#api-85d58f5cb9340b4e) | 启动项描述：启动模块提供的启动项说明。 |
| [` public static readonly VmQueryField<BootDeviceKind> Kind `](#api-d91a1af981948277) | 启动项类型：固件启动项对应的设备类型。 |
| [` public static readonly VmQueryField<string> Name `](#api-bfb5685ff3d47bc8) | 启动项名称：固件启动项的显示名称。 |
| [` public static readonly VmQueryField<int> Order `](#api-f85cf3823aa6fc91) | 启动顺序位置：启动项在固件启动顺序中的位置，从 0 开始。 |

#### 详细说明

```csharp
public static class VmBootFields
```

#### 字段与常量说明

<a id="api-85d58f5cb9340b4e"></a>

##### Description · 字段

```csharp
public static readonly VmQueryField<string> Description;
```

启动模块提供的启动项说明。

<a id="api-d91a1af981948277"></a>

##### Kind · 字段

```csharp
public static readonly VmQueryField<BootDeviceKind> Kind;
```

固件启动项对应的设备类型。

<a id="api-bfb5685ff3d47bc8"></a>

##### Name · 字段

```csharp
public static readonly VmQueryField<string> Name;
```

固件启动项的显示名称。

<a id="api-f85cf3823aa6fc91"></a>

##### Order · 字段

```csharp
public static readonly VmQueryField<int> Order;
```

启动项在固件启动顺序中的位置。

**取值说明：** 从 `0` 开始的整数序号。

<a id="api-56cb0e13eca6ab99"></a>

### `VmBootGlobalFields` 类

引导的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 引导 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Boot/VmBootGlobalFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> NumLock `](#api-6c26d466372362fc) | 启动数字锁定：虚拟机启动时是否启用数字键盘锁定。 |

#### 详细说明

```csharp
public static class VmBootGlobalFields
```

#### 字段与常量说明

<a id="api-6c26d466372362fc"></a>

##### NumLock · 字段

```csharp
public static readonly VmQueryField<bool> NumLock;
```

虚拟机启动时是否启用数字键盘锁定。

**取值说明：** `true`：启动时开启 Num Lock；`false`：关闭。

<a id="api-6b0a3fdedb6f2091"></a>

### `VmBootService` 类

读取和配置 BIOS 或 UEFI 启动设备顺序与 Num Lock。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 引导；管理层面 / 虚拟机 / 修改 / 引导 |
| 源文件 | ` Management/VirtualMachines/Boot/VmBootService.Rules.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanChangeBootOrder(int generation, bool isRunning) `](#api-8d881fe2280458cc) | 按虚拟机代数与运行状态判断是否允许改变启动顺序。 |
| [` public static bool CanEditBootNumLock(bool isRunning) `](#api-4da385c7c9fd0c9c) | 判断当前状态是否允许修改引导 Num Lock。 |
| [` public static async Task<ApiResponse<bool>> GetBootNumLockAsync(Guid vmId) `](#api-1ab114048d5aa660) | 读取当前固件配置的引导 Num Lock 设置，不按虚拟机代数过滤。 |
| [` public static async Task<ApiResponse<List<BootOrderItem>>> GetBootOrderAsync(Guid vmId) `](#api-697f57bf779efdad) | 读取第一代 BIOS 或第二代固件的启动设备顺序。 |
| [` public static async Task<ApiResponse> SetBootNumLockAsync(Guid vmId, bool enable) `](#api-684b166a8ccb7168) | 在虚拟机未运行时写入引导 Num Lock 设置。 |
| [` public static async Task<ApiResponse> SetBootOrderAsync(Guid vmId, List<BootOrderItem> items) `](#api-a779b5a43e874523) | 写入启动顺序；第二代按稳定固件设备路径匹配当前设备，并保留未列出的设备。 |
| [` public static async Task<ApiResponse> SetIsoFirstAsync(Guid vmId) `](#api-b77d3623b7ba7724) | 将已挂载 ISO 的光驱调整到启动顺序首位。 |

#### 详细说明

```csharp
public static partial class VmBootService
```

#### 静态方法说明

<a id="api-8d881fe2280458cc"></a>

##### CanChangeBootOrder · 方法

```csharp
public static bool CanChangeBootOrder(int generation, bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / 引导。

按虚拟机代数与运行状态判断是否允许改变启动顺序。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |

返回：第一代运行时不允许修改；其他受支持状态允许。

<a id="api-4da385c7c9fd0c9c"></a>

##### CanEditBootNumLock · 方法

```csharp
public static bool CanEditBootNumLock(bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / 引导。

判断当前状态是否允许修改引导 Num Lock。

| 参数 | 说明 |
| --- | --- |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |

返回：未运行时为 true。

<a id="api-1ab114048d5aa660"></a>

##### GetBootNumLockAsync · 方法

```csharp
public static async Task<ApiResponse<bool>> GetBootNumLockAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 引导。

读取当前固件配置的引导 Num Lock 设置，不按虚拟机代数过滤。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时返回实际 Num Lock 开关；虚拟机/配置不存在或查询失败返回失败。

<a id="api-697f57bf779efdad"></a>

##### GetBootOrderAsync · 方法

```csharp
public static async Task<ApiResponse<List<BootOrderItem>>> GetBootOrderAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 引导。

读取第一代 BIOS 或第二代固件的启动设备顺序。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时返回按优先级排列的设备，列表可以为空；找不到虚拟机/配置与查询失败分别返回失败原因，不返回部分读取的顺序。

<a id="api-684b166a8ccb7168"></a>

##### SetBootNumLockAsync · 方法

```csharp
public static async Task<ApiResponse> SetBootNumLockAsync(Guid vmId, bool enable);
```

功能归属：管理层面 / 虚拟机 / 修改 / 引导。

在虚拟机未运行时写入引导 Num Lock 设置。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enable` | true 请求启用该设置，false 请求关闭。 |

返回：写入成功或包含校验/执行原因的失败响应。

<a id="api-a779b5a43e874523"></a>

##### SetBootOrderAsync · 方法

```csharp
public static async Task<ApiResponse> SetBootOrderAsync(Guid vmId, List<BootOrderItem> items);
```

功能归属：管理层面 / 虚拟机 / 修改 / 引导。

写入启动顺序；第二代按稳定固件设备路径匹配当前设备，并保留未列出的设备。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `items` | 同一虚拟机读取结果中的设备项，按期望优先级排列；新建、重复、跨虚拟机或已失效的设备项会被拒绝。 |

返回：写入结果；身份、运行状态或 WMI 拒绝分别返回失败，不改变输入设备列表。

<a id="api-b77d3623b7ba7724"></a>

##### SetIsoFirstAsync · 方法

```csharp
public static async Task<ApiResponse> SetIsoFirstAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / 引导。

将已挂载 ISO 的光驱调整到启动顺序首位。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：已处于首位或没有可提升的 ISO 光驱时也视为成功；否则返回调整结果。

<a id="api-e7b7d8ebff4f68e5"></a>

### `VmCheckpointFields` 类

检查点的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 检查点 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Checkpoints/VmCheckpointFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<DateTime> CreatedDate `](#api-92838a055c81cd8a) | 检查点创建时间：检查点或当前配置节点的创建时间。 |
| [` public static readonly VmQueryField<IReadOnlyList<string>> DiskPaths `](#api-dcd2d24ef78bb47f) | 历史磁盘路径：当前节点关联的存储介质路径列表。 |
| [` public static readonly VmQueryField<string> Id `](#api-3e2477b6cfc585c4) | 检查点身份：检查点或当前配置节点的标识。 |
| [` public static readonly VmQueryField<bool> IsCurrent `](#api-383c3e1be4986062) | 当前所在位置：节点是否为当前配置或其直接父检查点，用于标记当前分支位置。 |
| [` public static readonly VmQueryField<bool> IsWormhole `](#api-2cb0fa6b370f702d) | 历史磁盘临时挂接状态：是否检测到该节点历史磁盘的临时挂接。 |
| [` public static readonly VmQueryField<string> Name `](#api-f3a3bd8cf760b53f) | 检查点名称：检查点或当前配置节点的显示名称。 |
| [` public static readonly VmQueryField<SpacetimeNodeType> NodeType `](#api-77f64dce3871ccf4) | 节点类型：节点的类型，用于区分当前配置和已保存的检查点。 |
| [` public static readonly VmQueryField<string> ParentId `](#api-8477d9dd02b8af1d) | 父检查点身份：在本次节点集合中解析出的父配置标识；未解析到时为空字符串。 |

#### 详细说明

```csharp
public static class VmCheckpointFields
```

#### 字段与常量说明

<a id="api-92838a055c81cd8a"></a>

##### CreatedDate · 字段

```csharp
public static readonly VmQueryField<DateTime> CreatedDate;
```

检查点或当前配置节点的创建时间。

**单位与格式：** 日期与时间

**取值说明：** 通过 ManagementDateTimeConverter.ToDateTime 将 WMI CreationTime 转为 DateTime。

<a id="api-dcd2d24ef78bb47f"></a>

##### DiskPaths · 字段

```csharp
public static readonly VmQueryField<IReadOnlyList<string>> DiskPaths;
```

当前节点直接关联的全部存储介质路径，可能包含光驱镜像；不展开虚拟磁盘父链。

**取值说明：** 返回本节点存储分配记录中的全部介质路径；没有关联路径时为空列表。

<a id="api-3e2477b6cfc585c4"></a>

##### Id · 字段

```csharp
public static readonly VmQueryField<string> Id;
```

后端 ConfigurationID。当前配置也使用真实配置标识，不使用 SpacetimeNode.CurrentId；此查询不生成 Genesis 逻辑节点。

<a id="api-383c3e1be4986062"></a>

##### IsCurrent · 字段

```csharp
public static readonly VmQueryField<bool> IsCurrent;
```

节点是否为当前配置或其直接父检查点，用于标记当前分支位置。

**取值说明：** 当前配置节点和其直接父检查点均可为 true。

<a id="api-2cb0fa6b370f702d"></a>

##### IsWormhole · 字段

```csharp
public static readonly VmQueryField<bool> IsWormhole;
```

是否检测到该节点历史磁盘的临时挂接。

**取值说明：** `true`：检测到历史磁盘的临时挂接；`false`：未检测到。

<a id="api-f3a3bd8cf760b53f"></a>

##### Name · 字段

```csharp
public static readonly VmQueryField<string> Name;
```

检查点或当前配置节点的显示名称。

<a id="api-77f64dce3871ccf4"></a>

##### NodeType · 字段

```csharp
public static readonly VmQueryField<SpacetimeNodeType> NodeType;
```

节点的类型，用于区分当前配置和已保存的检查点。

<a id="api-8477d9dd02b8af1d"></a>

##### ParentId · 字段

```csharp
public static readonly VmQueryField<string> ParentId;
```

在本次节点集合中解析出的父配置标识；空字符串也可能表示父配置未包含在结果中。

<a id="api-6d7251fc824bd58c"></a>

### `VmCheckpointGlobalFields` 类

检查点的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 检查点 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Checkpoints/VmCheckpointGlobalFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> Enabled `](#api-c3bacbefd3629100) | 检查点开关：是否允许为虚拟机创建检查点。 |

#### 详细说明

```csharp
public static class VmCheckpointGlobalFields
```

#### 字段与常量说明

<a id="api-c3bacbefd3629100"></a>

##### Enabled · 字段

```csharp
public static readonly VmQueryField<bool> Enabled;
```

UserSnapshotType 不为 2 时返回 true；这是检查点策略的有损开关视图，不区分生产、标准及后备策略。

**取值说明：** `true`：允许创建检查点；`false`：禁止创建检查点。

<a id="api-4c35b7ba174517dd"></a>

### `VmConfigurationState` record 类

一次设置操作需要的当前 VM 状态；无后台任务或进程级会话。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmConfigurationState.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmConfigurationState(string id, int generation, VmState state) `](#api-d30a365c6d366958) | 创建配置校验使用的状态快照；不读取或修改虚拟机。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Generation { get; } `](#api-ad0eb63ab973faff) | 虚拟机代数，1 或 2；读取时未识别配置子类型则为 0。 |
| [` public string Id { get; } `](#api-b005100defea7e54) | 虚拟机的稳定 GUID 文本，不是显示名称。 |
| [` public bool IsPoweredOff { get; } `](#api-55ae5e04f9771f42) | Hyper-V 状态码为 3，表示完全关机。 |
| [` public bool IsRunning { get; } `](#api-2fff23693534699e) | 当前状态属于活动状态；不等同于仅 EnabledState=2。 |
| [` public VmState State { get; } `](#api-4d7b8ca99b9a3df0) | 当前运行状态，不包含后端状态码。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<ApiResponse<VmConfigurationState>> ReadAsync(Guid vmId) `](#api-081e3e50ba3093f4) | 按稳定身份读取虚拟机配置校验状态。 |
| [` public static async Task<ApiResponse<VmConfigurationState>> ReadAsync(string vmName) `](#api-d6316dd6136c4190) | 读取用于配置校验的虚拟机标识、代数与当前 Hyper-V 状态。 |

#### 详细说明

```csharp
public sealed record VmConfigurationState
```

#### 构造函数说明

<a id="api-d30a365c6d366958"></a>

##### VmConfigurationState · 构造器

```csharp
public VmConfigurationState(string id, int generation, VmState state);
```

创建配置校验使用的状态快照；不读取或修改虚拟机。

| 参数 | 说明 |
| --- | --- |
| `id` | 虚拟机的稳定 GUID 文本。 |
| `generation` | 虚拟机代数。 |
| `state` | 虚拟机运行状态。 |

#### 属性说明

<a id="api-ad0eb63ab973faff"></a>

##### Generation · 属性

```csharp
public int Generation { get; }
```

虚拟机代数，1 或 2；读取时未识别配置子类型则为 0。

<a id="api-b005100defea7e54"></a>

##### Id · 属性

```csharp
public string Id { get; }
```

虚拟机的稳定 GUID 文本，不是显示名称。

<a id="api-55ae5e04f9771f42"></a>

##### IsPoweredOff · 属性

```csharp
public bool IsPoweredOff { get; }
```

Hyper-V 状态码为 3，表示完全关机。

<a id="api-2fff23693534699e"></a>

##### IsRunning · 属性

```csharp
public bool IsRunning { get; }
```

当前状态属于活动状态；不等同于仅 EnabledState=2。

<a id="api-4d7b8ca99b9a3df0"></a>

##### State · 属性

```csharp
public VmState State { get; }
```

当前运行状态，不包含后端状态码。

#### 静态方法说明

<a id="api-081e3e50ba3093f4"></a>

##### ReadAsync · 方法

```csharp
public static Task<ApiResponse<VmConfigurationState>> ReadAsync(Guid vmId);
```

按稳定身份读取虚拟机配置校验状态。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：当前状态；查询失败通过结果报告。

<a id="api-d6316dd6136c4190"></a>

##### ReadAsync · 方法

```csharp
public static async Task<ApiResponse<VmConfigurationState>> ReadAsync(string vmName);
```

读取用于配置校验的虚拟机标识、代数与当前 Hyper-V 状态。

| 参数 | 说明 |
| --- | --- |
| `vmName` | 目标虚拟机的显示名称，不是 GUID。 |

返回：配置状态快照；未找到虚拟机或 WMI 读取失败时不提供有效数据。

<a id="api-e90b77e987feedc5"></a>

### `VmConsoleFields` 类

控制台的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 控制台 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Console/VmConsoleFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> BasicEnabled `](#api-c6db329667e7f67f) | 基本控制台开关：虚拟机是否配置了基本控制台所需的合成显示控制器。 |
| [` public static readonly VmQueryField<bool> EnhancedAvailable `](#api-378a903d86ea7534) | 增强会话可用状态：Hyper-V 当前是否报告增强会话已可用。 |
| [` public static readonly VmQueryField<bool> LockOnDisconnect `](#api-24f299eba845f37a) | 控制台断开时锁定：是否配置在控制台断开连接时锁定来宾系统。 |

#### 详细说明

```csharp
public static class VmConsoleFields
```

#### 字段与常量说明

<a id="api-c6db329667e7f67f"></a>

##### BasicEnabled · 字段

```csharp
public static readonly VmQueryField<bool> BasicEnabled;
```

根据虚拟机配置中是否存在合成显示控制器判断基本控制台配置状态。实际连接状态由控制台连接接口提供。

**取值说明：** `true`：配置了合成显示控制器；`false`：未配置。

<a id="api-378a903d86ea7534"></a>

##### EnhancedAvailable · 字段

```csharp
public static readonly VmQueryField<bool> EnhancedAvailable;
```

Hyper-V 当前是否报告增强会话已可用。

**取值说明：** EnhancedSessionModeState 为 2（允许且可用）时返回 true；3（不允许）、6（允许但尚不可用）及其他非空编码返回 false，无法由此 bool 区分具体原因。

<a id="api-24f299eba845f37a"></a>

##### LockOnDisconnect · 字段

```csharp
public static readonly VmQueryField<bool> LockOnDisconnect;
```

是否配置在控制台断开连接时锁定来宾系统。

**取值说明：** `true`：断开时锁定；`false`：不自动锁定。

<a id="api-a83163a3d74dba4b"></a>

### `VmConsoleState` record 类

一次读取的控制台状态；只表示查询时的事实。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 控制台 |
| 源文件 | ` Management/VirtualMachines/Console/VmConsoleSupportService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmConsoleState(Guid VmId, string Name, bool IsRunning, bool IsEnhancedAvailable) `](#api-5eea6bf5d1d33ef5) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool IsEnhancedAvailable { get; init; } `](#api-61aa5a4c4377081d) | Hyper-V 是否报告增强会话可用。 |
| [` public bool IsRunning { get; init; } `](#api-ec1e35d572b932f5) | 是否处于活动状态。 |
| [` public string Name { get; init; } `](#api-9867faa8041705d4) | 查询时的显示名称。 |
| [` public Guid VmId { get; init; } `](#api-54d42d6b87061592) | 目标虚拟机稳定 GUID。 |

#### 详细说明

```csharp
public sealed record VmConsoleState(Guid VmId, string Name, bool IsRunning, bool IsEnhancedAvailable)
```

| 参数 | 说明 |
| --- | --- |
| `VmId` | 目标虚拟机稳定 GUID。 |
| `Name` | 查询时的显示名称。 |
| `IsRunning` | 是否处于活动状态。 |
| `IsEnhancedAvailable` | Hyper-V 是否报告增强会话可用。 |

#### 构造函数说明

<a id="api-5eea6bf5d1d33ef5"></a>

##### VmConsoleState · 主构造器

```csharp
public VmConsoleState(Guid VmId, string Name, bool IsRunning, bool IsEnhancedAvailable);
```

| 参数 | 说明 |
| --- | --- |
| `VmId` | 目标虚拟机稳定 GUID。 |
| `Name` | 查询时的显示名称。 |
| `IsRunning` | 是否处于活动状态。 |
| `IsEnhancedAvailable` | Hyper-V 是否报告增强会话可用。 |

#### 属性说明

<a id="api-61aa5a4c4377081d"></a>

##### IsEnhancedAvailable · record 属性

```csharp
public bool IsEnhancedAvailable { get; init; }
```

Hyper-V 是否报告增强会话可用。

<a id="api-ec1e35d572b932f5"></a>

##### IsRunning · record 属性

```csharp
public bool IsRunning { get; init; }
```

是否处于活动状态。

<a id="api-9867faa8041705d4"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

查询时的显示名称。

<a id="api-54d42d6b87061592"></a>

##### VmId · record 属性

```csharp
public Guid VmId { get; init; }
```

目标虚拟机稳定 GUID。

<a id="api-000ef782924fe6a9"></a>

### `VmConsoleSupportService` 类

读取虚拟机控制台状态，管理连接相关配置。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 控制台；管理层面 / 虚拟机 / 修改 / 控制台 |
| 源文件 | ` Management/VirtualMachines/Console/VmConsoleSupportService.Settings.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanToggleConsoleSupport(int generation, bool isRunning) `](#api-4fc0989ca5b307d3) | 判断是否允许添加或移除基本控制台所需的合成设备。 |
| [` public static Task<ApiResponse<VmConsoleState>> GetConsoleStateAsync(Guid vmId) `](#api-e8b0b9c1bf99f255) | 按稳定身份读取控制台名称、运行状态及增强会话可用性。 |
| [` public static async Task<ApiResponse<bool>> IsConsoleSupportEnabledAsync(Guid vmId) `](#api-0b848d91bf6c6582) | 以合成显示控制器是否存在判断基本控制台支持是否启用。 |
| [` public static async Task<ApiResponse<bool>> IsEnhancedSessionAvailableAsync(Guid vmId) `](#api-f50d6eb23736c6d4) | 检查 Hyper-V 当前是否报告增强会话可用。 |
| [` public static Task<ApiResponse> SetConsoleSupportAsync(Guid vmId, bool enable) `](#api-4d11e062bfef7aea) | 为已停止的第二代虚拟机添加或移除基本控制台的键盘、鼠标及显示设备。 |
| [` public static Task<ApiResponse> SetLockOnDisconnectAsync(Guid vmId, bool enabled) `](#api-4841b4f47d6619d8) | 设置控制台断开时是否锁定来宾。 |

#### 详细说明

```csharp
public static partial class VmConsoleSupportService
```

#### 静态方法说明

<a id="api-4fc0989ca5b307d3"></a>

##### CanToggleConsoleSupport · 方法

```csharp
public static bool CanToggleConsoleSupport(int generation, bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / 控制台。

判断是否允许添加或移除基本控制台所需的合成设备。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |

返回：仅第二代且未运行时允许。

<a id="api-e8b0b9c1bf99f255"></a>

##### GetConsoleStateAsync · 方法

```csharp
public static Task<ApiResponse<VmConsoleState>> GetConsoleStateAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 控制台。

按稳定身份读取控制台名称、运行状态及增强会话可用性。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：读取结果；找不到目标与查询失败均明确报告，不按同名虚拟机回退。

<a id="api-0b848d91bf6c6582"></a>

##### IsConsoleSupportEnabledAsync · 方法

```csharp
public static async Task<ApiResponse<bool>> IsConsoleSupportEnabledAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / 控制台。

以合成显示控制器是否存在判断基本控制台支持是否启用。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：true 表示有显示控制器，false 表示未配置；不存在的虚拟机与读取失败返回错误。

<a id="api-f50d6eb23736c6d4"></a>

##### IsEnhancedSessionAvailableAsync · 方法

```csharp
public static async Task<ApiResponse<bool>> IsEnhancedSessionAvailableAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / 控制台。

检查 Hyper-V 当前是否报告增强会话可用。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：原始状态 2 返回 true，3（不允许）、6（允许但尚不可用）及其他值返回 false；当前实现也把 null 折为 false。查询失败或找不到虚拟机返回失败。

<a id="api-4d11e062bfef7aea"></a>

##### SetConsoleSupportAsync · 方法

```csharp
public static Task<ApiResponse> SetConsoleSupportAsync(Guid vmId, bool enable);
```

功能归属：管理层面 / 虚拟机 / 修改 / 控制台。

为已停止的第二代虚拟机添加或移除基本控制台的键盘、鼠标及显示设备。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enable` | true 请求启用该设置，false 请求关闭。 |

返回：操作结果；步骤失败时返回错误，并保留此前已完成的设备变更。

<a id="api-4841b4f47d6619d8"></a>

##### SetLockOnDisconnectAsync · 方法

```csharp
public static Task<ApiResponse> SetLockOnDisconnectAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 控制台。

设置控制台断开时是否锁定来宾。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | true 启用，false 关闭。 |

返回：配置提交结果；属性不受支持或执行条件不满足时返回失败。

<a id="api-68709c8b2fa3259d"></a>

### `VmCreateService` 类

校验创建参数并完成一台虚拟机的配置、存储和网络创建。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 创建 |
| 源文件 | ` Management/VirtualMachines/Instances/Creation/VmCreateService.Options.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public static bool SupportsGeneration1 { get; } `](#api-d413e8a1de467376) | 当前操作系统架构是否支持第一代虚拟机；ARM64 为 false。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<VmCreationResult> CreateVirtualMachineAsync(VmCreationParameters p) `](#api-12395abb8ac46080) | 根据一份配置创建并注册一台虚拟机，返回实际 GUID 和名称。 |
| [` public static string? GetCreationError(VmCreationParameters p) `](#api-40bec9581168d915) | 校验一台虚拟机的名称、代数、内存、磁盘和部分隔离选项，不创建资源。 |
| [` public static async Task<ApiResponse<VmCreationOptions>> GetCreationOptionsAsync() `](#api-cdefab5cc16adfad) | 并行读取创建所需的宿主能力；不代选配置版本、交换机或创建参数。 |
| [` public static async Task<ApiResponse<VmHostDefaultPaths>> GetHostDefaultPathsAsync() `](#api-8a3eeeadb1c92070) | 读取 Hyper-V 宿主的默认配置目录和虚拟硬盘目录。 |
| [` public static async Task<ApiResponse<VmIsolationSupport>> GetIsolationSupportAsync() `](#api-d132df641dbbf97a) | 读取虚拟机硬件隔离支持情况和可选择的隔离类型。 |
| [` public static async Task<ApiResponse<List<string>>> GetSupportedVersionsAsync() `](#api-c8eb1f42437bb5e6) | 读取宿主支持的虚拟机配置版本。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public const string DefaultDiskDirectory = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks" `](#api-9314ac1fab2614be) | Windows 的默认虚拟硬盘目录。 |
| [` public const string DefaultVmDirectory = @"C:\ProgramData\Microsoft\Windows\Hyper-V" `](#api-14f206c2c5c65d86) | 宿主未提供配置目录时使用的 Hyper-V 默认配置根目录。 |

#### 详细说明

```csharp
public static partial class VmCreateService
```

#### 属性说明

<a id="api-d413e8a1de467376"></a>

##### SupportsGeneration1 · 属性

```csharp
public static bool SupportsGeneration1 { get; }
```

当前操作系统架构是否支持第一代虚拟机；ARM64 为 false。

#### 静态方法说明

<a id="api-12395abb8ac46080"></a>

##### CreateVirtualMachineAsync · 方法

```csharp
public static async Task<VmCreationResult> CreateVirtualMachineAsync(VmCreationParameters p);
```

根据一份配置创建并注册一台虚拟机，返回实际 GUID 和名称。

调用顺序：

1. 构造 `VmCreationParameters`，填写名称、配置目录、处理器、内存、磁盘和网络选项。新磁盘目录可以留空，沿用虚拟机目录。
2. 调用 `CreateVirtualMachineAsync`。如需提交前显示校验结果，可先调用 `GetCreationError`。
3. 读取结果的 GUID、`ActualName`、`ConfigurationDirectory` 和 `DiskPath`。创建成功但发生自动避让时，`NameAdjusted` 为 true，`Notice` 提供说明；可选配置问题见 `ConfigurationIssues`。

| 参数 | 说明 |
| --- | --- |
| `p` | 虚拟机创建参数；新磁盘目录留空时沿用虚拟机目录。 |

返回：创建结果包含独立的 GUID、实际名称和注册状态；失败时保留原始错误、回滚错误及未清理路径，不能假定没有副作用。

调用约定：此入口只创建一台虚拟机，不自动启动。调用方指定名称和磁盘方案，多台任务的编号、调度及后续启动由调用方安排。创建前发现名称或输出路径冲突时自动避让；成功结果的 NameAdjusted、Notice、ActualName、ConfigurationDirectory 和 DiskPath 报告调整及实际位置。

<a id="api-40bec9581168d915"></a>

##### GetCreationError · 方法

```csharp
public static string? GetCreationError(VmCreationParameters p);
```

校验一台虚拟机的名称、代数、内存、磁盘和部分隔离选项，不创建资源。

| 参数 | 说明 |
| --- | --- |
| `p` | 虚拟机创建参数；新磁盘目录留空时沿用虚拟机目录。 |

返回：首个本地化校验错误；校验通过时为 null。

<a id="api-cdefab5cc16adfad"></a>

##### GetCreationOptionsAsync · 方法

```csharp
public static async Task<ApiResponse<VmCreationOptions>> GetCreationOptionsAsync();
```

并行读取创建所需的宿主能力；不代选配置版本、交换机或创建参数。

返回：宿主目录和可选能力；必要查询失败时返回原因。

<a id="api-8a3eeeadb1c92070"></a>

##### GetHostDefaultPathsAsync · 方法

```csharp
public static async Task<ApiResponse<VmHostDefaultPaths>> GetHostDefaultPathsAsync();
```

读取 Hyper-V 宿主的默认配置目录和虚拟硬盘目录。

返回：成功时返回宿主目录；查询失败或宿主配置不存在返回失败。宿主属性为空时使用内置配置目录，磁盘目录允许为空。

<a id="api-d132df641dbbf97a"></a>

##### GetIsolationSupportAsync · 方法

```csharp
public static async Task<ApiResponse<VmIsolationSupport>> GetIsolationSupportAsync();
```

读取虚拟机硬件隔离支持情况和可选择的隔离类型。

返回：成功时明确区分不支持和支持，类型列表总含 Disabled；读取失败返回错误，不当作不支持。

<a id="api-c8eb1f42437bb5e6"></a>

##### GetSupportedVersionsAsync · 方法

```csharp
public static async Task<ApiResponse<List<string>>> GetSupportedVersionsAsync();
```

读取宿主支持的虚拟机配置版本。

返回：成功时为去重并降序排列的版本列表，可为空；宿主能力对象缺失或查询失败返回失败。

#### 字段与常量说明

<a id="api-9314ac1fab2614be"></a>

##### DefaultDiskDirectory · 字段

```csharp
public const string DefaultDiskDirectory = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";
```

Windows 的默认虚拟硬盘目录。

<a id="api-14f206c2c5c65d86"></a>

##### DefaultVmDirectory · 字段

```csharp
public const string DefaultVmDirectory = @"C:\ProgramData\Microsoft\Windows\Hyper-V";
```

宿主未提供配置目录时使用的 Hyper-V 默认配置根目录。

<a id="api-dd7828c0554323b6"></a>

### `VmCreationOptions` record 类

创建前可查询的宿主目录、配置版本、隔离能力和交换机清单；具体选项由调用方决定。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 创建 |
| 源文件 | ` Management/VirtualMachines/Instances/Creation/VmCreationOptions.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmCreationOptions(string DefaultVmPath, string DefaultVhdPath, IReadOnlyList<string> Versions, bool IsolationSupported, IReadOnlyList<string> IsolationTypes, IReadOnlyDictionary<Guid, string> Switches) `](#api-7e98c933e5d039d7) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DefaultVhdPath { get; init; } `](#api-ed6edf777c62f6c8) | 宿主默认虚拟硬盘根目录；宿主未设置时可为空。 |
| [` public string DefaultVmPath { get; init; } `](#api-54f3d0ba1a67d515) | 宿主默认虚拟机配置根目录。 |
| [` public bool IsolationSupported { get; init; } `](#api-224d1bfedff34fc7) | 宿主是否支持硬件隔离。 |
| [` public IReadOnlyList<string> IsolationTypes { get; init; } `](#api-79999722025c8f29) | 宿主支持的隔离类型。 |
| [` public IReadOnlyDictionary<Guid, string> Switches { get; init; } `](#api-c879cabe1e3210e3) | 可用虚拟交换机 GUID 到显示名称的字典；重名交换机保留各自身份，null 选择由上层表达。 |
| [` public IReadOnlyList<string> Versions { get; init; } `](#api-9361ca93a9b44a39) | 宿主支持的配置版本。 |

#### 详细说明

```csharp
public sealed record VmCreationOptions(string DefaultVmPath, string DefaultVhdPath,
    IReadOnlyList<string> Versions, bool IsolationSupported,
    IReadOnlyList<string> IsolationTypes, IReadOnlyDictionary<Guid, string> Switches)
```

| 参数 | 说明 |
| --- | --- |
| `DefaultVmPath` | 宿主默认虚拟机配置根目录。 |
| `DefaultVhdPath` | 宿主默认虚拟硬盘根目录；宿主未设置时可为空。 |
| `Versions` | 宿主支持的配置版本。 |
| `IsolationSupported` | 宿主是否支持硬件隔离。 |
| `IsolationTypes` | 宿主支持的隔离类型。 |
| `Switches` | 可用虚拟交换机 GUID 到显示名称的字典；重名交换机保留各自身份，null 选择由上层表达。 |

#### 构造函数说明

<a id="api-7e98c933e5d039d7"></a>

##### VmCreationOptions · 主构造器

```csharp
public VmCreationOptions(string DefaultVmPath, string DefaultVhdPath, IReadOnlyList<string> Versions, bool IsolationSupported, IReadOnlyList<string> IsolationTypes, IReadOnlyDictionary<Guid, string> Switches);
```

| 参数 | 说明 |
| --- | --- |
| `DefaultVmPath` | 宿主默认虚拟机配置根目录。 |
| `DefaultVhdPath` | 宿主默认虚拟硬盘根目录；宿主未设置时可为空。 |
| `Versions` | 宿主支持的配置版本。 |
| `IsolationSupported` | 宿主是否支持硬件隔离。 |
| `IsolationTypes` | 宿主支持的隔离类型。 |
| `Switches` | 可用虚拟交换机 GUID 到显示名称的字典；重名交换机保留各自身份，null 选择由上层表达。 |

#### 属性说明

<a id="api-ed6edf777c62f6c8"></a>

##### DefaultVhdPath · record 属性

```csharp
public string DefaultVhdPath { get; init; }
```

宿主默认虚拟硬盘根目录；宿主未设置时可为空。

<a id="api-54f3d0ba1a67d515"></a>

##### DefaultVmPath · record 属性

```csharp
public string DefaultVmPath { get; init; }
```

宿主默认虚拟机配置根目录。

<a id="api-224d1bfedff34fc7"></a>

##### IsolationSupported · record 属性

```csharp
public bool IsolationSupported { get; init; }
```

宿主是否支持硬件隔离。

<a id="api-79999722025c8f29"></a>

##### IsolationTypes · record 属性

```csharp
public IReadOnlyList<string> IsolationTypes { get; init; }
```

宿主支持的隔离类型。

<a id="api-c879cabe1e3210e3"></a>

##### Switches · record 属性

```csharp
public IReadOnlyDictionary<Guid, string> Switches { get; init; }
```

可用虚拟交换机 GUID 到显示名称的字典；重名交换机保留各自身份，null 选择由上层表达。

<a id="api-9361ca93a9b44a39"></a>

##### Versions · record 属性

```csharp
public IReadOnlyList<string> Versions { get; init; }
```

宿主支持的配置版本。

<a id="api-3b4fb39ed87dbfa4"></a>

### `VmCreationResult` record 类

单台创建结果，分别保留业务完成、真实身份与失败后的资源状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 创建 |
| 源文件 | ` Management/VirtualMachines/Instances/Creation/VmCreationResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmCreationResult(bool Success, Guid? VmId, string? ActualName, VmRegistrationState RegistrationState, IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null) `](#api-06f9ff74118ca9c6) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? ActualName { get; init; } `](#api-8849f664a2a33e71) | 已创建虚拟机的实际名称；尚未确认创建时为 null。 |
| [` public string? CleanupError { get; init; } `](#api-c2eb440ce29ff833) | 回滚或清理失败原因；不覆盖创建失败原因。 |
| [` public ApiErrorKind CleanupErrorKind { get; init; } = CleanupError == null ? ApiErrorKind.None : ApiErrorKind.CleanupRequired `](#api-d505490cc6c2ac14) | 回滚或清理失败类别；没有清理错误时为 None，与主操作错误分别保留。 |
| [` public string? ConfigurationDirectory { get; init; } `](#api-8483f4026a691be5) | 创建成功后的实际配置目录；失败时资源去向由 RetainedPaths 和注册状态表达。 |
| [` public IReadOnlyList<ApiResponse> ConfigurationIssues { get; init; } = [] `](#api-4821ca1c23a27f92) | 可选配置的失败响应；目前用于 ISO 优先引导，创建成功不表示这些可选配置已经完成。 |
| [` public string? DiskPath { get; init; } `](#api-055f65f6e768f0c8) | 创建成功后的实际磁盘文件路径；未挂接磁盘或创建失败时为 null。 |
| [` public string? Error { get; init; } `](#api-eab97375c81c889c) | 创建失败原因；成功时为 null。 |
| [` public ApiErrorKind ErrorKind { get; init; } = Success ? ApiErrorKind.None : ApiErrorKind.OperationFailed `](#api-d92c84fe6d1b7c4a) | 主操作失败类别；成功时为 None。 |
| [` public bool NameAdjusted { get; } `](#api-c9c85afe660536c2) | 实际名称是否因名称或输出路径冲突发生调整；这种调整不使创建失败。 |
| [` public string? Notice { get; } `](#api-2505e36a9f3b39e5) | 成功时的非阻塞调整说明；名称和目录未发生冲突时为 null。 |
| [` public bool OutcomeUnknown { get; init; } `](#api-92b9f980c0eebe46) | 至少一个已提交操作的结果尚未确认，可能仍在执行；不得盲目重试创建或清理保留资源。 |
| [` public VmRegistrationState RegistrationState { get; init; } `](#api-660ae8f5042e679c) | 本次创建对象当前已确认的注册状态。 |
| [` public string? RequestedName { get; init; } `](#api-18e8f322e03dbb4c) | 调用方请求的原始名称；可与 ActualName 对照。 |
| [` public IReadOnlyList<string> RetainedPaths { get; init; } `](#api-7fe10a766b0f0f96) | 失败后仍保留或未确认删除的本次操作文件、目录，不包含 WMI 路径。 |
| [` public bool Success { get; init; } `](#api-c414575605cce40e) | 创建及所需配置是否完成，不包含后续启动。 |
| [` public Guid? VmId { get; init; } `](#api-ba71498f0a5b7b14) | 已取得的本次创建 GUID；回滚成功后仍保留该历史身份，未取得时为 null。 |

#### 详细说明

```csharp
public sealed record VmCreationResult(bool Success, Guid? VmId, string? ActualName,
    VmRegistrationState RegistrationState, IReadOnlyList<string> RetainedPaths,
    string? Error = null, string? CleanupError = null)
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 创建及所需配置是否完成，不包含后续启动。 |
| `VmId` | 已取得的本次创建 GUID；回滚成功后仍保留该历史身份，未取得时为 null。 |
| `ActualName` | 已创建虚拟机的实际名称；尚未确认创建时为 null。 |
| `RegistrationState` | 本次创建对象当前已确认的注册状态。 |
| `RetainedPaths` | 失败后仍保留或未确认删除的本次操作文件、目录，不包含 WMI 路径。 |
| `Error` | 创建失败原因；成功时为 null。 |
| `CleanupError` | 回滚或清理失败原因；不覆盖创建失败原因。 |

#### 构造函数说明

<a id="api-06f9ff74118ca9c6"></a>

##### VmCreationResult · 主构造器

```csharp
public VmCreationResult(bool Success, Guid? VmId, string? ActualName, VmRegistrationState RegistrationState, IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null);
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 创建及所需配置是否完成，不包含后续启动。 |
| `VmId` | 已取得的本次创建 GUID；回滚成功后仍保留该历史身份，未取得时为 null。 |
| `ActualName` | 已创建虚拟机的实际名称；尚未确认创建时为 null。 |
| `RegistrationState` | 本次创建对象当前已确认的注册状态。 |
| `RetainedPaths` | 失败后仍保留或未确认删除的本次操作文件、目录，不包含 WMI 路径。 |
| `Error` | 创建失败原因；成功时为 null。 |
| `CleanupError` | 回滚或清理失败原因；不覆盖创建失败原因。 |

#### 属性说明

<a id="api-8849f664a2a33e71"></a>

##### ActualName · record 属性

```csharp
public string? ActualName { get; init; }
```

已创建虚拟机的实际名称；尚未确认创建时为 null。

<a id="api-c2eb440ce29ff833"></a>

##### CleanupError · record 属性

```csharp
public string? CleanupError { get; init; }
```

回滚或清理失败原因；不覆盖创建失败原因。

<a id="api-d505490cc6c2ac14"></a>

##### CleanupErrorKind · 属性

```csharp
public ApiErrorKind CleanupErrorKind { get; init; } = CleanupError == null ? ApiErrorKind.None : ApiErrorKind.CleanupRequired;
```

回滚或清理失败类别；没有清理错误时为 None，与主操作错误分别保留。

<a id="api-8483f4026a691be5"></a>

##### ConfigurationDirectory · 属性

```csharp
public string? ConfigurationDirectory { get; init; }
```

创建成功后的实际配置目录；失败时资源去向由 RetainedPaths 和注册状态表达。

<a id="api-4821ca1c23a27f92"></a>

##### ConfigurationIssues · 属性

```csharp
public IReadOnlyList<ApiResponse> ConfigurationIssues { get; init; } = [];
```

可选配置的失败响应；目前用于 ISO 优先引导，创建成功不表示这些可选配置已经完成。

<a id="api-055f65f6e768f0c8"></a>

##### DiskPath · 属性

```csharp
public string? DiskPath { get; init; }
```

创建成功后的实际磁盘文件路径；未挂接磁盘或创建失败时为 null。

<a id="api-eab97375c81c889c"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

创建失败原因；成功时为 null。

<a id="api-d92c84fe6d1b7c4a"></a>

##### ErrorKind · 属性

```csharp
public ApiErrorKind ErrorKind { get; init; } = Success ? ApiErrorKind.None : ApiErrorKind.OperationFailed;
```

主操作失败类别；成功时为 None。不能仅凭失败判断虚拟机未创建。

<a id="api-c9c85afe660536c2"></a>

##### NameAdjusted · 属性

```csharp
public bool NameAdjusted { get; }
```

实际名称是否因名称或输出路径冲突发生调整；这种调整不使创建失败。

<a id="api-2505e36a9f3b39e5"></a>

##### Notice · 属性

```csharp
public string? Notice { get; }
```

成功时的非阻塞调整说明；名称和目录未发生冲突时为 null。程序判断使用 NameAdjusted。

<a id="api-92b9f980c0eebe46"></a>

##### OutcomeUnknown · 属性

```csharp
public bool OutcomeUnknown { get; init; }
```

至少一个已提交操作的结果尚未确认，可能仍在执行；不得盲目重试创建或清理保留资源。

<a id="api-660ae8f5042e679c"></a>

##### RegistrationState · record 属性

```csharp
public VmRegistrationState RegistrationState { get; init; }
```

本次创建对象当前已确认的注册状态。

<a id="api-18e8f322e03dbb4c"></a>

##### RequestedName · 属性

```csharp
public string? RequestedName { get; init; }
```

调用方请求的原始名称；可与 ActualName 对照。

<a id="api-7fe10a766b0f0f96"></a>

##### RetainedPaths · record 属性

```csharp
public IReadOnlyList<string> RetainedPaths { get; init; }
```

失败后仍保留或未确认删除的本次操作文件、目录，不包含 WMI 路径。

<a id="api-c414575605cce40e"></a>

##### Success · record 属性

```csharp
public bool Success { get; init; }
```

创建及所需配置是否完成，不包含后续启动。

<a id="api-ba71498f0a5b7b14"></a>

##### VmId · record 属性

```csharp
public Guid? VmId { get; init; }
```

已取得的本次创建 GUID；回滚成功后仍保留该历史身份，未取得时为 null。

<a id="api-daf0227033e23922"></a>

### `VmDeleteService` 类

提供保留磁盘与恢复配置的删除，以及注销后清理专属文件的彻底删除。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 删除 |
| 源文件 | ` Management/VirtualMachines/Instances/Deletion/VmDeleteService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<VmDeletionResult> DeleteVmAsync(string vmName, Guid vmId) `](#api-efc4cae2801b6dbc) | 保存恢复配置后注销一台虚拟机，保留其磁盘文件。 |
| [` public static async Task<ApiResponse<PurgePreview>> PreviewPurgeAsync(Guid vmId) `](#api-313eb39e56fc2f69) | 只读收集当前可证明属于该虚拟机的配置和磁盘清理目标。 |
| [` public static async Task<VmDeletionResult> PurgeVmAsync(string vmName, Guid vmId) `](#api-53daa375428d2e8e) | 注销一台虚拟机，并删除已确认由它独占的配置和磁盘文件。 |

#### 详细说明

```csharp
public static class VmDeleteService
```

调用约定：执行前会按需要使虚拟机停止；部分失败可能发生在注销或文件操作已经完成后，应读取具体操作返回的状态，不能将 Success=false 解释为没有发生修改。

#### 静态方法说明

<a id="api-efc4cae2801b6dbc"></a>

##### DeleteVmAsync · 方法

```csharp
public static async Task<VmDeletionResult> DeleteVmAsync(string vmName, Guid vmId);
```

保存恢复配置后注销一台虚拟机，保留其磁盘文件。

调用顺序：

1. 准备目标虚拟机的 GUID 和显示名称；GUID 用于确定操作对象。
2. 调用 `DeleteVmAsync`。Core 先保存不含磁盘副本的恢复配置，再注销虚拟机并将配置放回原目录。
3. 读取 `VmDeletionResult`，分别确认注销状态和配置保留情况。处理多台虚拟机时，由调用方逐次提交并记录每台结果。

| 参数 | 说明 |
| --- | --- |
| `vmName` | 仅用于错误说明的显示名称；目标定位始终使用 vmId。 |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：流程结果分别报告注册状态、恢复位置与清理错误；失败可能发生在已注销后，Unknown 不表示仍在册。

<a id="api-313eb39e56fc2f69"></a>

##### PreviewPurgeAsync · 方法

```csharp
public static async Task<ApiResponse<PurgePreview>> PreviewPurgeAsync(Guid vmId);
```

只读收集当前可证明属于该虚拟机的配置和磁盘清理目标。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：当前清理目标的查询响应；空 GUID 返回 InvalidArgument，虚拟机不存在返回 NotFound，读取失败保留错误。预览不预留或批准固定文件范围，彻底删除以执行时重新核对的最新归属清单为准。

<a id="api-53daa375428d2e8e"></a>

##### PurgeVmAsync · 方法

```csharp
public static async Task<VmDeletionResult> PurgeVmAsync(string vmName, Guid vmId);
```

注销一台虚拟机，并删除已确认由它独占的配置和磁盘文件。

调用顺序：

1. 如需展示待删除范围，先调用 `PreviewPurgeAsync`；执行时会重新读取文件归属。
2. 确认使用彻底删除模式后调用 `PurgeVmAsync`，传入目标 GUID 和显示名称。处理多台虚拟机时，由调用方逐次提交并决定遇到失败是否继续。
3. 读取删除结果及保留路径。共享磁盘、依赖无法确认的磁盘和清理失败的文件会保留并报告。

| 参数 | 说明 |
| --- | --- |
| `vmName` | 仅用于错误说明的显示名称；目标定位始终使用 vmId。 |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：注销状态、未清理路径和错误分别返回。执行时重新核对最新归属，不绑定此前预览；注销结果未知时不继续删除文件。磁盘依赖无法核对时仍注销并清理专属配置，保留无法排除依赖的磁盘，返回 Success=false 及 CleanupError。

调用约定：依赖保护覆盖当前宿主已登记虚拟机及检查点的磁盘父链，不扫描未登记的离线磁盘。父链只用于保护，不扩大删除范围。文件身份与引用在删除前复查，外部工具不遵循 Core 锁时仍不存在原子比较删除保证。

<a id="api-a806775d80fa93ae"></a>

### `VmDeleteService.PurgePreview` record 类

彻底删除预览。这里只返回可证明属于该 VM 的文件；共享根目录永远不会被当成 VM 私有目录。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 删除 |
| 源文件 | ` Management/VirtualMachines/Instances/Deletion/VmDeleteService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public PurgePreview(string? ConfigDirectory, bool DeletesConfigDirectory, List<string> ConfigFiles, List<string> DiskFiles) `](#api-790556e8e47ac8b8) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? ConfigDirectory { get; init; } `](#api-42d4d541763459cd) | 可确定的虚拟机配置目录；成功查询但无法确定目录时为 null。 |
| [` public List<string> ConfigFiles { get; init; } `](#api-31f051017e0488d7) | 明确识别为该虚拟机所有的配置文件路径。 |
| [` public bool DeletesConfigDirectory { get; init; } `](#api-a8dcab651d368a9f) | 是否能够证明整个配置目录归该虚拟机独占并计划删除。 |
| [` public IReadOnlyList<string> DiskDependencyIssues { get; init; } = Array.Empty<string>() `](#api-387b0a52e3ef02c4) | 未能完成磁盘依赖核对的原因；执行时仍注销虚拟机并仅清理已确认专属的配置。 |
| [` public List<string> DiskFiles { get; init; } `](#api-8f57ca3e0766497f) | 计划删除的专属磁盘文件路径。 |
| [` public IReadOnlyList<string> RetainedDiskFiles { get; init; } = Array.Empty<string>() `](#api-da18c45e4f41c5dc) | 因共享依赖、身份或依赖不明而保留的磁盘文件。 |

#### 详细说明

```csharp
public sealed record PurgePreview(
        string? ConfigDirectory,
        bool DeletesConfigDirectory,
        List<string> ConfigFiles,
        List<string> DiskFiles)
```

| 参数 | 说明 |
| --- | --- |
| `ConfigDirectory` | 可确定的虚拟机配置目录；成功查询但无法确定目录时为 null。预览失败由外层响应表达。 |
| `DeletesConfigDirectory` | 是否能够证明整个配置目录归该虚拟机独占并计划删除。 |
| `ConfigFiles` | 明确识别为该虚拟机所有的配置文件路径。 |
| `DiskFiles` | 计划删除的专属磁盘文件路径。 |

#### 构造函数说明

<a id="api-790556e8e47ac8b8"></a>

##### PurgePreview · 主构造器

```csharp
public PurgePreview(string? ConfigDirectory, bool DeletesConfigDirectory, List<string> ConfigFiles, List<string> DiskFiles);
```

| 参数 | 说明 |
| --- | --- |
| `ConfigDirectory` | 可确定的虚拟机配置目录；成功查询但无法确定目录时为 null。预览失败由外层响应表达。 |
| `DeletesConfigDirectory` | 是否能够证明整个配置目录归该虚拟机独占并计划删除。 |
| `ConfigFiles` | 明确识别为该虚拟机所有的配置文件路径。 |
| `DiskFiles` | 计划删除的专属磁盘文件路径。 |

#### 属性说明

<a id="api-42d4d541763459cd"></a>

##### ConfigDirectory · record 属性

```csharp
public string? ConfigDirectory { get; init; }
```

可确定的虚拟机配置目录；成功查询但无法确定目录时为 null。预览失败由外层响应表达。

<a id="api-31f051017e0488d7"></a>

##### ConfigFiles · record 属性

```csharp
public List<string> ConfigFiles { get; init; }
```

明确识别为该虚拟机所有的配置文件路径。

<a id="api-a8dcab651d368a9f"></a>

##### DeletesConfigDirectory · record 属性

```csharp
public bool DeletesConfigDirectory { get; init; }
```

是否能够证明整个配置目录归该虚拟机独占并计划删除。

<a id="api-387b0a52e3ef02c4"></a>

##### DiskDependencyIssues · 属性

```csharp
public IReadOnlyList<string> DiskDependencyIssues { get; init; } = Array.Empty<string>();
```

未能完成磁盘依赖核对的原因；执行时仍注销虚拟机并仅清理已确认专属的配置。

<a id="api-8f57ca3e0766497f"></a>

##### DiskFiles · record 属性

```csharp
public List<string> DiskFiles { get; init; }
```

计划删除的专属磁盘文件路径。

<a id="api-da18c45e4f41c5dc"></a>

##### RetainedDiskFiles · 属性

```csharp
public IReadOnlyList<string> RetainedDiskFiles { get; init; } = Array.Empty<string>();
```

因共享依赖、身份或依赖不明而保留的磁盘文件。

<a id="api-ca976d50d35e3e64"></a>

### `VmDeletionResult` record 类

删除的实际完成情况；注销状态、恢复配置与文件清理分别报告。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 删除 |
| 源文件 | ` Management/VirtualMachines/Instances/Deletion/VmDeletionResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmDeletionResult(bool Success, VmRegistrationState RegistrationState, string? RecoveryPath, IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null) `](#api-5d05da2ff0d12ea6) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? CleanupError { get; init; } `](#api-6912275fe65b6d48) | 配置恢复或文件清理失败原因，不覆盖主操作错误。 |
| [` public ApiFailure? CleanupFailure { get; init; } `](#api-4f7391492f266bfd) | 清理失败的结构化信息，与主操作失败分开；没有原始接口码时为 -1。 |
| [` public string? Error { get; init; } `](#api-2f74c37f385719b2) | 主操作失败原因；成功时为 null。 |
| [` public ApiFailure? Failure { get; init; } `](#api-c3ceff55c5cb329d) | 主操作失败的结构化信息；没有更具体来源时 Kind 为 Unspecified。 |
| [` public string? RecoveryPath { get; init; } `](#api-f3d147f0308413ce) | 可重新导入的配置目录或保留的临时恢复包；没有可用恢复位置时为 null。 |
| [` public VmRegistrationState RegistrationState { get; init; } `](#api-4908ea74a1a46156) | 结束时已确认的注册状态；Unknown 不应解释为仍在册。 |
| [` public IReadOnlyList<string> RetainedPaths { get; init; } `](#api-8b267ac7414b8530) | 未删除或未能确认已删除的文件、目录；需要结合 CleanupError 处理。 |
| [` public bool Success { get; init; } `](#api-de4731404d902994) | 所请求的删除流程是否完成。 |

#### 详细说明

```csharp
public sealed record VmDeletionResult(bool Success, VmRegistrationState RegistrationState,
    string? RecoveryPath, IReadOnlyList<string> RetainedPaths,
    string? Error = null, string? CleanupError = null)
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 所请求的删除流程是否完成。 |
| `RegistrationState` | 结束时已确认的注册状态；Unknown 不应解释为仍在册。 |
| `RecoveryPath` | 可重新导入的配置目录或保留的临时恢复包；没有可用恢复位置时为 null。 |
| `RetainedPaths` | 未删除或未能确认已删除的文件、目录；需要结合 CleanupError 处理。 |
| `Error` | 主操作失败原因；成功时为 null。 |
| `CleanupError` | 配置恢复或文件清理失败原因，不覆盖主操作错误。 |

#### 构造函数说明

<a id="api-5d05da2ff0d12ea6"></a>

##### VmDeletionResult · 主构造器

```csharp
public VmDeletionResult(bool Success, VmRegistrationState RegistrationState, string? RecoveryPath, IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null);
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 所请求的删除流程是否完成。 |
| `RegistrationState` | 结束时已确认的注册状态；Unknown 不应解释为仍在册。 |
| `RecoveryPath` | 可重新导入的配置目录或保留的临时恢复包；没有可用恢复位置时为 null。 |
| `RetainedPaths` | 未删除或未能确认已删除的文件、目录；需要结合 CleanupError 处理。 |
| `Error` | 主操作失败原因；成功时为 null。 |
| `CleanupError` | 配置恢复或文件清理失败原因，不覆盖主操作错误。 |

#### 属性说明

<a id="api-6912275fe65b6d48"></a>

##### CleanupError · record 属性

```csharp
public string? CleanupError { get; init; }
```

配置恢复或文件清理失败原因，不覆盖主操作错误。

<a id="api-4f7391492f266bfd"></a>

##### CleanupFailure · 属性

```csharp
public ApiFailure? CleanupFailure { get; init; }
```

清理失败的结构化信息，与主操作失败分开；没有原始接口码时为 -1。

<a id="api-2f74c37f385719b2"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

主操作失败原因；成功时为 null。

<a id="api-c3ceff55c5cb329d"></a>

##### Failure · 属性

```csharp
public ApiFailure? Failure { get; init; }
```

主操作失败的结构化信息；没有更具体来源时 Kind 为 Unspecified。

<a id="api-f3d147f0308413ce"></a>

##### RecoveryPath · record 属性

```csharp
public string? RecoveryPath { get; init; }
```

可重新导入的配置目录或保留的临时恢复包；没有可用恢复位置时为 null。

<a id="api-4908ea74a1a46156"></a>

##### RegistrationState · record 属性

```csharp
public VmRegistrationState RegistrationState { get; init; }
```

结束时已确认的注册状态；Unknown 不应解释为仍在册。

<a id="api-8b267ac7414b8530"></a>

##### RetainedPaths · record 属性

```csharp
public IReadOnlyList<string> RetainedPaths { get; init; }
```

未删除或未能确认已删除的文件、目录；需要结合 CleanupError 处理。

<a id="api-de4731404d902994"></a>

##### Success · record 属性

```csharp
public bool Success { get; init; }
```

所请求的删除流程是否完成。

<a id="api-f028f6e3c9b3914e"></a>

### `VmDisplayFields` 类

显示的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 显示 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Display/VmDisplayFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<int> Height `](#api-6c3eff7af18098c3) | 显示高度：虚拟显示控制器配置的垂直分辨率，单位为像素。 |
| [` public static readonly VmQueryField<int> ResolutionType `](#api-84ecc62c3b275b65) | 分辨率策略：虚拟显示控制器使用的分辨率策略。 |
| [` public static readonly VmQueryField<int> Width `](#api-f93738ea149efc05) | 显示宽度：虚拟显示控制器配置的水平分辨率，单位为像素。 |

#### 详细说明

```csharp
public static class VmDisplayFields
```

#### 字段与常量说明

<a id="api-6c3eff7af18098c3"></a>

##### Height · 字段

```csharp
public static readonly VmQueryField<int> Height;
```

虚拟显示控制器配置的垂直分辨率，单位为像素。

**取值说明：** 整数，单位为像素；例如 `1920` 表示 1920 像素。

<a id="api-84ecc62c3b275b65"></a>

##### ResolutionType · 字段

```csharp
public static readonly VmQueryField<int> ResolutionType;
```

虚拟显示控制器使用的分辨率策略。

**单位与格式：** 策略编码

**取值说明：** 2：最大分辨率；3：固定分辨率；4：默认策略。

<a id="api-f93738ea149efc05"></a>

##### Width · 字段

```csharp
public static readonly VmQueryField<int> Width;
```

虚拟显示控制器配置的水平分辨率，单位为像素。

**取值说明：** 整数，单位为像素；例如 `1920` 表示 1920 像素。

<a id="api-e3e41eea722e9f8f"></a>

### `VmDisplayResolution` record 类

虚拟显示设备的分辨率策略与物理像素尺寸。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 显示 |
| 源文件 | ` Management/VirtualMachines/Display/VmDisplaySettingsService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmDisplayResolution(int ResolutionType, int Width, int Height) `](#api-ecd64abc3771b052) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Height { get; init; } `](#api-e6d852f399f9403a) | 图像高度，单位为像素。 |
| [` public int ResolutionType { get; init; } `](#api-94fd4d0a9f4a4439) | 2 为最大分辨率，3 为固定分辨率，4 为默认策略。 |
| [` public int Width { get; init; } `](#api-2d6bbe80b56fd670) | 图像宽度，单位为像素。 |

#### 详细说明

```csharp
public sealed record VmDisplayResolution(int ResolutionType, int Width, int Height)
```

| 参数 | 说明 |
| --- | --- |
| `ResolutionType` | 2 为最大分辨率，3 为固定分辨率，4 为默认策略。 |
| `Width` | 图像宽度，单位为像素。 |
| `Height` | 图像高度，单位为像素。 |

#### 构造函数说明

<a id="api-ecd64abc3771b052"></a>

##### VmDisplayResolution · 主构造器

```csharp
public VmDisplayResolution(int ResolutionType, int Width, int Height);
```

| 参数 | 说明 |
| --- | --- |
| `ResolutionType` | 2 为最大分辨率，3 为固定分辨率，4 为默认策略。 |
| `Width` | 图像宽度，单位为像素。 |
| `Height` | 图像高度，单位为像素。 |

#### 属性说明

<a id="api-e6d852f399f9403a"></a>

##### Height · record 属性

```csharp
public int Height { get; init; }
```

图像高度，单位为像素。

<a id="api-94fd4d0a9f4a4439"></a>

##### ResolutionType · record 属性

```csharp
public int ResolutionType { get; init; }
```

2 为最大分辨率，3 为固定分辨率，4 为默认策略。

<a id="api-2d6bbe80b56fd670"></a>

##### Width · record 属性

```csharp
public int Width { get; init; }
```

图像宽度，单位为像素。

<a id="api-0b921ec439a343fc"></a>

### `VmDisplaySettingsService` 类

读取和写入虚拟显示设备的分辨率策略。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 显示；管理层面 / 虚拟机 / 修改 / 显示 |
| 源文件 | ` Management/VirtualMachines/Display/VmDisplaySettingsService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanEdit(bool isRunning) `](#api-ccbb01325c9a7bf9) | 判断虚拟机显示设备分辨率配置是否可修改。 |
| [` public static async Task<ApiResponse<VmDisplayResolution>> GetResolutionAsync(Guid vmId) `](#api-aa378b3abc854dae) | 读取虚拟显示设备的分辨率策略和像素尺寸。 |
| [` public static string? GetResolutionError(int resolutionType, int width, int height) `](#api-1709fcff18c53f85) | 固定分辨率模式下检查宽度 200 至 7680、高度 200 至 4320 像素的范围。 |
| [` public static async Task<ApiResponse> SetResolutionAsync(Guid vmId, int resolutionType, int width, int height) `](#api-96463d85dd12d1fa) | 在虚拟机未运行时写入分辨率策略；固定分辨率的宽高会向下取偶数。 |

#### 详细说明

```csharp
public static class VmDisplaySettingsService
```

#### 静态方法说明

<a id="api-ccbb01325c9a7bf9"></a>

##### CanEdit · 方法

```csharp
public static bool CanEdit(bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / 显示。

判断虚拟机显示设备分辨率配置是否可修改。

| 参数 | 说明 |
| --- | --- |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |

返回：虚拟机未运行时为 true。

<a id="api-aa378b3abc854dae"></a>

##### GetResolutionAsync · 方法

```csharp
public static async Task<ApiResponse<VmDisplayResolution>> GetResolutionAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 显示。

读取虚拟显示设备的分辨率策略和像素尺寸。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时返回策略和尺寸，失败不返回默认分辨率。

<a id="api-1709fcff18c53f85"></a>

##### GetResolutionError · 方法

```csharp
public static string? GetResolutionError(int resolutionType, int width, int height);
```

功能归属：管理层面 / 虚拟机 / 修改 / 显示。

固定分辨率模式下检查宽度 200 至 7680、高度 200 至 4320 像素的范围。

| 参数 | 说明 |
| --- | --- |
| `resolutionType` | Hyper-V 分辨率类型：2 为最大分辨率，3 为固定分辨率，4 为默认策略。 |
| `width` | 固定模式下要求 200 至 7680 像素宽。 |
| `height` | 固定模式下要求 200 至 4320 像素高。 |

返回：策略无效或固定模式尺寸超出范围时的说明；有效时为 null。

<a id="api-96463d85dd12d1fa"></a>

##### SetResolutionAsync · 方法

```csharp
public static async Task<ApiResponse> SetResolutionAsync(Guid vmId, int resolutionType, int width, int height);
```

功能归属：管理层面 / 虚拟机 / 修改 / 显示。

在虚拟机未运行时写入分辨率策略；固定分辨率的宽高会向下取偶数。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `resolutionType` | Hyper-V 分辨率类型：2 为最大分辨率，3 为固定分辨率，4 为默认策略。 |
| `width` | 固定模式下为 200 至 7680 像素宽，写入前向下取偶数。 |
| `height` | 固定模式下为 200 至 4320 像素高，写入前向下取偶数。 |

返回：结构化写入结果；仅固定分辨率模式写入宽高。

<a id="api-3152e140f9edbf4b"></a>

### `VmEditService` 类

校验和修改虚拟机显示名称。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 基本信息 |
| 源文件 | ` Management/VirtualMachines/Identity/VmEditService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static string? GetNameError(string? newName) `](#api-c027ec2049f7ff70) | 检查新虚拟机名称是否为空白。 |
| [` public static async Task<ApiResponse> RenameVmAsync(Guid vmId, string newName) `](#api-feb5a07dbfc23262) | 按稳定 GUID 重命名虚拟机。 |

#### 详细说明

```csharp
public static class VmEditService
```

#### 静态方法说明

<a id="api-c027ec2049f7ff70"></a>

##### GetNameError · 方法

```csharp
public static string? GetNameError(string? newName);
```

检查新虚拟机名称是否为空白。

| 参数 | 说明 |
| --- | --- |
| `newName` | 要设置的新显示名称；不得为空白。 |

返回：空白名称的错误说明，其他名称返回 null；不检查重名或主机约束。

<a id="api-feb5a07dbfc23262"></a>

##### RenameVmAsync · 方法

```csharp
public static async Task<ApiResponse> RenameVmAsync(Guid vmId, string newName);
```

按稳定 GUID 重命名虚拟机。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `newName` | 要设置的新显示名称；不得为空白。 |

返回：修改结果；错误包含分类与底层诊断。

<a id="api-96131a1b1362b9c4"></a>

### `VmExportCheckpointMode` 枚举

本次导出包含哪些检查点。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` All = 1 `](#api-c4c3ff9c322159a5) | 导出全部检查点。 |
| [` None = 0 `](#api-b2c932b7d6d21a8c) | 仅导出当前虚拟机，不包含检查点树。 |
| [` Single = 2 `](#api-7b3629b66b77d4e0) | 导出选定检查点；执行时强制包含其磁盘和运行状态。 |

#### 详细说明

```csharp
public enum VmExportCheckpointMode
```

#### 枚举值说明

<a id="api-c4c3ff9c322159a5"></a>

##### All · 枚举值

```csharp
All = 1
```

导出全部检查点。

<a id="api-b2c932b7d6d21a8c"></a>

##### None · 枚举值

```csharp
None = 0
```

仅导出当前虚拟机，不包含检查点树。

<a id="api-7b3629b66b77d4e0"></a>

##### Single · 枚举值

```csharp
Single = 2
```

导出选定检查点；执行时强制包含其磁盘和运行状态。

<a id="api-9ede9b4035f4d383"></a>

### `VmExportCompressionLevel` 枚举

导出 ZIP 的压缩级别；压缩级别与是否加密互相独立。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackageOptions.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Fastest = 1 `](#api-11cea256d333c2f7) | 优先压缩速度。 |
| [` Optimal = 2 `](#api-bedf6d18b0add592) | 在压缩时间和文件体积之间取均衡。 |
| [` SmallestSize = 3 `](#api-8046c2c9973d8b86) | 优先减小文件体积，通常需要更长时间；不保证每种文件都比均衡档更小。 |
| [` Store = 0 `](#api-fb1bed7086029b1c) | 不压缩，优先降低处理器开销。 |

#### 详细说明

```csharp
public enum VmExportCompressionLevel
```

#### 枚举值说明

<a id="api-11cea256d333c2f7"></a>

##### Fastest · 枚举值

```csharp
Fastest = 1
```

优先压缩速度。

<a id="api-bedf6d18b0add592"></a>

##### Optimal · 枚举值

```csharp
Optimal = 2
```

在压缩时间和文件体积之间取均衡。

<a id="api-8046c2c9973d8b86"></a>

##### SmallestSize · 枚举值

```csharp
SmallestSize = 3
```

优先减小文件体积，通常需要更长时间；不保证每种文件都比均衡档更小。

<a id="api-fb1bed7086029b1c"></a>

##### Store · 枚举值

```csharp
Store = 0
```

不压缩，优先降低处理器开销。

<a id="api-f20936e8ebe738c4"></a>

### `VmExportEncryption` 枚举

新建 ZIP 的内容加密方式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackageOptions.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Aes256 = 1 `](#api-6feba3b9c02260fb) | WinZip AES-256；加密文件内容，文件名、大小等目录信息仍可见。 |
| [` None = 0 `](#api-e34b8e884b642238) | 不加密，不应同时提供密码。 |

#### 详细说明

```csharp
public enum VmExportEncryption
```

#### 枚举值说明

<a id="api-6feba3b9c02260fb"></a>

##### Aes256 · 枚举值

```csharp
Aes256 = 1
```

WinZip AES-256；加密文件内容，文件名、大小等目录信息仍可见。

<a id="api-e34b8e884b642238"></a>

##### None · 枚举值

```csharp
None = 0
```

不加密，不应同时提供密码。

<a id="api-525cd2677b70ce87"></a>

### `VmExportPackageException` 类

打包失败的诊断附件，保存在失败 ApiResponse 的 Exception 属性中，不要求解析错误文本。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackageException.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? CleanupError { get; } `](#api-7e857473f2ad96be) | 临时归档清理失败原因；没有遗留时为 null。 |
| [` public ApiFailure Failure { get; } `](#api-395f755f05bccd8b) | 主失败的结构化信息。 |
| [` public IReadOnlyList<string> RetainedPaths { get; } `](#api-84a74231443841d4) | 本次创建但未成功清理的临时归档路径；不包含仍保留的源目录。 |

#### 详细说明

```csharp
public sealed class VmExportPackageException : IOException
```

#### 属性说明

<a id="api-7e857473f2ad96be"></a>

##### CleanupError · 属性

```csharp
public string? CleanupError { get; }
```

临时归档清理失败原因；没有遗留时为 null。

<a id="api-395f755f05bccd8b"></a>

##### Failure · 属性

```csharp
public ApiFailure Failure { get; }
```

主失败的结构化信息。

<a id="api-84a74231443841d4"></a>

##### RetainedPaths · 属性

```csharp
public IReadOnlyList<string> RetainedPaths { get; }
```

本次创建但未成功清理的临时归档路径；不包含仍保留的源目录。

<a id="api-2f3d1a9483e188d8"></a>

### `VmExportPackageMode` 枚举

ZIP 包的存储或压缩方式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackagingService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Compress = 1 `](#api-baf03c752e903c2a) | 使用 ZIP 压缩存储文件内容。 |
| [` Store = 0 `](#api-90980a1a2599043e) | 不压缩文件内容，优先降低打包 CPU 开销。 |

#### 详细说明

```csharp
public enum VmExportPackageMode
```

#### 枚举值说明

<a id="api-baf03c752e903c2a"></a>

##### Compress · 枚举值

```csharp
Compress = 1
```

使用 ZIP 压缩存储文件内容。

<a id="api-90980a1a2599043e"></a>

##### Store · 枚举值

```csharp
Store = 0
```

不压缩文件内容，优先降低打包 CPU 开销。

<a id="api-d564e43f787184c7"></a>

### `VmExportPackageOptions` 类

创建导出 ZIP 的选项，默认均衡压缩、不加密并保留源目录。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackageOptions.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public VmExportCompressionLevel CompressionLevel { get; init; } = VmExportCompressionLevel.Optimal `](#api-255cfff6d563944f) | 压缩级别；默认 Optimal。 |
| [` public VmExportEncryption Encryption { get; init; } `](#api-40028d5a1b95fe09) | 内容加密方式；默认 None。 |
| [` public string? Password { get; init; } `](#api-584b24d3967194c6) | 本次归档的密码，按 UTF-8 编码；默认 null。 |
| [` public VmExportSourceHandling SourceHandling { get; init; } = VmExportSourceHandling.Keep `](#api-07f1ca2718042f28) | 校验成功后的源目录处理；默认 Keep。 |

#### 详细说明

```csharp
public sealed class VmExportPackageOptions
```

调用约定：旧的 VmExportPackageMode 重载仍保留原行为：Store 不压缩，Compress 使用最小体积档，成功后清理暂存源目录。密码不写入日志、结果或归档注释；Core 不弹出密码窗口。源目录权限补充仍按打包服务的既有规则执行。

#### 属性说明

<a id="api-255cfff6d563944f"></a>

##### CompressionLevel · 属性

```csharp
public VmExportCompressionLevel CompressionLevel { get; init; } = VmExportCompressionLevel.Optimal;
```

压缩级别；默认 Optimal。即使 Store 也可以使用 AES-256。

<a id="api-40028d5a1b95fe09"></a>

##### Encryption · 属性

```csharp
public VmExportEncryption Encryption { get; init; }
```

内容加密方式；默认 None。选择 Aes256 时必须提供非空密码。

<a id="api-584b24d3967194c6"></a>

##### Password · 属性

```csharp
public string? Password { get; init; }
```

本次归档的密码，按 UTF-8 编码；默认 null。None 模式拒绝非 null 密码，避免误以为已加密；密码不会被自动保存。

调用约定：与其他 ZIP 软件交换时建议使用 ASCII 密码。ZIP 没有统一的密码字符编码标记；部分 Windows 工具使用系统代码页，可能无法用相同的中文密码打开 UTF-8 加密包。Core 可使用相同的 Unicode 密码读取自己的包。

<a id="api-07f1ca2718042f28"></a>

##### SourceHandling · 属性

```csharp
public VmExportSourceHandling SourceHandling { get; init; } = VmExportSourceHandling.Keep;
```

校验成功后的源目录处理；默认 Keep。只有明确指定 DeleteAfterValidation 才会删除源文件。

<a id="api-48e2a36722f1a3d6"></a>

### `VmExportPackageProgress` record 结构

当前打包阶段及该阶段的整数百分比。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackagingService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmExportPackageProgress(VmExportPackageStage Stage, int Percentage) `](#api-23e2265295c563a0) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Percentage { get; init; } `](#api-b814a153a225224e) | 当前 CreatingArchive 或 ValidatingArchive 阶段的百分比，不是两个阶段合并的总进度。 |
| [` public VmExportPackageStage Stage { get; init; } `](#api-4158302bad38c1ee) | 报告进度或结果时所处的操作阶段。 |

#### 详细说明

```csharp
public readonly record struct VmExportPackageProgress(
    VmExportPackageStage Stage,
    int Percentage)
```

| 参数 | 说明 |
| --- | --- |
| `Stage` | 报告进度或结果时所处的操作阶段。 |
| `Percentage` | 当前 CreatingArchive 或 ValidatingArchive 阶段的百分比，不是两个阶段合并的总进度。 |

#### 构造函数说明

<a id="api-23e2265295c563a0"></a>

##### VmExportPackageProgress · 主构造器

```csharp
public VmExportPackageProgress(VmExportPackageStage Stage, int Percentage);
```

| 参数 | 说明 |
| --- | --- |
| `Stage` | 报告进度或结果时所处的操作阶段。 |
| `Percentage` | 当前 CreatingArchive 或 ValidatingArchive 阶段的百分比，不是两个阶段合并的总进度。 |

#### 属性说明

<a id="api-b814a153a225224e"></a>

##### Percentage · record 属性

```csharp
public int Percentage { get; init; }
```

当前 CreatingArchive 或 ValidatingArchive 阶段的百分比，不是两个阶段合并的总进度。

<a id="api-4158302bad38c1ee"></a>

##### Stage · record 属性

```csharp
public VmExportPackageStage Stage { get; init; }
```

报告进度或结果时所处的操作阶段。

<a id="api-c33e99fb2d79a509"></a>

### `VmExportPackageResult` record 类

已完成归档的路径及独立的源目录清理结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackagingService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmExportPackageResult(string ArchivePath, bool SourceDirectoryRemoved, string? CleanupError) `](#api-ec25f1940e61acf1) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string ArchivePath { get; init; } `](#api-45b5d5cf64ca4b73) | 归档成功后的 ZIP 文件路径。 |
| [` public string? CleanupError { get; init; } `](#api-96199e1af64ded5d) | 归档成功后清理源目录的错误；无清理错误时为 null。 |
| [` public ApiFailure? CleanupFailure { get; init; } `](#api-d5dc3e8321534d0e) | 源目录清理失败的结构化信息；归档仍有效。 |
| [` public bool SourceDirectoryRemoved { get; init; } `](#api-8a50a12942461619) | 归档完成后是否成功移除了整个源目录。 |
| [` public VmExportSourceHandling SourceHandling { get; init; } = VmExportSourceHandling.DeleteAfterValidation `](#api-c29cd8df0ca1c339) | 本次采用的源目录处理方式；Keep 且 SourceDirectoryRemoved=false 表示按要求保留，不是清理失败。 |

#### 详细说明

```csharp
public sealed record VmExportPackageResult(
    string ArchivePath,
    bool SourceDirectoryRemoved,
    string? CleanupError)
```

| 参数 | 说明 |
| --- | --- |
| `ArchivePath` | 归档成功后的 ZIP 文件路径。 |
| `SourceDirectoryRemoved` | 归档完成后是否成功移除了整个源目录。 |
| `CleanupError` | 归档成功后清理源目录的错误；无清理错误时为 null。 |

#### 构造函数说明

<a id="api-ec25f1940e61acf1"></a>

##### VmExportPackageResult · 主构造器

```csharp
public VmExportPackageResult(string ArchivePath, bool SourceDirectoryRemoved, string? CleanupError);
```

| 参数 | 说明 |
| --- | --- |
| `ArchivePath` | 归档成功后的 ZIP 文件路径。 |
| `SourceDirectoryRemoved` | 归档完成后是否成功移除了整个源目录。 |
| `CleanupError` | 归档成功后清理源目录的错误；无清理错误时为 null。 |

#### 属性说明

<a id="api-45b5d5cf64ca4b73"></a>

##### ArchivePath · record 属性

```csharp
public string ArchivePath { get; init; }
```

归档成功后的 ZIP 文件路径。

<a id="api-96199e1af64ded5d"></a>

##### CleanupError · record 属性

```csharp
public string? CleanupError { get; init; }
```

归档成功后清理源目录的错误；无清理错误时为 null。

<a id="api-d5dc3e8321534d0e"></a>

##### CleanupFailure · 属性

```csharp
public ApiFailure? CleanupFailure { get; init; }
```

源目录清理失败的结构化信息；归档仍有效。

<a id="api-8a50a12942461619"></a>

##### SourceDirectoryRemoved · record 属性

```csharp
public bool SourceDirectoryRemoved { get; init; }
```

归档完成后是否成功移除了整个源目录。

<a id="api-c29cd8df0ca1c339"></a>

##### SourceHandling · 属性

```csharp
public VmExportSourceHandling SourceHandling { get; init; } = VmExportSourceHandling.DeleteAfterValidation;
```

本次采用的源目录处理方式；Keep 且 SourceDirectoryRemoved=false 表示按要求保留，不是清理失败。

<a id="api-3212c0d6001d42a9"></a>

### `VmExportPackageStage` 枚举

导出包正在创建或校验的阶段。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackagingService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` CreatingArchive = 0 `](#api-6fa17d8c25e2e092) | 正在将源文件写入 ZIP。 |
| [` ValidatingArchive = 1 `](#api-0712a13406364cc2) | 正在校验归档条目及内容。 |

#### 详细说明

```csharp
public enum VmExportPackageStage
```

#### 枚举值说明

<a id="api-6fa17d8c25e2e092"></a>

##### CreatingArchive · 枚举值

```csharp
CreatingArchive = 0
```

正在将源文件写入 ZIP。

<a id="api-0712a13406364cc2"></a>

##### ValidatingArchive · 枚举值

```csharp
ValidatingArchive = 1
```

正在校验归档条目及内容。

<a id="api-6abc347896f94f96"></a>

### `VmExportPackagingService` 类

将虚拟机导出目录打包为经校验的 ZIP，支持压缩、AES-256 和源目录保留选择。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackagingService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(string sourceDirectory, string destinationArchivePath, VmExportPackageMode mode, IProgress<VmExportPackageProgress>? progress = null, CancellationToken cancellationToken = default) `](#api-e45b809876908cf5) | 将已有导出目录写成 ZIP；归档校验成功后清理源目录。 |
| [` public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(string sourceDirectory, string destinationArchivePath, VmExportPackageOptions options, IProgress<VmExportPackageProgress>? progress = null, CancellationToken cancellationToken = default) `](#api-50c46fdc957179dd) | 按指定压缩级别、加密方式和源目录处理方式创建 ZIP。 |

#### 详细说明

```csharp
public static class VmExportPackagingService
```

#### 静态方法说明

<a id="api-e45b809876908cf5"></a>

##### CreatePackageAsync · 方法

```csharp
public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(string sourceDirectory, string destinationArchivePath, VmExportPackageMode mode, IProgress<VmExportPackageProgress>? progress = null, CancellationToken cancellationToken = default);
```

将已有导出目录写成 ZIP；归档校验成功后清理源目录。

| 参数 | 说明 |
| --- | --- |
| `sourceDirectory` | 仅供打包使用、成功后允许整体删除的导出暂存目录；不要传入需保留的工作目录。 |
| `destinationArchivePath` | 最终 ZIP 文件路径；必须在源目录之外，祖先目录不能包含重解析点，且不能覆盖已有归档。 |
| `mode` | Store 不压缩，Compress 使用压缩；两种模式均会校验归档后清理源目录。 |
| `progress` | 可选进度接收器；null 时不通知。回调线程由具体 IProgress 实现决定。 |
| `cancellationToken` | 协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。 |

返回：归档成功时返回实际路径及独立的源目录清理结果；SourceDirectoryRemoved=false 不影响归档成功。调度前和执行中的取消均返回 ErrorKind=Cancelled；最终归档提交后视为完成，不因后来的取消撤销归档。

调用约定：保留自动补充当前用户修改权限：仅向逐个核实身份的对象追加非继承权限，不递归传播到未知对象。源文件和目录持有句柄直到校验及清理结束，期间阻止写入和替换；重解析点和多硬链接文件会被拒绝。最终归档写入前使用 .partial 文件；已有同名暂存文件会报告冲突并保留。源目录必须是允许整体删除的导出暂存目录。取消或失败仅清理本次创建的临时归档，保留源目录；临时归档清理失败时，Exception 中的 VmExportPackageException.RetainedPaths 返回保留路径；已追加的 ACL 不撤销。归档校验逐项比较长度和 SHA-256。清理前重查清单，新增对象时保留源目录；清理只删除已归档的原对象，不递归删除后来添加的内容。

<a id="api-50c46fdc957179dd"></a>

##### CreatePackageAsync · 方法

```csharp
public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(string sourceDirectory, string destinationArchivePath, VmExportPackageOptions options, IProgress<VmExportPackageProgress>? progress = null, CancellationToken cancellationToken = default);
```

按指定压缩级别、加密方式和源目录处理方式创建 ZIP。

| 参数 | 说明 |
| --- | --- |
| `sourceDirectory` | 完整导出目录；选择 DeleteAfterValidation 时必须是允许成功后删除的暂存目录。 |
| `destinationArchivePath` | 最终 ZIP 路径，位于源目录之外；已有目标和同名 .partial 文件均不覆盖。 |
| `options` | 打包选项；默认选项为均衡压缩、不加密、保留源目录。参数错误在权限补充与归档写入前返回。 |
| `progress` | 创建和校验两阶段的进度；null 不通知。 |
| `cancellationToken` | 取消打包或校验；最终归档提交后不再撤销成功结果。 |

返回：归档路径和独立的源目录处理结果；保留源目录或清理失败不会把已经校验成功的归档改为失败。

调用约定：使用标准 ZIP/ZIP64；普通 ZIP 保留 .NET 压缩路径，加密 ZIP 使用 WinZip AES-256，文件名和大小不隐藏。归档始终逐项核对长度和 SHA-256，通过后才发布最终文件，再按 SourceHandling 处理源目录。取消、密码或完整性校验失败保留源目录。两种源目录模式都沿用仅向核实身份的对象补充非继承权限的规则，已增加的权限不撤销。失败的临时归档清理信息见 VmExportPackageException.RetainedPaths。

<a id="api-f27e83724f607882"></a>

### `VmExportResult` record 类

单台虚拟机导出的完成状态和保留文件。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmExportResult(bool Success, Guid VmId, string? OutputPath, bool OperationStateUnknown, IReadOnlyList<string> RetainedPaths, string? Error = null, int Code = 0, ApiErrorSource ErrorSource = ApiErrorSource.None, ApiErrorKind ErrorKind = ApiErrorKind.None) `](#api-5f4cc7ebcd00cc53) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Code { get; init; } `](#api-61ad046ed120ca66) | 底层错误码，需结合 ErrorSource 解释。 |
| [` public string? Error { get; init; } `](#api-8f6d7bea64e07225) | 失败原因，成功时为 null。 |
| [` public ApiErrorKind ErrorKind { get; init; } `](#api-36ed84b7fd15f122) | 与底层接口无关的失败类别。 |
| [` public ApiErrorSource ErrorSource { get; init; } `](#api-c2663c52e89d28a0) | 错误码来源。 |
| [` public bool OperationStateUnknown { get; init; } `](#api-f867c5ab9bb2de94) | 提交后尚不能确认原生导出终态；失败不代表后台任务已停止。 |
| [` public string? OutputPath { get; init; } `](#api-afa2590f6bd5cd25) | 成功完成的导出目录；未成功时为 null。 |
| [` public IReadOnlyList<string> RetainedPaths { get; init; } `](#api-46619c90331f5e04) | 本次产生且保留供检查的文件或目录；未知状态时不得直接删除。 |
| [` public bool Success { get; init; } `](#api-7092cc30499c4ab1) | 是否确认导出成功并找到配置文件。 |
| [` public Guid VmId { get; init; } `](#api-be4330161282285a) | 请求导出的虚拟机稳定 GUID。 |

#### 详细说明

```csharp
public sealed record VmExportResult(bool Success, Guid VmId, string? OutputPath,
    bool OperationStateUnknown, IReadOnlyList<string> RetainedPaths, string? Error = null,
    int Code = 0, ApiErrorSource ErrorSource = ApiErrorSource.None, ApiErrorKind ErrorKind = ApiErrorKind.None)
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 是否确认导出成功并找到配置文件。 |
| `VmId` | 请求导出的虚拟机稳定 GUID。 |
| `OutputPath` | 成功完成的导出目录；未成功时为 null。 |
| `OperationStateUnknown` | 提交后尚不能确认原生导出终态；失败不代表后台任务已停止。 |
| `RetainedPaths` | 本次产生且保留供检查的文件或目录；未知状态时不得直接删除。 |
| `Error` | 失败原因，成功时为 null。 |
| `Code` | 底层错误码，需结合 ErrorSource 解释。 |
| `ErrorSource` | 错误码来源。 |
| `ErrorKind` | 与底层接口无关的失败类别。 |

#### 构造函数说明

<a id="api-5f4cc7ebcd00cc53"></a>

##### VmExportResult · 主构造器

```csharp
public VmExportResult(bool Success, Guid VmId, string? OutputPath, bool OperationStateUnknown, IReadOnlyList<string> RetainedPaths, string? Error = null, int Code = 0, ApiErrorSource ErrorSource = ApiErrorSource.None, ApiErrorKind ErrorKind = ApiErrorKind.None);
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 是否确认导出成功并找到配置文件。 |
| `VmId` | 请求导出的虚拟机稳定 GUID。 |
| `OutputPath` | 成功完成的导出目录；未成功时为 null。 |
| `OperationStateUnknown` | 提交后尚不能确认原生导出终态；失败不代表后台任务已停止。 |
| `RetainedPaths` | 本次产生且保留供检查的文件或目录；未知状态时不得直接删除。 |
| `Error` | 失败原因，成功时为 null。 |
| `Code` | 底层错误码，需结合 ErrorSource 解释。 |
| `ErrorSource` | 错误码来源。 |
| `ErrorKind` | 与底层接口无关的失败类别。 |

#### 属性说明

<a id="api-61ad046ed120ca66"></a>

##### Code · record 属性

```csharp
public int Code { get; init; }
```

底层错误码，需结合 ErrorSource 解释。

<a id="api-8f6d7bea64e07225"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

失败原因，成功时为 null。

<a id="api-36ed84b7fd15f122"></a>

##### ErrorKind · record 属性

```csharp
public ApiErrorKind ErrorKind { get; init; }
```

与底层接口无关的失败类别。

<a id="api-c2663c52e89d28a0"></a>

##### ErrorSource · record 属性

```csharp
public ApiErrorSource ErrorSource { get; init; }
```

错误码来源。

<a id="api-f867c5ab9bb2de94"></a>

##### OperationStateUnknown · record 属性

```csharp
public bool OperationStateUnknown { get; init; }
```

提交后尚不能确认原生导出终态；失败不代表后台任务已停止。

<a id="api-afa2590f6bd5cd25"></a>

##### OutputPath · record 属性

```csharp
public string? OutputPath { get; init; }
```

成功完成的导出目录；未成功时为 null。

<a id="api-46619c90331f5e04"></a>

##### RetainedPaths · record 属性

```csharp
public IReadOnlyList<string> RetainedPaths { get; init; }
```

本次产生且保留供检查的文件或目录；未知状态时不得直接删除。

<a id="api-7092cc30499c4ab1"></a>

##### Success · record 属性

```csharp
public bool Success { get; init; }
```

是否确认导出成功并找到配置文件。

<a id="api-be4330161282285a"></a>

##### VmId · record 属性

```csharp
public Guid VmId { get; init; }
```

请求导出的虚拟机稳定 GUID。

<a id="api-d0ef56e709d38d6f"></a>

### `VmExportService` 类

导出一台虚拟机的配置、磁盘、检查点和运行状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<VmExportResult> ExportAsync(Guid vmId, string destinationRoot, bool includeVirtualHardDisks, IReadOnlyCollection<VirtualHardDiskInfo> excludedVirtualHardDisks, VmExportCheckpointMode checkpointMode, CheckpointInfo? selectedCheckpoint, bool includeRuntimeState, IProgress<int>? progress = null) `](#api-a2a418f3df55cafb) | 将一台虚拟机的配置及选定的磁盘、检查点和运行状态导出到目录。 |
| [` public static async Task<ApiResponse<List<CheckpointInfo>>> GetCheckpointsAsync(Guid vmId) `](#api-b2c764360bd4d4db) | 读取指定虚拟机的检查点和父子关系。 |
| [` public static async Task<ApiResponse<List<VirtualHardDiskInfo>>> GetVirtualHardDisksAsync(Guid vmId) `](#api-6aacddf312b4fbd4) | 读取可用于导出排除选择的虚拟硬盘对象和文件路径。 |

#### 详细说明

```csharp
public static partial class VmExportService
```

#### 静态方法说明

<a id="api-a2a418f3df55cafb"></a>

##### ExportAsync · 方法

```csharp
public static Task<VmExportResult> ExportAsync(Guid vmId, string destinationRoot, bool includeVirtualHardDisks, IReadOnlyCollection<VirtualHardDiskInfo> excludedVirtualHardDisks, VmExportCheckpointMode checkpointMode, CheckpointInfo? selectedCheckpoint, bool includeRuntimeState, IProgress<int>? progress = null);
```

将一台虚拟机的配置及选定的磁盘、检查点和运行状态导出到目录。

调用顺序：

1. 指定目标 GUID 和已存在的输出根目录，决定是否包含磁盘、检查点和运行状态。
2. 需要排除磁盘或选择单个检查点时，先用 `GetVirtualHardDisksAsync` 或 `GetCheckpointsAsync` 取得对应对象。随后调用 `ExportAsync`。
3. 从结果的 `OutputPath` 取得导出目录；如需 ZIP，再执行可选打包步骤，并分别处理导出和归档结果。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `destinationRoot` | 已存在的导出根目录，服务在其中建立 GUID 命名的输出目录。 |
| `includeVirtualHardDisks` | 是否复制虚拟硬盘；单检查点模式会强制开启。 |
| `excludedVirtualHardDisks` | 由 GetVirtualHardDisksAsync 返回的待排除磁盘对象，可为空集合；Core 重新核对其所属虚拟机和挂接状态，不能与检查点模式组合。 |
| `checkpointMode` | 不含检查点、全部检查点或单个检查点的导出模式。 |
| `selectedCheckpoint` | 单检查点模式下由 GetCheckpointsAsync 返回的对象；Core 重新检查其仍存在且属于 vmId，其他模式可为 null。 |
| `includeRuntimeState` | 是否包含运行状态；单检查点模式会强制开启。 |
| `progress` | 可选进度接收器；null 时不通知。回调线程由具体 IProgress 实现决定。 |

返回：成功时 OutputPath 为实际导出目录；失败保留已产生路径及未知操作状态。单检查点模式强制包含磁盘与运行状态，不能与磁盘排除选择组合。

调用约定：输出目录由 `ExportAsync` 完成。打包是调用方选择的后续步骤：成功后将结果的 `OutputPath` 交给 `VmExportPackagingService`，指定 ZIP 路径和 `VmExportPackageOptions`；可选择压缩级别、AES-256 密码以及保留源目录或校验后删除。Core 不自动命名归档或串联多台导出。



选项重载默认均衡压缩、不加密、保留源目录。原 `VmExportPackageMode` 重载保持原行为：`Store` 不压缩，`Compress` 优先最小体积，两者都在校验成功后尝试清理专用导出暂存目录。归档结果分别报告 `SourceHandling`、`SourceDirectoryRemoved` 和清理错误。



本入口不提供取消原生导出。提交后的失败如果未能确认终态会标记 OperationStateUnknown，此时不得删除保留目录或假定任务已停止；Core 不自动清理失败输出。

<a id="api-b2c764360bd4d4db"></a>

##### GetCheckpointsAsync · 方法

```csharp
public static async Task<ApiResponse<List<CheckpointInfo>>> GetCheckpointsAsync(Guid vmId);
```

读取指定虚拟机的检查点和父子关系。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：包含检查点 GUID、父子关系、名称和创建时间的列表；单检查点导出原样传回所选对象，失败通过响应返回。

<a id="api-6aacddf312b4fbd4"></a>

##### GetVirtualHardDisksAsync · 方法

```csharp
public static async Task<ApiResponse<List<VirtualHardDiskInfo>>> GetVirtualHardDisksAsync(Guid vmId);
```

读取可用于导出排除选择的虚拟硬盘对象和文件路径。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：磁盘列表或查询错误；排除磁盘时原样传回所选对象，导出前会重新验证其所属虚拟机和当前挂接状态。

<a id="api-534d3f3efef99cf5"></a>

### `VmExportService.CheckpointInfo` 类

检查点身份和父子关系；单检查点导出使用查询返回的此对象。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public DateTime CreatedDate { get; } `](#api-ceb370bc581362e9) | 检查点创建时间。 |
| [` public string Id { get; } `](#api-2c1de765878bb1b5) | 检查点配置的 GUID 文本；主机未提供可识别的 GUID 时为空，仍可使用此对象导出。 |
| [` public string Name { get; } `](#api-d6182b0f64c3c0de) | 检查点的显示名称。 |
| [` public string? ParentId { get; } `](#api-8d3d4cebe260aac4) | 父检查点的配置 GUID 文本；根节点或无法识别父节点时为 null。 |

#### 详细说明

```csharp
public sealed class CheckpointInfo
```

#### 属性说明

<a id="api-ceb370bc581362e9"></a>

##### CreatedDate · 属性

```csharp
public DateTime CreatedDate { get; }
```

检查点创建时间。

<a id="api-2c1de765878bb1b5"></a>

##### Id · 属性

```csharp
public string Id { get; }
```

检查点配置的 GUID 文本；主机未提供可识别的 GUID 时为空，仍可使用此对象导出。

<a id="api-d6182b0f64c3c0de"></a>

##### Name · 属性

```csharp
public string Name { get; }
```

检查点的显示名称。

<a id="api-8d3d4cebe260aac4"></a>

##### ParentId · 属性

```csharp
public string? ParentId { get; }
```

父检查点的配置 GUID 文本；根节点或无法识别父节点时为 null。

<a id="api-25182f12944e0c0c"></a>

### `VmExportService.VirtualHardDiskInfo` 类

可供导出排除选择的虚拟硬盘；将查询返回的对象传回导出方法。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Path { get; } `](#api-31b4e5e0d3699678) | 虚拟硬盘的实际文件路径。 |

#### 详细说明

```csharp
public sealed class VirtualHardDiskInfo
```

#### 属性说明

<a id="api-31b4e5e0d3699678"></a>

##### Path · 属性

```csharp
public string Path { get; }
```

虚拟硬盘的实际文件路径。

<a id="api-cca6bbe6993f1c17"></a>

### `VmExportSourceHandling` 枚举

归档通过完整性校验后的源目录处理方式。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导出 |
| 源文件 | ` Management/VirtualMachines/Instances/Export/VmExportPackageOptions.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` DeleteAfterValidation = 1 `](#api-8bf00b186fb4f557) | 删除本次归档的原对象；源目录新增内容或清理失败时保留剩余内容并报告。 |
| [` Keep = 0 `](#api-f9f94ce741975f79) | 保留源目录和文件。 |

#### 详细说明

```csharp
public enum VmExportSourceHandling
```

#### 枚举值说明

<a id="api-8bf00b186fb4f557"></a>

##### DeleteAfterValidation · 枚举值

```csharp
DeleteAfterValidation = 1
```

删除本次归档的原对象；源目录新增内容或清理失败时保留剩余内容并报告。

<a id="api-f9f94ce741975f79"></a>

##### Keep · 枚举值

```csharp
Keep = 0
```

保留源目录和文件。

<a id="api-cfc336ca4a0f6001"></a>

### `VmGpuFields` 类

GPU 分区的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / GPU |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Gpu/VmGpuFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<string> DriverVersion `](#api-35aeeb6d43ed3896) | 宿主驱动版本：关联宿主 GPU 当前安装的驱动版本。 |
| [` public static readonly VmQueryField<string> HostDeviceInstanceId `](#api-94f32b210b07d29a) | 宿主 GPU 身份：当前 GPU 分区关联的宿主 GPU 设备标识。 |
| [` public static readonly VmQueryField<string> Manu `](#api-3c665de5fd0cb2da) | GPU 芯片商：关联宿主 GPU 的芯片制造商名称。 |
| [` public static readonly VmQueryField<ulong> MemoryBytes `](#api-b5f1b36203de8525) | 宿主专用显存容量：关联宿主 GPU 的 DXGI 专用显存容量，单位为字节。 |
| [` public static readonly VmQueryField<string> Name `](#api-00de41d78f54b325) | GPU 名称：当前 GPU 分区关联的宿主 GPU 显示名称。 |
| [` public static readonly VmQueryField<string> Vendor `](#api-8220467136b4cd66) | GPU 板卡商：关联宿主 GPU 的板卡厂商名称。 |

#### 详细说明

```csharp
public static class VmGpuFields
```

#### 字段与常量说明

<a id="api-35aeeb6d43ed3896"></a>

##### DriverVersion · 字段

```csharp
public static readonly VmQueryField<string> DriverVersion;
```

关联宿主 GPU 当前安装的驱动版本。

<a id="api-94f32b210b07d29a"></a>

##### HostDeviceInstanceId · 字段

```csharp
public static readonly VmQueryField<string> HostDeviceInstanceId;
```

当前 GPU 分区关联的宿主 GPU 设备标识。

<a id="api-3c665de5fd0cb2da"></a>

##### Manu · 字段

```csharp
public static readonly VmQueryField<string> Manu;
```

关联宿主 GPU 的芯片制造商名称。

<a id="api-b5f1b36203de8525"></a>

##### MemoryBytes · 字段

```csharp
public static readonly VmQueryField<ulong> MemoryBytes;
```

当前 GPU 分区所关联宿主 GPU 的专用显存容量，不包含共享或专用系统内存，也不是进程预算。0 是有效容量；未取得时字段状态为 Unavailable。虚拟机分区的显存配额由 GPU 分区设置提供。

**取值说明：** 单位为字节；`1073741824` 表示 1 GiB，`0` 是有效容量。

<a id="api-00de41d78f54b325"></a>

##### Name · 字段

```csharp
public static readonly VmQueryField<string> Name;
```

当前 GPU 分区关联的宿主 GPU 显示名称。

<a id="api-8220467136b4cd66"></a>

##### Vendor · 字段

```csharp
public static readonly VmQueryField<string> Vendor;
```

关联宿主 GPU 的板卡厂商名称。

<a id="api-7a118e7aa337efa0"></a>

### `VmGpuPerformanceFields` 类

GPU 性能的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / GPU |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Gpu/VmGpuPerformanceFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<double> Gpu3d `](#api-9c4117780634e25c) | GPU 三维引擎使用率：虚拟机 GPU 三维引擎的合计使用率，单位为 %。 |
| [` public static readonly VmQueryField<double> GpuCopy `](#api-96f2c2efce683a3c) | GPU 复制引擎使用率：虚拟机 GPU 复制引擎的合计使用率，单位为 %。 |
| [` public static readonly VmQueryField<double> GpuDecode `](#api-5ec2720e23b59885) | GPU 解码引擎使用率：虚拟机 GPU 视频解码引擎的合计使用率，单位为 %。 |
| [` public static readonly VmQueryField<double> GpuEncode `](#api-d084df9e88b6802d) | GPU 编码引擎使用率：虚拟机 GPU 视频编码引擎的合计使用率，单位为 %。 |
| [` public static readonly VmQueryField<bool> IsDriverBound `](#api-dfb2d63923a092ad) | 本次是否枚举到归属于虚拟机进程的 GPU 引擎计数器实例。 |

#### 详细说明

```csharp
public static class VmGpuPerformanceFields
```

#### 字段与常量说明

<a id="api-9c4117780634e25c"></a>

##### Gpu3d · 字段

```csharp
public static readonly VmQueryField<double> Gpu3d;
```

虚拟机 GPU 三维引擎的合计使用率，单位为 %。

**单位与格式：** %

**取值说明：** 对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。

<a id="api-96f2c2efce683a3c"></a>

##### GpuCopy · 字段

```csharp
public static readonly VmQueryField<double> GpuCopy;
```

虚拟机 GPU 复制引擎的合计使用率，单位为 %。

**单位与格式：** %

**取值说明：** 对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。

<a id="api-5ec2720e23b59885"></a>

##### GpuDecode · 字段

```csharp
public static readonly VmQueryField<double> GpuDecode;
```

虚拟机 GPU 视频解码引擎的合计使用率，单位为 %。

**单位与格式：** %

**取值说明：** 对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。

<a id="api-d084df9e88b6802d"></a>

##### GpuEncode · 字段

```csharp
public static readonly VmQueryField<double> GpuEncode;
```

虚拟机 GPU 视频编码引擎的合计使用率，单位为 %。

**单位与格式：** %

**取值说明：** 对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。

<a id="api-dfb2d63923a092ad"></a>

##### IsDriverBound · 字段

```csharp
public static readonly VmQueryField<bool> IsDriverBound;
```

存在匹配的引擎实例即为 true，即使使用率为 0%；不证明来宾驱动已安装、正常工作或正在执行 GPU 负载。

**取值说明：** `true`：找到匹配的 GPU 引擎实例；`false`：未找到。

<a id="api-f4ea588e1d3931d7"></a>

### `VmGpuRepairService` 类

失效 GPU-PV(GPU Partition 钉死的物理 GPU 路径在当前主机失配)的检测与修复。 此类记录不会出现在 Get-VMGpuPartitionAdapter 或 Msvm_GpuPartitionSettingData 中， 因此需要通过统一 VmcxDocument / VmcxEditSession 编辑对应的 .vmcx 文件。 检测读取文件快照，修复通过 VmDataStore 提交配置修改。 失配判定使用完整路径精确比对；GPU-PV 绑定的是某块卡的 完整 GPUPARAV 路径(含 PCIe 位置段),主机重启/重插后即使同一张卡仍在,位置段也可能变 → 路径失配 → 起不来。 失配后再比对硬件标识(VEN/DEV/SUBSYS/REV)区分: &#183; 只有一个同标识候选：更新为该候选路径；多个候选必须由调用方选择； &#183; 池里无同标识的卡：删除失效的 GPU-PV 设备。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / GPU；管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Repair/VmGpuRepairService.Selection.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse<IReadOnlyList<StaleGpuPartition>>> FindStaleGpuPartitionsAsync(Guid vmId) `](#api-bdd690a0e0d67d32) | 扫描注册虚拟机的结构残缺或宿主 GPU 绑定失效的分区设备。 |
| [` public static async Task<ApiResponse<RepairResult>> RepairAsync(Guid vmId, IEnumerable<StaleGpuPartition> targets) `](#api-51f294b67b59bbe8) | 按扫描结果及显卡选择修复 GPU 分区；零候选或结构残缺时移除失效定义，多候选未选择时不执行。 |
| [` public static async Task<ApiResponse<ConfigurationFile>> ResolveVmcxAsync(Guid vmId) `](#api-b2b874d6f85a0ba0) | 按稳定身份定位注册虚拟机的配置文件，并读取当前运行状态。 |

#### 详细说明

```csharp
public static partial class VmGpuRepairService
```

示例：

调用方已确认修复范围后，为每个多候选设备取得用户选择。choose 返回候选 Id，不应默认取列表第一项。



```csharp
using ExHyperV.Services;
using ExHyperV.Tools;
public static class GpuRepairChoiceExample
{
    public static async Task<ApiResponse<VmGpuRepairService.RepairResult>> RepairSelectedAsync(
        Guid vmId, IReadOnlyList<VmGpuRepairService.StaleGpuPartition> scanned,
        Func<VmGpuRepairService.StaleGpuPartition, Task<string>> choose)
    {
        var targets = new List<VmGpuRepairService.StaleGpuPartition>();
        foreach (var target in scanned)
            targets.Add(target.RequiresSelection ? target.SelectCandidate(await choose(target)) : target);
        return await VmGpuRepairService.RepairAsync(vmId, targets);
    }

    public static async Task ResumeApprovedPowerRepairAsync(VmPowerOperationSession session,
        Func<VmGpuRepairService.StaleGpuPartition, Task<string>> choose)
    {
        var confirmation = session.Confirmation ?? throw new InvalidOperationException("尚无修复确认。");
        var selections = new Dictionary<Guid, string>();
        foreach (var target in confirmation.GpuTargets.Where(t => t.RequiresSelection))
            selections.Add(Guid.Parse(target.Instance.Trim('{', '}', '_', ' ')), await choose(target));
        await session.SubmitDecisionAsync(confirmation.RequestId, true, selections);
        if (session.Stage == VmPowerOperationStage.ReadyToRetry) await session.AdvanceAsync();
    }
}
```

#### 静态方法说明

<a id="api-bdd690a0e0d67d32"></a>

##### FindStaleGpuPartitionsAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyList<StaleGpuPartition>>> FindStaleGpuPartitionsAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / GPU。

扫描注册虚拟机的结构残缺或宿主 GPU 绑定失效的分区设备。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：完整扫描结果；配置读取或必要的 GPU 池查询失败时返回失败，不把未完成的扫描表示为成功空列表。

调用约定：返回全部匹配候选。RequiresSelection 为 true 时，由调用方选择 Candidates 中的一项并调用 SelectCandidate；扫描和选择本身均不修改虚拟机。

<a id="api-51f294b67b59bbe8"></a>

##### RepairAsync · 方法

```csharp
public static async Task<ApiResponse<RepairResult>> RepairAsync(Guid vmId, IEnumerable<StaleGpuPartition> targets);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

按扫描结果及显卡选择修复 GPU 分区；零候选或结构残缺时移除失效定义，多候选未选择时不执行。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；必须完全关机。 |
| `targets` | 扫描目标或其 SelectCandidate 返回的新目标。所提交目标中的全部多候选项都必须完成选择；空集合表示无需修复。 |

返回：成功时返回整批重绑与删除计数；失败不提供虚假的零计数，提交故障的备份位置包含在说明中。

调用约定：全部目标先校验，再准备文件修改。选中的卡失效时返回 Conflict，不替换成另一张卡或删除该设备。单候选变为多候选时要求重新扫描选择。显式选择仍在当前候选中时，不受候选枚举顺序影响。原生提交失败可能留下需要恢复的配置，请保留错误中的备份。

<a id="api-b2b874d6f85a0ba0"></a>

##### ResolveVmcxAsync · 方法

```csharp
public static async Task<ApiResponse<ConfigurationFile>> ResolveVmcxAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

按稳定身份定位注册虚拟机的配置文件，并读取当前运行状态。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：现存配置文件与状态；查询失败或文件缺失均返回失败，不返回拼凑默认路径。

<a id="api-423609453ea668ba"></a>

### `VmGpuRepairService.ConfigurationFile` record 类

注册虚拟机的配置文件位置与当前运行状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Repair/VmGpuRepairService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ConfigurationFile(Guid VmId, string Path, VmState State) `](#api-ffc08098a4e71a9b) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Path { get; init; } `](#api-4ff997604e83ec19) | 现存配置文件的绝对路径。 |
| [` public VmState State { get; init; } `](#api-e986305a9b8d9352) | 本次查询的类型化运行状态。 |
| [` public Guid VmId { get; init; } `](#api-610756c0060cce4a) | 虚拟机的稳定 GUID。 |

#### 详细说明

```csharp
public sealed record ConfigurationFile(Guid VmId, string Path, VmState State)
```

| 参数 | 说明 |
| --- | --- |
| `VmId` | 虚拟机的稳定 GUID。 |
| `Path` | 现存配置文件的绝对路径。 |
| `State` | 本次查询的类型化运行状态。 |

#### 构造函数说明

<a id="api-ffc08098a4e71a9b"></a>

##### ConfigurationFile · 主构造器

```csharp
public ConfigurationFile(Guid VmId, string Path, VmState State);
```

| 参数 | 说明 |
| --- | --- |
| `VmId` | 虚拟机的稳定 GUID。 |
| `Path` | 现存配置文件的绝对路径。 |
| `State` | 本次查询的类型化运行状态。 |

#### 属性说明

<a id="api-4ff997604e83ec19"></a>

##### Path · record 属性

```csharp
public string Path { get; init; }
```

现存配置文件的绝对路径。

<a id="api-e986305a9b8d9352"></a>

##### State · record 属性

```csharp
public VmState State { get; init; }
```

本次查询的类型化运行状态。

<a id="api-610756c0060cce4a"></a>

##### VmId · record 属性

```csharp
public Guid VmId { get; init; }
```

虚拟机的稳定 GUID。

<a id="api-432c27a8ad57f0a0"></a>

### `VmGpuRepairService.RebindCandidate` record 类

当前宿主上可用于重绑的具体 GPU；由扫描结果提供。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Repair/VmGpuRepairService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DeviceInstanceId { get; } `](#api-13bfdd5712f3484e) | PnP 设备实例标识，包含区分同型号显卡的位置段；可与 HostGpuInfo.InstanceId 比对以取得名称。 |
| [` public string Id { get; } `](#api-c9eb5e63edc0f794) | 本候选的完整 GPU 分区路径，用作 SelectCandidate 的不透明标识；只代表当前宿主的这次设备身份，不保证跨宿主或重新枚举后不变。 |

#### 详细说明

```csharp
public sealed record RebindCandidate
```

#### 属性说明

<a id="api-13bfdd5712f3484e"></a>

##### DeviceInstanceId · 属性

```csharp
public string DeviceInstanceId { get; }
```

PnP 设备实例标识，包含区分同型号显卡的位置段；可与 HostGpuInfo.InstanceId 比对以取得名称。无法按标准分区路径解析时保留原路径。

<a id="api-c9eb5e63edc0f794"></a>

##### Id · 属性

```csharp
public string Id { get; }
```

本候选的完整 GPU 分区路径，用作 SelectCandidate 的不透明标识；只代表当前宿主的这次设备身份，不保证跨宿主或重新枚举后不变。

<a id="api-4e53775e4963caa5"></a>

### `VmGpuRepairService.RepairResult` record 类

本次提交完成的 GPU 分区修复数量。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Repair/VmGpuRepairService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public RepairResult(int Rebound, int Removed) `](#api-e563c04a4142fbf9) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Rebound { get; init; } `](#api-005c29f14b4d892e) | 重新绑定宿主 GPU 的设备数量。 |
| [` public int Removed { get; init; } `](#api-3a95f9724d1a734b) | 删除失效设备定义的数量。 |

#### 详细说明

```csharp
public sealed record RepairResult(int Rebound, int Removed)
```

| 参数 | 说明 |
| --- | --- |
| `Rebound` | 重新绑定宿主 GPU 的设备数量。 |
| `Removed` | 删除失效设备定义的数量。 |

#### 构造函数说明

<a id="api-e563c04a4142fbf9"></a>

##### RepairResult · 主构造器

```csharp
public RepairResult(int Rebound, int Removed);
```

| 参数 | 说明 |
| --- | --- |
| `Rebound` | 重新绑定宿主 GPU 的设备数量。 |
| `Removed` | 删除失效设备定义的数量。 |

#### 属性说明

<a id="api-005c29f14b4d892e"></a>

##### Rebound · record 属性

```csharp
public int Rebound { get; init; }
```

重新绑定宿主 GPU 的设备数量。

<a id="api-3a95f9724d1a734b"></a>

##### Removed · record 属性

```csharp
public int Removed { get; init; }
```

删除失效设备定义的数量。

<a id="api-93567a4f8b8309b4"></a>

### `VmGpuRepairService.StaleGpuPartition` record 类

扫描得到的失效 GPU 分区配置；可交给 RepairAsync 修复。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Repair/VmGpuRepairService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool CanRebind { get; } `](#api-39fa7adf9278232b) | 是否找到重绑候选；多候选时仍需先选择，不能仅凭此属性直接批准修复。 |
| [` public IReadOnlyList<RebindCandidate> Candidates { get; } `](#api-e9563146867984a5) | 具有相同硬件标识的全部候选，只读且已按完整身份去重；排序仅用于展示，不表示推荐优先级。 |
| [` public string Instance { get; } `](#api-68fa39fc24fc8356) | 该设备配置的实例 GUID 文本。 |
| [` public bool IsStructurallyIncomplete { get; } `](#api-e500861aa25ff6e2) | 设备缺少核心字段，无法按完整 GPU 分区设备使用。 |
| [` public bool RequiresSelection { get; } `](#api-8c5f3b044a6e02f9) | 有多个候选且尚未明确选择；此时修复返回 AmbiguousTarget，不改绑也不删除设备。 |
| [` public RebindCandidate? SelectedCandidate { get; } `](#api-811a716ead8becd4) | 本次将使用的候选；单候选自动采用，多候选经 SelectCandidate 选择，零候选或待选择时为 null。 |
| [` public int VdevNumber { get; } `](#api-5142b6c69ab8dea1) | VMCX 中的虚拟设备编号。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public StaleGpuPartition SelectCandidate(string candidateId) `](#api-dfade3408006cb2f) | 选择一张候选显卡，返回携带选择的新修复目标；原扫描结果和宿主均不改变。 |

#### 详细说明

```csharp
public sealed record StaleGpuPartition
```

#### 属性说明

<a id="api-39fa7adf9278232b"></a>

##### CanRebind · 属性

```csharp
public bool CanRebind { get; }
```

是否找到重绑候选；多候选时仍需先选择，不能仅凭此属性直接批准修复。

<a id="api-e9563146867984a5"></a>

##### Candidates · 属性

```csharp
public IReadOnlyList<RebindCandidate> Candidates { get; }
```

具有相同硬件标识的全部候选，只读且已按完整身份去重；排序仅用于展示，不表示推荐优先级。

<a id="api-68fa39fc24fc8356"></a>

##### Instance · 属性

```csharp
public string Instance { get; }
```

该设备配置的实例 GUID 文本。

<a id="api-e500861aa25ff6e2"></a>

##### IsStructurallyIncomplete · 属性

```csharp
public bool IsStructurallyIncomplete { get; }
```

设备缺少核心字段，无法按完整 GPU 分区设备使用。

<a id="api-8c5f3b044a6e02f9"></a>

##### RequiresSelection · 属性

```csharp
public bool RequiresSelection { get; }
```

有多个候选且尚未明确选择；此时修复返回 AmbiguousTarget，不改绑也不删除设备。

<a id="api-811a716ead8becd4"></a>

##### SelectedCandidate · 属性

```csharp
public RebindCandidate? SelectedCandidate { get; }
```

本次将使用的候选；单候选自动采用，多候选经 SelectCandidate 选择，零候选或待选择时为 null。

<a id="api-5142b6c69ab8dea1"></a>

##### VdevNumber · 属性

```csharp
public int VdevNumber { get; }
```

VMCX 中的虚拟设备编号。

#### 方法说明

<a id="api-dfade3408006cb2f"></a>

##### SelectCandidate · 方法

```csharp
public StaleGpuPartition SelectCandidate(string candidateId);
```

选择一张候选显卡，返回携带选择的新修复目标；原扫描结果和宿主均不改变。

| 参数 | 说明 |
| --- | --- |
| `candidateId` | Candidates 中某一项的 Id；不能传型号名称或数组序号。 |

返回：可交给 RepairAsync 的新目标；执行时还会核对该候选仍然可用。

- 异常 `ArgumentException`：标识为空，或不是本目标的候选。

<a id="api-491dd675104b9ad1"></a>

### `VmGpuService` 类

虚拟 GPU 分配及来宾驱动部署入口。单项方法提供底层能力；需要分步确认的完整流程使用 GpuDeploymentSession。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / GPU；管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Assignment/VmGpuService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmGpuService(VmQueryService queryService) `](#api-a41e3bd35586bfde) | 建立 GPU 服务，复用传入的虚拟机查询服务。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public async Task<ApiResponse> AssignGpuPartitionAsync(Guid vmId, HostGpuInfo gpu) `](#api-5b2aa9a823caac5e) | 为虚拟机分配所选宿主 GPU 的分区；执行前重新解析当前可分区资源。 |
| [` public async Task<ApiResponse<bool>> CheckVmForGpuAsync(Guid vmId) `](#api-12d4bfcddbae5680) | 检查虚拟机来宾缓存控制及高位 MMIO 是否满足当前 GPU 配置要求，不执行配置写入。 |
| [` public async Task<List<LinuxGpuDeploymentScript>> GetAvailableScriptsAsync() `](#api-369e7cd15ebcac51) | 从在线索引读取可选 Linux GPU 部署脚本。 |
| [` public async Task<ApiResponse<IReadOnlyList<VmGpuAssignment>>> GetVmGpuAssignmentsAsync(Guid vmId) `](#api-49e6c19e632fcd5c) | 按稳定身份读取虚拟机 GPU 分配；查询失败不伪装为空清单。 |
| [` public async Task<ApiResponse> OptimizeVmForGpuAsync(Guid vmId) `](#api-dd4d875bda997af8) | 配置 GPU 所需的来宾缓存控制和 MMIO 空间。 |
| [` public Task<string> ProvisionLinuxGpuAsync(string vmName, string gpuInstancePath, string gpuManufacturer, LinuxGpuDeploymentScript script, SshCredentials credentials, Action<string> progressCallback, CancellationToken ct) `](#api-d053e61d72713b5a) | 通过 SSH 上传 GPU 文件并执行所选 Linux 部署脚本，必要时启动或重启来宾。 |
| [` public Task<ApiResponse> RemoveGpuPartitionAsync(Guid vmId, VmGpuAssignment assignment) `](#api-497cbbfa6e9b9a6b) | 移除所查询的 GPU 分配，执行前重新核对其属于目标虚拟机。 |
| [` public async Task<VmPartitionScanResult> ScanVmPartitionsAsync(Guid vmId, CancellationToken cancellationToken = default) `](#api-afdd19aa765db4a2) | 在目标虚拟机关闭时扫描磁盘分区，并恢复扫描前的宿主磁盘状态。 |
| [` public async Task<ApiResponse> SyncWindowsDriversAsync(Guid vmId, string gpuInstancePath, string gpuManu, DiskPartitionInfo selectedPartition, Action<string> progressCallback = null) `](#api-bf4324136f4573c6) | 向指定离线 Windows 系统分区同步宿主 GPU 驱动包及相关文件。 |

#### 详细说明

```csharp
public class VmGpuService
```

#### 构造函数说明

<a id="api-a41e3bd35586bfde"></a>

##### VmGpuService · 构造器

```csharp
public VmGpuService(VmQueryService queryService);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

建立 GPU 服务，复用传入的虚拟机查询服务。

| 参数 | 说明 |
| --- | --- |
| `queryService` | 用于虚拟机身份和来宾网络等查询的服务。 |

#### 方法说明

<a id="api-5b2aa9a823caac5e"></a>

##### AssignGpuPartitionAsync · 方法

```csharp
public async Task<ApiResponse> AssignGpuPartitionAsync(Guid vmId, HostGpuInfo gpu);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

为虚拟机分配所选宿主 GPU 的分区；执行前重新解析当前可分区资源。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `gpu` | GetHostGpusAsync 返回的宿主 GPU，按其 PnP 设备身份重新查询。 |

返回：添加和后续验证结果；验证失败时新适配器可能已经存在，不自动回滚。

调用约定：此单项入口不自动清理已创建的设备。创建结果或资源归属无法确认时返回 OutcomeUnknown；已确认失败且留有本次设备时返回 CleanupRequired。两种情况都保留设备；需要分步核对和清理本次创建资源时使用 GpuDeploymentSession。

<a id="api-12d4bfcddbae5680"></a>

##### CheckVmForGpuAsync · 方法

```csharp
public async Task<ApiResponse<bool>> CheckVmForGpuAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / GPU。

检查虚拟机来宾缓存控制及高位 MMIO 是否满足当前 GPU 配置要求，不执行配置写入。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：数据为 true 表示满足条件，false 表示需要配置；读取错误为失败响应。

<a id="api-369e7cd15ebcac51"></a>

##### GetAvailableScriptsAsync · 方法

```csharp
public async Task<List<LinuxGpuDeploymentScript>> GetAvailableScriptsAsync();
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

从在线索引读取可选 Linux GPU 部署脚本。

返回：新的脚本列表；请求超时、解析或网络失败时返回空列表。

调用约定：索引请求使用 5 秒超时；空列表不能区分没有脚本和在线查询失败。

<a id="api-49e6c19e632fcd5c"></a>

##### GetVmGpuAssignmentsAsync · 方法

```csharp
public async Task<ApiResponse<IReadOnlyList<VmGpuAssignment>>> GetVmGpuAssignmentsAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / GPU。

按稳定身份读取虚拟机 GPU 分配；查询失败不伪装为空清单。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：分配快照或查询错误；缺少宿主显示信息时仍保留设备身份。

调用约定：旧宿主未返回可用的 HostResource 时，此入口可能用第一张可分区 GPU 补充宿主身份和显示信息；这些补充值不能证明实际分配到该卡。精确关联应使用 VmGpuFields 的逐字段查询，并检查结果是否可用。

<a id="api-dd4d875bda997af8"></a>

##### OptimizeVmForGpuAsync · 方法

```csharp
public async Task<ApiResponse> OptimizeVmForGpuAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

配置 GPU 所需的来宾缓存控制和 MMIO 空间。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：配置执行结果；不自动回滚已完成的宿主探测或电源变化。

调用约定：可能复用宿主 MMIO 检测结果；没有结果时，检测可能启动并强制关闭目标虚拟机。调用完整部署流程可在 Core 的确认步骤后执行这些操作。

<a id="api-d053e61d72713b5a"></a>

##### ProvisionLinuxGpuAsync · 方法

```csharp
public Task<string> ProvisionLinuxGpuAsync(string vmName, string gpuInstancePath, string gpuManufacturer, LinuxGpuDeploymentScript script, SshCredentials credentials, Action<string> progressCallback, CancellationToken ct);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

通过 SSH 上传 GPU 文件并执行所选 Linux 部署脚本，必要时启动或重启来宾。

| 参数 | 说明 |
| --- | --- |
| `vmName` | 目标虚拟机名称。 |
| `gpuInstancePath` | 宿主 GPU 设备路径，用于定位驱动包。 |
| `gpuManufacturer` | GPU 芯片商名称。 |
| `script` | 下载并执行的部署脚本描述。 |
| `credentials` | SSH 凭据及脚本选项；本方法会改写 Host 为选出的 IPv4 地址。 |
| `progressCallback` | 进展文本回调，应快速返回且不抛出异常；界面更新需调度到界面线程。 |
| `ct` | 协作式取消令牌，在等待和部分步骤之间检查；SSH 命令和文件传输继续执行至完成。 |

返回：成功时返回 OK，否则通常返回错误说明；取消也可能转换为失败文本。

调用约定：脚本可修改来宾软件包、内核模块、服务和环境，不是原子事务，也没有文件级回滚。SSH 命令可长时间运行；此方法不接管宿主剪贴板或交互登录。

<a id="api-497cbbfa6e9b9a6b"></a>

##### RemoveGpuPartitionAsync · 方法

```csharp
public Task<ApiResponse> RemoveGpuPartitionAsync(Guid vmId, VmGpuAssignment assignment);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

移除所查询的 GPU 分配，执行前重新核对其属于目标虚拟机。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `assignment` | GetVmGpuAssignmentsAsync 返回的分配对象。 |

返回：移除结果；对象归属不符、设备已移除或查询失败均明确报告。

<a id="api-afdd19aa765db4a2"></a>

##### ScanVmPartitionsAsync · 方法

```csharp
public async Task<VmPartitionScanResult> ScanVmPartitionsAsync(Guid vmId, CancellationToken cancellationToken = default);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

在目标虚拟机关闭时扫描磁盘分区，并恢复扫描前的宿主磁盘状态。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `cancellationToken` | 等待资源锁及磁盘之间检查取消；已开始的磁盘操作完成恢复后才返回。 |

返回：分区、扫描错误和独立的恢复错误；失败时分区可能不完整。

调用约定：同磁盘或映像的 Core 操作跨进程排队，锁覆盖原状态读取、扫描及恢复。既有挂载只借用，本次新增挂载才卸载；物理盘按原状态恢复并覆盖期间外部状态修改，恢复前核对设备身份，不修改替代设备。

<a id="api-bf4324136f4573c6"></a>

##### SyncWindowsDriversAsync · 方法

```csharp
public async Task<ApiResponse> SyncWindowsDriversAsync(Guid vmId, string gpuInstancePath, string gpuManu, DiskPartitionInfo selectedPartition, Action<string> progressCallback = null);
```

功能归属：管理层面 / 虚拟机 / 修改 / GPU。

向指定离线 Windows 系统分区同步宿主 GPU 驱动包及相关文件。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 已关闭的目标虚拟机的稳定 GUID。 |
| `gpuInstancePath` | 宿主 GPU 设备实例或分区资源路径。 |
| `gpuManu` | GPU 芯片商名称，用于厂商相关文件处理。 |
| `selectedPartition` | 同一虚拟机由 ScanVmPartitionsAsync 产生的分区；磁盘来源不得改写。执行前重新核对挂接关系与物理盘身份。 |
| `progressCallback` | 可选的进展文本回调，应快速返回且不抛出异常；界面更新需调度到界面线程。 |

返回：主要驱动同步是否成功及消息；可选文件和注册表增强问题通过 progressCallback 报告警告，不单独使主流程失败。

调用约定：会临时挂载 VHD 或断开物理盘直通、联机并分配盘符。同磁盘或映像的 Core 操作跨进程排队，锁覆盖使用、卸载及重接；重接后再次恢复原宿主磁盘状态，覆盖期间的外部状态修改，身份不符时停止恢复。注册表增强的加载、写入或卸载失败仅告警，继续部署及原磁盘恢复流程；调用方需提供 progressCallback 接收这些警告。磁盘清理或重接失败返回 CleanupRequired，并保留业务错误；不回滚已复制驱动文件。

<a id="api-e8293dd4c364f26d"></a>

### `VmGroupFields` 类

虚拟机直接所属分组的查询成员。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroupFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<Guid> Id `](#api-ac067cd5ec029afa) | 分组身份：虚拟机直接所属分组的稳定标识。 |
| [` public static readonly VmQueryField<string> Name `](#api-a3cdbe318d0b22ba) | 分组名称：虚拟机直接所属分组的显示名称。 |

#### 详细说明

```csharp
public static class VmGroupFields
```

#### 字段与常量说明

<a id="api-ac067cd5ec029afa"></a>

##### Id · 字段

```csharp
public static readonly VmQueryField<Guid> Id;
```

虚拟机直接所属分组的唯一标识。

**取值说明：** GUID 文本由 32 个十六进制字符和 4 个连字符组成，按 8-4-4-4-12 分组。

<a id="api-a3cdbe318d0b22ba"></a>

##### Name · 字段

```csharp
public static readonly VmQueryField<string> Name;
```

虚拟机直接所属分组的显示名称。

<a id="api-cc37958683c518ee"></a>

### `VmGroupMoveResult` record 类

分组移动后的目标标识，以及目标组是否发生重建。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroupMoveResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmGroupMoveResult(Guid? RequestedTargetId, Guid? TargetId) `](#api-881aa9ea51013768) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public Guid? RequestedTargetId { get; init; } `](#api-314de8a5e29225d8) | 调用方最初指定的目标分组 GUID；未指定目标组时为 null。 |
| [` public Guid? TargetId { get; init; } `](#api-d458e42ed8c3bae9) | 实际接收成员的分组 GUID；目标空组发生类型转换时可能与原 GUID 不同。 |
| [` public bool TargetRecreated { get; } `](#api-f4e319be55d78543) | 目标组是否因成员类型改变而重建。 |

#### 详细说明

```csharp
public sealed record VmGroupMoveResult(Guid? RequestedTargetId, Guid? TargetId)
```

| 参数 | 说明 |
| --- | --- |
| `RequestedTargetId` | 调用方最初指定的目标分组 GUID；未指定目标组时为 null。 |
| `TargetId` | 实际接收成员的分组 GUID；目标空组发生类型转换时可能与原 GUID 不同。 |

#### 构造函数说明

<a id="api-881aa9ea51013768"></a>

##### VmGroupMoveResult · 主构造器

```csharp
public VmGroupMoveResult(Guid? RequestedTargetId, Guid? TargetId);
```

| 参数 | 说明 |
| --- | --- |
| `RequestedTargetId` | 调用方最初指定的目标分组 GUID；未指定目标组时为 null。 |
| `TargetId` | 实际接收成员的分组 GUID；目标空组发生类型转换时可能与原 GUID 不同。 |

#### 属性说明

<a id="api-314de8a5e29225d8"></a>

##### RequestedTargetId · record 属性

```csharp
public Guid? RequestedTargetId { get; init; }
```

调用方最初指定的目标分组 GUID；未指定目标组时为 null。

<a id="api-d458e42ed8c3bae9"></a>

##### TargetId · record 属性

```csharp
public Guid? TargetId { get; init; }
```

实际接收成员的分组 GUID；目标空组发生类型转换时可能与原 GUID 不同。

<a id="api-f4e319be55d78543"></a>

##### TargetRecreated · 属性

```csharp
public bool TargetRecreated { get; }
```

目标组是否因成员类型改变而重建。重建后使用 TargetId 作为新的目标标识。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 目标组是否因成员类型改变而重建。重建后使用 TargetId 作为新的目标标识。 |
| 取值与默认 | true：目标组已重建；false：目标组标识保持不变。 |

<a id="api-cafcb4c2db564ebe"></a>

### `VmGroupMoveRules` 类

仅根据分组快照判断移动是否合法，不访问或修改宿主。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 修改 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroupMoveRules.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanMove(VmGroupSnapshot snapshot, IReadOnlyList<VmGroupMove> items, Guid? targetId) `](#api-484b229460918b45) | 在分组快照上检查一批虚拟机或分组是否能移动到目标组。 |

#### 详细说明

```csharp
public static class VmGroupMoveRules
```

#### 静态方法说明

<a id="api-484b229460918b45"></a>

##### CanMove · 方法

```csharp
public static bool CanMove(VmGroupSnapshot snapshot, IReadOnlyList<VmGroupMove> items, Guid? targetId);
```

在分组快照上检查一批虚拟机或分组是否能移动到目标组。

| 参数 | 说明 |
| --- | --- |
| `snapshot` | 查询时的分组和成员快照。 |
| `items` | 准备移动的虚拟机或分组身份，不应混合两种类型。 |
| `targetId` | 目标分组 GUID；null 表示移出当前分组。 |

返回：当前快照满足类型、层级和循环限制时为 true；不锁定主机状态。

<a id="api-99e3f6668ba1efe6"></a>

### `VmGroupService` 类

通过 Hyper-V 分组接口读取及修改分组；本进程内串行执行写入。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / 分组；管理层面 / 宿主 / 修改 / 分组 |
| 源文件 | ` Management/Host/Groups/VmGroupService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<Guid> CreateAsync(string name, VmGroupKind kind, Guid? parentId) `](#api-62c865a8bbe08991) | 创建虚拟机分组或管理分组，并按需加入父分组。 |
| [` public static async Task DeleteAsync(Guid id) `](#api-aeb342d87c9d6bac) | 删除指定分组及其父子成员关系，保留成员虚拟机和子分组本身。 |
| [` public static async Task<VmGroupMoveResult> MoveAsync(IReadOnlyList<VmGroupMove> moves, Guid? targetId) `](#api-7bab9d8de671d5ae) | 串行修改虚拟机或子分组归属，并在失败时尝试恢复已执行的变更。 |
| [` public static Task<VmGroupSnapshot> ReadAsync() `](#api-736e5ab8fa340994) | 读取当前虚拟机分组、成员及层级关系。 |
| [` public static async Task RenameAsync(Guid id, string name) `](#api-5b9db5323811a972) | 按 GUID 重命名分组；失败以异常报告。 |

#### 详细说明

```csharp
public static class VmGroupService
```

#### 静态方法说明

<a id="api-62c865a8bbe08991"></a>

##### CreateAsync · 方法

```csharp
public static async Task<Guid> CreateAsync(string name, VmGroupKind kind, Guid? parentId);
```

功能归属：管理层面 / 宿主 / 修改 / 分组。

创建虚拟机分组或管理分组，并按需加入父分组。

| 参数 | 说明 |
| --- | --- |
| `name` | 分组显示名称。 |
| `kind` | 组类型：容纳虚拟机的集合组或容纳子组的管理组。 |
| `parentId` | 可选父分组 GUID；null 表示创建顶层分组。 |

返回：新分组 GUID；失败抛出异常，回滚失败时可能包含聚合错误。底层结果尚未确认时保留现状，不自动补偿或重试。

调用约定：指定的父组若为空的虚拟机组，会先重建为容纳子组的管理组，父组 GUID 随之改变；本返回值只有新建子组的 GUID，调用后须重新读取父组关系。后续创建失败也不保证恢复原父组身份。

- 异常 `CoreOperationException`：创建或补偿操作的结果尚未确认；Result.ErrorKind 为 OutcomeUnknown，保留现状。

<a id="api-aeb342d87c9d6bac"></a>

##### DeleteAsync · 方法

```csharp
public static async Task DeleteAsync(Guid id);
```

功能归属：管理层面 / 宿主 / 修改 / 分组。

删除指定分组及其父子成员关系，保留成员虚拟机和子分组本身。

| 参数 | 说明 |
| --- | --- |
| `id` | 目标分组 GUID。 |

- 异常 `CoreOperationException`：删除或恢复成员关系的结果尚未确认；停止后续补偿，需先核对实际状态。

<a id="api-7bab9d8de671d5ae"></a>

##### MoveAsync · 方法

```csharp
public static async Task<VmGroupMoveResult> MoveAsync(IReadOnlyList<VmGroupMove> moves, Guid? targetId);
```

功能归属：管理层面 / 宿主 / 修改 / 分组。

串行修改虚拟机或子分组归属，并在失败时尝试恢复已执行的变更。

| 参数 | 说明 |
| --- | --- |
| `moves` | 待移动的虚拟机或分组身份列表。 |
| `targetId` | 目标分组 GUID；null 表示移出当前分组。 |

返回：调用时选择的目标 GUID 与实际目标 GUID；目标空组被重建时两者不同，后续操作应使用 TargetId。移出当前分组时两者均为 null。

调用约定：只移除每项 SourceParentId 指定的源关系，未指定的其他父组关系保留。移动进入类型不匹配的空组时会重建该组并改变 GUID；成功结果返回此次身份变化，成员列表仍需重读。成员移动失败也可能已完成空组重建；异常不会返回该身份映射。尽力恢复失败可形成 AggregateException，底层操作或恢复结果未知时停止后续补偿。

- 异常 `CoreOperationException`：成员修改或补偿操作的结果尚未确认；停止后续修改，需先核对实际归属。

<a id="api-736e5ab8fa340994"></a>

##### ReadAsync · 方法

```csharp
public static Task<VmGroupSnapshot> ReadAsync();
```

功能归属：管理层面 / 宿主 / 查询 / 分组。

读取当前虚拟机分组、成员及层级关系。

返回：分组快照；底层查询失败以异常报告。

<a id="api-5b9db5323811a972"></a>

##### RenameAsync · 方法

```csharp
public static async Task RenameAsync(Guid id, string name);
```

功能归属：管理层面 / 宿主 / 修改 / 分组。

按 GUID 重命名分组；失败以异常报告。

| 参数 | 说明 |
| --- | --- |
| `id` | 目标分组 GUID。 |
| `name` | 分组显示名称。 |

- 异常 `CoreOperationException`：重命名结果尚未确认；不能将其视为没有修改。

<a id="api-4b902ac18bf9c3fd"></a>

### `VmHostDefaultPaths` record 类

宿主创建虚拟机和虚拟硬盘时使用的默认目录。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/VmHostDefaultPaths.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmHostDefaultPaths(string DefaultVmPath, string DefaultVhdPath) `](#api-ad7b9612028d3ede) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DefaultVhdPath { get; init; } `](#api-c5cdadbe97152f05) | 默认虚拟硬盘目录，宿主未提供时为空字符串。 |
| [` public string DefaultVmPath { get; init; } `](#api-3a8df0d2a923b9a7) | 新虚拟机配置文件的宿主默认存放目录。 |

#### 详细说明

```csharp
public sealed record VmHostDefaultPaths(string DefaultVmPath, string DefaultVhdPath)
```

| 参数 | 说明 |
| --- | --- |
| `DefaultVmPath` | 新虚拟机配置文件的宿主默认存放目录。 |
| `DefaultVhdPath` | 默认虚拟硬盘目录，宿主未提供时为空字符串。 |

#### 构造函数说明

<a id="api-ad7b9612028d3ede"></a>

##### VmHostDefaultPaths · 主构造器

```csharp
public VmHostDefaultPaths(string DefaultVmPath, string DefaultVhdPath);
```

| 参数 | 说明 |
| --- | --- |
| `DefaultVmPath` | 新虚拟机配置文件的宿主默认存放目录。 |
| `DefaultVhdPath` | 默认虚拟硬盘目录，宿主未提供时为空字符串。 |

#### 属性说明

<a id="api-c5cdadbe97152f05"></a>

##### DefaultVhdPath · record 属性

```csharp
public string DefaultVhdPath { get; init; }
```

默认虚拟硬盘目录，宿主未提供时为空字符串。

<a id="api-3a8df0d2a923b9a7"></a>

##### DefaultVmPath · record 属性

```csharp
public string DefaultVmPath { get; init; }
```

新虚拟机配置文件的宿主默认存放目录。

<a id="api-49c4513fba39b777"></a>

### `VmIdentityFields` 类

基本信息的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 基本信息 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Identity/VmIdentityFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<string> ConfigurationFilePath `](#api-ab090d13b00570ee) | 虚拟机 VMCX 配置文件（.vmcx）在宿主上的完整路径。 |
| [` public static readonly VmQueryField<string> ConfigurationVersion `](#api-40b91ae5e61eeb3b) | 这台虚拟机使用的 Hyper-V 配置版本。 |
| [` public static readonly VmQueryField<int> Generation `](#api-14b3dc00ee8efdc6) | 创建虚拟机时选定的虚拟硬件代数。 |
| [` public static readonly VmQueryField<string> GuestStateFilePath `](#api-57b369c4c0b4f55d) | 虚拟机来宾状态文件（.vmgs）的完整路径。 |
| [` public static readonly VmQueryField<Guid> Id `](#api-4abbd85c015d5839) | 用于指定这台虚拟机的 GUID；修改显示名称不会改变它。 |
| [` public static readonly VmQueryField<string> Name `](#api-f3c1fa34812a0e0f) | 虚拟机在管理工具中显示的名称。 |
| [` public static readonly VmQueryField<string> RuntimeStateFilePath `](#api-c8bc16925ba1cd31) | 虚拟机运行状态文件（.vmrs）的完整路径。 |
| [` public static readonly VmQueryField<VmState> State `](#api-70634d256d750078) | 查询时虚拟机的电源与执行状态。 |
| [` public static readonly VmQueryField<TimeSpan> Uptime `](#api-e25f64da683d5a94) | Hyper-V 报告的虚拟机本次启动以来的时长。 |

#### 详细说明

```csharp
public static class VmIdentityFields
```

#### 字段与常量说明

<a id="api-ab090d13b00570ee"></a>

##### ConfigurationFilePath · 字段

```csharp
public static readonly VmQueryField<string> ConfigurationFilePath;
```

虚拟机 VMCX 配置文件（.vmcx）在宿主上的完整路径。

**取值说明：** Windows 完整文件路径，扩展名为 `.vmcx`。例如 `D:\VMs\Demo\Virtual Machines\example.vmcx`。

<a id="api-40b91ae5e61eeb3b"></a>

##### ConfigurationVersion · 字段

```csharp
public static readonly VmQueryField<string> ConfigurationVersion;
```

这台虚拟机使用的配置版本。目标宿主需支持该版本才能使用此配置；下表列出各宿主支持的版本。

**取值说明：** `主版本号.次版本号`，两部分均为十进制整数，例如 `12.5`。

**配置版本与宿主 build**

Windows build 属于宿主系统，配置版本属于虚拟机。同一宿主可支持多个配置版本。

| 宿主 build | 系统 | 最高已记录版本 | 支持的配置版本 |
| --- | --- | --- | --- |
| 29639 | Microsoft Host OS · Rubidium（研究构建） | 12.5 | 12.5、12.4、12.3、12.2、12.1、12.0、11.2、11.1、11.0、10.5、10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 29617 | Microsoft Host OS · Krypton（研究构建） | 12.4 | 12.4、12.3、12.2、12.1、12.0、11.2、11.1、11.0、10.5、10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 28000 | Windows 11 26H1 | 12.3 | 12.3、12.2、12.1、12.0、11.2、11.1、11.0、10.5、10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 26200 | Windows 11 25H2 | 12.0 | 12.0、11.2、11.1、11.0、10.5、10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 26100 | Windows 11 24H2 / Windows Server 2025 | 12.0 | 12.0、11.2、11.1、11.0、10.5、10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 22631 | Windows 11 23H2 | 11.0 | 11.0、10.5、10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 22621 | Windows 11 22H2 | 11.0 | 11.0、10.5、10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 22000 | Windows 11 21H2 | 10.0 | 10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 20348 | Windows Server 2022 | 10.0 | 10.0、9.3、9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 19045 | Windows 10 22H2 | 9.2 | 9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 19044 | Windows 10 21H2 / Enterprise LTSC 2021 | 9.2 | 9.2、9.1、9.0、8.3、8.2、8.1、8.0 |
| 17763 | Windows Server 2019 / Windows 10 LTSC 2019 | 9.0 | 9.0、8.3、8.2、8.1、8.0、7.1、7.0、6.2、5.0 |
| 14393 | Windows 10 1607 / Windows Server 2016 | 8.0 | 8.0、7.1、7.0、6.2、5.0 |

<!--
MIT License

Copyright (c) 2026 Justsenger

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
-->

<a id="api-14b3dc00ee8efdc6"></a>

##### Generation · 字段

```csharp
public static readonly VmQueryField<int> Generation;
```

创建虚拟机时选定的硬件代数，决定虚拟固件、启动方式及可用设备类型。

**取值说明：** - `1`：第一代，使用传统 BIOS 虚拟固件。
- `2`：第二代，使用 UEFI 虚拟固件。

<a id="api-57b369c4c0b4f55d"></a>

##### GuestStateFilePath · 字段

```csharp
public static readonly VmQueryField<string> GuestStateFilePath;
```

虚拟机 VMGS 来宾状态文件（.vmgs）在宿主上的完整路径。

**取值说明：** Windows 完整文件路径，扩展名为 `.vmgs`。例如 `D:\VMs\Demo\Virtual Machines\example.vmgs`。

<a id="api-4abbd85c015d5839"></a>

##### Id · 字段

```csharp
public static readonly VmQueryField<Guid> Id;
```

这台虚拟机的唯一标识（GUID），用于查询、修改配置和电源操作时指定目标。创建或导入时确定，重命名不会改变它。

**取值说明：** 常用文本格式为 36 个字符：32 个十六进制字符（`0–9`、`a–f`，字母不区分大小写）与 4 个连字符，按 `8-4-4-4-12` 分组。



例如 `6f0c2e91-8a45-4b37-9d60-c1257e3a8f42`。

<a id="api-f3c1fa34812a0e0f"></a>

##### Name · 字段

```csharp
public static readonly VmQueryField<string> Name;
```

Hyper-V 管理工具中的显示名称，可以修改，也允许重名。

**取值说明：** Unicode 文本，Hyper-V 规定名称最长 100 个字符；例如 `开发环境-01`。

<a id="api-c8bc16925ba1cd31"></a>

##### RuntimeStateFilePath · 字段

```csharp
public static readonly VmQueryField<string> RuntimeStateFilePath;
```

虚拟机 VMRS 运行状态文件（.vmrs）在宿主上的完整路径。

**取值说明：** Windows 完整文件路径，扩展名为 `.vmrs`。例如 `D:\VMs\Demo\Virtual Machines\example.vmrs`。

<a id="api-70634d256d750078"></a>

##### State · 字段

```csharp
public static readonly VmQueryField<VmState> State;
```

VMMS 维护的电源与执行状态，包括运行、关机、暂停、已保存及状态切换过程。

<a id="api-e25f64da683d5a94"></a>

##### Uptime · 字段

```csharp
public static readonly VmQueryField<TimeSpan> Uptime;
```

VMMS 维护的虚拟机运行计时，表示本次启动以来经过的时间。每次查询返回当时的计时值。

**取值说明：** 非负时间间隔。例如 90 秒为 `00:01:30`，一天加 90 秒为 `1.00:01:30`。

<a id="api-c6787d3b02e89ecb"></a>

### `VmImportArchiveError` 枚举

ZIP 读取失败中需要调用方区分的原因。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportArchiveOptions.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` DecryptionFailed = 1 `](#api-1872911af7a0887d) | 密码不正确或加密数据已损坏；不能仅凭此错误区分二者。 |
| [` LimitExceeded = 3 `](#api-1eb289f3037edc8a) | 条目数量、单文件大小或累计解压量超过调用方设置。 |
| [` PasswordRequired = 0 `](#api-3ed2a8fd7f437673) | 遇到加密条目，但调用方尚未提供密码。 |
| [` UnsupportedEncryption = 2 `](#api-7115943b161d48f8) | 条目采用当前不支持的加密方式。 |

#### 详细说明

```csharp
public enum VmImportArchiveError
```

#### 枚举值说明

<a id="api-1872911af7a0887d"></a>

##### DecryptionFailed · 枚举值

```csharp
DecryptionFailed = 1
```

密码不正确或加密数据已损坏；不能仅凭此错误区分二者。

<a id="api-1eb289f3037edc8a"></a>

##### LimitExceeded · 枚举值

```csharp
LimitExceeded = 3
```

条目数量、单文件大小或累计解压量超过调用方设置。

<a id="api-3ed2a8fd7f437673"></a>

##### PasswordRequired · 枚举值

```csharp
PasswordRequired = 0
```

遇到加密条目，但调用方尚未提供密码。

<a id="api-7115943b161d48f8"></a>

##### UnsupportedEncryption · 枚举值

```csharp
UnsupportedEncryption = 2
```

条目采用当前不支持的加密方式。

<a id="api-d3966461a931a6ad"></a>

### `VmImportArchiveException` 类

ZIP 读取失败的异常附件，可从准备结果的 Result.Exception 取得；正式导入通过 ArchiveFailure 返回可序列化的详情。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportArchiveOptions.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public long? Actual { get; } `](#api-cb28b3bc75d3029c) | 触发失败的声明值或已读取量；非额度失败为 null。 |
| [` public string? EntryName { get; } `](#api-d68a8633df5c92ea) | 涉及的归档内相对路径；归档整体限制失败时可以为 null。 |
| [` public long? Limit { get; } `](#api-790f6c4617350e2c) | 调用方设置的上限；非额度失败为 null。 |
| [` public string? LimitName { get; } `](#api-43ccf7ac629dd948) | 触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。 |
| [` public VmImportArchiveError Reason { get; } `](#api-a35e22617edefd14) | 密码、格式能力或额度方面的失败原因。 |

#### 详细说明

```csharp
public sealed class VmImportArchiveException : IOException
```

#### 属性说明

<a id="api-cb28b3bc75d3029c"></a>

##### Actual · 属性

```csharp
public long? Actual { get; }
```

触发失败的声明值或已读取量；非额度失败为 null。

<a id="api-d68a8633df5c92ea"></a>

##### EntryName · 属性

```csharp
public string? EntryName { get; }
```

涉及的归档内相对路径；归档整体限制失败时可以为 null。

<a id="api-790f6c4617350e2c"></a>

##### Limit · 属性

```csharp
public long? Limit { get; }
```

调用方设置的上限；非额度失败为 null。

<a id="api-43ccf7ac629dd948"></a>

##### LimitName · 属性

```csharp
public string? LimitName { get; }
```

触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。

<a id="api-a35e22617edefd14"></a>

##### Reason · 属性

```csharp
public VmImportArchiveError Reason { get; }
```

密码、格式能力或额度方面的失败原因。

<a id="api-b713808ff11bc45e"></a>

### `VmImportArchiveFailure` record 类

可序列化的 ZIP 失败详情；用于导入结果，完整错误说明仍从结果的 Failure 读取。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportArchiveOptions.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmImportArchiveFailure(VmImportArchiveError Reason, string? EntryName, string? LimitName, long? Limit, long? Actual) `](#api-49956ada862541f4) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public long? Actual { get; init; } `](#api-737bd8f57bbe5136) | 触发失败的声明值或已读取量；非额度失败为 null。 |
| [` public string? EntryName { get; init; } `](#api-ba34523ef8cb291e) | 涉及的归档内相对路径；归档整体失败时可以为 null。 |
| [` public long? Limit { get; init; } `](#api-36262a1ba969b0be) | 调用方设置的上限；非额度失败为 null。 |
| [` public string? LimitName { get; init; } `](#api-d23be6813e9f1c69) | 触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。 |
| [` public VmImportArchiveError Reason { get; init; } `](#api-95b665a15ef80af3) | 密码、加密能力或额度方面的失败原因。 |

#### 详细说明

```csharp
public sealed record VmImportArchiveFailure(VmImportArchiveError Reason, string? EntryName,
    string? LimitName, long? Limit, long? Actual)
```

| 参数 | 说明 |
| --- | --- |
| `Reason` | 密码、加密能力或额度方面的失败原因。 |
| `EntryName` | 涉及的归档内相对路径；归档整体失败时可以为 null。 |
| `LimitName` | 触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。 |
| `Limit` | 调用方设置的上限；非额度失败为 null。 |
| `Actual` | 触发失败的声明值或已读取量；非额度失败为 null。 |

#### 构造函数说明

<a id="api-49956ada862541f4"></a>

##### VmImportArchiveFailure · 主构造器

```csharp
public VmImportArchiveFailure(VmImportArchiveError Reason, string? EntryName, string? LimitName, long? Limit, long? Actual);
```

| 参数 | 说明 |
| --- | --- |
| `Reason` | 密码、加密能力或额度方面的失败原因。 |
| `EntryName` | 涉及的归档内相对路径；归档整体失败时可以为 null。 |
| `LimitName` | 触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。 |
| `Limit` | 调用方设置的上限；非额度失败为 null。 |
| `Actual` | 触发失败的声明值或已读取量；非额度失败为 null。 |

#### 属性说明

<a id="api-737bd8f57bbe5136"></a>

##### Actual · record 属性

```csharp
public long? Actual { get; init; }
```

触发失败的声明值或已读取量；非额度失败为 null。

<a id="api-ba34523ef8cb291e"></a>

##### EntryName · record 属性

```csharp
public string? EntryName { get; init; }
```

涉及的归档内相对路径；归档整体失败时可以为 null。

<a id="api-36262a1ba969b0be"></a>

##### Limit · record 属性

```csharp
public long? Limit { get; init; }
```

调用方设置的上限；非额度失败为 null。

<a id="api-d23be6813e9f1c69"></a>

##### LimitName · record 属性

```csharp
public string? LimitName { get; init; }
```

触发的选项名：MaxEntries、MaxEntryBytes 或 MaxTotalBytes；其他失败为 null。

<a id="api-95b665a15ef80af3"></a>

##### Reason · record 属性

```csharp
public VmImportArchiveError Reason { get; init; }
```

密码、加密能力或额度方面的失败原因。

<a id="api-c111a8996844ae5b"></a>

### `VmImportArchiveOptions` 类

从 ZIP 准备预览及导入时使用的密码、解压额度和预览工作目录。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportArchiveOptions.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int? MaxEntries { get; init; } `](#api-f5e8bfe014286c4b) | 归档允许包含的条目总数，包括目录；null 不限制，0 仅允许空归档。 |
| [` public long? MaxEntryBytes { get; init; } `](#api-afa65aec41a14fe2) | 单个条目的最大未压缩字节数；null 不限制。 |
| [` public long? MaxTotalBytes { get; init; } `](#api-91cc504a4c1f946a) | 本次导入批次累计允许解出的字节数；null 不限制。 |
| [` public string? Password { get; init; } `](#api-8b5bcd2bcc772a54) | 读取加密条目使用的 UTF-8 密码；默认 null。 |
| [` public string? TemporaryDirectory { get; init; } `](#api-f6ac4d614b638273) | 预览工作目录的父目录，必须是已存在的绝对路径；Core 在其中创建并清理独立子目录，不删除父目录。 |

#### 详细说明

```csharp
public sealed class VmImportArchiveOptions
```

调用约定：在 PreparePreviewsAsync 提交一次，之后所有单机会话沿用同一组选项和累计额度。null 额度表示不设置该项上限，保持旧调用行为；Core 不替调用方规定虚拟磁盘容量。额度限制解压数据，不等同于整个进程的内存或耗时上限。选项不包含虚拟机名称、GUID 或目标目录，那些由 VmImportOptions 指定。

#### 属性说明

<a id="api-f5e8bfe014286c4b"></a>

##### MaxEntries · 属性

```csharp
public int? MaxEntries { get; init; }
```

归档允许包含的条目总数，包括目录；null 不限制，0 仅允许空归档。准备预览及正式解压前都会检查。

<a id="api-afa65aec41a14fe2"></a>

##### MaxEntryBytes · 属性

```csharp
public long? MaxEntryBytes { get; init; }
```

单个条目的最大未压缩字节数；null 不限制。检查目录记录中的声明大小，并在读取时再次限制实际字节数。

<a id="api-91cc504a4c1f946a"></a>

##### MaxTotalBytes · 属性

```csharp
public long? MaxTotalBytes { get; init; }
```

本次导入批次累计允许解出的字节数；null 不限制。包括配置预览、磁盘头读取和正式解压；重复读取也计入，并行单机会话共用额度。

调用约定：准备时还检查整个归档声明的未压缩总量。预览与导入会重复读取部分内容，因此额度应大于归档声明总量；失败读取已消耗的额度不返还。

<a id="api-8b5bcd2bcc772a54"></a>

##### Password · 属性

```csharp
public string? Password { get; init; }
```

读取加密条目使用的 UTF-8 密码；默认 null。缺少密码时返回 PasswordRequired；Core 不弹窗、不保存密码。一个归档中的加密条目应使用同一密码。

调用约定：部分 ZIP 工具使用系统代码页编码非 ASCII 密码，与本接口不兼容；ASCII 密码不受这类编码差异影响。

<a id="api-f6ac4d614b638273"></a>

##### TemporaryDirectory · 属性

```csharp
public string? TemporaryDirectory { get; init; }
```

预览工作目录的父目录，必须是已存在的绝对路径；Core 在其中创建并清理独立子目录，不删除父目录。null 使用系统临时目录。

调用约定：正式导入的配置和磁盘暂存目录仍靠近各自目标目录，以便校验后移动提交，不受此选项影响。

<a id="api-89518e59536dbfc5"></a>

### `VmImportBatchSession` 类

共享来源和临时目录的一组导入会话；即使只查看预览也应释放。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportSession.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? CleanupError { get; } `](#api-5e3cfcd3dc68cdaa) | 批次释放时记录的清理错误；没有错误时为 null。 |
| [` public VmImportCleanupState CleanupState { get; } = VmImportCleanupState.Pending `](#api-ce6f22713c138368) | 批次工作资源当前的清理状态。 |
| [` public required VmImportPlacementMode PlacementMode { get; init; } `](#api-bf236047a662efdb) | 批次的文件放置策略。 |
| [` public IReadOnlyList<string> RetainedPaths { get; } = [] `](#api-fabfbba78a6dc66a) | 尚未确认删除的工作文件、目录；释放后仍可读取。 |
| [` public required VmImportSourceKind SourceKind { get; init; } `](#api-ef55fb2db085e75f) | 批次来源类型。 |
| [` public required string SourcePath { get; init; } `](#api-d2a59090e9b2bc6d) | 该批次原始目录或 ZIP 的路径。 |
| [` public IReadOnlyList<VmImportSession> VirtualMachines { get; } = [] `](#api-b03d2dcc20de820e) | 预览中识别到的单机导入会话，按识别顺序排列。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public ValueTask DisposeAsync() `](#api-4e23478df5e33027) | 释放各单机会话，再清理共享解压目录；已导入的虚拟机予以保留。 |

#### 详细说明

```csharp
public sealed class VmImportBatchSession : IAsyncDisposable
```

调用约定：它管理同一目录或归档的工作资源，不自动调度多台导入。调用方选择 VirtualMachines 中的会话，逐次调用 ImportAsync；执行顺序和失败后是否继续由调用方决定。

#### 属性说明

<a id="api-5e3cfcd3dc68cdaa"></a>

##### CleanupError · 属性

```csharp
public string? CleanupError { get; }
```

批次释放时记录的清理错误；没有错误时为 null。

<a id="api-ce6f22713c138368"></a>

##### CleanupState · 属性

```csharp
public VmImportCleanupState CleanupState { get; } = VmImportCleanupState.Pending;
```

批次工作资源当前的清理状态。

<a id="api-bf236047a662efdb"></a>

##### PlacementMode · 属性

```csharp
public required VmImportPlacementMode PlacementMode { get; init; }
```

批次的文件放置策略。

<a id="api-fabfbba78a6dc66a"></a>

##### RetainedPaths · 属性

```csharp
public IReadOnlyList<string> RetainedPaths { get; } = [];
```

尚未确认删除的工作文件、目录；释放后仍可读取。

<a id="api-ef55fb2db085e75f"></a>

##### SourceKind · 属性

```csharp
public required VmImportSourceKind SourceKind { get; init; }
```

批次来源类型。

<a id="api-d2a59090e9b2bc6d"></a>

##### SourcePath · 属性

```csharp
public required string SourcePath { get; init; }
```

该批次原始目录或 ZIP 的路径。

<a id="api-b03d2dcc20de820e"></a>

##### VirtualMachines · 属性

```csharp
public IReadOnlyList<VmImportSession> VirtualMachines { get; } = [];
```

预览中识别到的单机导入会话，按识别顺序排列。

#### 方法说明

<a id="api-4e23478df5e33027"></a>

##### DisposeAsync · 方法

```csharp
public ValueTask DisposeAsync();
```

释放各单机会话，再清理共享解压目录；已导入的虚拟机予以保留。

调用约定：先取消批次及所有子会话的后续步骤，等待在途操作安全结束，再释放资源。原生状态未确认时保留共享目录。清理失败后可重复调用重试；批次及子会话不再接受新导入。结果由 CleanupState、RetainedPaths 和 CleanupError 表达。

<a id="api-919fbe6209ea2900"></a>

### `VmImportCleanupState` 枚举

导入会话工作资源的清理状态，与虚拟机是否已经导入分别判断。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportCleanupState.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Completed = 1 `](#api-2598f84334660482) | 应清理的工作资源已清理，不包含已成功导入的虚拟机文件。 |
| [` Deferred = 2 `](#api-ea7f12d41edd905a) | 原生操作或计划系统移除结果未确认，保留相关资源。 |
| [` Failed = 3 `](#api-8fbc330d17b99cf9) | 已允许清理，但部分文件或目录未能删除。 |
| [` Pending = 0 `](#api-d503e12da6e014e6) | 资源仍由会话持有，调用者使用完成后应异步释放。 |

#### 详细说明

```csharp
public enum VmImportCleanupState
```

#### 枚举值说明

<a id="api-2598f84334660482"></a>

##### Completed · 枚举值

```csharp
Completed = 1
```

应清理的工作资源已清理，不包含已成功导入的虚拟机文件。

<a id="api-ea7f12d41edd905a"></a>

##### Deferred · 枚举值

```csharp
Deferred = 2
```

原生操作或计划系统移除结果未确认，保留相关资源。

<a id="api-8fbc330d17b99cf9"></a>

##### Failed · 枚举值

```csharp
Failed = 3
```

已允许清理，但部分文件或目录未能删除。

<a id="api-d503e12da6e014e6"></a>

##### Pending · 枚举值

```csharp
Pending = 0
```

资源仍由会话持有，调用者使用完成后应异步释放。

<a id="api-3a7a2c1f20362d6e"></a>

### `VmImportPreparationResult` 类

导入预览的准备结果，以及仍由调用方负责释放的批次。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportPreparationResult.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public VmImportBatchSession? Batch { get; } `](#api-eb1a0e8ec32930b2) | 本次创建的批次；尚未创建时为 null。 |
| [` public ApiResponse Result { get; } `](#api-259ea82a9e01c7d1) | 准备阶段的成功或失败信息；不包含清理错误，清理结果从 Batch 读取。 |
| [` public bool Success { get; } `](#api-9646628e92c48479) | 是否已成功生成完整预览；为 true 时 Batch 不为 null。 |

#### 详细说明

```csharp
public sealed class VmImportPreparationResult
```

调用约定：准备失败也可能留下未清理完的工作目录。此时 Batch 只用于检查清理结果和再次 DisposeAsync，不可继续导入；不能仅根据 Success 丢弃它。

#### 属性说明

<a id="api-eb1a0e8ec32930b2"></a>

##### Batch · 属性

```csharp
public VmImportBatchSession? Batch { get; }
```

本次创建的批次；尚未创建时为 null。失败时可能已释放但清理未完成，应保留引用并重试 DisposeAsync。

<a id="api-259ea82a9e01c7d1"></a>

##### Result · 属性

```csharp
public ApiResponse Result { get; }
```

准备阶段的成功或失败信息；不包含清理错误，清理结果从 Batch 读取。

<a id="api-9646628e92c48479"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

是否已成功生成完整预览；为 true 时 Batch 不为 null。

<a id="api-8e5a833b3845c549"></a>

### `VmImportResult` record 类

单台导入结果；失败仍可能已经注册虚拟机或留下需要处理的工作资源。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmImportResult(bool Success, Guid? VmId, bool IsRealized, bool OperationStateUnknown, VmImportCleanupState CleanupState, IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null) `](#api-6220bef7c3c365e2) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public VmImportArchiveFailure? ArchiveFailure { get; init; } `](#api-c3792b332c658447) | ZIP 密码、格式或额度方面的具体失败；其他原因或成功时为 null。 |
| [` public string? CleanupError { get; init; } `](#api-a6100e6449bd4a19) | 资源清理失败或延期原因，不覆盖主操作错误。 |
| [` public ApiFailure? CleanupFailure { get; init; } `](#api-f406a43177bf1f14) | 清理失败的结构化信息，与主操作失败分开；没有原始接口码时为 -1。 |
| [` public VmImportCleanupState CleanupState { get; init; } `](#api-7175bf61bd036b29) | 返回时的工作资源清理状态；释放会话后读取会话属性取得最新状态。 |
| [` public string? Error { get; init; } `](#api-9c8f8e618f508cc7) | 导入失败原因；成功时为 null。 |
| [` public ApiFailure? Failure { get; init; } `](#api-42e7494aaf550bfe) | 主操作失败的结构化信息；没有更具体来源时 Kind 为 Unspecified。 |
| [` public bool IsRealized { get; init; } `](#api-dc69523f7b2ee258) | 是否已经确认实现为注册虚拟机；原生状态未知时 false 不能证明未注册。 |
| [` public bool OperationStateUnknown { get; init; } `](#api-a12f4a065c33e7b5) | 是否仍不能确认原生操作结束或计划系统清理完成。 |
| [` public IReadOnlyList<string> RetainedPaths { get; init; } `](#api-eec89a0f8573d94a) | 本次工作资源中保留或未确认删除的文件、目录，不包含原生对象路径。 |
| [` public bool Success { get; init; } `](#api-b9765e28e4bc7e0e) | 导入流程是否完成。 |
| [` public Guid? VmId { get; init; } `](#api-982d04ad1bad81ca) | 已知的计划或已导入虚拟机 GUID；尚未取得时为 null。 |

#### 详细说明

```csharp
public sealed record VmImportResult(bool Success, Guid? VmId, bool IsRealized,
    bool OperationStateUnknown, VmImportCleanupState CleanupState,
    IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null)
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 导入流程是否完成。 |
| `VmId` | 已知的计划或已导入虚拟机 GUID；尚未取得时为 null。 |
| `IsRealized` | 是否已经确认实现为注册虚拟机；原生状态未知时 false 不能证明未注册。 |
| `OperationStateUnknown` | 是否仍不能确认原生操作结束或计划系统清理完成。 |
| `CleanupState` | 返回时的工作资源清理状态；释放会话后读取会话属性取得最新状态。 |
| `RetainedPaths` | 本次工作资源中保留或未确认删除的文件、目录，不包含原生对象路径。 |
| `Error` | 导入失败原因；成功时为 null。 |
| `CleanupError` | 资源清理失败或延期原因，不覆盖主操作错误。 |

#### 构造函数说明

<a id="api-6220bef7c3c365e2"></a>

##### VmImportResult · 主构造器

```csharp
public VmImportResult(bool Success, Guid? VmId, bool IsRealized, bool OperationStateUnknown, VmImportCleanupState CleanupState, IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null);
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 导入流程是否完成。 |
| `VmId` | 已知的计划或已导入虚拟机 GUID；尚未取得时为 null。 |
| `IsRealized` | 是否已经确认实现为注册虚拟机；原生状态未知时 false 不能证明未注册。 |
| `OperationStateUnknown` | 是否仍不能确认原生操作结束或计划系统清理完成。 |
| `CleanupState` | 返回时的工作资源清理状态；释放会话后读取会话属性取得最新状态。 |
| `RetainedPaths` | 本次工作资源中保留或未确认删除的文件、目录，不包含原生对象路径。 |
| `Error` | 导入失败原因；成功时为 null。 |
| `CleanupError` | 资源清理失败或延期原因，不覆盖主操作错误。 |

#### 属性说明

<a id="api-c3792b332c658447"></a>

##### ArchiveFailure · 属性

```csharp
public VmImportArchiveFailure? ArchiveFailure { get; init; }
```

ZIP 密码、格式或额度方面的具体失败；其他原因或成功时为 null。Failure 仍提供整体失败信息。

<a id="api-a6100e6449bd4a19"></a>

##### CleanupError · record 属性

```csharp
public string? CleanupError { get; init; }
```

资源清理失败或延期原因，不覆盖主操作错误。

<a id="api-f406a43177bf1f14"></a>

##### CleanupFailure · 属性

```csharp
public ApiFailure? CleanupFailure { get; init; }
```

清理失败的结构化信息，与主操作失败分开；没有原始接口码时为 -1。

<a id="api-7175bf61bd036b29"></a>

##### CleanupState · record 属性

```csharp
public VmImportCleanupState CleanupState { get; init; }
```

返回时的工作资源清理状态；释放会话后读取会话属性取得最新状态。

<a id="api-9c8f8e618f508cc7"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

导入失败原因；成功时为 null。

<a id="api-42e7494aaf550bfe"></a>

##### Failure · 属性

```csharp
public ApiFailure? Failure { get; init; }
```

主操作失败的结构化信息；没有更具体来源时 Kind 为 Unspecified。

<a id="api-dc69523f7b2ee258"></a>

##### IsRealized · record 属性

```csharp
public bool IsRealized { get; init; }
```

是否已经确认实现为注册虚拟机；原生状态未知时 false 不能证明未注册。

<a id="api-a12f4a065c33e7b5"></a>

##### OperationStateUnknown · record 属性

```csharp
public bool OperationStateUnknown { get; init; }
```

是否仍不能确认原生操作结束或计划系统清理完成。

<a id="api-eec89a0f8573d94a"></a>

##### RetainedPaths · record 属性

```csharp
public IReadOnlyList<string> RetainedPaths { get; init; }
```

本次工作资源中保留或未确认删除的文件、目录，不包含原生对象路径。

<a id="api-b9765e28e4bc7e0e"></a>

##### Success · record 属性

```csharp
public bool Success { get; init; }
```

导入流程是否完成。

<a id="api-982d04ad1bad81ca"></a>

##### VmId · record 属性

```csharp
public Guid? VmId { get; init; }
```

已知的计划或已导入虚拟机 GUID；尚未取得时为 null。

<a id="api-e735a2b9bb924f8e"></a>

### `VmImportService` 类

解析和准备导入预览，再按选择实现虚拟机；会话负责暂存资源的释放。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportService.Options.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static string? GetPreviewError(IEnumerable<VmImportPreview> previews) `](#api-92447067f1ddeb27) | 检查预览集合是否为空或包含阻止导入的兼容性问题。 |
| [` public static async Task<VmImportResult> ImportAsync(VmImportSession session, VmImportOptions options, IProgress<int>? progress = null, CancellationToken cancellationToken = default) `](#api-6001c0801ee3398b) | 将一个已准备的导入会话注册为虚拟机，按所选模式复制或重用配置和磁盘。 |
| [` public static async Task<ApiResponse> PrepareBatchImportAsync(VmImportBatchSession batch, CancellationToken cancellationToken = default) `](#api-76472f488afb9b44) | 预先检查同一来源的预览和宿主交换机查询是否可用；不应用导入选项。 |
| [` public static Task<VmImportPreparationResult> PreparePreviewsAsync(string sourcePath, VmImportPlacementMode placementMode, IProgress<(int Current, int Total)>? progress = null, CancellationToken cancellationToken = default) `](#api-caaaab2a91e74a5f) | 解析目录或 ZIP 中的虚拟机配置，生成待导入预览及拥有暂存资源的批次。 |
| [` public static async Task<VmImportPreparationResult> PreparePreviewsAsync(string sourcePath, VmImportArchiveOptions archiveOptions, VmImportPlacementMode placementMode, IProgress<(int Current, int Total)>? progress = null, CancellationToken cancellationToken = default) `](#api-c61821570c4fcf69) | 使用指定的 ZIP 密码、解压额度和预览工作目录准备导入预览。 |

#### 详细说明

```csharp
public static partial class VmImportService
```

#### 静态方法说明

<a id="api-92447067f1ddeb27"></a>

##### GetPreviewError · 方法

```csharp
public static string? GetPreviewError(IEnumerable<VmImportPreview> previews);
```

检查预览集合是否为空或包含阻止导入的兼容性问题。

| 参数 | 说明 |
| --- | --- |
| `previews` | 待校验的单机预览集合。 |

返回：首个阻止继续的说明；可进入下一阶段时为 null。修复源问题后需要重新生成预览。

<a id="api-6001c0801ee3398b"></a>

##### ImportAsync · 方法

```csharp
public static async Task<VmImportResult> ImportAsync(VmImportSession session, VmImportOptions options, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
```

将一个已准备的导入会话注册为虚拟机，按所选模式复制或重用配置和磁盘。

调用顺序：

1. 调用 `PreparePreviewsAsync` 解析来源目录或 ZIP，取得导入批次并检查预览。使用 `await using` 管理返回批次。
2. ZIP 需要密码、解压额度或指定预览工作目录时，在准备入口传入 `VmImportArchiveOptions`；这些参数及累计额度会沿用到后续导入，不在 ImportAsync 重新设置。
3. 检查预览并创建 `VmImportOptions`，明确 Preserve 或 GenerateNew；按需填写名称、完整目标目录和源交换机 GUID 映射。预览不用于修改选项。
4. 从 `batch.VirtualMachines` 选择一个会话，调用 `ImportAsync(session, options)`；查看结果中的 GUID、注册状态和清理信息。方法会自行校验来源、目标和网络；释放批次只清理工作资源，不删除已导入虚拟机。

| 参数 | 说明 |
| --- | --- |
| `session` | 由预览创建的单机会话；目录来源沿用预览解析的源磁盘路径，文件消失后返回失败，不改用其他同名磁盘。此方法按需解压和复制文件，不支持并发重复导入同一会话。 |
| `options` | 该虚拟机的导入选择，必须明确 GUID 策略；名称、目录及交换机映射未指定时沿用原规则。选项校验失败不会开始导入，可以修正选项后重试。 |
| `progress` | 可选进度接收器；null 时不通知。回调线程由具体 IProgress 实现决定。 |
| `cancellationToken` | 协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。 |

返回：返回独立的虚拟机 GUID、已实现标记、原生状态是否未知及清理结果；失败不表示未注册或没有留下资源。

调用约定：进入最后的实现计划系统步骤后不再取消，返回实际实现结果；此前的原生写入先确认终态再响应取消。已导入的虚拟机予以保留，由调用者管理；会话释放清理工作资源，原生状态未确认时保留资源并报告。网络引用不存在时按导入规则断开相关连接。一旦开始执行导入，无论成功或失败，该会话都不再重新导入；重试需先清理并重新准备预览。

- 异常 `ObjectDisposedException`：会话已请求释放，不能再次导入。
- 异常 `InvalidOperationException`：同一会话正用于其他导入或批次准备操作。

<a id="api-76472f488afb9b44"></a>

##### PrepareBatchImportAsync · 方法

```csharp
public static async Task<ApiResponse> PrepareBatchImportAsync(VmImportBatchSession batch, CancellationToken cancellationToken = default);
```

预先检查同一来源的预览和宿主交换机查询是否可用；不应用导入选项。

| 参数 | 说明 |
| --- | --- |
| `batch` | 由 PreparePreviewsAsync 返回的批次；使用完成后必须释放。 |
| `cancellationToken` | 协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。 |

返回：预览或交换机查询结果。此可选入口不执行导入，也不预留目录或 GUID；ImportAsync 会按自己的选项重新校验。

- 异常 `ObjectDisposedException`：批次或其中某个会话已请求释放。
- 异常 `InvalidOperationException`：批次或其中某个会话正在使用，不能同时更改准备信息。

<a id="api-caaaab2a91e74a5f"></a>

##### PreparePreviewsAsync · 方法

```csharp
public static Task<VmImportPreparationResult> PreparePreviewsAsync(string sourcePath, VmImportPlacementMode placementMode, IProgress<(int Current, int Total)>? progress = null, CancellationToken cancellationToken = default);
```

解析目录或 ZIP 中的虚拟机配置，生成待导入预览及拥有暂存资源的批次。

| 参数 | 说明 |
| --- | --- |
| `sourcePath` | 包含导出配置的目录或 ZIP 文件路径。 |
| `placementMode` | 在原目录就地导入或复制到新位置；ZIP 不支持原目录模式。 |
| `progress` | 可选的预览扫描计数回调，Current 和 Total 表示当前处理进度，不是百分比。 |
| `cancellationToken` | 协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。 |

返回：Success 表示预览是否准备完成；Batch 非 null 时始终由调用方管理，失败时也应检查清理结果并保留待重试对象。ZIP 不支持原目录就地导入。

<a id="api-c61821570c4fcf69"></a>

##### PreparePreviewsAsync · 方法

```csharp
public static async Task<VmImportPreparationResult> PreparePreviewsAsync(string sourcePath, VmImportArchiveOptions archiveOptions, VmImportPlacementMode placementMode, IProgress<(int Current, int Total)>? progress = null, CancellationToken cancellationToken = default);
```

使用指定的 ZIP 密码、解压额度和预览工作目录准备导入预览。

| 参数 | 说明 |
| --- | --- |
| `sourcePath` | 包含导出配置的目录或 ZIP 文件路径。 |
| `archiveOptions` | ZIP 读取选项，贯穿预览和后续导入；目录来源不使用这些选项。一个批次的各单机会话共享累计额度。 |
| `placementMode` | 文件放置方式；ZIP 只能复制到目标目录。 |
| `progress` | 预览处理计数；null 不通知。 |
| `cancellationToken` | 取消预览准备；已创建的工作文件仍按批次清理规则处理。 |

返回：预览批次或失败信息；密码、加密方式和额度失败可从 Result.Exception 取得 VmImportArchiveException。失败时也应检查并释放 Batch。

调用约定：未加密 ZIP 和 AES-128/AES-256 ZIP 可读取，传统 ZipCrypto 不支持。预览只完整校验配置条目；磁盘头仅作预览，整盘内容与 AES 认证在正式解压时校验。缺密码或密码错误时，释放失败批次后用正确密码重新准备。密码仅由批次在内存中持有，批次释放后 Core 解除其引用，不承诺清除调用方持有的字符串。

<a id="api-eeb2c99295cf89c9"></a>

### `VmImportSession` 类

单台虚拟机的导入工作状态；由预览入口创建，使用后必须异步释放。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 / 导入 |
| 源文件 | ` Management/VirtualMachines/Instances/Import/VmImportSession.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? CleanupError { get; } `](#api-efc0e7b9d3d7d048) | 清理失败或原生状态未确认时的说明；不覆盖导入失败原因。 |
| [` public VmImportCleanupState CleanupState { get; } = VmImportCleanupState.Pending `](#api-5d40effa33118010) | 工作资源的当前清理状态；DisposeAsync 后也可读取。 |
| [` public bool IsRealized { get; } `](#api-3663873b93f76376) | 计划系统是否已成功实现为注册的虚拟机；释放会话不删除它。 |
| [` public bool OperationStateUnknown { get; } `](#api-d4dd1244ff6efe6d) | 是否仍不能确认原生操作结束或计划系统移除完成。 |
| [` public required VmImportPlacementMode PlacementMode { get; init; } `](#api-892556e627978247) | 采用原目录还是新的目标目录放置导入文件。 |
| [` public VmImportPreview Preview { get; } = new() `](#api-15c7ac5b6ab6e7f1) | 来源配置和兼容性问题的只读预览；导入选择通过 VmImportOptions 提交。 |
| [` public int RemovedMissingGroupCount { get; } `](#api-3821622dda630611) | 导入时已清除的不存在于当前宿主的分组引用数量。 |
| [` public IReadOnlyList<string> RetainedPaths { get; } = [] `](#api-1dc6f060b584b295) | 仍由会话持有或未确认删除的文件、目录；不包括已导入的虚拟机业务文件。 |
| [` public required VmImportSourceKind SourceKind { get; init; } `](#api-c6b7b065c9293e11) | 原始来源是目录还是 ZIP 归档。 |
| [` public required string SourcePath { get; init; } `](#api-799420789cccb33d) | 用户提供的原始目录或 ZIP 路径。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public ValueTask DisposeAsync() `](#api-21d43ea8007027dd) | 释放本会话拥有的计划系统和工作文件，保留已实现的虚拟机。 |

#### 详细说明

```csharp
public sealed class VmImportSession : IAsyncDisposable
```

#### 属性说明

<a id="api-efc0e7b9d3d7d048"></a>

##### CleanupError · 属性

```csharp
public string? CleanupError { get; }
```

清理失败或原生状态未确认时的说明；不覆盖导入失败原因。

<a id="api-5d40effa33118010"></a>

##### CleanupState · 属性

```csharp
public VmImportCleanupState CleanupState { get; } = VmImportCleanupState.Pending;
```

工作资源的当前清理状态；DisposeAsync 后也可读取。

<a id="api-3663873b93f76376"></a>

##### IsRealized · 属性

```csharp
public bool IsRealized { get; }
```

计划系统是否已成功实现为注册的虚拟机；释放会话不删除它。

<a id="api-d4dd1244ff6efe6d"></a>

##### OperationStateUnknown · 属性

```csharp
public bool OperationStateUnknown { get; }
```

是否仍不能确认原生操作结束或计划系统移除完成。

<a id="api-892556e627978247"></a>

##### PlacementMode · 属性

```csharp
public required VmImportPlacementMode PlacementMode { get; init; }
```

采用原目录还是新的目标目录放置导入文件。

<a id="api-15c7ac5b6ab6e7f1"></a>

##### Preview · 属性

```csharp
public VmImportPreview Preview { get; } = new();
```

来源配置和兼容性问题的只读预览；导入选择通过 VmImportOptions 提交。

<a id="api-3821622dda630611"></a>

##### RemovedMissingGroupCount · 属性

```csharp
public int RemovedMissingGroupCount { get; }
```

导入时已清除的不存在于当前宿主的分组引用数量。

<a id="api-1dc6f060b584b295"></a>

##### RetainedPaths · 属性

```csharp
public IReadOnlyList<string> RetainedPaths { get; } = [];
```

仍由会话持有或未确认删除的文件、目录；不包括已导入的虚拟机业务文件。

<a id="api-c6b7b065c9293e11"></a>

##### SourceKind · 属性

```csharp
public required VmImportSourceKind SourceKind { get; init; }
```

原始来源是目录还是 ZIP 归档。

<a id="api-799420789cccb33d"></a>

##### SourcePath · 属性

```csharp
public required string SourcePath { get; init; }
```

用户提供的原始目录或 ZIP 路径。

#### 方法说明

<a id="api-21d43ea8007027dd"></a>

##### DisposeAsync · 方法

```csharp
public ValueTask DisposeAsync();
```

释放本会话拥有的计划系统和工作文件，保留已实现的虚拟机。

调用约定：请求取消后续步骤，等待正在进行的操作安全结束后再清理。已导入的虚拟机保留。原生任务未确认结束时记录 Deferred，清理失败记录 Failed；重复调用可重试清理，但不能重新开始导入。并发释放共用同一次清理结果；调用后检查 CleanupState 和 RetainedPaths。

<a id="api-bb863f5938e764bd"></a>

### `VmInputService` 类

直接通过 Hyper-V 向虚拟机发送 Ctrl+Alt+Del。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 控制 / 键盘 |
| 源文件 | ` Business/Keyboard/VmInputService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse> SendCtrlAltDelAsync(Guid vmId) `](#api-4f4575475f490453) | 向指定虚拟机发送 Ctrl+Alt+Del 组合键。 |

#### 详细说明

```csharp
public static class VmInputService
```

调用约定：这是独立于控制台连接的合成键盘操作，适合登录或打开来宾安全界面。 日常键盘、鼠标输入使用控制台连接的 IConsoleInput。

#### 静态方法说明

<a id="api-4f4575475f490453"></a>

##### SendCtrlAltDelAsync · 方法

```csharp
public static async Task<ApiResponse> SendCtrlAltDelAsync(Guid vmId);
```

向指定虚拟机发送 Ctrl+Alt+Del 组合键。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的 GUID。 |

返回：按键请求的执行结果，失败时包含 Hyper-V 返回的原因。

调用约定：不需要先建立 RDP 连接，也不需要窗口获得焦点。成功表示底层按键请求已执行，不表示来宾已完成登录。

<a id="api-b63adbe1d39ffbae"></a>

### `VmInventorySnapshot` record 类

一次虚拟机清单快照，包含虚拟机及未完整取得的可选信息。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmInventorySnapshot(IReadOnlyList<VmInstance> VirtualMachines, IReadOnlyList<VmQueryIssue> Issues) `](#api-811875dfaa43cf2e) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool IsComplete { get; } `](#api-dc7a5f76afd0f92c) | 本次列表所需的必需和可选读取均未报告问题。 |
| [` public IReadOnlyList<VmQueryIssue> Issues { get; init; } `](#api-1efe7dd893642dbb) | 可选读取失败或不支持的明细；必需读取失败不会产生成功快照。 |
| [` public IReadOnlyList<VmInstance> VirtualMachines { get; init; } `](#api-9e7bd6b62fa03799) | 按活动状态和名称排序的虚拟机列表。 |

#### 详细说明

```csharp
public sealed record VmInventorySnapshot(IReadOnlyList<VmInstance> VirtualMachines, IReadOnlyList<VmQueryIssue> Issues)
```

| 参数 | 说明 |
| --- | --- |
| `VirtualMachines` | 按活动状态和名称排序的虚拟机列表。 |
| `Issues` | 可选读取失败或不支持的明细；必需读取失败不会产生成功快照。 |

#### 构造函数说明

<a id="api-811875dfaa43cf2e"></a>

##### VmInventorySnapshot · 主构造器

```csharp
public VmInventorySnapshot(IReadOnlyList<VmInstance> VirtualMachines, IReadOnlyList<VmQueryIssue> Issues);
```

| 参数 | 说明 |
| --- | --- |
| `VirtualMachines` | 按活动状态和名称排序的虚拟机列表。 |
| `Issues` | 可选读取失败或不支持的明细；必需读取失败不会产生成功快照。 |

#### 属性说明

<a id="api-dc7a5f76afd0f92c"></a>

##### IsComplete · 属性

```csharp
public bool IsComplete { get; }
```

本次列表所需的必需和可选读取均未报告问题。

<a id="api-1efe7dd893642dbb"></a>

##### Issues · record 属性

```csharp
public IReadOnlyList<VmQueryIssue> Issues { get; init; }
```

可选读取失败或不支持的明细；必需读取失败不会产生成功快照。

<a id="api-9e7bd6b62fa03799"></a>

##### VirtualMachines · record 属性

```csharp
public IReadOnlyList<VmInstance> VirtualMachines { get; init; }
```

按活动状态和名称排序的虚拟机列表。

<a id="api-b819752faee5254b"></a>

### `VmIpAddressAssociation` 枚举

地址与请求目标之间能够确认的关联范围；不是服务器认证或防伪结论。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmIpAddressObservation.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` GuestAdapter = 1 `](#api-0bb1136c866dbf10) | 通过 Hyper-V 的网卡关联取得，并核对了该网卡的 MAC。 |
| [` MacAddress = 0 `](#api-dcfefdf9ed48e359) | 按 MAC 匹配得到；同 MAC 的其他设备或过期缓存仍可能影响结果。 |
| [` VirtualMachine = 2 `](#api-ceced24526c68117) | 只能关联到虚拟机，尚不能确定属于请求的网卡；不能自动填给某张网卡。 |

#### 详细说明

```csharp
public enum VmIpAddressAssociation
```

#### 枚举值说明

<a id="api-0bb1136c866dbf10"></a>

##### GuestAdapter · 枚举值

```csharp
GuestAdapter = 1
```

通过 Hyper-V 的网卡关联取得，并核对了该网卡的 MAC。

<a id="api-dcfefdf9ed48e359"></a>

##### MacAddress · 枚举值

```csharp
MacAddress = 0
```

按 MAC 匹配得到；同 MAC 的其他设备或过期缓存仍可能影响结果。

<a id="api-ceced24526c68117"></a>

##### VirtualMachine · 枚举值

```csharp
VirtualMachine = 2
```

只能关联到虚拟机，尚不能确定属于请求的网卡；不能自动填给某张网卡。

<a id="api-8bcc6af80b1bd206"></a>

### `VmIpAddressObservation` record 类

一个 IPv4 地址及来源信息；多个地址分别返回，不拼成一个连接地址。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmIpAddressObservation.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmIpAddressObservation(string Address, string? AdapterId, string? MacAddress, VmIpAddressSource Source, VmIpAddressAssociation Association, DateTimeOffset? ObservedAtUtc, DateTimeOffset ReadAtUtc) `](#api-456124e0baad1a8d) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? AdapterId { get; init; } `](#api-b71263a69476003c) | 确认关联的虚拟网卡身份；只能确认 MAC 或整台 VM 时为 null。 |
| [` public string Address { get; init; } `](#api-2062678e5e62292a) | 经过解析和规范化的单个 IPv4 文本。 |
| [` public VmIpAddressAssociation Association { get; init; } `](#api-3a08e3f596889b60) | 与所请求目标的关联依据。 |
| [` public string? MacAddress { get; init; } `](#api-dcceeddf373ce93d) | 已用于关联的 MAC；整台 VM 的后备数据为 null。 |
| [` public DateTimeOffset? ObservedAtUtc { get; init; } `](#api-3591d37f24e565a3) | 有来源观测时间时返回；来宾上报和邻居缓存未提供可靠时间时为 null。 |
| [` public DateTimeOffset ReadAtUtc { get; init; } `](#api-96eb73c7466fa307) | Core 本次读取该来源的 UTC 时间；不表示地址刚刚形成。 |
| [` public VmIpAddressSource Source { get; init; } `](#api-8731043b8daeb543) | 地址来源。 |

#### 详细说明

```csharp
public sealed record VmIpAddressObservation(string Address, string? AdapterId, string? MacAddress,
    VmIpAddressSource Source, VmIpAddressAssociation Association, DateTimeOffset? ObservedAtUtc, DateTimeOffset ReadAtUtc)
```

| 参数 | 说明 |
| --- | --- |
| `Address` | 经过解析和规范化的单个 IPv4 文本。 |
| `AdapterId` | 确认关联的虚拟网卡身份；只能确认 MAC 或整台 VM 时为 null。 |
| `MacAddress` | 已用于关联的 MAC；整台 VM 的后备数据为 null。 |
| `Source` | 地址来源。 |
| `Association` | 与所请求目标的关联依据。 |
| `ObservedAtUtc` | 有来源观测时间时返回；来宾上报和邻居缓存未提供可靠时间时为 null。 |
| `ReadAtUtc` | Core 本次读取该来源的 UTC 时间；不表示地址刚刚形成。 |

#### 构造函数说明

<a id="api-456124e0baad1a8d"></a>

##### VmIpAddressObservation · 主构造器

```csharp
public VmIpAddressObservation(string Address, string? AdapterId, string? MacAddress, VmIpAddressSource Source, VmIpAddressAssociation Association, DateTimeOffset? ObservedAtUtc, DateTimeOffset ReadAtUtc);
```

| 参数 | 说明 |
| --- | --- |
| `Address` | 经过解析和规范化的单个 IPv4 文本。 |
| `AdapterId` | 确认关联的虚拟网卡身份；只能确认 MAC 或整台 VM 时为 null。 |
| `MacAddress` | 已用于关联的 MAC；整台 VM 的后备数据为 null。 |
| `Source` | 地址来源。 |
| `Association` | 与所请求目标的关联依据。 |
| `ObservedAtUtc` | 有来源观测时间时返回；来宾上报和邻居缓存未提供可靠时间时为 null。 |
| `ReadAtUtc` | Core 本次读取该来源的 UTC 时间；不表示地址刚刚形成。 |

#### 属性说明

<a id="api-b71263a69476003c"></a>

##### AdapterId · record 属性

```csharp
public string? AdapterId { get; init; }
```

确认关联的虚拟网卡身份；只能确认 MAC 或整台 VM 时为 null。

<a id="api-2062678e5e62292a"></a>

##### Address · record 属性

```csharp
public string Address { get; init; }
```

经过解析和规范化的单个 IPv4 文本。

<a id="api-3a08e3f596889b60"></a>

##### Association · record 属性

```csharp
public VmIpAddressAssociation Association { get; init; }
```

与所请求目标的关联依据。

<a id="api-dcceeddf373ce93d"></a>

##### MacAddress · record 属性

```csharp
public string? MacAddress { get; init; }
```

已用于关联的 MAC；整台 VM 的后备数据为 null。

<a id="api-3591d37f24e565a3"></a>

##### ObservedAtUtc · record 属性

```csharp
public DateTimeOffset? ObservedAtUtc { get; init; }
```

有来源观测时间时返回；来宾上报和邻居缓存未提供可靠时间时为 null。

<a id="api-96eb73c7466fa307"></a>

##### ReadAtUtc · record 属性

```csharp
public DateTimeOffset ReadAtUtc { get; init; }
```

Core 本次读取该来源的 UTC 时间；不表示地址刚刚形成。

<a id="api-8731043b8daeb543"></a>

##### Source · record 属性

```csharp
public VmIpAddressSource Source { get; init; }
```

地址来源。

<a id="api-0f1ce389f30b3647"></a>

### `VmIpAddressResolution` record 类

合并已有地址的结果及是否仍需发起独立查询。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmIpService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmIpAddressResolution(List<string> Addresses, bool RequiresLookup) `](#api-fdeebb13cb3fa32e) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public List<string> Addresses { get; init; } `](#api-5c45bb448818ebb3) | 按来源优先级选取的地址文本列表。 |
| [` public bool RequiresLookup { get; init; } `](#api-458401e06e9eb502) | 现有来源没有地址且存在 MAC，调用者可继续执行独立查询。 |

#### 详细说明

```csharp
public sealed record VmIpAddressResolution(List<string> Addresses, bool RequiresLookup)
```

| 参数 | 说明 |
| --- | --- |
| `Addresses` | 按来源优先级选取的地址文本列表。 |
| `RequiresLookup` | 现有来源没有地址且存在 MAC，调用者可继续执行独立查询。 |

#### 构造函数说明

<a id="api-fdeebb13cb3fa32e"></a>

##### VmIpAddressResolution · 主构造器

```csharp
public VmIpAddressResolution(List<string> Addresses, bool RequiresLookup);
```

| 参数 | 说明 |
| --- | --- |
| `Addresses` | 按来源优先级选取的地址文本列表。 |
| `RequiresLookup` | 现有来源没有地址且存在 MAC，调用者可继续执行独立查询。 |

#### 属性说明

<a id="api-5c45bb448818ebb3"></a>

##### Addresses · record 属性

```csharp
public List<string> Addresses { get; init; }
```

按来源优先级选取的地址文本列表。

<a id="api-458401e06e9eb502"></a>

##### RequiresLookup · record 属性

```csharp
public bool RequiresLookup { get; init; }
```

现有来源没有地址且存在 MAC，调用者可继续执行独立查询。

<a id="api-5eeacef878c1a201"></a>

### `VmIpAddressSource` 枚举

地址信息的来源，按宿主嗅探、来宾上报、系统邻居缓存的优先级使用。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmIpAddressObservation.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` ArpCapture = 0 `](#api-7546804431170b25) | 宿主捕获到的 ARP 报文。 |
| [` GuestIntegration = 1 `](#api-b38312eaff645e39) | 来宾集成服务上报的网络配置。 |
| [` NeighborCache = 2 `](#api-05e96b1f9dca742a) | Windows 的邻居缓存。 |

#### 详细说明

```csharp
public enum VmIpAddressSource
```

#### 枚举值说明

<a id="api-7546804431170b25"></a>

##### ArpCapture · 枚举值

```csharp
ArpCapture = 0
```

宿主捕获到的 ARP 报文。

<a id="api-b38312eaff645e39"></a>

##### GuestIntegration · 枚举值

```csharp
GuestIntegration = 1
```

来宾集成服务上报的网络配置。

<a id="api-05e96b1f9dca742a"></a>

##### NeighborCache · 枚举值

```csharp
NeighborCache = 2
```

Windows 的邻居缓存。

<a id="api-0fc26b641e227c07"></a>

### `VmIpService` 类

按宿主被动嗅探、来宾上报和宿主邻居缓存合并地址来源；宿主已观察到的地址优先。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmIpService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static string GetPrimaryAddress(IEnumerable<VmNetworkAdapter> adapters, string fallback = "") `](#api-a0f25a29a0fa99d7) | 从网卡地址列表中选择首个有效 IPv4 地址用于简要显示或连接。 |
| [` public static async Task<ApiResponse<IReadOnlyList<VmIpAddressObservation>>> LookupAsync(Guid vmId, string macAddressWithColons) `](#api-04d686e8f171e829) | 查找 IPv4，返回独立地址及网卡关联、来源和时间；宿主嗅探优先。 |
| [` public static VmIpAddressResolution ResolveAdapterAddresses(string? macAddress, IEnumerable<string>? reportedAddresses, string? lookupResult = null) `](#api-b174efa7170368ed) | 从已有来源中选择地址：宿主已嗅探到的值优先，其次来宾上报列表，再次独立查询文本。 |

#### 详细说明

```csharp
public static class VmIpService
```

调用约定：合并各数据源的观测值或缓存地址。ARP 持续嗅探由调用方启动。

#### 静态方法说明

<a id="api-a0f25a29a0fa99d7"></a>

##### GetPrimaryAddress · 方法

```csharp
public static string GetPrimaryAddress(IEnumerable<VmNetworkAdapter> adapters, string fallback = "");
```

从网卡地址列表中选择首个有效 IPv4 地址用于简要显示或连接。

| 参数 | 说明 |
| --- | --- |
| `adapters` | 网卡对象集合。 |
| `fallback` | 没有可显示地址时返回的文本，默认为空字符串。 |

返回：单个 IPv4 地址或 fallback；逗号分隔的地址先拆分，无效文本及 IPv6 不作为 IPv4 返回。

<a id="api-04d686e8f171e829"></a>

##### LookupAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyList<VmIpAddressObservation>>> LookupAsync(Guid vmId, string macAddressWithColons);
```

查找 IPv4，返回独立地址及网卡关联、来源和时间；宿主嗅探优先。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID，用于来宾上报查询。 |
| `macAddressWithColons` | 目标 MAC，允许冒号、横线或连续 12 位十六进制；空值返回空集合。 |

返回：按优先级选取的地址观测集合。若只能取得整台 VM 的来宾地址，Association 为 VirtualMachine，不能当成该 MAC 的确切地址。来源均无地址且查询失败时返回结构化错误。

调用约定：ARP 和邻居缓存仅按 MAC 关联，不证明地址仍属于目标 VM，也不替代 SSH 等服务器身份验证。来宾网卡通过系统关联查询；缓存无可靠观测时间时 ObservedAtUtc 为 null。

<a id="api-b174efa7170368ed"></a>

##### ResolveAdapterAddresses · 方法

```csharp
public static VmIpAddressResolution ResolveAdapterAddresses(string? macAddress, IEnumerable<string>? reportedAddresses, string? lookupResult = null);
```

从已有来源中选择地址：宿主已嗅探到的值优先，其次来宾上报列表，再次独立查询文本。

| 参数 | 说明 |
| --- | --- |
| `macAddress` | 网卡 MAC；null 或空字符串时不尝试宿主按 MAC 查找。 |
| `reportedAddresses` | 来宾已上报的地址集合；null 表示没有上报数据。 |
| `lookupResult` | 此前独立查询得到的字符串；null 或空表示暂无查询结果。 |

返回：所选来源中去重后的有效 IP 地址及是否仍需查询的标记。逗号分隔的结果会拆成独立地址，空白和无效项会被忽略。

<a id="api-621a83d450366f2e"></a>

### `VmIsolationSupport` record 类

宿主支持的虚拟机硬件隔离能力。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 安全 |
| 源文件 | ` Management/VirtualMachines/Security/VmIsolationSupport.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmIsolationSupport(bool Supported, IReadOnlyList<string> Types) `](#api-3ba00ef4f6ae8e0f) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool Supported { get; init; } `](#api-d1296f57924662c0) | 是否存在 Disabled 以外的已识别隔离类型。 |
| [` public IReadOnlyList<string> Types { get; init; } `](#api-3cdb27194ea537c3) | 可选隔离类型名称，始终包含 Disabled。 |

#### 详细说明

```csharp
public sealed record VmIsolationSupport(bool Supported, IReadOnlyList<string> Types)
```

| 参数 | 说明 |
| --- | --- |
| `Supported` | 是否存在 Disabled 以外的已识别隔离类型。 |
| `Types` | 可选隔离类型名称，始终包含 Disabled。 |

#### 构造函数说明

<a id="api-3ba00ef4f6ae8e0f"></a>

##### VmIsolationSupport · 主构造器

```csharp
public VmIsolationSupport(bool Supported, IReadOnlyList<string> Types);
```

| 参数 | 说明 |
| --- | --- |
| `Supported` | 是否存在 Disabled 以外的已识别隔离类型。 |
| `Types` | 可选隔离类型名称，始终包含 Disabled。 |

#### 属性说明

<a id="api-d1296f57924662c0"></a>

##### Supported · record 属性

```csharp
public bool Supported { get; init; }
```

是否存在 Disabled 以外的已识别隔离类型。

<a id="api-3cdb27194ea537c3"></a>

##### Types · record 属性

```csharp
public IReadOnlyList<string> Types { get; init; }
```

可选隔离类型名称，始终包含 Disabled。

<a id="api-943caf626a4d8869"></a>

### `VmMemoryFields` 类

内存的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 内存 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Memory/VmMemoryFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<long> AssignedMiB `](#api-6753301b848a0480) | 运行时已分配内存：Hyper-V 当前分配给运行中虚拟机的内存大小，单位为 MiB。 |
| [` public static readonly VmQueryField<int> AvailablePercent `](#api-5ba20292feb21228) | 来宾可用内存比例：按来宾内存需求换算的可用内存比例，单位为 %。 |
| [` public static readonly VmQueryField<byte> BackingPageSize `](#api-cdcc4d00ce904585) | 后备页大小：承载虚拟机内存所使用的页面类型：标准页、大页或巨页。 |
| [` public static readonly VmQueryField<byte> BackingType `](#api-085553727c32ce93) | 内存后备类型：选择承载虚拟机内存的后备方式。 |
| [` public static readonly VmQueryField<int> Buffer `](#api-9dc238f2ec64a90a) | 内存缓冲比例：动态内存的目标额外缓冲量，占来宾内存需求的百分比。 |
| [` public static readonly VmQueryField<bool> CxlEnabled `](#api-5719a1b27f82c102) | CXL 开关：实验性配置，已知部分 VMMS 版本在写入时可能崩溃；Core 保留写入入口，由调用方提示风险。 |
| [` public static readonly VmQueryField<uint> DynMemOperationAlignment `](#api-5c20e553289c6fc9) | 动态内存操作对齐：动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。 |
| [` public static readonly VmQueryField<bool> DynamicMemoryEnabled `](#api-9477a620d59ce7d0) | 动态内存开关：是否启用动态内存。 |
| [` public static readonly VmQueryField<bool> EnableColdHint `](#api-020c8373f6ba8090) | 冷内存提示：控制内存配置中的冷提示开关。 |
| [` public static readonly VmQueryField<bool> EnableEpf `](#api-ab29e7b3591d4600) | EPF 开关：保留 Hyper-V 的 EnableEpf 配置开关。 |
| [` public static readonly VmQueryField<bool> EnableGpaPinning `](#api-f3c84d11dfd17563) | GPA 固定开关：来宾物理地址（GPA）固定配置的开关。 |
| [` public static readonly VmQueryField<bool> EnableHotHint `](#api-fd421450537c3134) | 热内存提示：控制内存配置中的热提示开关。 |
| [` public static readonly VmQueryField<bool> EnablePrivateCompressionStore `](#api-42e6cd53fe85fea6) | 专用压缩存储：内存配置中的专用压缩存储开关。 |
| [` public static readonly VmQueryField<ulong> MaxMemoryBlocksPerNumaNode `](#api-c1194858b03dc692) | 每 NUMA 节点内存块上限：限制来宾可见的单个虚拟 NUMA 节点的内存容量。 |
| [` public static readonly VmQueryField<long> Maximum `](#api-22d2202d4756e5f0) | 最大内存：动态内存允许分配的最高内存大小，单位为 MiB。 |
| [` public static readonly VmQueryField<byte> MemoryAccessTrackingPolicy `](#api-0a424f7b01f07277) | 内存访问跟踪策略：配置内存访问跟踪的粒度策略。 |
| [` public static readonly VmQueryField<byte> MemoryAccessTrackingState `](#api-2a71380a6e9b099c) | 内存访问跟踪状态：配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。 |
| [` public static readonly VmQueryField<byte> MemoryEncryptionPolicy `](#api-46c0b0951d716b76) | 内存加密策略：请求虚拟机内存采用的加密策略。 |
| [` public static readonly VmQueryField<long> Minimum `](#api-60fe4a712286afcb) | 最小内存：动态内存允许分配的最低内存大小，单位为 MiB。 |
| [` public static readonly VmQueryField<bool> SgxEnabled `](#api-dacbe0e17edb530b) | SGX 开关：请求为虚拟机启用 SGX 支持。 |
| [` public static readonly VmQueryField<string> SgxLaunchControlDefault `](#api-724785468fc860e0) | SGX 默认启动控制：SGX 默认启动控制配置的原始文本。 |
| [` public static readonly VmQueryField<uint> SgxLaunchControlMode `](#api-82f58951c1e978ad) | SGX 启动控制模式：SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。 |
| [` public static readonly VmQueryField<double> SgxSize `](#api-deb7de2a490c2aa1) | SGX 内存大小：为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。 |
| [` public static readonly VmQueryField<long> Startup `](#api-d2547f93da8b2561) | 启动内存：为虚拟机启动配置的内存大小，单位为 MiB。 |
| [` public static readonly VmQueryField<uint> Weight `](#api-8eb7156ab5917a0f) | 内存权重：宿主内存发生竞争时，虚拟机参与内存分配的优先权重。 |

#### 详细说明

```csharp
public static class VmMemoryFields
```

#### 字段与常量说明

<a id="api-6753301b848a0480"></a>

##### AssignedMiB · 字段

```csharp
public static readonly VmQueryField<long> AssignedMiB;
```

Hyper-V 当前分配给运行中虚拟机的内存大小。

**取值说明：** 单位为 MiB；1 MiB = 1,048,576 字节。例如 `1024` 表示 1 GiB。

<a id="api-5ba20292feb21228"></a>

##### AvailablePercent · 字段

```csharp
public static readonly VmQueryField<int> AvailablePercent;
```

当前分配内存相对来宾内存需求的剩余比例，单位为 %。不等同于来宾任务管理器的进程工作集或空闲页比例。

**单位与格式：** %

**取值说明：** 可为负数，表示内存需求超过当前分配量。

**适用条件：** 适用于运行中且能取得来宾内存需求的虚拟机。固定内存计数器必须在采样前后唯一对应同一 GUID，运行时间未倒退且分配量一致；同名、未报告需求或查询失败时返回缺测状态，不能当作零占用。

<a id="api-cdcc4d00ce904585"></a>

##### BackingPageSize · 字段

```csharp
public static readonly VmQueryField<byte> BackingPageSize;
```

承载虚拟机内存所使用的页面类型：标准页、大页或巨页。

**取值说明：** 0：4 KiB 标准页；1：2 MiB 大页；2：1 GiB 巨页。0 是明确的标准页请求，不表示自动或未设置。

**适用条件：** 仅有 HugePagesEnabled 的宿主无法表达 4 KiB，Core 返回 Unsupported，不将 0 转成 false。高级页策略通常要求工作进程已退出；暂停、保存状态和重复提交同值都不能证明允许修改。配置读回不等于实际分配页粒度。

<a id="api-085553727c32ce93"></a>

##### BackingType · 字段

```csharp
public static readonly VmQueryField<byte> BackingType;
```

选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。

**单位与格式：** 类型编码，不是内存大小。

**取值说明：** 现有选项映射为 0：物理映射；1：虚拟映射；2：混合映射。Core 保留原始编码，不为未识别值赋予含义。

**适用条件：** 实验性读写能力。混合映射涉及逐 NUMA 节点的后备配置；当前入口没有提供这部分配套配置。保留编码读写不代表仅写入 2 就能完成混合映射。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-9dc238f2ec64a90a"></a>

##### Buffer · 字段

```csharp
public static readonly VmQueryField<int> Buffer;
```

动态内存的目标额外缓冲量，占来宾内存需求的百分比。

**单位与格式：** %

**取值说明：** 例如 20 表示以额外提供来宾需求量的 20% 作为缓冲目标。

**适用条件：** 适用于动态内存。

<a id="api-5719a1b27f82c102"></a>

##### CxlEnabled · 字段

```csharp
public static readonly VmQueryField<bool> CxlEnabled;
```

虚拟机内存配置中的 CXL 开关。此入口只修改 CxlEnabled，不枚举、分配或直通具体 CXL 设备。

**取值说明：** true：请求开启；false：请求关闭。当前没有由 Core 定义的宿主默认值。

**适用条件：** ARM64 VMMS 10.0.29671.1000 已实测写 true 导致服务崩溃，10.0.28000.2804 静态确认同类缺陷；其他版本的修复边界未确认。保存设置即可触发，无需启动来宾。已研究的 28000 路径要求第二代、配置版本至少 12.2 且没有工作进程；旧配置为 true 时还可能在处理新 false 前校验失败，关闭不能作为可靠恢复手段。Core 保留读写入口，不按研究版本封禁；满足前述门槛不证明 CXL 可用。

<a id="api-5c20e553289c6fc9"></a>

##### DynMemOperationAlignment · 字段

```csharp
public static readonly VmQueryField<uint> DynMemOperationAlignment;
```

动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。

**单位与格式：** 对齐选项编码，不是直接以字节传入的对齐量。

**取值说明：** 现有选项映射为 0：禁用；1：4 KB；2：2 MB；3：1 GB。这些是项目已有选项的标签，Core 直接传递编码，不进行字节换算。

**适用条件：** 实验性读写能力。属于动态内存扩展配置。当前仅写入该属性，没有额外执行内存调整。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-9477a620d59ce7d0"></a>

##### DynamicMemoryEnabled · 字段

```csharp
public static readonly VmQueryField<bool> DynamicMemoryEnabled;
```

是否启用动态内存。启用后，Hyper-V 可以按需调整虚拟机的内存分配。

**取值说明：** `true`：启用动态内存；`false`：使用静态内存。

<a id="api-020c8373f6ba8090"></a>

##### EnableColdHint · 字段

```csharp
public static readonly VmQueryField<bool> EnableColdHint;
```

控制内存配置中的冷提示开关。此入口不负责提交某段内存的冷热信息。

**取值说明：** true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。

**适用条件：** 实验性读写能力。按当前 Core 规则，只在非物理后备配置下保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-ab29e7b3591d4600"></a>

##### EnableEpf · 字段

```csharp
public static readonly VmQueryField<bool> EnableEpf;
```

保留 Hyper-V 的 EnableEpf 配置开关。缩写全称及功能用途尚未确认，因此不将它解释成某种已知内存优化。

**取值说明：** true、false。系统默认值和两种取值的实际行为尚未确认。

**适用条件：** 实验性读写能力。现有 Core 规则将它与冷提示、热提示和专用压缩存储一起处理：物理后备时不保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-f3c84d11dfd17563"></a>

##### EnableGpaPinning · 字段

```csharp
public static readonly VmQueryField<bool> EnableGpaPinning;
```

来宾物理地址（GPA）固定配置的开关。Core 目前读取并提交对应属性，不提供指定 GPA 范围的独立操作。

**取值说明：** true：请求开启；false：请求关闭。当前没有由 Core 定义的宿主默认值。

**适用条件：** 实验性读写能力。宿主必须提供 EnableGpaPinning 属性；读取到开关不表示某个指定地址范围已经固定。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-fd421450537c3134"></a>

##### EnableHotHint · 字段

```csharp
public static readonly VmQueryField<bool> EnableHotHint;
```

控制内存配置中的热提示开关。此入口不负责提交某段内存的冷热信息。

**取值说明：** true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。

**适用条件：** 实验性读写能力。按当前 Core 规则，只在非物理后备配置下保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-42e6cd53fe85fea6"></a>

##### EnablePrivateCompressionStore · 字段

```csharp
public static readonly VmQueryField<bool> EnablePrivateCompressionStore;
```

内存配置中的专用压缩存储开关。当前入口仅传递配置，不返回压缩量或压缩率。

**取值说明：** true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。

**适用条件：** 实验性读写能力。按当前 Core 规则，只在非物理后备配置下保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-c1194858b03dc692"></a>

##### MaxMemoryBlocksPerNumaNode · 字段

```csharp
public static readonly VmQueryField<ulong> MaxMemoryBlocksPerNumaNode;
```

限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。

**单位与格式：** MiB。表示容量，不是页面数，也不是 NUMA 节点数。

**取值说明：** Core 按当前页设置向下对齐：巨页为 1024 MiB，其余为 2 MiB；结果至少达到按同一粒度向上对齐的 32 MiB。因此巨页模式的最小提交值为 1024 MiB。

**适用条件：** 实验性读写能力。这是虚拟 NUMA 配置，不代表宿主物理节点容量，也不固定来宾内存到某个宿主节点。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-22d2202d4756e5f0"></a>

##### Maximum · 字段

```csharp
public static readonly VmQueryField<long> Maximum;
```

动态内存允许分配的最高内存大小。

**取值说明：** 单位为 MiB；1 MiB = 1,048,576 字节。例如 `1024` 表示 1 GiB。

**适用条件：** 动态内存启用时作为内存分配上限。

<a id="api-0a424f7b01f07277"></a>

##### MemoryAccessTrackingPolicy · 字段

```csharp
public static readonly VmQueryField<byte> MemoryAccessTrackingPolicy;
```

配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。

**单位与格式：** 跟踪策略编码；不能与 BackingPageSize 的编码互换。

**取值说明：** 现有选项映射为 0：最佳；1：4 KB；2：2 MB；3：1 GB。Core 原样传递编码，不主动开启跟踪。

**适用条件：** 实验性读写能力。应结合 MemoryAccessTrackingState 理解配置。读取到策略值本身不能说明跟踪已启用。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-2a71380a6e9b099c"></a>

##### MemoryAccessTrackingState · 字段

```csharp
public static readonly VmQueryField<byte> MemoryAccessTrackingState;
```

配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。此值是配置，不是一次访问采样结果。

**取值说明：** 现有选项映射为 0：关闭；1：开启；2：按 NUMA 节点配置。

**适用条件：** 实验性读写能力。当前入口没有提供编码 2 所需的逐节点配置。保留该编码读写不表示配套能力完整。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-46c0b0951d716b76"></a>

##### MemoryEncryptionPolicy · 字段

```csharp
public static readonly VmQueryField<byte> MemoryEncryptionPolicy;
```

请求虚拟机内存采用的加密策略。此字段表示配置，不能用它单独证明运行中的内存已经加密。

**取值说明：** 现有选项映射为 0：禁用；1：支持时启用；2：始终启用。Core 原样传递编码。

**适用条件：** 实验性读写能力。需要宿主提供 MemoryEncryptionPolicy；当前入口不探测完整的机密计算能力，也不更改安全启动或 vTPM。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-60fe4a712286afcb"></a>

##### Minimum · 字段

```csharp
public static readonly VmQueryField<long> Minimum;
```

动态内存允许分配的最低内存大小。

**取值说明：** 单位为 MiB；1 MiB = 1,048,576 字节。例如 `1024` 表示 1 GiB。

**适用条件：** 动态内存启用时参与内存分配；关闭时仍可读取保存的配置。

<a id="api-dacbe0e17edb530b"></a>

##### SgxEnabled · 字段

```csharp
public static readonly VmQueryField<bool> SgxEnabled;
```

请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置；启用配置本身不表示来宾已能使用 SGX。

**取值说明：** true：请求启用；false：请求关闭。设置模型中将它设为 true 时，若 SgxSize 为空或小于 2，会先补为 2 MiB。

**适用条件：** 实验性读写能力。微软文档记录该属性从 Windows 10 1703 加入。实际使用仍需宿主硬件、Hyper-V 配置和来宾共同支持。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-724785468fc860e0"></a>

##### SgxLaunchControlDefault · 字段

```csharp
public static readonly VmQueryField<string> SgxLaunchControlDefault;
```

SGX 默认启动控制配置的原始文本。当前 Core 不解析、生成或改写其中的内容。

**取值说明：** 宿主返回的文本原样保留。格式、允许的内容、空字符串是否有效及默认值尚未确认。

**适用条件：** 实验性读写能力。属于 SGX 扩展配置；与 SgxLaunchControlMode 的配合要求尚未验证。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-82f58951c1e978ad"></a>

##### SgxLaunchControlMode · 字段

```csharp
public static readonly VmQueryField<uint> SgxLaunchControlMode;
```

SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。

**取值说明：** 现有选项映射为 0：禁止访问；1：仅限读取；2：允许读写。Core 原样传递该编码。

**适用条件：** 实验性读写能力。属于 SGX 扩展配置；当前入口不自动启用 SGX，也不补齐默认启动控制字符串。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

<a id="api-deb7de2a490c2aa1"></a>

##### SgxSize · 字段

```csharp
public static readonly VmQueryField<double> SgxSize;
```

为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。

**单位与格式：** MiB；Core 的容量单位。底层属性为 UInt64，当前公开模型以 Double 表示，不提供小数 MiB 的写入精度。

**取值说明：** 提交时转换为 UInt64，最小 2 MiB，再按 2 MiB 向下对齐。例如 5 MiB 最终写为 4 MiB。需要精确请求时传入不小于 2 的偶数整数。

**适用条件：** 实验性读写能力。只有合并后的 SgxEnabled 为 true 时，单独选择此字段才会写入容量；关闭 SGX 时该分支不写。微软文档记录此属性从 Windows 10 1703 加入。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。

**缺测与错误：** 自由查询按字段状态区分缺值；GetVmMemorySettingsAsync 的设置模型未读到容量时返回 null，已读到的 0 原样保留，不能把两者混同。

<a id="api-d2547f93da8b2561"></a>

##### Startup · 字段

```csharp
public static readonly VmQueryField<long> Startup;
```

虚拟机启动时使用的内存容量。启用动态内存后，运行期间实际分配的内存可以变化。

**取值说明：** 单位为 MiB；1 MiB = 1,048,576 字节。例如 `1024` 表示 1 GiB。

<a id="api-8eb7156ab5917a0f"></a>

##### Weight · 字段

```csharp
public static readonly VmQueryField<uint> Weight;
```

宿主内存发生竞争时，虚拟机参与内存分配的优先权重。

**取值说明：** 无符号整数权重；数值越大，内存竞争时的相对优先级越高。

<a id="api-699a5f0f3788cfe2"></a>

### `VmMemoryService` 类

读取、校验和应用内存设置，处理动态内存、后备页和 vNUMA 的联动。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 内存；管理层面 / 虚拟机 / 修改 / 内存 |
| 源文件 | ` Management/VirtualMachines/Memory/VmMemoryService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Capabilities GetCapabilities(bool isRunning, bool dynamicMemoryEnabled) `](#api-dd0769d5a75f506a) | 根据运行状态和当前动态内存开关计算内存编辑能力。 |
| [` public static string? GetChangeError(VmMemorySettings current, VmMemorySettings requested, bool isRunning, string? changedProperty = null) `](#api-04c74eb1d0f19b8e) | 比较当前与请求设置，检查运行中的虚拟机是否允许这些内存变更。 |
| [` public static async Task<ApiResponse<VmMemorySettings>> GetVmMemorySettingsAsync(Guid vmId) `](#api-2dcebf0d06274822) | 读取虚拟机内存配置及当前主机可提供的高级内存属性。 |
| [` public static void NormalizeMemoryBackingSettings(VmMemorySettings settings, string? changedProperty) `](#api-52bb45cdb2267299) | 原地规范化内存后备页设置及其互斥项；指定字段时保留用户最后修改的选择，null 使用整组应用规则。 |
| [` public static async Task<ApiResponse> SetVmMemorySettingsAsync(Guid vmId, VmMemorySettings newSettings, bool isVmRunning, IReadOnlyCollection<string> changedProperties) `](#api-5bd3b5d5348f1371) | 只应用明确选择的内存字段，并处理必要的页对齐、动态内存和 vNUMA 联动。 |

#### 详细说明

```csharp
public static class VmMemoryService
```

#### 静态方法说明

<a id="api-dd0769d5a75f506a"></a>

##### GetCapabilities · 方法

```csharp
public static Capabilities GetCapabilities(bool isRunning, bool dynamicMemoryEnabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 内存。

根据运行状态和当前动态内存开关计算内存编辑能力。

| 参数 | 说明 |
| --- | --- |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |
| `dynamicMemoryEnabled` | 当前是否启用动态内存。 |

返回：离线设置、启动内存和动态上下限是否允许编辑的标记。

<a id="api-04c74eb1d0f19b8e"></a>

##### GetChangeError · 方法

```csharp
public static string? GetChangeError(VmMemorySettings current, VmMemorySettings requested, bool isRunning, string? changedProperty = null);
```

功能归属：管理层面 / 虚拟机 / 修改 / 内存。

比较当前与请求设置，检查运行中的虚拟机是否允许这些内存变更。

| 参数 | 说明 |
| --- | --- |
| `current` | 从主机读取的当前内存配置。 |
| `requested` | 计划写入的目标内存配置；本方法只比较，不修改。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |
| `changedProperty` | 只修改的 VmMemorySettings 属性名，建议使用 nameof；null 表示检查或规范化传入模型的全部相关属性；不提交宿主。 |

返回：首个状态限制说明；无状态限制时为 null。数值范围在 WMI 提交时校验。

<a id="api-2dcebf0d06274822"></a>

##### GetVmMemorySettingsAsync · 方法

```csharp
public static async Task<ApiResponse<VmMemorySettings>> GetVmMemorySettingsAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 内存。

读取虚拟机内存配置及当前主机可提供的高级内存属性。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时返回内存设置；虚拟机或配置缺失为 NotFound，查询异常保留失败原因。可空扩展属性未取得值时保留 null，包括 SgxSize；字段级缺测状态通过自由查询取得。

<a id="api-52bb45cdb2267299"></a>

##### NormalizeMemoryBackingSettings · 方法

```csharp
public static void NormalizeMemoryBackingSettings(VmMemorySettings settings, string? changedProperty);
```

功能归属：管理层面 / 虚拟机 / 修改 / 内存。

原地规范化内存后备页设置及其互斥项；指定字段时保留用户最后修改的选择，null 使用整组应用规则。

| 参数 | 说明 |
| --- | --- |
| `settings` | 要原地规范化的内存配置对象；调用后其中互斥项可能改变。 |
| `changedProperty` | 只修改的 VmMemorySettings 属性名，建议使用 nameof；null 表示检查或规范化传入模型的全部相关属性；不提交宿主。 |

<a id="api-5bd3b5d5348f1371"></a>

##### SetVmMemorySettingsAsync · 方法

```csharp
public static async Task<ApiResponse> SetVmMemorySettingsAsync(Guid vmId, VmMemorySettings newSettings, bool isVmRunning, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 内存。

只应用明确选择的内存字段，并处理必要的页对齐、动态内存和 vNUMA 联动。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID。 |
| `newSettings` | 本次请求值；只读取 changedProperties 中的属性，容量单位为 MiB，Weight 为原始权重。 |
| `isVmRunning` | 兼容参数；执行时重新查询实际运行状态。 |
| `changedProperties` | 要提交的属性名集合，不能为空。例如仅修改启动内存时包含 `nameof(VmMemorySettings.Startup)`；支持模型全部公开可写属性。不比较旧值或版本，同字段按本次请求覆盖。CPU 与内存设置在同一虚拟机上跨 Core 进程串行；锁覆盖读取、vNUMA 联动和写入，不阻止外部工具修改。 |

返回：应用结果；Steps 按顺序记录已执行的 DisableVirtualNuma、Memory、EnableVirtualNuma 步骤，含各步骤的状态与原始错误。不需要或未开始的步骤不列出。

调用约定：未选字段使用执行时的当前值。切换动态内存、后备页、后备类型及 SGX 时仍执行其必要联动；仅修改无关字段不会重写这些设置。



实验性内存字段继续接受显式提交，不增加启用总开关。字段用途、现有选项和未验证范围见 `VmMemoryFields`；实验性标记不是功能已在所有 Windows 版本上验证的承诺。



可空请求值为 null 时忽略该选择，不表示清空或恢复默认，也不因该选择触发联动；其他非空选择仍可触发必要联动。全部选择均为 null 时不提交。明确提交非空值但宿主没有对应属性，或旧 HugePagesEnabled 无法表达请求的页大小时，在任何修改前返回 Unsupported，不部分应用其余选择。



成功仅表示本次配置调用完成，不证明实际页粒度、提示投递、压缩或加密已经生效；同值请求也不能用来验证字段可写。Core 不新增基于研究风险或版本的开关拦截，调用方应展示字段文档中的风险。尤其不得通过试写 CxlEnabled 来探测 CXL 支持，已确认版本可能在保存时导致 VMMS 崩溃，详见 `VmMemoryFields.CxlEnabled`。

示例：

```csharp
using System;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Services;

public static class MemoryFieldExample
{
    public static async Task SetStartupAsync(Guid vmId)
    {
        var settings = new VmMemorySettings { Startup = 4096 };
        var result = await VmMemoryService.SetVmMemorySettingsAsync(
            vmId, settings, isVmRunning: false,
            changedProperties: [nameof(VmMemorySettings.Startup)]);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }
}
```

<a id="api-87d4c54605ffd52f"></a>

### `VmMemoryService.Capabilities` record 结构

按运行状态计算出的内存字段编辑能力。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 内存 |
| 源文件 | ` Management/VirtualMachines/Memory/VmMemoryService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public Capabilities(bool OfflineSettings, bool Startup, bool DynamicLimits) `](#api-b904d031888f0852) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool DynamicLimits { get; init; } `](#api-3623cf84e07c43dd) | 是否允许编辑动态内存上下限及缓冲。 |
| [` public bool OfflineSettings { get; init; } `](#api-24043f735835d811) | 是否允许修改要求离线的内存字段。 |
| [` public bool Startup { get; init; } `](#api-10ac1057a1574e49) | 是否允许修改启动内存。 |

#### 详细说明

```csharp
public readonly record struct Capabilities(bool OfflineSettings, bool Startup, bool DynamicLimits)
```

| 参数 | 说明 |
| --- | --- |
| `OfflineSettings` | 是否允许修改要求离线的内存字段。 |
| `Startup` | 是否允许修改启动内存。 |
| `DynamicLimits` | 是否允许编辑动态内存上下限及缓冲。 |

#### 构造函数说明

<a id="api-b904d031888f0852"></a>

##### Capabilities · 主构造器

```csharp
public Capabilities(bool OfflineSettings, bool Startup, bool DynamicLimits);
```

| 参数 | 说明 |
| --- | --- |
| `OfflineSettings` | 是否允许修改要求离线的内存字段。 |
| `Startup` | 是否允许修改启动内存。 |
| `DynamicLimits` | 是否允许编辑动态内存上下限及缓冲。 |

#### 属性说明

<a id="api-3623cf84e07c43dd"></a>

##### DynamicLimits · record 属性

```csharp
public bool DynamicLimits { get; init; }
```

是否允许编辑动态内存上下限及缓冲。

<a id="api-24043f735835d811"></a>

##### OfflineSettings · record 属性

```csharp
public bool OfflineSettings { get; init; }
```

是否允许修改要求离线的内存字段。

<a id="api-10ac1057a1574e49"></a>

##### Startup · record 属性

```csharp
public bool Startup { get; init; }
```

是否允许修改启动内存。

<a id="api-46296dc54cee191f"></a>

### `VmMetadataFields` 类

随虚拟机配置保存的自定义数据，以键和值组成一项。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 额外元数据 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Metadata/VmMetadataFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<string> Key `](#api-b29932b7273e88a1) | 元数据键：虚拟机 HostOnlyItems 元数据项的键名。 |
| [` public static readonly VmQueryField<string> Value `](#api-df80cf1bf4e6b9c0) | 元数据值：同一元数据项的字符串值，通过键名关联。 |

#### 详细说明

```csharp
public static class VmMetadataFields
```

#### 字段与常量说明

<a id="api-b29932b7273e88a1"></a>

##### Key · 字段

```csharp
public static readonly VmQueryField<string> Key;
```

每项自定义数据的名称，用于读取、修改或删除该项。数据保存在虚拟机配置的 HostOnlyItems 中。

**取值说明：** 非空字符串，区分大小写，例如 `MyApp.Environment`。

<a id="api-df80cf1bf4e6b9c0"></a>

##### Value · 字段

```csharp
public static readonly VmQueryField<string> Value;
```

指定键对应的字符串内容。每个键保存一个值，内容格式由使用该键的程序约定。

**取值说明：** 字符串，可以为空；可保存普通文本或 JSON 等结构化文本。

<a id="api-76ce30bc80984d54"></a>

### `VmMetadataService` 类

通过 Hyper-V HostOnlyItems 读取和保存随虚拟机配置携带的字符串元数据。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 额外元数据；管理层面 / 虚拟机 / 修改 / 额外元数据 |
| 源文件 | ` Management/VirtualMachines/Metadata/VmMetadataService.OperatingSystem.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse<IReadOnlyDictionary<string, string>>> ReadAsync(Guid vmId) `](#api-987cb5dc889f2286) | 读取指定虚拟机当前配置的全部 HostOnlyItems，不包含来宾交换数据。 |
| [` public static Task<ApiResponse> RemoveAsync(Guid vmId, string key) `](#api-25bccb731f217a85) | 删除指定 HostOnlyItems 键；键已不存在时直接成功。 |
| [` public static Task<ApiResponse> SetAsync(Guid vmId, string key, string value) `](#api-36243ce225f467d9) | 新增或修改一个 HostOnlyItems 键，等待 VMMS 作业结束并回读确认。 |
| [` public static Task<ApiResponse> SetOperatingSystemTypeAsync(Guid vmId, string osType) `](#api-7eabd73ae6dd2ead) | 按稳定 GUID 将用于界面识别的操作系统分类保存到 HostOnlyItems。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public const string CpuAffinityKey = "ExHyperV.CpuAffinity" `](#api-31eda3684731eb6a) | 处理器亲和性的键名。 |
| [` public const string OsTypeKey = "ExHyperV.OSType" `](#api-840af435315cad08) | 操作系统分类的键名；值与 OsImages 使用的分类名称一致。 |

#### 详细说明

```csharp
public static partial class VmMetadataService
```

调用约定：使用本机 WMI 和当前进程权限，Source 固定为 4，不交换给来宾。只更新指定键，保留其他程序的数据；不读取、迁移或修改虚拟机备注。键名区分大小写，值是普通字符串；结构化内容由调用方约定。

示例：

```csharp
using System;
using System.Threading.Tasks;
using ExHyperV.Services;

public static class MetadataExample
{
    public static async Task SaveOsTypeAsync(Guid vmId)
    {
        var result = await VmMetadataService.SetAsync(vmId, VmMetadataService.OsTypeKey, "Linux");
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }
}
```

#### 静态方法说明

<a id="api-987cb5dc889f2286"></a>

##### ReadAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyDictionary<string, string>>> ReadAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 额外元数据。

读取指定虚拟机当前配置的全部 HostOnlyItems，不包含来宾交换数据。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 虚拟机稳定 GUID，不能是空 GUID。 |

返回：成功时返回独立只读字典，未存任何键时字典为空；虚拟机不存在、查询失败或数据损坏时返回失败。

<a id="api-25bccb731f217a85"></a>

##### RemoveAsync · 方法

```csharp
public static Task<ApiResponse> RemoveAsync(Guid vmId, string key);
```

功能归属：管理层面 / 虚拟机 / 修改 / 额外元数据。

删除指定 HostOnlyItems 键；键已不存在时直接成功。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 虚拟机稳定 GUID，不能是空 GUID。 |
| `key` | 要删除的非空键名，区分大小写。 |

返回：指定键不存在时成功；查询或删除失败返回失败，不清理其他键。

<a id="api-36243ce225f467d9"></a>

##### SetAsync · 方法

```csharp
public static Task<ApiResponse> SetAsync(Guid vmId, string key, string value);
```

功能归属：管理层面 / 虚拟机 / 修改 / 额外元数据。

新增或修改一个 HostOnlyItems 键，等待 VMMS 作业结束并回读确认。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 虚拟机稳定 GUID，不能是空 GUID。 |
| `key` | 非空键名，区分大小写；使用自己的名称前缀避免与其他程序冲突。 |
| `value` | 要保存的非 null 字符串，可以为空；JSON 等内容由调用方序列化。 |

返回：指定键已保存且回读一致时成功。权限、容量、数据格式和并发修改等错误返回失败，不回退到其他存储。

调用约定：新增和修改分别通过 AddKvpItems、ModifyKvpItems 提交。与其他进程并发修改同一键时不提供比较交换或跨调用事务；失败后应重新读取。容量上限由 VMMS 校验。

<a id="api-7eabd73ae6dd2ead"></a>

##### SetOperatingSystemTypeAsync · 方法

```csharp
public static Task<ApiResponse> SetOperatingSystemTypeAsync(Guid vmId, string osType);
```

功能归属：管理层面 / 虚拟机 / 修改 / 额外元数据。

按稳定 GUID 将用于界面识别的操作系统分类保存到 HostOnlyItems。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `osType` | 操作系统分类，不改变来宾系统本身。 |

返回：元数据写入结果；此操作不读取或修改备注，也不会安装或更改来宾操作系统。

#### 字段与常量说明

<a id="api-31eda3684731eb6a"></a>

##### CpuAffinityKey · 字段

```csharp
public const string CpuAffinityKey = "ExHyperV.CpuAffinity";
```

功能归属：管理层面 / 虚拟机 / 修改 / 额外元数据。

处理器亲和性的键名。新保存使用 Version=2 的 JSON，包含 Backend、LogicalProcessors、HostFingerprint 和 TopologyFingerprint；后两项为宿主 UUID 和编号拓扑的摘要。旧数组与版本 1 记录保留可读，但非空旧选择需要重新保存。空集合表示不限制；保存值不代表实际绑定。

<a id="api-840af435315cad08"></a>

##### OsTypeKey · 字段

```csharp
public const string OsTypeKey = "ExHyperV.OSType";
```

功能归属：管理层面 / 虚拟机 / 修改 / 额外元数据。

操作系统分类的键名；值与 OsImages 使用的分类名称一致。

<a id="api-85d07c02798ee6d2"></a>

### `VmMmioFields` 类

MMIO的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / MMIO |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Mmio/VmMmioFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<ulong> HighBaseMiB `](#api-ced5d854b4ebfd58) | 高位 MMIO 基址：高位 MMIO 地址空间的起始地址，以 MiB 表示。 |
| [` public static readonly VmQueryField<ulong> HighSizeMiB `](#api-9febb14fd2b978b6) | 高位 MMIO 空间：为设备预留的高位 MMIO 地址空间大小，单位为 MiB。 |
| [` public static readonly VmQueryField<ulong> LowSizeMiB `](#api-864c62caf7888cdb) | 低位 MMIO 空间：为设备预留的低位 MMIO 地址空间大小，单位为 MiB。 |

#### 详细说明

```csharp
public static class VmMmioFields
```

#### 字段与常量说明

<a id="api-ced5d854b4ebfd58"></a>

##### HighBaseMiB · 字段

```csharp
public static readonly VmQueryField<ulong> HighBaseMiB;
```

高位 MMIO 地址空间的起始地址，以 MiB 表示。

**取值说明：** 单位为 MiB；1 MiB = 1,048,576 字节。例如 `1024` 表示 1 GiB。

<a id="api-9febb14fd2b978b6"></a>

##### HighSizeMiB · 字段

```csharp
public static readonly VmQueryField<ulong> HighSizeMiB;
```

为设备预留的高位 MMIO 地址空间大小。

**取值说明：** 单位为 MiB；1 MiB = 1,048,576 字节。例如 `1024` 表示 1 GiB。

<a id="api-864c62caf7888cdb"></a>

##### LowSizeMiB · 字段

```csharp
public static readonly VmQueryField<ulong> LowSizeMiB;
```

为设备预留的低位 MMIO 地址空间大小。

**取值说明：** 单位为 MiB；1 MiB = 1,048,576 字节。例如 `1024` 表示 1 GiB。

<a id="api-8782e1daccf68c59"></a>

### `VmMmioService` 类

读取和配置虚拟机 MMIO 地址空间，并按需通过启停目标虚拟机选择可用的宿主上限。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / MMIO；管理层面 / 虚拟机 / 修改 / MMIO |
| 源文件 | ` Management/VirtualMachines/Mmio/VmMmioService.Recommendation.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanEdit(bool isRunning) `](#api-9bd9ed220dac0b42) | 判断当前状态是否允许手动编辑 MMIO 配置。 |
| [` public static async Task<ApiResponse<MmioCheckResultType>> CheckMmioSpaceAsync(Guid vmId) `](#api-022f3c81cef9991f) | 比较指定虚拟机当前高 MMIO 空间与主机分配要求。 |
| [` public static MmioPlan? ComputeMmioPlan() `](#api-a42f2189437b381c) | 仅根据进程内已缓存的宿主 MMIO 上限计算间隙；不读取保存文件，也不启动探测。 |
| [` public static async Task<ApiResponse> ConfigureMmioAsync(Guid vmId) `](#api-70b0d58283e2e77e) | 复用主机已保存的 MMIO 上限；无结果时通过目标虚拟机逐档启停探测，再写入 MMIO 方案。 |
| [` public static async Task<ApiResponse<VmMmioSettings>> GetSettingsAsync(Guid vmId) `](#api-2268d2c49ebc6f49) | 读取虚拟机 MMIO 基址和间隙大小，单位为 MiB。 |
| [` public static async Task<ApiResponse> SetSettingAsync(Guid vmId, VmMmioSettings settings, string propertyName) `](#api-09d7e0f83abb5ead) | 核对虚拟机未运行后，只写入指定的一个 MMIO 属性。 |
| [` public static async Task<ApiResponse> UpdateMmioSpaceAsync(Guid vmId) `](#api-59938fcfe0277294) | 确保指定虚拟机完全关机后应用主机推荐的 MMIO 配置。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public const ulong DefaultHighSizeMiB = 262144UL `](#api-519d310072a2ecc8) | 高位 MMIO 间隙目标大小，262144 MiB，即 256 GiB。 |

#### 详细说明

```csharp
public static partial class VmMmioService
```

调用约定：ConfigureMmioAsync 的首次探测会改写配置、反复启动并强制关闭目标；结果保存到主机共享配置并在进程内缓存。ComputeMmioPlan 只使用已有结果，不发起探测。

#### 静态方法说明

<a id="api-9bd9ed220dac0b42"></a>

##### CanEdit · 方法

```csharp
public static bool CanEdit(bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / MMIO。

判断当前状态是否允许手动编辑 MMIO 配置。

| 参数 | 说明 |
| --- | --- |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |

返回：虚拟机未运行时为 true。

<a id="api-022f3c81cef9991f"></a>

##### CheckMmioSpaceAsync · 方法

```csharp
public static async Task<ApiResponse<MmioCheckResultType>> CheckMmioSpaceAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / MMIO。

比较指定虚拟机当前高 MMIO 空间与主机分配要求。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时为满足或需要扩展；读取失败返回错误，不当作空间不足。

<a id="api-a42f2189437b381c"></a>

##### ComputeMmioPlan · 方法

```csharp
public static MmioPlan? ComputeMmioPlan();
```

功能归属：管理层面 / 虚拟机 / 修改 / MMIO。

仅根据进程内已缓存的宿主 MMIO 上限计算间隙；不读取保存文件，也不启动探测。

返回：单位 MiB 的基址和高低间隙方案；没有已知上限时为 null。

<a id="api-70b0d58283e2e77e"></a>

##### ConfigureMmioAsync · 方法

```csharp
public static async Task<ApiResponse> ConfigureMmioAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / MMIO。

复用主机已保存的 MMIO 上限；无结果时通过目标虚拟机逐档启停探测，再写入 MMIO 方案。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：最终配置写入结果；探测或提交失败保留说明。操作可能部分改写配置，首次探测包括强制下电及保存主机级结果。

调用约定：没有缓存时不是纯配置计算：会反复启动并强制关闭目标虚拟机，探测结果写入主机共享配置并在进程内复用。调用者须将这一副作用纳入操作确认，不应将它当作只读能力探测。

<a id="api-2268d2c49ebc6f49"></a>

##### GetSettingsAsync · 方法

```csharp
public static async Task<ApiResponse<VmMmioSettings>> GetSettingsAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / MMIO。

读取虚拟机 MMIO 基址和间隙大小，单位为 MiB。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时返回设置；目标不存在为 NotFound，读取失败保留错误；可空属性表示主机未提供该属性。

<a id="api-09d7e0f83abb5ead"></a>

##### SetSettingAsync · 方法

```csharp
public static async Task<ApiResponse> SetSettingAsync(Guid vmId, VmMmioSettings settings, string propertyName);
```

功能归属：管理层面 / 虚拟机 / 修改 / MMIO。

核对虚拟机未运行后，只写入指定的一个 MMIO 属性。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `settings` | 所选字段的新值，单位 MiB；null 属性不提供可写数值。 |
| `propertyName` | 要写入的 VmMmioSettings 属性名：LowSizeMiB、HighSizeMiB 或 HighBaseMiB。例如修改高位 MMIO 起始地址时传入 `nameof(VmMmioSettings.HighBaseMiB)`，只读取 settings 中该属性的值。 |

返回：WMI 修改结果；无效属性名或执行失败通过错误响应报告。

<a id="api-59938fcfe0277294"></a>

##### UpdateMmioSpaceAsync · 方法

```csharp
public static async Task<ApiResponse> UpdateMmioSpaceAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / MMIO。

确保指定虚拟机完全关机后应用主机推荐的 MMIO 配置。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：关机及配置结果；失败前可能已经关机或完成部分探测。

调用约定：非 Off 状态会执行 TurnOff 强制断电；配置可能启停探测，完成后不自动恢复运行。交互调用应先取得 MMIO 确认。

#### 字段与常量说明

<a id="api-519d310072a2ecc8"></a>

##### DefaultHighSizeMiB · 字段

```csharp
public const ulong DefaultHighSizeMiB = 262144UL;
```

功能归属：管理层面 / 虚拟机 / 修改 / MMIO。

高位 MMIO 间隙目标大小，262144 MiB，即 256 GiB。

<a id="api-64c7d76a38e1d017"></a>

### `VmMmioService.MmioPlan` record 结构

MMIO 间隙方案（单位 MiB）：高位基址、高位大小、低位大小。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / MMIO |
| 源文件 | ` Management/VirtualMachines/Mmio/VmMmioService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public MmioPlan(ulong BaseMiB, ulong HighSizeMiB, ulong LowSizeMiB) `](#api-1e7f08e988c10ac6) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ulong BaseMiB { get; init; } `](#api-4f5175909e479732) | 高位 MMIO 基址，单位 MiB。 |
| [` public ulong HighSizeMiB { get; init; } `](#api-e61db5ad33ebc365) | 高位 MMIO 间隙大小，单位 MiB。 |
| [` public ulong LowSizeMiB { get; init; } `](#api-757fd92a798406f8) | 低位 MMIO 间隙大小，单位 MiB。 |

#### 详细说明

```csharp
public readonly record struct MmioPlan(ulong BaseMiB, ulong HighSizeMiB, ulong LowSizeMiB)
```

| 参数 | 说明 |
| --- | --- |
| `BaseMiB` | 高位 MMIO 基址，单位 MiB。 |
| `HighSizeMiB` | 高位 MMIO 间隙大小，单位 MiB。 |
| `LowSizeMiB` | 低位 MMIO 间隙大小，单位 MiB。 |

#### 构造函数说明

<a id="api-1e7f08e988c10ac6"></a>

##### MmioPlan · 主构造器

```csharp
public MmioPlan(ulong BaseMiB, ulong HighSizeMiB, ulong LowSizeMiB);
```

| 参数 | 说明 |
| --- | --- |
| `BaseMiB` | 高位 MMIO 基址，单位 MiB。 |
| `HighSizeMiB` | 高位 MMIO 间隙大小，单位 MiB。 |
| `LowSizeMiB` | 低位 MMIO 间隙大小，单位 MiB。 |

#### 属性说明

<a id="api-4f5175909e479732"></a>

##### BaseMiB · record 属性

```csharp
public ulong BaseMiB { get; init; }
```

高位 MMIO 基址，单位 MiB。

<a id="api-e61db5ad33ebc365"></a>

##### HighSizeMiB · record 属性

```csharp
public ulong HighSizeMiB { get; init; }
```

高位 MMIO 间隙大小，单位 MiB。

<a id="api-757fd92a798406f8"></a>

##### LowSizeMiB · record 属性

```csharp
public ulong LowSizeMiB { get; init; }
```

低位 MMIO 间隙大小，单位 MiB。

<a id="api-ca59f84f69e91911"></a>

### `VmNetworkAdapterCreationResult` record 类

添加网卡的结果，分别报告初始化、自动撤销和剩余资源。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkAdapterCreationResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmNetworkAdapterCreationResult(bool Success, Guid? DeviceId, VmNetworkAdapterCreationState AdapterState, VmNetworkAdapterInitializationState InitializationState, IReadOnlyList<Guid> RetainedDeviceIds, string? Error = null, string? CleanupError = null, ApiErrorKind ErrorKind = ApiErrorKind.None) `](#api-0ea0df5761b011cf) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public VmNetworkAdapterCreationState AdapterState { get; init; } `](#api-d881130728f1ed47) | 返回时已确认的网卡资源状态。 |
| [` public string? CleanupError { get; init; } `](#api-d319500959416824) | 自动撤销失败或因结果不确定而保留资源的说明；不覆盖原始错误。 |
| [` public Guid? DeviceId { get; init; } `](#api-5ad2e46b19e76323) | 已确认属于本次创建的网卡 GUID；撤销后仍保留历史身份，无法唯一确认时为 null。 |
| [` public string? Error { get; init; } `](#api-e5ca70cada3209b2) | 原始创建或初始化失败说明；成功时为 null。 |
| [` public ApiErrorKind ErrorKind { get; init; } `](#api-25d7abfa1cc153f2) | 原始操作的错误类别；撤销问题另见 CleanupError 和 AdapterState。 |
| [` public VmNetworkAdapterInitializationState InitializationState { get; init; } `](#api-98d1557c04c7afb8) | 初始化步骤的执行结果。 |
| [` public IReadOnlyList<Guid> RetainedDeviceIds { get; init; } `](#api-7236ca66fd25ab01) | 失败后仍保留或未确认删除的本次网卡 GUID。 |
| [` public bool Success { get; init; } `](#api-db8745b8c2211666) | 网卡及连接资源是否均已确认创建成功。 |

#### 详细说明

```csharp
public sealed record VmNetworkAdapterCreationResult(
    bool Success, Guid? DeviceId, VmNetworkAdapterCreationState AdapterState,
    VmNetworkAdapterInitializationState InitializationState, IReadOnlyList<Guid> RetainedDeviceIds,
    string? Error = null, string? CleanupError = null, ApiErrorKind ErrorKind = ApiErrorKind.None)
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 网卡及连接资源是否均已确认创建成功。 |
| `DeviceId` | 已确认属于本次创建的网卡 GUID；撤销后仍保留历史身份，无法唯一确认时为 null。 |
| `AdapterState` | 返回时已确认的网卡资源状态。 |
| `InitializationState` | 初始化步骤的执行结果。 |
| `RetainedDeviceIds` | 失败后仍保留或未确认删除的本次网卡 GUID。归属尚未确认时可能为空，空集合不能覆盖 Unknown 状态。 |
| `Error` | 原始创建或初始化失败说明；成功时为 null。 |
| `CleanupError` | 自动撤销失败或因结果不确定而保留资源的说明；不覆盖原始错误。 |
| `ErrorKind` | 原始操作的错误类别；撤销问题另见 CleanupError 和 AdapterState。 |

调用约定：失败且任务已确认结束时只撤销可验证属于本次创建的资源。任务、归属或配置无法确认时保留资源；不公开原生 WMI 对象或路径。状态是本次核对所得快照，不保证外部程序随后不再修改。

#### 构造函数说明

<a id="api-0ea0df5761b011cf"></a>

##### VmNetworkAdapterCreationResult · 主构造器

```csharp
public VmNetworkAdapterCreationResult(bool Success, Guid? DeviceId, VmNetworkAdapterCreationState AdapterState, VmNetworkAdapterInitializationState InitializationState, IReadOnlyList<Guid> RetainedDeviceIds, string? Error = null, string? CleanupError = null, ApiErrorKind ErrorKind = ApiErrorKind.None);
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 网卡及连接资源是否均已确认创建成功。 |
| `DeviceId` | 已确认属于本次创建的网卡 GUID；撤销后仍保留历史身份，无法唯一确认时为 null。 |
| `AdapterState` | 返回时已确认的网卡资源状态。 |
| `InitializationState` | 初始化步骤的执行结果。 |
| `RetainedDeviceIds` | 失败后仍保留或未确认删除的本次网卡 GUID。归属尚未确认时可能为空，空集合不能覆盖 Unknown 状态。 |
| `Error` | 原始创建或初始化失败说明；成功时为 null。 |
| `CleanupError` | 自动撤销失败或因结果不确定而保留资源的说明；不覆盖原始错误。 |
| `ErrorKind` | 原始操作的错误类别；撤销问题另见 CleanupError 和 AdapterState。 |

#### 属性说明

<a id="api-d881130728f1ed47"></a>

##### AdapterState · record 属性

```csharp
public VmNetworkAdapterCreationState AdapterState { get; init; }
```

返回时已确认的网卡资源状态。

<a id="api-d319500959416824"></a>

##### CleanupError · record 属性

```csharp
public string? CleanupError { get; init; }
```

自动撤销失败或因结果不确定而保留资源的说明；不覆盖原始错误。

<a id="api-5ad2e46b19e76323"></a>

##### DeviceId · record 属性

```csharp
public Guid? DeviceId { get; init; }
```

已确认属于本次创建的网卡 GUID；撤销后仍保留历史身份，无法唯一确认时为 null。

<a id="api-e5ca70cada3209b2"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

原始创建或初始化失败说明；成功时为 null。

<a id="api-25d7abfa1cc153f2"></a>

##### ErrorKind · record 属性

```csharp
public ApiErrorKind ErrorKind { get; init; }
```

原始操作的错误类别；撤销问题另见 CleanupError 和 AdapterState。

<a id="api-98d1557c04c7afb8"></a>

##### InitializationState · record 属性

```csharp
public VmNetworkAdapterInitializationState InitializationState { get; init; }
```

初始化步骤的执行结果。

<a id="api-7236ca66fd25ab01"></a>

##### RetainedDeviceIds · record 属性

```csharp
public IReadOnlyList<Guid> RetainedDeviceIds { get; init; }
```

失败后仍保留或未确认删除的本次网卡 GUID。归属尚未确认时可能为空，空集合不能覆盖 Unknown 状态。

<a id="api-db8745b8c2211666"></a>

##### Success · record 属性

```csharp
public bool Success { get; init; }
```

网卡及连接资源是否均已确认创建成功。

<a id="api-2a7105bf5eb83ced"></a>

### `VmNetworkAdapterCreationState` 枚举

本次添加网卡在返回时已确认的资源状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkAdapterCreationResult.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` NotCreated = 0 `](#api-26f6bcc40061114b) | 未创建网卡，包括提交前失败或底层明确拒绝创建。 |
| [` Present = 1 `](#api-4041033ac3c76ae7) | 已确认本次网卡仍存在；失败时查看清理错误。 |
| [` Removed = 2 `](#api-8bf24e9fa37d934a) | 曾创建网卡，但失败后的自动撤销已确认将其移除。 |
| [` Unknown = 3 `](#api-0d80258e9323df2d) | 无法确认创建、撤销或资源归属；不得据此直接重试添加。 |

#### 详细说明

```csharp
public enum VmNetworkAdapterCreationState
```

#### 枚举值说明

<a id="api-26f6bcc40061114b"></a>

##### NotCreated · 枚举值

```csharp
NotCreated = 0
```

未创建网卡，包括提交前失败或底层明确拒绝创建。

<a id="api-4041033ac3c76ae7"></a>

##### Present · 枚举值

```csharp
Present = 1
```

已确认本次网卡仍存在；失败时查看清理错误。

<a id="api-8bf24e9fa37d934a"></a>

##### Removed · 枚举值

```csharp
Removed = 2
```

曾创建网卡，但失败后的自动撤销已确认将其移除。

<a id="api-0d80258e9323df2d"></a>

##### Unknown · 枚举值

```csharp
Unknown = 3
```

无法确认创建、撤销或资源归属；不得据此直接重试添加。

<a id="api-5ae63e7a6d49476d"></a>

### `VmNetworkAdapterInitializationState` 枚举

本次网卡连接资源初始化步骤的执行结果，不等同于网卡当前是否连接交换机。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkAdapterCreationResult.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Completed = 1 `](#api-27701c8a5cf0a439) | 连接资源初始化已完成。 |
| [` Failed = 2 `](#api-0800671ef3c657c3) | 初始化已确认失败，网卡可能已自动撤销。 |
| [` NotStarted = 0 `](#api-5294a7cecfd36c3f) | 尚未提交连接资源初始化。 |
| [` Unknown = 3 `](#api-b5ba38fc03e97ff3) | 初始化结果尚未确认，底层任务可能仍在执行。 |

#### 详细说明

```csharp
public enum VmNetworkAdapterInitializationState
```

#### 枚举值说明

<a id="api-27701c8a5cf0a439"></a>

##### Completed · 枚举值

```csharp
Completed = 1
```

连接资源初始化已完成。

<a id="api-0800671ef3c657c3"></a>

##### Failed · 枚举值

```csharp
Failed = 2
```

初始化已确认失败，网卡可能已自动撤销。

<a id="api-5294a7cecfd36c3f"></a>

##### NotStarted · 枚举值

```csharp
NotStarted = 0
```

尚未提交连接资源初始化。

<a id="api-b5ba38fc03e97ff3"></a>

##### Unknown · 枚举值

```csharp
Unknown = 3
```

初始化结果尚未确认，底层任务可能仍在执行。

<a id="api-7b60facd46cbd2aa"></a>

### `VmNetworkFields` 类

网络的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 网络 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Network/VmNetworkFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<int> AccessVlanId `](#api-6028d5e953bda6f7) | 访问 VLAN 编号：接入模式下，网卡端口所属的 VLAN 编号。 |
| [` public static readonly VmQueryField<ulong> BandwidthLimitBitsPerSecond `](#api-095bb0a502357a04) | 带宽上限：为虚拟网卡配置的带宽上限，单位为 bit/s。 |
| [` public static readonly VmQueryField<ulong> BandwidthReservationBitsPerSecond `](#api-ab817df85355b2ce) | 带宽保留：为虚拟网卡配置的带宽保留量，单位为 bit/s。 |
| [` public static readonly VmQueryField<Guid> DeviceId `](#api-6f1114eefd22e5da) | 网卡身份：虚拟网卡的标识，与虚拟机标识共同定位该网卡。 |
| [` public static readonly VmQueryField<bool> DhcpGuardEnabled `](#api-02308e69979a2b8c) | DHCP 防护：是否阻止来宾通过此网卡发送 DHCP Offer 报文。 |
| [` public static readonly VmQueryField<string> IpAddressSource `](#api-980b92bca0dfd7ed) | IP 地址来源：本次 IP 地址列表采用的数据来源。 |
| [` public static readonly VmQueryField<IReadOnlyList<string>> IpAddresses `](#api-c0d549a66582f030) | IP 地址：关联到虚拟网卡的 IP 地址列表，优先采用已有宿主 ARP 嗅探结果。 |
| [` public static readonly VmQueryField<bool> IpsecOffloadEnabled `](#api-fd6d73654692d71c) | IPsec 卸载：是否配置启用 IPsec 硬件卸载。 |
| [` public static readonly VmQueryField<bool> IsConnected `](#api-5f4785d7742b2318) | 连接开关：虚拟网卡连接资源是否处于启用状态。 |
| [` public static readonly VmQueryField<bool> IsStaticMac `](#api-3fbde5f0e1ac8801) | 静态 MAC 开关：虚拟网卡是否使用手动指定的静态 MAC 地址。 |
| [` public static readonly VmQueryField<string> MacAddress `](#api-46429eccaf2cf253) | MAC 地址：虚拟网卡当前配置的 MAC 地址。 |
| [` public static readonly VmQueryField<bool> MacSpoofingAllowed `](#api-01350d39eaa70d45) | MAC 欺骗：是否允许来宾使用与 Hyper-V 配置不同的源 MAC 地址发送数据。 |
| [` public static readonly VmQueryField<PortMonitorMode> MonitorMode `](#api-da43c78e52a5c947) | 端口镜像模式：虚拟网卡在端口镜像中的角色：关闭、来源或接收端。 |
| [` public static readonly VmQueryField<string> Name `](#api-40cd016d12f89ef6) | 网卡名称：虚拟网卡的显示名称。 |
| [` public static readonly VmQueryField<int> NativeVlanId `](#api-d95ba2e3b9638888) | 本征 VLAN 编号：干线模式下，用于未标记流量的本征 VLAN 编号。 |
| [` public static readonly VmQueryField<PvlanMode> PvlanMode `](#api-bbba9fb126f381a9) | 专用 VLAN 模式：网卡端口在专用 VLAN 中的隔离角色。 |
| [` public static readonly VmQueryField<int> PvlanPrimaryId `](#api-b765e42aaeafc341) | 专用 VLAN 主编号：专用 VLAN 的主 VLAN 编号。 |
| [` public static readonly VmQueryField<int> PvlanSecondaryId `](#api-b6e8016c0ecc2005) | 专用 VLAN 次编号：专用 VLAN 的次 VLAN 编号。 |
| [` public static readonly VmQueryField<ulong> ReceiveBytesPerSecond `](#api-d760198a0292c6e0) | 网卡接收速率：虚拟网卡当前的接收速率，单位为字节每秒。 |
| [` public static readonly VmQueryField<bool> RouterGuardEnabled `](#api-b8470bcae457c28e) | 路由器防护：是否阻止来宾通过此网卡发送路由器通告及重定向报文。 |
| [` public static readonly VmQueryField<ulong> SendBytesPerSecond `](#api-ee4797e5bcfa0bac) | 网卡发送速率：虚拟网卡当前的发送速率，单位为字节每秒。 |
| [` public static readonly VmQueryField<bool> SriovEnabled `](#api-f2afcfc8f9cb39b0) | SR-IOV 开关：是否配置启用 SR-IOV 卸载。 |
| [` public static readonly VmQueryField<uint> StormLimit `](#api-5e9beb26f1550e86) | 广播风暴限制：广播和组播流量每秒允许通过的包数量上限。 |
| [` public static readonly VmQueryField<Guid> SwitchId `](#api-9934f8fff203ab07) | 连接交换机身份：虚拟网卡连接的交换机标识。 |
| [` public static readonly VmQueryField<string> SwitchName `](#api-5f5302fbbb2b63bf) | 连接交换机名称：虚拟网卡连接的交换机显示名称。 |
| [` public static readonly VmQueryField<bool> TeamingAllowed `](#api-7c9af1646d48f7d7) | 网卡组合：是否允许来宾将此网卡加入网卡组合。 |
| [` public static readonly VmQueryField<IReadOnlyList<int>> TrunkAllowedVlanIds `](#api-3f5ce919cc0f73c7) | 允许的干线 VLAN 编号：干线模式下，网卡端口允许通过的 VLAN 编号列表。 |
| [` public static readonly VmQueryField<VlanOperationMode> VlanMode `](#api-0a0195829597dac9) | VLAN 模式：网卡端口使用的 VLAN 工作模式：接入、干线或专用 VLAN。 |
| [` public static readonly VmQueryField<bool> VmqEnabled `](#api-7d7e567a01b26796) | 虚拟机队列：是否配置启用虚拟机队列（VMQ）卸载。 |

#### 详细说明

```csharp
public static class VmNetworkFields
```

#### 字段与常量说明

<a id="api-6028d5e953bda6f7"></a>

##### AccessVlanId · 字段

```csharp
public static readonly VmQueryField<int> AccessVlanId;
```

接入模式下，网卡端口所属的 VLAN 编号。

**取值说明：** VLAN 的整数编号。

**适用条件：** 适用于 Access 模式。

<a id="api-095bb0a502357a04"></a>

##### BandwidthLimitBitsPerSecond · 字段

```csharp
public static readonly VmQueryField<ulong> BandwidthLimitBitsPerSecond;
```

为虚拟网卡配置的带宽上限。

**取值说明：** 单位为 bit/s；例如 `1000000000` 表示 1 Gbit/s。

<a id="api-ab817df85355b2ce"></a>

##### BandwidthReservationBitsPerSecond · 字段

```csharp
public static readonly VmQueryField<ulong> BandwidthReservationBitsPerSecond;
```

为虚拟网卡配置的带宽保留量。

**取值说明：** 单位为 bit/s；例如 `1000000000` 表示 1 Gbit/s。

<a id="api-6f1114eefd22e5da"></a>

##### DeviceId · 字段

```csharp
public static readonly VmQueryField<Guid> DeviceId;
```

虚拟网卡的标识，与虚拟机标识共同定位该网卡。

**取值说明：** GUID，常用文本格式为 36 个字符，按 `8-4-4-4-12` 分组。

<a id="api-02308e69979a2b8c"></a>

##### DhcpGuardEnabled · 字段

```csharp
public static readonly VmQueryField<bool> DhcpGuardEnabled;
```

是否阻止来宾通过此网卡发送 DHCP Offer 报文。

**取值说明：** `true`：阻止 DHCP Offer；`false`：不启用此保护。

<a id="api-980b92bca0dfd7ed"></a>

##### IpAddressSource · 字段

```csharp
public static readonly VmQueryField<string> IpAddressSource;
```

本次 IP 地址列表采用的数据来源。

**取值说明：** HostArp：宿主嗅探；Guest：来宾上报；NeighborCache：系统邻居缓存；None：暂无来源。

<a id="api-c0d549a66582f030"></a>

##### IpAddresses · 字段

```csharp
public static readonly VmQueryField<IReadOnlyList<string>> IpAddresses;
```

关联到虚拟网卡的 IP 地址列表，优先采用已有宿主 ARP 嗅探结果。

**取值说明：** 按宿主 ARP 嗅探、来宾上报、系统邻居缓存的顺序，采用首个有有效地址的来源。

**适用条件：** 使用宿主嗅探结果前，由宿主程序启动 ARP 嗅探服务。

<a id="api-fd6d73654692d71c"></a>

##### IpsecOffloadEnabled · 字段

```csharp
public static readonly VmQueryField<bool> IpsecOffloadEnabled;
```

是否配置启用 IPsec 硬件卸载。

**取值说明：** IPSecOffloadLimit 大于 0 时返回 true，表示配置启用。

<a id="api-5f4785d7742b2318"></a>

##### IsConnected · 字段

```csharp
public static readonly VmQueryField<bool> IsConnected;
```

虚拟网卡连接资源的启用状态。网络可达性需要调用方另行探测。

**取值说明：** `true`：连接资源已启用；`false`：未启用。

<a id="api-3fbde5f0e1ac8801"></a>

##### IsStaticMac · 字段

```csharp
public static readonly VmQueryField<bool> IsStaticMac;
```

虚拟网卡是否使用手动指定的静态 MAC 地址。

**取值说明：** `true`：静态 MAC；`false`：动态 MAC。

<a id="api-46429eccaf2cf253"></a>

##### MacAddress · 字段

```csharp
public static readonly VmQueryField<string> MacAddress;
```

虚拟网卡当前配置的 MAC 地址。

<a id="api-01350d39eaa70d45"></a>

##### MacSpoofingAllowed · 字段

```csharp
public static readonly VmQueryField<bool> MacSpoofingAllowed;
```

是否允许来宾使用与 Hyper-V 配置不同的源 MAC 地址发送数据。

**取值说明：** `true`：允许不同的源 MAC；`false`：禁止。

<a id="api-da43c78e52a5c947"></a>

##### MonitorMode · 字段

```csharp
public static readonly VmQueryField<PortMonitorMode> MonitorMode;
```

虚拟网卡在端口镜像中的角色：关闭、来源或接收端。

<a id="api-40cd016d12f89ef6"></a>

##### Name · 字段

```csharp
public static readonly VmQueryField<string> Name;
```

虚拟网卡的显示名称。

<a id="api-d95ba2e3b9638888"></a>

##### NativeVlanId · 字段

```csharp
public static readonly VmQueryField<int> NativeVlanId;
```

干线模式下，用于未标记流量的本征 VLAN 编号。

**取值说明：** VLAN 的整数编号。

**适用条件：** 适用于 Trunk 模式。

<a id="api-bbba9fb126f381a9"></a>

##### PvlanMode · 字段

```csharp
public static readonly VmQueryField<PvlanMode> PvlanMode;
```

网卡端口在专用 VLAN 中的隔离角色。

**适用条件：** 适用于 Private 模式。

<a id="api-b765e42aaeafc341"></a>

##### PvlanPrimaryId · 字段

```csharp
public static readonly VmQueryField<int> PvlanPrimaryId;
```

专用 VLAN 的主 VLAN 编号。

**取值说明：** VLAN 的整数编号。

**适用条件：** 适用于 Private 模式。

<a id="api-b6e8016c0ecc2005"></a>

##### PvlanSecondaryId · 字段

```csharp
public static readonly VmQueryField<int> PvlanSecondaryId;
```

专用 VLAN 的次 VLAN 编号。

**取值说明：** VLAN 的整数编号。

**适用条件：** 适用于 Private 模式。

<a id="api-d760198a0292c6e0"></a>

##### ReceiveBytesPerSecond · 字段

```csharp
public static readonly VmQueryField<ulong> ReceiveBytesPerSecond;
```

虚拟网卡当前的接收速率。

**取值说明：** 单位为字节/秒；例如 `1048576` 表示 1 MiB/s。

<a id="api-b8470bcae457c28e"></a>

##### RouterGuardEnabled · 字段

```csharp
public static readonly VmQueryField<bool> RouterGuardEnabled;
```

是否阻止来宾通过此网卡发送路由器通告及重定向报文。

**取值说明：** `true`：阻止路由器通告和重定向；`false`：不启用此保护。

<a id="api-ee4797e5bcfa0bac"></a>

##### SendBytesPerSecond · 字段

```csharp
public static readonly VmQueryField<ulong> SendBytesPerSecond;
```

虚拟网卡当前的发送速率。

**取值说明：** 单位为字节/秒；例如 `1048576` 表示 1 MiB/s。

<a id="api-f2afcfc8f9cb39b0"></a>

##### SriovEnabled · 字段

```csharp
public static readonly VmQueryField<bool> SriovEnabled;
```

是否配置启用 SR-IOV 卸载。

**取值说明：** IOVOffloadWeight 大于 0 时返回 true，表示配置启用。

<a id="api-5e9beb26f1550e86"></a>

##### StormLimit · 字段

```csharp
public static readonly VmQueryField<uint> StormLimit;
```

广播和组播流量每秒允许通过的包数量上限。

**取值说明：** 单位为包/秒；`0` 表示未配置风暴速率限制。

<a id="api-9934f8fff203ab07"></a>

##### SwitchId · 字段

```csharp
public static readonly VmQueryField<Guid> SwitchId;
```

虚拟网卡连接的交换机标识。

**取值说明：** GUID，常用文本格式为 36 个字符，按 `8-4-4-4-12` 分组。

**缺测与错误：** 未配置目标交换机时返回 NotApplicable。

<a id="api-5f5302fbbb2b63bf"></a>

##### SwitchName · 字段

```csharp
public static readonly VmQueryField<string> SwitchName;
```

虚拟网卡连接的交换机显示名称。

<a id="api-7c9af1646d48f7d7"></a>

##### TeamingAllowed · 字段

```csharp
public static readonly VmQueryField<bool> TeamingAllowed;
```

是否允许来宾将此网卡加入网卡组合。

**取值说明：** `true`：允许来宾网卡组合；`false`：禁止。

<a id="api-3f5ce919cc0f73c7"></a>

##### TrunkAllowedVlanIds · 字段

```csharp
public static readonly VmQueryField<IReadOnlyList<int>> TrunkAllowedVlanIds;
```

干线模式下，网卡端口允许通过的 VLAN 编号列表。

**取值说明：** VLAN 整数编号的只读列表，例如 `[10, 20]`。

**适用条件：** 适用于 Trunk 模式。

<a id="api-0a0195829597dac9"></a>

##### VlanMode · 字段

```csharp
public static readonly VmQueryField<VlanOperationMode> VlanMode;
```

网卡端口使用的 VLAN 工作模式：接入、干线或专用 VLAN。

**取值说明：** 底层编码 0 在当前接口中归为 Access。

<a id="api-7d7e567a01b26796"></a>

##### VmqEnabled · 字段

```csharp
public static readonly VmQueryField<bool> VmqEnabled;
```

是否配置启用虚拟机队列（VMQ）卸载。

**取值说明：** VMQOffloadWeight 大于 0 时返回 true，表示配置启用。

<a id="api-401cb52499f1e6cd"></a>

### `VmNetworkService` 类

读取网卡和交换机，配置连接、MAC、VLAN、带宽、安全及卸载能力。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 网络；管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkService.Changes.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<VmNetworkAdapterCreationResult> AddNetworkAdapterAsync(Guid vmId) `](#api-aacde1d20e35d063) | 为虚拟机添加合成网卡并初始化连接资源；确认失败时自动撤销本次创建。 |
| [` public static Task<ApiResponse> ApplyBandwidthSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties) `](#api-d69efcf2d3d49539) | 只修改本次选择的带宽字段；同字段按本次值覆盖。 |
| [` public static Task<ApiResponse> ApplyOffloadSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties) `](#api-fb9ec61bc500c03e) | 只修改本次选择的卸载字段；同字段按本次值覆盖。 |
| [` public static async Task<ApiResponse> ApplySecurityAndOffloadSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties) `](#api-4ee4f8ad1857a216) | 应用明确选中的安全与卸载字段；未涉及的类别不执行。 |
| [` public static async Task<VmNetworkSettingsApplyResult> ApplySecurityAndOffloadSettingsWithResultAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties) `](#api-4ba0c38a35fa9515) | 先安全后卸载，分别报告本次选择并已完成的类别；后一步失败不回滚前一步。 |
| [` public static Task<ApiResponse> ApplySecuritySettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties) `](#api-b0d1378a011750c1) | 只修改本次选择的安全字段；同字段按本次值覆盖。 |
| [` public static Task<ApiResponse> ApplyVlanSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties) `](#api-f766818e11f23553) | 只修改本次选择的VLAN字段；同字段按本次值覆盖。 |
| [` public static bool CanEditMacAddress(bool isRunning) `](#api-3d90d2fd0bb8557a) | 判断当前运行状态是否允许编辑 MAC 地址。 |
| [` public static async Task<ApiResponse> FillDynamicIpsAsync(Guid vmId, IEnumerable<VmNetworkAdapter> adapters) `](#api-79bc16b3c4480748) | 依次解析网卡 IPv4 并更新传入网卡对象；宿主嗅探结果优先。 |
| [` public static async Task<ApiResponse<IReadOnlyDictionary<Guid, string>>> GetAvailableSwitchesAsync() `](#api-a17cc7ba74299e78) | 读取当前宿主可用虚拟交换机的 GUID 和显示名称。 |
| [` public static string? GetConnectionError(bool isConnected, int availableSwitchCount) `](#api-d40cf06a5ccf520f) | 在要求接通网卡时检查是否存在可选虚拟交换机。 |
| [` public static Task<ApiResponse<List<VmNetworkAdapter>>> GetNetworkAdaptersAsync(Guid vmId) `](#api-15ad6562bcf53251) | 读取网卡、交换机连接及可用的 VLAN、带宽、安全和卸载设置。 |
| [` public static Task<ApiResponse> RemoveNetworkAdapterAsync(Guid vmId, VmNetworkAdapter adapter) `](#api-e438fe21f7877f87) | 移除查询返回的虚拟机网卡及其连接资源，执行前核对网卡属于指定虚拟机。 |
| [` public static async Task<ApiResponse> SetMacAddressAsync(Guid vmId, VmNetworkAdapter adapter, string newMac) `](#api-6d6df8652089f317) | 核对虚拟机状态后写入静态 MAC，空地址恢复由系统动态分配。 |
| [` public static async Task<ApiResponse> UpdateConnectionAsync(Guid vmId, VmNetworkAdapter adapter, Guid? switchId) `](#api-62854d2d46bea402) | 按网卡连接开关及明确的交换机 GUID 更新连接，不按交换机名称重新匹配。 |

#### 详细说明

```csharp
public static partial class VmNetworkService
```

#### 静态方法说明

<a id="api-aacde1d20e35d063"></a>

##### AddNetworkAdapterAsync · 方法

```csharp
public static Task<VmNetworkAdapterCreationResult> AddNetworkAdapterAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

为虚拟机添加合成网卡并初始化连接资源；确认失败时自动撤销本次创建。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：分别报告实际网卡状态、初始化结果、原始错误及撤销错误；失败后应刷新实际列表。

调用约定：底层任务或资源归属尚未确认时保留相关资源，不盲目撤销或重试。撤销前核对当前配置与附属资源；外部程序在最终核对后修改对象的竞态仍不能由 WMI 原子比较删除消除。

<a id="api-d69efcf2d3d49539"></a>

##### ApplyBandwidthSettingsAsync · 方法

```csharp
public static Task<ApiResponse> ApplyBandwidthSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

只修改本次选择的带宽字段；同字段按本次值覆盖。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID。 |
| `adapter` | 查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。 |
| `changedProperties` | 要提交的带宽属性名集合，不能为空。例如仅修改带宽上限时包含 `nameof(VmNetworkAdapter.BandwidthLimitBitsPerSecond)`，带宽保留值保持不变；其他类别的属性名在任何查询或写入前拒绝。 |

返回：成功或带原因的失败响应。

<a id="api-fb9ec61bc500c03e"></a>

##### ApplyOffloadSettingsAsync · 方法

```csharp
public static Task<ApiResponse> ApplyOffloadSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

只修改本次选择的卸载字段；同字段按本次值覆盖。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID。 |
| `adapter` | 查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。 |
| `changedProperties` | 非空属性名集合，使用 nameof；支持本接口所属卸载字段，其他名称在任何查询或写入前拒绝。 |

返回：成功或带原因的失败响应。

<a id="api-4ee4f8ad1857a216"></a>

##### ApplySecurityAndOffloadSettingsAsync · 方法

```csharp
public static async Task<ApiResponse> ApplySecurityAndOffloadSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

应用明确选中的安全与卸载字段；未涉及的类别不执行。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID。 |
| `adapter` | 查询所得网卡及请求值。 |
| `changedProperties` | 安全或卸载属性名的非空集合，使用 nameof。 |

返回：整体结果；需要已完成类别时使用带 Result 的入口。

<a id="api-4ba0c38a35fa9515"></a>

##### ApplySecurityAndOffloadSettingsWithResultAsync · 方法

```csharp
public static async Task<VmNetworkSettingsApplyResult> ApplySecurityAndOffloadSettingsWithResultAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

先安全后卸载，分别报告本次选择并已完成的类别；后一步失败不回滚前一步。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID。 |
| `adapter` | 查询所得网卡及请求值。 |
| `changedProperties` | 安全或卸载属性名的非空集合，使用 nameof；开始前复制全部请求值。 |

返回：整体结果及各类别是否已执行成功；未选择的类别为 false。

<a id="api-b0d1378a011750c1"></a>

##### ApplySecuritySettingsAsync · 方法

```csharp
public static Task<ApiResponse> ApplySecuritySettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

只修改本次选择的安全字段；同字段按本次值覆盖。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID。 |
| `adapter` | 查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。 |
| `changedProperties` | 非空属性名集合，使用 nameof；支持本接口所属安全字段，其他名称在任何查询或写入前拒绝。 |

返回：成功或带原因的失败响应。

<a id="api-f766818e11f23553"></a>

##### ApplyVlanSettingsAsync · 方法

```csharp
public static Task<ApiResponse> ApplyVlanSettingsAsync(Guid vmId, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

只修改本次选择的VLAN字段；同字段按本次值覆盖。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机 GUID。 |
| `adapter` | 查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。 |
| `changedProperties` | 要提交的 VLAN 属性名集合，使用 nameof，不能为空。切换模式时同时包含 VlanMode 和目标模式所需的字段；其他类别的属性名在任何查询或写入前拒绝。 |

返回：成功或带原因的失败响应。切换 VLAN 模式时清理不兼容的旧模式字段。

<a id="api-3d90d2fd0bb8557a"></a>

##### CanEditMacAddress · 方法

```csharp
public static bool CanEditMacAddress(bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

判断当前运行状态是否允许编辑 MAC 地址。

| 参数 | 说明 |
| --- | --- |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |

返回：未运行时为 true。

<a id="api-79bc16b3c4480748"></a>

##### FillDynamicIpsAsync · 方法

```csharp
public static async Task<ApiResponse> FillDynamicIpsAsync(Guid vmId, IEnumerable<VmNetworkAdapter> adapters);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

依次解析网卡 IPv4 并更新传入网卡对象；宿主嗅探结果优先。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `adapters` | 该虚拟机查询返回的网卡对象集合。 |

返回：全部解析完成时成功；失败时返回具体错误，前面已经更新的地址不会撤回。

<a id="api-a17cc7ba74299e78"></a>

##### GetAvailableSwitchesAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyDictionary<Guid, string>>> GetAvailableSwitchesAsync();
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

读取当前宿主可用虚拟交换机的 GUID 和显示名称。

返回：以 GUID 为键、名称为值的只读字典；允许重名，空字典表示没有交换机。

<a id="api-d40cf06a5ccf520f"></a>

##### GetConnectionError · 方法

```csharp
public static string? GetConnectionError(bool isConnected, int availableSwitchCount);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

在要求接通网卡时检查是否存在可选虚拟交换机。

| 参数 | 说明 |
| --- | --- |
| `isConnected` | 是否要求网卡连接虚拟交换机。 |
| `availableSwitchCount` | 当前可用虚拟交换机数量。 |

返回：需要交换机但数量为零时返回说明，否则为 null。

<a id="api-15ad6562bcf53251"></a>

##### GetNetworkAdaptersAsync · 方法

```csharp
public static Task<ApiResponse<List<VmNetworkAdapter>>> GetNetworkAdaptersAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 网络。

读取网卡、交换机连接及可用的 VLAN、带宽、安全和卸载设置。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时包含网卡快照列表，可为空；虚拟机不存在或任一必要查询失败时返回失败，不用空集合代替错误。

调用约定：当前连接没有某类可选功能设置时保留模型默认值；读取这些设置发生错误时仍返回失败。

<a id="api-e438fe21f7877f87"></a>

##### RemoveNetworkAdapterAsync · 方法

```csharp
public static Task<ApiResponse> RemoveNetworkAdapterAsync(Guid vmId, VmNetworkAdapter adapter);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

移除查询返回的虚拟机网卡及其连接资源，执行前核对网卡属于指定虚拟机。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `adapter` | GetNetworkAdaptersAsync 返回的目标网卡。 |

返回：Success 表示移除成功，Error 为结果说明。

<a id="api-6d6df8652089f317"></a>

##### SetMacAddressAsync · 方法

```csharp
public static async Task<ApiResponse> SetMacAddressAsync(Guid vmId, VmNetworkAdapter adapter, string newMac);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

核对虚拟机状态后写入静态 MAC，空地址恢复由系统动态分配。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `adapter` | GetNetworkAdaptersAsync 返回并填入修改值的网卡；内部定位信息由 Core 保管。 |
| `newMac` | 新 MAC 字符串，会规范化并校验；空字符串用于恢复动态 MAC。 |

返回：Success 表示写入成功，Error 为地址格式、状态或底层失败原因。

<a id="api-62854d2d46bea402"></a>

##### UpdateConnectionAsync · 方法

```csharp
public static async Task<ApiResponse> UpdateConnectionAsync(Guid vmId, VmNetworkAdapter adapter, Guid? switchId);
```

功能归属：管理层面 / 虚拟机 / 修改 / 网络。

按网卡连接开关及明确的交换机 GUID 更新连接，不按交换机名称重新匹配。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；执行前核对查询所得网卡属于该虚拟机。 |
| `adapter` | GetNetworkAdaptersAsync 返回并填入修改值的网卡；内部定位信息由 Core 保管。 |
| `switchId` | 接通时必须提供目标交换机 GUID；断开时忽略，保留原目标。可从宿主交换机查询或 adapter.SwitchId 取得。 |

返回：Success 表示连接配置修改成功，Error 为错误或结果说明。

<a id="api-d1f57570480ce4e9"></a>

### `VmNetworkSettingsApplyResult` record 类

安全和卸载设置的组合写入结果，分别保留两步是否成功。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 网络 |
| 源文件 | ` Management/VirtualMachines/Network/VmNetworkService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmNetworkSettingsApplyResult(bool Success, string Message, bool SecurityApplied, bool OffloadApplied) `](#api-88cb5900463bd0a5) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ApiFailure? Failure { get; init; } `](#api-88651ad643879cea) | 主失败的类别、来源和原始码；成功时为 null。 |
| [` public string Message { get; init; } `](#api-1607b0aad4129a8b) | 本地化结果或确认说明，供调用者呈现，不作为稳定的错误代码。 |
| [` public OperationStepResult Offload { get; init; } = new("Offload", OffloadApplied ? OperationStepState.Succeeded : OperationStepState.NotStarted) `](#api-b4dd40718f2de417) | 卸载步骤的执行状态及错误；安全步骤失败或未选择时为 NotStarted。 |
| [` public bool OffloadApplied { get; init; } `](#api-7be1661f2e08dd1a) | 卸载设置步骤是否已经成功写入。 |
| [` public OperationStepResult Security { get; init; } = new("Security", SecurityApplied ? OperationStepState.Succeeded : OperationStepState.NotStarted) `](#api-8ef309082d0c47b1) | 安全步骤的执行状态及错误；未选择或前置校验未通过时为 NotStarted。 |
| [` public bool SecurityApplied { get; init; } `](#api-9db8d6840e584b05) | 安全设置步骤是否已经成功写入。 |
| [` public bool Success { get; init; } `](#api-42c6ed865953b76c) | 该项操作是否完整成功；部分完成信息由同一结果中的其他字段表达。 |

#### 详细说明

```csharp
public sealed record VmNetworkSettingsApplyResult(bool Success, string Message, bool SecurityApplied, bool OffloadApplied)
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 该项操作是否完整成功；部分完成信息由同一结果中的其他字段表达。 |
| `Message` | 本地化结果或确认说明，供调用者呈现，不作为稳定的错误代码。 |
| `SecurityApplied` | 安全设置步骤是否已经成功写入。 |
| `OffloadApplied` | 卸载设置步骤是否已经成功写入。 |

#### 构造函数说明

<a id="api-88cb5900463bd0a5"></a>

##### VmNetworkSettingsApplyResult · 主构造器

```csharp
public VmNetworkSettingsApplyResult(bool Success, string Message, bool SecurityApplied, bool OffloadApplied);
```

| 参数 | 说明 |
| --- | --- |
| `Success` | 该项操作是否完整成功；部分完成信息由同一结果中的其他字段表达。 |
| `Message` | 本地化结果或确认说明，供调用者呈现，不作为稳定的错误代码。 |
| `SecurityApplied` | 安全设置步骤是否已经成功写入。 |
| `OffloadApplied` | 卸载设置步骤是否已经成功写入。 |

#### 属性说明

<a id="api-88651ad643879cea"></a>

##### Failure · 属性

```csharp
public ApiFailure? Failure { get; init; }
```

主失败的类别、来源和原始码；成功时为 null。

<a id="api-1607b0aad4129a8b"></a>

##### Message · record 属性

```csharp
public string Message { get; init; }
```

本地化结果或确认说明，供调用者呈现，不作为稳定的错误代码。

<a id="api-b4dd40718f2de417"></a>

##### Offload · 属性

```csharp
public OperationStepResult Offload { get; init; } = new("Offload", OffloadApplied ? OperationStepState.Succeeded : OperationStepState.NotStarted);
```

卸载步骤的执行状态及错误；安全步骤失败或未选择时为 NotStarted。

<a id="api-7be1661f2e08dd1a"></a>

##### OffloadApplied · record 属性

```csharp
public bool OffloadApplied { get; init; }
```

卸载设置步骤是否已经成功写入。

<a id="api-8ef309082d0c47b1"></a>

##### Security · 属性

```csharp
public OperationStepResult Security { get; init; } = new("Security", SecurityApplied ? OperationStepState.Succeeded : OperationStepState.NotStarted);
```

安全步骤的执行状态及错误；未选择或前置校验未通过时为 NotStarted。

<a id="api-9db8d6840e584b05"></a>

##### SecurityApplied · record 属性

```csharp
public bool SecurityApplied { get; init; }
```

安全设置步骤是否已经成功写入。

<a id="api-42c6ed865953b76c"></a>

##### Success · record 属性

```csharp
public bool Success { get; init; }
```

该项操作是否完整成功；部分完成信息由同一结果中的其他字段表达。

<a id="api-d9b095e53b531a9d"></a>

### `VmPartitionScanResult` record 类

GPU 分区扫描结果，区分扫描和恢复失败。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / GPU |
| 源文件 | ` Management/VirtualMachines/Gpu/Deployment/VmPartitionScanResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmPartitionScanResult(IReadOnlyList<ExHyperV.Models.DiskPartitionInfo> Partitions, IReadOnlyList<string> Errors, IReadOnlyList<string> CleanupErrors, bool IsCancelled = false) `](#api-39b47c8ed2c5eab4) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<string> CleanupErrors { get; init; } `](#api-3c154a26a72ec86e) | 恢复原始磁盘状态或卸载临时映像失败的说明。 |
| [` public bool CleanupRequired { get; } `](#api-650610344cb723bd) | 存在仍需处理的磁盘状态恢复。 |
| [` public IReadOnlyList<string> Errors { get; init; } `](#api-6dd3f3f251ffcfd4) | 扫描失败说明。 |
| [` public bool IsCancelled { get; init; } `](#api-811296367c5811f9) | 是否在扫描边界收到取消；已完成的分区和恢复结果仍保留。 |
| [` public bool OutcomeUnknown { get; init; } `](#api-1c6ae3b74d40b197) | 底层提交或清理结果尚未确认；不得据此重试写入或卸载，须先核对实际状态。 |
| [` public IReadOnlyList<ExHyperV.Models.DiskPartitionInfo> Partitions { get; init; } `](#api-ab09d7046165a5a0) | 已读取分区；失败时可能不完整。 |
| [` public bool Success { get; } `](#api-b3c620e7b2155637) | 全部扫描和恢复均成功。 |

#### 详细说明

```csharp
public sealed record VmPartitionScanResult(IReadOnlyList<ExHyperV.Models.DiskPartitionInfo> Partitions,
    IReadOnlyList<string> Errors, IReadOnlyList<string> CleanupErrors, bool IsCancelled = false)
```

| 参数 | 说明 |
| --- | --- |
| `Partitions` | 已读取分区；失败时可能不完整。 |
| `Errors` | 扫描失败说明。 |
| `CleanupErrors` | 恢复原始磁盘状态或卸载临时映像失败的说明。 |
| `IsCancelled` | 是否在扫描边界收到取消；已完成的分区和恢复结果仍保留。 |

#### 构造函数说明

<a id="api-39b47c8ed2c5eab4"></a>

##### VmPartitionScanResult · 主构造器

```csharp
public VmPartitionScanResult(IReadOnlyList<ExHyperV.Models.DiskPartitionInfo> Partitions, IReadOnlyList<string> Errors, IReadOnlyList<string> CleanupErrors, bool IsCancelled = false);
```

| 参数 | 说明 |
| --- | --- |
| `Partitions` | 已读取分区；失败时可能不完整。 |
| `Errors` | 扫描失败说明。 |
| `CleanupErrors` | 恢复原始磁盘状态或卸载临时映像失败的说明。 |
| `IsCancelled` | 是否在扫描边界收到取消；已完成的分区和恢复结果仍保留。 |

#### 属性说明

<a id="api-3c154a26a72ec86e"></a>

##### CleanupErrors · record 属性

```csharp
public IReadOnlyList<string> CleanupErrors { get; init; }
```

恢复原始磁盘状态或卸载临时映像失败的说明。

<a id="api-650610344cb723bd"></a>

##### CleanupRequired · 属性

```csharp
public bool CleanupRequired { get; }
```

存在仍需处理的磁盘状态恢复。

<a id="api-6dd3f3f251ffcfd4"></a>

##### Errors · record 属性

```csharp
public IReadOnlyList<string> Errors { get; init; }
```

扫描失败说明。

<a id="api-811296367c5811f9"></a>

##### IsCancelled · record 属性

```csharp
public bool IsCancelled { get; init; }
```

是否在扫描边界收到取消；已完成的分区和恢复结果仍保留。

<a id="api-1c6ae3b74d40b197"></a>

##### OutcomeUnknown · 属性

```csharp
public bool OutcomeUnknown { get; init; }
```

底层提交或清理结果尚未确认；不得据此重试写入或卸载，须先核对实际状态。

<a id="api-ab09d7046165a5a0"></a>

##### Partitions · record 属性

```csharp
public IReadOnlyList<ExHyperV.Models.DiskPartitionInfo> Partitions { get; init; }
```

已读取分区；失败时可能不完整。

<a id="api-b3c620e7b2155637"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

全部扫描和恢复均成功。

<a id="api-a94b7e1622e15e00"></a>

### `VmPcieChangeKind` 枚举

本次 PCIe 修改的目标类型。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/VmPcieChangeSession.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` DeviceMode = 2 `](#api-efe262d3c1852b4f) | 修改单个 PCIe 设备的来宾模式；按需先启用模拟。 |
| [` EnableEmulation = 0 `](#api-7bf8e3f9f04e43de) | 永久启用 PCIe 模拟。 |
| [` Topology = 1 `](#api-c2aecf1ad9fade4a) | 修改 PCIe 拓扑；按需先启用模拟。 |

#### 详细说明

```csharp
public enum VmPcieChangeKind
```

#### 枚举值说明

<a id="api-efe262d3c1852b4f"></a>

##### DeviceMode · 枚举值

```csharp
DeviceMode = 2
```

修改单个 PCIe 设备的来宾模式；按需先启用模拟。

<a id="api-7bf8e3f9f04e43de"></a>

##### EnableEmulation · 枚举值

```csharp
EnableEmulation = 0
```

永久启用 PCIe 模拟。

<a id="api-c2aecf1ad9fade4a"></a>

##### Topology · 枚举值

```csharp
Topology = 1
```

修改 PCIe 拓扑；按需先启用模拟。

<a id="api-c1479126dc2f9102"></a>

### `VmPcieChangeResult` record 类

PCIe 一次修改的取消标记和底层执行响应。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/VmPcieChangeSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmPcieChangeResult(bool Cancelled, ApiResponse Response) `](#api-e01088b7761eeee6) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool Cancelled { get; init; } `](#api-f1bc62fd89f47501) | 是否因为用户未确认永久开启模拟而取消。 |
| [` public string Error { get; } `](#api-797acc8916518e10) | 底层响应的错误文本。 |
| [` public ApiResponse Response { get; init; } `](#api-30193c6ca252c6e9) | 底层操作的响应；取消状态还需检查 Cancelled。 |
| [` public bool Success { get; } `](#api-4275dbebfd2cf179) | 未取消且底层响应成功。 |

#### 详细说明

```csharp
public sealed record VmPcieChangeResult(bool Cancelled, ApiResponse Response)
```

| 参数 | 说明 |
| --- | --- |
| `Cancelled` | 是否因为用户未确认永久开启模拟而取消。 |
| `Response` | 底层操作的响应；取消状态还需检查 Cancelled。 |

#### 构造函数说明

<a id="api-e01088b7761eeee6"></a>

##### VmPcieChangeResult · 主构造器

```csharp
public VmPcieChangeResult(bool Cancelled, ApiResponse Response);
```

| 参数 | 说明 |
| --- | --- |
| `Cancelled` | 是否因为用户未确认永久开启模拟而取消。 |
| `Response` | 底层操作的响应；取消状态还需检查 Cancelled。 |

#### 属性说明

<a id="api-f1bc62fd89f47501"></a>

##### Cancelled · record 属性

```csharp
public bool Cancelled { get; init; }
```

是否因为用户未确认永久开启模拟而取消。

<a id="api-797acc8916518e10"></a>

##### Error · 属性

```csharp
public string Error { get; }
```

底层响应的错误文本。

<a id="api-30193c6ca252c6e9"></a>

##### Response · record 属性

```csharp
public ApiResponse Response { get; init; }
```

底层操作的响应；取消状态还需检查 Cancelled。

<a id="api-4275dbebfd2cf179"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

未取消且底层响应成功。

<a id="api-40f4032888198ce8"></a>

### `VmPcieChangeSession` 类

保存一次 PCIe 修改的输入、永久开启模拟确认要求和执行结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/VmPcieChangeSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmPcieChangeSession(VmPcieChangeKind kind, Guid vmId, bool emulationEnabled, ushort topology, VmPcieDeviceSetting? device = null, VmPcieGuestMode deviceMode = VmPcieGuestMode.Paravirtualized) `](#api-6429d01279dc636a) | 建立一次 PCIe 修改请求，不立即修改主机或虚拟机。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool EmulationEnabled { get; } `](#api-a19bd6a5d3c031d7) | 会话已知的模拟启用状态；后续设备修改失败时仍可能为 true。 |
| [` public bool RequiresConfirmation { get; } `](#api-6676c971d5a8c507) | 本次修改是否需要用户先确认永久启用 PCIe 模拟；执行前重新读取配置时会更新。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public Task<VmPcieChangeResult> ApplyAsync(bool permanentEmulationConfirmed = false) `](#api-933f93b6744283d6) | 在需要时取得永久开启 PCIe 模拟的确认，然后执行本次请求；同一实例只执行一次。 |

#### 详细说明

```csharp
public sealed class VmPcieChangeSession
```

调用约定：ApplyAsync 的第一次调用确定执行任务；同一实例不能靠第二次批准来重试首次取消，需要创建新请求。

#### 构造函数说明

<a id="api-6429d01279dc636a"></a>

##### VmPcieChangeSession · 构造器

```csharp
public VmPcieChangeSession(VmPcieChangeKind kind, Guid vmId, bool emulationEnabled, ushort topology, VmPcieDeviceSetting? device = null, VmPcieGuestMode deviceMode = VmPcieGuestMode.Paravirtualized);
```

建立一次 PCIe 修改请求，不立即修改主机或虚拟机。

| 参数 | 说明 |
| --- | --- |
| `kind` | 本次请求修改模拟开关、拓扑或单个设备模式。 |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `emulationEnabled` | 会话建立时已知的 PCIe 模拟状态。 |
| `topology` | Hyper-V 原始 PCIe 拓扑编码；当前 Core 未提供有效值目录，提交时由提供者校验。 |
| `device` | GetSettingsAsync 返回的目标 PCIe 设备；非设备模式修改可省略。 |
| `deviceMode` | 待设置的 PCIe 设备来宾模式。 |

#### 属性说明

<a id="api-a19bd6a5d3c031d7"></a>

##### EmulationEnabled · 属性

```csharp
public bool EmulationEnabled { get; }
```

会话已知的模拟启用状态；后续设备修改失败时仍可能为 true。

<a id="api-6676c971d5a8c507"></a>

##### RequiresConfirmation · 属性

```csharp
public bool RequiresConfirmation { get; }
```

本次修改是否需要用户先确认永久启用 PCIe 模拟；执行前重新读取配置时会更新。

#### 方法说明

<a id="api-933f93b6744283d6"></a>

##### ApplyAsync · 方法

```csharp
public Task<VmPcieChangeResult> ApplyAsync(bool permanentEmulationConfirmed = false);
```

在需要时取得永久开启 PCIe 模拟的确认，然后执行本次请求；同一实例只执行一次。

| 参数 | 说明 |
| --- | --- |
| `permanentEmulationConfirmed` | 是否已得到永久启用 PCIe 模拟的用户确认；未确认且需要模拟时返回取消。 |

返回：首次执行的结果；后续调用返回同一任务，包括首次拒绝后再次批准的情况。写入前核对最新归属与能力；后续步骤失败不撤销已开启的模拟。

<a id="api-c38e41da03e378aa"></a>

### `VmPcieDeviceFields` 类

PCIe的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / PCIe |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Pcie/VmPcieDeviceFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<string> ClassType `](#api-d1980feeba970047) | PCIe 设备类别：已分配 PCIe 设备的类别名称。 |
| [` public static readonly VmQueryField<string> DeviceInstanceId `](#api-c1d241c2c8284ba3) | PCIe 设备身份：已分配 PCIe 设备的宿主设备标识。 |
| [` public static readonly VmQueryField<VmPcieGuestMode> GuestMode `](#api-109b9a81b03f00ae) | PCIe 来宾模式：PCIe 设备向来宾呈现的访问模式。 |
| [` public static readonly VmQueryField<string> Name `](#api-3df811e2c71f1db4) | PCIe 设备名称：已分配 PCIe 设备的显示名称。 |
| [` public static readonly VmQueryField<string> Path `](#api-9394bc9838a6e022) | PCIe 设备位置：已分配 PCIe 设备所引用的宿主资源路径。 |
| [` public static readonly VmQueryField<string> Vendor `](#api-5823ef9abf80b5f5) | PCIe 设备厂商：已分配 PCIe 设备的厂商名称。 |

#### 详细说明

```csharp
public static class VmPcieDeviceFields
```

#### 字段与常量说明

<a id="api-d1980feeba970047"></a>

##### ClassType · 字段

```csharp
public static readonly VmQueryField<string> ClassType;
```

已分配 PCIe 设备的类别名称。

<a id="api-c1d241c2c8284ba3"></a>

##### DeviceInstanceId · 字段

```csharp
public static readonly VmQueryField<string> DeviceInstanceId;
```

已分配 PCIe 设备的宿主设备标识。

<a id="api-109b9a81b03f00ae"></a>

##### GuestMode · 字段

```csharp
public static readonly VmQueryField<VmPcieGuestMode> GuestMode;
```

PCIe 设备向来宾呈现的访问模式。

<a id="api-3df811e2c71f1db4"></a>

##### Name · 字段

```csharp
public static readonly VmQueryField<string> Name;
```

已分配 PCIe 设备的显示名称。

<a id="api-9394bc9838a6e022"></a>

##### Path · 字段

```csharp
public static readonly VmQueryField<string> Path;
```

已分配 PCIe 设备所引用的宿主资源路径。

**单位与格式：** 设备资源路径

<a id="api-5823ef9abf80b5f5"></a>

##### Vendor · 字段

```csharp
public static readonly VmQueryField<string> Vendor;
```

已分配 PCIe 设备的厂商名称。

<a id="api-455d9c1e196c68b4"></a>

### `VmPcieFields` 类

PCIe的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / PCIe |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Pcie/VmPcieFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> BootPciExpress `](#api-ec7d7d2e4198a066) | PCIe 启动支持：是否启用通过 PCI Express 设备启动的支持。 |
| [` public static readonly VmQueryField<bool> EmulationEnabled `](#api-32d8b006b9045266) | PCIe 模拟开关：是否为虚拟机启用 PCIe 仿真。 |
| [` public static readonly VmQueryField<ushort> Topology `](#api-f73c293c723344f0) | PCIe 拓扑：虚拟机使用的 PCIe 拓扑编码，具体布局含义待补充。 |

#### 详细说明

```csharp
public static class VmPcieFields
```

#### 字段与常量说明

<a id="api-ec7d7d2e4198a066"></a>

##### BootPciExpress · 字段

```csharp
public static readonly VmQueryField<bool> BootPciExpress;
```

是否启用通过 PCI Express 设备启动的支持。

**取值说明：** `true`：启用 PCIe 启动；`false`：关闭。

**适用条件：** 配置开关，不是具体控制器的引导能力检测。ARM64 固件 26100.4202、28000.1 对小于 4 KiB 的有效 64 位内存 BAR 存在零页计算缺陷；28000.1450 样本已修正此项。NVMe 类码、其他 BAR 条件和实际固件仍影响启动，不能仅凭版本或开关为 true 判断可引导；Core 保留写入入口。

<a id="api-32d8b006b9045266"></a>

##### EmulationEnabled · 字段

```csharp
public static readonly VmQueryField<bool> EmulationEnabled;
```

是否为虚拟机启用 PCIe 仿真。

**取值说明：** `true`：启用 PCIe 仿真；`false`：关闭。

<a id="api-f73c293c723344f0"></a>

##### Topology · 字段

```csharp
public static readonly VmQueryField<ushort> Topology;
```

虚拟机使用的 PCIe 拓扑编码，具体布局含义待补充。

**单位与格式：** 原始编码

<a id="api-2166a73f1d6e8682"></a>

### `VmPcieService` 类

单台虚拟机的 PCIe 设置。 所有可选属性均从实际 WMI 对象探测，旧版主机缺少属性时将对应设置标记为不可用。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / PCIe；管理层面 / 虚拟机 / 修改 / PCIe |
| 源文件 | ` Management/VirtualMachines/Pcie/VmPcieService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<ApiResponse<VmPcieSettings>> GetSettingsAsync(Guid vmId) `](#api-ffb021f22735ddd7) | 读取虚拟机 PCIe 模拟、拓扑和设备来宾模式等设置。 |
| [` public static async Task<ApiResponse> SetBootPciExpressAsync(Guid vmId, bool enabled) `](#api-7cc61c2334f91566) | 在引导配置中设置指定虚拟机是否启用 PCI Express。 |
| [` public static async Task<ApiResponse> SetDeviceModeAsync(Guid vmId, VmPcieDeviceSetting device, VmPcieGuestMode mode) `](#api-d95525daf82b485c) | 修改指定虚拟机中已查询设备的来宾模式，执行前重新核对设备归属和能力。 |
| [` public static async Task<ApiResponse> SetSystemSettingsAsync(Guid vmId, bool enableEmulation, ushort topology) `](#api-7cdab0a831f5fe92) | 修改虚拟机的 PCIe 模拟与拓扑；开启模拟可能需要临时修改宿主功能开关。 |

#### 详细说明

```csharp
public static class VmPcieService
```

#### 静态方法说明

<a id="api-ffb021f22735ddd7"></a>

##### GetSettingsAsync · 方法

```csharp
public static async Task<ApiResponse<VmPcieSettings>> GetSettingsAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / PCIe。

读取虚拟机 PCIe 模拟、拓扑和设备来宾模式等设置。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：当前配置和支持情况；读取失败通过响应返回。

<a id="api-7cc61c2334f91566"></a>

##### SetBootPciExpressAsync · 方法

```csharp
public static async Task<ApiResponse> SetBootPciExpressAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / PCIe。

在引导配置中设置指定虚拟机是否启用 PCI Express。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | true 请求启用该功能，false 请求关闭；互锁限制由对应操作执行。 |

返回：启动阶段 vPCI 开关的配置提交结果，不证明已发现可启动磁盘或已成功引导。

调用约定：研究中的 NVMe 路径筛选 PCI 类码 01/08/02，不能据此承诺 SATA/AHCI 也可从同一路径引导。开关、控制器分配、UEFI 初始化和来宾启动是分别需要确认的环节。



ARM64 vmfirmware.dll 10.0.26100.4202、10.0.28000.1 的已核对样本会将非零且小于 4 KiB 的有效 64 位内存 BAR 算成零页，使设备初始化失败；10.0.28000.1450 样本修正了此算法，但仍拒绝 I/O BAR 和 32 位内存 BAR。不能将“28000”直接视为修复保证，也不能用宿主 build 替代实际组件版本。



Core 保留开关写入，不按这些研究样本封禁其他设备或版本。调用方可展示限制；本接口没有读取控制器 BAR 或验证引导结果，不应将配置成功显示为 NVMe 启动能力已验证。



证据：Hyper-V WMI 研究的 nvme-boot 组件对照；微软固件修复 [3fe19d0](https://github.com/microsoft/mu_msvm/commit/3fe19d067b3eacd5e3991e5e02e476a810c94f6b)。首次修复版本、未采样维护分支和其他架构不据此推定。

<a id="api-d95525daf82b485c"></a>

##### SetDeviceModeAsync · 方法

```csharp
public static async Task<ApiResponse> SetDeviceModeAsync(Guid vmId, VmPcieDeviceSetting device, VmPcieGuestMode mode);
```

功能归属：管理层面 / 虚拟机 / 修改 / PCIe。

修改指定虚拟机中已查询设备的来宾模式，执行前重新核对设备归属和能力。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `device` | GetSettingsAsync 返回的设备；不接受自行构造的底层对象标识。 |
| `mode` | 要配置的来宾设备模式。 |

返回：修改结果；设备已移除、归属不符或不支持此设置时失败。

<a id="api-7cdab0a831f5fe92"></a>

##### SetSystemSettingsAsync · 方法

```csharp
public static async Task<ApiResponse> SetSystemSettingsAsync(Guid vmId, bool enableEmulation, ushort topology);
```

功能归属：管理层面 / 虚拟机 / 修改 / PCIe。

修改虚拟机的 PCIe 模拟与拓扑；开启模拟可能需要临时修改宿主功能开关。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enableEmulation` | true 将模拟模式置为开启；false 只保留现有模拟状态，仍写入 topology，不执行关闭。 |
| `topology` | Hyper-V 原始 PCIe 拓扑编码；当前 Core 未提供有效值目录，提交时由提供者校验。 |

返回：配置结果；模拟开启具有持久副作用，调用者应先完成必要确认。

<a id="api-23a2145335728dda"></a>

### `VmPowerBatteryFields` 类

电源的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 电源 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Power/VmPowerBatteryFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> BatteryEnabled `](#api-c0c8b60b7c7f2546) | 虚拟电池开关：虚拟机是否配置了虚拟电池组件。 |
| [` public static readonly VmQueryField<bool> EnableHibernation `](#api-4fa3e0232f69e89e) | 来宾休眠支持：是否为虚拟机启用来宾休眠支持。 |
| [` public static readonly VmQueryField<bool> TurnOffOnGuestRestart `](#api-ab3184fc24bff95e) | 来宾重启时关机：是否将来宾的重启请求处理为关闭虚拟机。 |

#### 详细说明

```csharp
public static class VmPowerBatteryFields
```

#### 字段与常量说明

<a id="api-c0c8b60b7c7f2546"></a>

##### BatteryEnabled · 字段

```csharp
public static readonly VmQueryField<bool> BatteryEnabled;
```

虚拟机是否配置了虚拟电池组件。

**取值说明：** `true`：配置了虚拟电池；`false`：未配置。

**缺测与错误：** 宿主未提供虚拟电池默认模板时返回 Unsupported。

<a id="api-4fa3e0232f69e89e"></a>

##### EnableHibernation · 字段

```csharp
public static readonly VmQueryField<bool> EnableHibernation;
```

是否为虚拟机启用来宾休眠支持。

**取值说明：** `true`：允许来宾休眠；`false`：不允许。

**适用条件：** 这是来宾休眠许可配置，不是来宾能力或当前电源状态。Windows 来宾仍需支持 S4，并具有完整型休眠文件。

<a id="api-ab3184fc24bff95e"></a>

##### TurnOffOnGuestRestart · 字段

```csharp
public static readonly VmQueryField<bool> TurnOffOnGuestRestart;
```

是否将来宾的重启请求处理为关闭虚拟机。

**取值说明：** `true`：来宾重启时关闭虚拟机；`false`：执行重启。

<a id="api-a82fe27a02230e33"></a>

### `VmPowerOperationSession` 类

单台虚拟机的一次电源动作状态机，包含失败识别、修复确认及重试。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 电源 |
| 源文件 | ` Management/VirtualMachines/Power/VmPowerOperationSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmPowerOperationSession(Guid vmId, VmPowerAction action, string? displayName = null) `](#api-a0f4ba66eccfb262) | 为稳定身份对应的虚拟机建立一次电源及修复流程，尚不执行操作。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public VmPowerAction Action { get; } `](#api-6002433919afa61e) | 本次执行的类型化电源动作。 |
| [` public VmPowerRepairConfirmation? Confirmation { get; } `](#api-eca41b0b5811d7a8) | 等待用户决定时的确认内容，其他阶段可为 null。 |
| [` public string Error { get; } = string.Empty `](#api-99b6a8e0ff82ce3a) | 最终失败或当前电源动作失败的说明。 |
| [` public ApiResponse? Failure { get; } `](#api-a6a15a1abfb10b7b) | 当前电源、查询或修复失败的结构化结果；尚未失败或最终成功时为 null。 |
| [` public bool RepairFailed { get; } `](#api-ea4bb441fc6553ac) | 是否已有修复步骤失败。 |
| [` public string RepairNotice { get; } = string.Empty `](#api-c6d84b38e01522fa) | 修复过程提供的补充说明。 |
| [` public VmPowerOperationStage Stage { get; } = VmPowerOperationStage.Created `](#api-6619f928f15592b9) | 当前流程阶段；终态不再接受继续推进。 |
| [` public Guid VmId { get; } `](#api-63c95cf749e24981) | 本次电源操作定位的稳定 GUID；内部名称入口在首次推进时唯一解析。 |
| [` public string VmName { get; } `](#api-6e5eab655ba31ecd) | 供确认消息显示的名称，不参与目标定位。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public Task AdvanceAsync() `](#api-2bc126c7885e1dba) | 执行初始电源动作，或在修复完成后重试；必要时停在等待用户确认状态。 |
| [` public async Task RunAsync(Func<VmPowerRepairConfirmation, Task<bool>>? decide = null, Action<VmPowerOperationSession>? changed = null) `](#api-2aad4399ef07db16) | 连续推进电源流程；遇到确认时调用 decide，未提供决定回调则停在待确认状态。 |
| [` public Task SubmitDecisionAsync(Guid requestId, bool approve, IReadOnlyDictionary<Guid, string>? gpuSelections = null) `](#api-826d8ec71a6d622b) | 提交当前修复确认的决定；批准后执行修复并转为可重试，拒绝则结束为取消。 |

#### 详细说明

```csharp
public sealed class VmPowerOperationSession
```

#### 构造函数说明

<a id="api-a0f4ba66eccfb262"></a>

##### VmPowerOperationSession · 构造器

```csharp
public VmPowerOperationSession(Guid vmId, VmPowerAction action, string? displayName = null);
```

为稳定身份对应的虚拟机建立一次电源及修复流程，尚不执行操作。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID；空值在推进时返回失败。 |
| `action` | 电源动作；未定义值在推进时返回失败。 |
| `displayName` | 可选显示名称，只用于消息；省略时显示 GUID。 |

#### 属性说明

<a id="api-6002433919afa61e"></a>

##### Action · 属性

```csharp
public VmPowerAction Action { get; }
```

本次执行的类型化电源动作。

<a id="api-eca41b0b5811d7a8"></a>

##### Confirmation · 属性

```csharp
public VmPowerRepairConfirmation? Confirmation { get; }
```

等待用户决定时的确认内容，其他阶段可为 null。

<a id="api-99b6a8e0ff82ce3a"></a>

##### Error · 属性

```csharp
public string Error { get; } = string.Empty;
```

最终失败或当前电源动作失败的说明。

<a id="api-a6a15a1abfb10b7b"></a>

##### Failure · 属性

```csharp
public ApiResponse? Failure { get; }
```

当前电源、查询或修复失败的结构化结果；尚未失败或最终成功时为 null。

<a id="api-ea4bb441fc6553ac"></a>

##### RepairFailed · 属性

```csharp
public bool RepairFailed { get; }
```

是否已有修复步骤失败。

<a id="api-c6d84b38e01522fa"></a>

##### RepairNotice · 属性

```csharp
public string RepairNotice { get; } = string.Empty;
```

修复过程提供的补充说明。

<a id="api-6619f928f15592b9"></a>

##### Stage · 属性

```csharp
public VmPowerOperationStage Stage { get; } = VmPowerOperationStage.Created;
```

当前流程阶段；终态不再接受继续推进。

<a id="api-63c95cf749e24981"></a>

##### VmId · 属性

```csharp
public Guid VmId { get; }
```

本次电源操作定位的稳定 GUID；内部名称入口在首次推进时唯一解析。

<a id="api-6e5eab655ba31ecd"></a>

##### VmName · 属性

```csharp
public string VmName { get; }
```

供确认消息显示的名称，不参与目标定位。

#### 方法说明

<a id="api-2bc126c7885e1dba"></a>

##### AdvanceAsync · 方法

```csharp
public Task AdvanceAsync();
```

执行初始电源动作，或在修复完成后重试；必要时停在等待用户确认状态。

- 异常 `InvalidOperationException`：当前阶段不可推进，或同一实例已有正在执行的修改。

<a id="api-2aad4399ef07db16"></a>

##### RunAsync · 方法

```csharp
public async Task RunAsync(Func<VmPowerRepairConfirmation, Task<bool>>? decide = null, Action<VmPowerOperationSession>? changed = null);
```

连续推进电源流程；遇到确认时调用 decide，未提供决定回调则停在待确认状态。

| 参数 | 说明 |
| --- | --- |
| `decide` | 没有选卡需求时，用此回调批准或拒绝修复；null 或 RequiresGpuSelection 为 true 时停在 AwaitingConfirmation。需要选卡时读取 Confirmation.GpuTargets，再调用 SubmitDecisionAsync 传入选择。 |
| `changed` | 每次推进后接收当前会话的通知，可为 null；调用者负责界面线程切换。 |

调用约定：同一实例不得并发调用推进或提交决定。终态不可重新执行；新的电源操作应创建新实例。决定或通知回调抛出的异常会传播给调用者，不会自动将会话改为 Failed。

<a id="api-826d8ec71a6d622b"></a>

##### SubmitDecisionAsync · 方法

```csharp
public Task SubmitDecisionAsync(Guid requestId, bool approve, IReadOnlyDictionary<Guid, string>? gpuSelections = null);
```

提交当前修复确认的决定；批准后执行修复并转为可重试，拒绝则结束为取消。

| 参数 | 说明 |
| --- | --- |
| `requestId` | 用户实际确认的 Confirmation.RequestId；不匹配时保留待确认状态，Failure 返回 Conflict，不修复或取消当前请求。 |
| `approve` | 用户是否同意当前修复；false 将会话结束为取消，不执行修复。 |
| `gpuSelections` | 待修复设备 Instance GUID 到候选 Id 的映射；每个多候选设备必须提供一项。只提交本次清单内的选择，方法开始时复制映射。 |

调用约定：选卡缺失或无效时保留 AwaitingConfirmation，通过 Failure 和 Error 说明原因，调用方可以更正后再提交；此时没有执行任何修复。

- 异常 `InvalidOperationException`：当前不在待确认阶段，或同一实例已有正在执行的修改。

<a id="api-0224dda187b691bd"></a>

### `VmPowerOperationStage` 枚举

电源动作及启动修复确认流程的当前阶段。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 电源 |
| 源文件 | ` Management/VirtualMachines/Power/VmPowerOperationSession.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` AwaitingConfirmation = 1 `](#api-f135440d5618b897) | 发现可修复问题，正在等待用户决定。 |
| [` Cancelled = 5 `](#api-b40a0f2def98c331) | 用户拒绝修复，流程结束。 |
| [` Completed = 3 `](#api-b608021c0fc9daba) | 电源动作成功结束。 |
| [` Created = 0 `](#api-c0b3ee922ed66797) | 尚未尝试电源动作。 |
| [` Failed = 4 `](#api-a3d451e61153b1df) | 执行或修复失败，流程结束。 |
| [` ReadyToRetry = 2 `](#api-92c00ed8ca3c8b88) | 已完成确认对应的修复，可再次执行电源动作。 |

#### 详细说明

```csharp
public enum VmPowerOperationStage
```

#### 枚举值说明

<a id="api-f135440d5618b897"></a>

##### AwaitingConfirmation · 枚举值

```csharp
AwaitingConfirmation = 1
```

发现可修复问题，正在等待用户决定。

<a id="api-b40a0f2def98c331"></a>

##### Cancelled · 枚举值

```csharp
Cancelled = 5
```

用户拒绝修复，流程结束。

<a id="api-b608021c0fc9daba"></a>

##### Completed · 枚举值

```csharp
Completed = 3
```

电源动作成功结束。

<a id="api-c0b3ee922ed66797"></a>

##### Created · 枚举值

```csharp
Created = 0
```

尚未尝试电源动作。

<a id="api-a3d451e61153b1df"></a>

##### Failed · 枚举值

```csharp
Failed = 4
```

执行或修复失败，流程结束。

<a id="api-92c00ed8ca3c8b88"></a>

##### ReadyToRetry · 枚举值

```csharp
ReadyToRetry = 2
```

已完成确认对应的修复，可再次执行电源动作。

<a id="api-a8ec61904ea7c346"></a>

### `VmPowerRepairConfirmation` record 类

由 Core 生成的修复确认内容；调用者呈现后提交用户决定。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 电源 |
| 源文件 | ` Management/VirtualMachines/Power/VmPowerOperationSession.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmPowerRepairConfirmation(VmPowerRepairKind Kind, string Title, string Message, string ConfirmText) `](#api-0d2caa1ebc03a9d1) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string ConfirmText { get; init; } `](#api-1dc962f5bb6fdaaa) | 确认按钮建议文案，不作为机器判定条件。 |
| [` public IReadOnlyList<VmGpuRepairService.StaleGpuPartition> GpuTargets { get; } = Array.Empty<VmGpuRepairService.StaleGpuPartition>() `](#api-6059ffc37d6e3c26) | 本次 GPU 修复清单及每个设备的候选；其他修复类型为空。 |
| [` public VmPowerRepairKind Kind { get; init; } `](#api-fc23de668845b8cc) | 本次需要确认的修复类型。 |
| [` public string Message { get; init; } `](#api-9cb21934f6599c43) | 修复原因和拟采取操作的确认正文。 |
| [` public Guid RequestId { get; } = Guid.NewGuid() `](#api-cdac83b871c1f14b) | 本次确认请求的唯一编号；提交决定时原样回传，不能用另一会话或旧请求的编号批准当前修复。 |
| [` public bool RequiresGpuSelection { get; } `](#api-2f7fe5ba560f3185) | 是否有 GPU 设备需要明确选卡；为 true 时，单纯批准不能执行修复。 |
| [` public string Title { get; init; } `](#api-75fe50d4a2e35429) | 可供调用者展示的确认标题。 |

#### 详细说明

```csharp
public sealed record VmPowerRepairConfirmation(VmPowerRepairKind Kind, string Title, string Message, string ConfirmText)
```

| 参数 | 说明 |
| --- | --- |
| `Kind` | 本次需要确认的修复类型。 |
| `Title` | 可供调用者展示的确认标题。 |
| `Message` | 修复原因和拟采取操作的确认正文。 |
| `ConfirmText` | 确认按钮建议文案，不作为机器判定条件。 |

#### 构造函数说明

<a id="api-0d2caa1ebc03a9d1"></a>

##### VmPowerRepairConfirmation · 主构造器

```csharp
public VmPowerRepairConfirmation(VmPowerRepairKind Kind, string Title, string Message, string ConfirmText);
```

| 参数 | 说明 |
| --- | --- |
| `Kind` | 本次需要确认的修复类型。 |
| `Title` | 可供调用者展示的确认标题。 |
| `Message` | 修复原因和拟采取操作的确认正文。 |
| `ConfirmText` | 确认按钮建议文案，不作为机器判定条件。 |

#### 属性说明

<a id="api-1dc962f5bb6fdaaa"></a>

##### ConfirmText · record 属性

```csharp
public string ConfirmText { get; init; }
```

确认按钮建议文案，不作为机器判定条件。

<a id="api-6059ffc37d6e3c26"></a>

##### GpuTargets · 属性

```csharp
public IReadOnlyList<VmGpuRepairService.StaleGpuPartition> GpuTargets { get; } = Array.Empty<VmGpuRepairService.StaleGpuPartition>();
```

本次 GPU 修复清单及每个设备的候选；其他修复类型为空。列表只读，选择通过 SubmitDecisionAsync 提交。

<a id="api-fc23de668845b8cc"></a>

##### Kind · record 属性

```csharp
public VmPowerRepairKind Kind { get; init; }
```

本次需要确认的修复类型。

<a id="api-9cb21934f6599c43"></a>

##### Message · record 属性

```csharp
public string Message { get; init; }
```

修复原因和拟采取操作的确认正文。

<a id="api-cdac83b871c1f14b"></a>

##### RequestId · 属性

```csharp
public Guid RequestId { get; } = Guid.NewGuid();
```

本次确认请求的唯一编号；提交决定时原样回传，不能用另一会话或旧请求的编号批准当前修复。

<a id="api-2f7fe5ba560f3185"></a>

##### RequiresGpuSelection · 属性

```csharp
public bool RequiresGpuSelection { get; }
```

是否有 GPU 设备需要明确选卡；为 true 时，单纯批准不能执行修复。

<a id="api-75fe50d4a2e35429"></a>

##### Title · record 属性

```csharp
public string Title { get; init; }
```

可供调用者展示的确认标题。

<a id="api-dac39fcfb92d79df"></a>

### `VmPowerRepairKind` 枚举

启动或电源动作失败后提出的修复类型。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 电源 |
| 源文件 | ` Management/VirtualMachines/Power/VmPowerOperationSession.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Gpu = 0 `](#api-0958709f151502a3) | 需要修复 GPU 相关配置。 |
| [` PassthroughDisk = 1 `](#api-61fe65f7f51b6f37) | 需要移除失效的物理直通磁盘挂接后重试电源动作。 |

#### 详细说明

```csharp
public enum VmPowerRepairKind
```

#### 枚举值说明

<a id="api-0958709f151502a3"></a>

##### Gpu · 枚举值

```csharp
Gpu = 0
```

需要修复 GPU 相关配置。

<a id="api-61fe65f7f51b6f37"></a>

##### PassthroughDisk · 枚举值

```csharp
PassthroughDisk = 1
```

需要移除失效的物理直通磁盘挂接后重试电源动作。

<a id="api-311d6ac85314661b"></a>

### `VmPowerService` 类

执行单台虚拟机电源控制，管理电源行为和虚拟电池。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 电源；管理层面 / 虚拟机 / 修改 / 电源 |
| 源文件 | ` Management/VirtualMachines/Power/VmPowerService.Battery.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanEditHibernation(bool available, bool isRunning) `](#api-d578abf45b29f44e) | 根据属性可用性和当前运行状态判断休眠设置能否修改。 |
| [` public static Task<ApiResponse> ExecuteControlActionAsync(Guid vmId, VmPowerAction action) `](#api-820886e49b7ca692) | 按稳定身份执行类型化电源动作，不隐式升级正常关机或重启。 |
| [` public static Task<ApiResponse> ExecuteControlActionAsync(Guid vmId, string action) `](#api-fcc28063736ec953) | 按稳定身份执行指定名称的电源动作。 |
| [` public static async Task<ApiResponse> ExecuteControlActionAsync(string vmName, VmPowerAction action) `](#api-2347873620c306f7) | 按唯一显示名称定位虚拟机并执行类型化电源动作。 |
| [` public static Task<ApiResponse> ExecuteControlActionAsync(string vmName, string action) `](#api-e76167868dc4f2a0) | 按唯一显示名称定位虚拟机并执行指定电源动作。 |
| [` public static async Task<ApiResponse<VmBatteryState>> GetBatteryStateAsync(Guid vmId) `](#api-4c5dc7ef677ad796) | 读取虚拟电池设备是否可用及是否已添加到指定虚拟机。 |
| [` public static async Task<ApiResponse> SetBatteryEnabledAsync(Guid vmId, bool enabled) `](#api-0edff40740b1a340) | 添加或移除虚拟电池设备；设备已处于目标状态时不重复修改。 |
| [` public static Task<ApiResponse> SetHibernationEnabledAsync(Guid vmId, bool enabled) `](#api-d3369256ce9e922f) | 设置来宾休眠支持；执行时核对虚拟机未运行。 |
| [` public static Task<ApiResponse> SetTurnOffOnGuestRestartAsync(Guid vmId, bool enabled) `](#api-92c9b6f2ceeffec4) | 设置是否将来宾重启请求转换为关机。 |

#### 详细说明

```csharp
public static partial class VmPowerService
```

#### 静态方法说明

<a id="api-d578abf45b29f44e"></a>

##### CanEditHibernation · 方法

```csharp
public static bool CanEditHibernation(bool available, bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

根据属性可用性和当前运行状态判断休眠设置能否修改。

| 参数 | 说明 |
| --- | --- |
| `available` | 当前宿主是否提供此属性。 |
| `isRunning` | 虚拟机当前是否属于活动状态。 |

返回：属性可用且虚拟机未运行时为 true；提交时仍重新检查状态。

<a id="api-820886e49b7ca692"></a>

##### ExecuteControlActionAsync · 方法

```csharp
public static Task<ApiResponse> ExecuteControlActionAsync(Guid vmId, VmPowerAction action);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

按稳定身份执行类型化电源动作，不隐式升级正常关机或重启。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `action` | 要执行的动作；未定义的枚举值返回失败。 |

返回：参数校验或电源请求的结果；启动成功不表示后台亲和性恢复已经结束。

调用约定：Stop 和 Restart 直接返回请求结果。TurnOff 先请求下电，失败或超时后可终止对应 vmwp 进程；终止前核对 VM 归属、创建时间和当前进程映射。本方法不提供取消已提交系统操作的保证。

<a id="api-fcc28063736ec953"></a>

##### ExecuteControlActionAsync · 方法

```csharp
public static Task<ApiResponse> ExecuteControlActionAsync(Guid vmId, string action);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

按稳定身份执行指定名称的电源动作。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `action` | 区分大小写的 Start、TurnOff、Stop、Restart、Save 或 Suspend；其他值返回失败。 |

返回：参数校验或电源请求的结果。

调用约定：Stop 或 Restart 失败不会自动升级为强制操作。TurnOff 明确允许必要时终止该虚拟机工作进程。

<a id="api-2347873620c306f7"></a>

##### ExecuteControlActionAsync · 方法

```csharp
public static async Task<ApiResponse> ExecuteControlActionAsync(string vmName, VmPowerAction action);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

按唯一显示名称定位虚拟机并执行类型化电源动作。

| 参数 | 说明 |
| --- | --- |
| `vmName` | 目标虚拟机的显示名称；重名时拒绝操作。 |
| `action` | 要执行的动作；未定义的枚举值返回失败。 |

返回：身份解析或电源请求的结果。

调用约定：解析成功后只使用 GUID 定位执行目标，避免名称变化导致操作其他虚拟机。

<a id="api-e76167868dc4f2a0"></a>

##### ExecuteControlActionAsync · 方法

```csharp
public static Task<ApiResponse> ExecuteControlActionAsync(string vmName, string action);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

按唯一显示名称定位虚拟机并执行指定电源动作。

| 参数 | 说明 |
| --- | --- |
| `vmName` | 目标虚拟机的显示名称；重名时拒绝操作。 |
| `action` | 区分大小写的 Start、TurnOff、Stop、Restart、Save 或 Suspend；其他值返回失败。 |

返回：身份解析或电源请求的结果。

调用约定：名称仅在入口解析一次，后续执行使用 GUID。Stop 或 Restart 失败不会自动升级为强制操作。

<a id="api-4c5dc7ef677ad796"></a>

##### GetBatteryStateAsync · 方法

```csharp
public static async Task<ApiResponse<VmBatteryState>> GetBatteryStateAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 电源。

读取虚拟电池设备是否可用及是否已添加到指定虚拟机。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；字符串形式也应使用 GUID，而非显示名称。 |

返回：有效虚拟机上缺少电池 WMI 支持时返回 Available=false；查询失败通过响应错误返回。

<a id="api-0edff40740b1a340"></a>

##### SetBatteryEnabledAsync · 方法

```csharp
public static async Task<ApiResponse> SetBatteryEnabledAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

添加或移除虚拟电池设备；设备已处于目标状态时不重复修改。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；字符串形式也应使用 GUID，而非显示名称。 |
| `enabled` | true 请求启用该功能，false 请求关闭；互锁限制由对应操作执行。 |

返回：修改或无需修改的成功响应；虚拟机不存在、主机不支持或底层失败时返回错误。

<a id="api-d3369256ce9e922f"></a>

##### SetHibernationEnabledAsync · 方法

```csharp
public static Task<ApiResponse> SetHibernationEnabledAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

设置来宾休眠支持；执行时核对虚拟机未运行。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | true 启用，false 关闭。 |

返回：配置提交结果；属性不受支持或执行条件不满足时返回失败。

调用约定：本方法只保存 EnableHibernation 许可，不发起休眠，也不证明来宾具备 S4 和完整型休眠文件。研究规则 released-domain-153:1 在 17763、28000 来宾组件中确认：即使来宾接受休眠请求，也会先应答再调用休眠 API，因此肯定应答仍不能当作已休眠。实际状态需另行观察；当前 Core 电源动作未提供休眠请求。

<a id="api-92c9b6f2ceeffec4"></a>

##### SetTurnOffOnGuestRestartAsync · 方法

```csharp
public static Task<ApiResponse> SetTurnOffOnGuestRestartAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 电源。

设置是否将来宾重启请求转换为关机。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | true 启用，false 关闭。 |

返回：配置提交结果；属性不受支持或执行条件不满足时返回失败。

<a id="api-a69b9ad31071d846"></a>

### `VmProcessorFields` 类

处理器的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Processor/VmProcessorFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> AllowAcountMcount `](#api-deead2d33723fa83) | 活动计数器：是否允许来宾访问 ACNT/MCNT 活动计数器。 |
| [` public static readonly VmQueryField<VmApicMode> ApicMode `](#api-9706ef8e59c814a3) | 中断控制器模式：向来宾呈现的 APIC 中断控制器模式。 |
| [` public static readonly VmQueryField<bool> CompatibilityForMigrationEnabled `](#api-6fbc759c309f5ee3) | 迁移兼容：是否限制向来宾公开的处理器功能，以提高跨处理器迁移的兼容性。 |
| [` public static readonly VmQueryField<VmMigrationCompatibilityMode> CompatibilityForMigrationMode `](#api-547d4560e71ca43d) | 迁移兼容模式：迁移兼容采用的处理器功能集模式。 |
| [` public static readonly VmQueryField<bool> CompatibilityForOlderOperatingSystemsEnabled `](#api-8b0a92292331ebc6) | 旧系统兼容：是否限制来宾可见的 CPUID 功能，以兼容较旧的操作系统。 |
| [` public static readonly VmQueryField<int> Count `](#api-ceaf7a2d2850f9bb) | 处理器数量：分配给虚拟机的虚拟处理器数量。 |
| [` public static readonly VmQueryField<string> CpuBrandString `](#api-bd5f865f174b18bc) | 处理器品牌字符串：向来宾公开的处理器品牌名称字符串。 |
| [` public static readonly VmQueryField<bool> DisableSpeculationControls `](#api-f272a7e54caa7409) | 禁用推测控制：是否禁用虚拟机的推测执行控制。 |
| [` public static readonly VmQueryField<bool> EnableHostResourceProtection `](#api-90827e650cb9c32c) | 宿主资源保护：是否启用宿主资源保护，以限制来宾工作负载对宿主资源的影响。 |
| [` public static readonly VmQueryField<bool> EnablePerfmonArchPmu `](#api-7661e50b8f81a26a) | 架构性能监测：是否向来宾提供体系结构性能监测单元。 |
| [` public static readonly VmQueryField<bool> EnablePerfmonIpt `](#api-17818d016186987a) | 处理器指令跟踪：是否向来宾开放 IPT 处理器指令跟踪功能。 |
| [` public static readonly VmQueryField<bool> EnablePerfmonLbr `](#api-ce347aba41283394) | 最近分支记录：是否向来宾开放 LBR 最近分支记录功能。 |
| [` public static readonly VmQueryField<bool> EnablePerfmonPebs `](#api-227c36bad72b2c5b) | 精确事件采样：是否向来宾开放 PEBS 精确事件采样功能。 |
| [` public static readonly VmQueryField<bool> EnablePerfmonPmu `](#api-db7c4ef1ab5f1cd3) | 性能监测单元：是否向来宾开放 PMU 性能监测功能。 |
| [` public static readonly VmQueryField<bool> ExposeVirtualizationExtensions `](#api-7eb4dbb6aee60d25) | 嵌套虚拟化：是否向来宾公开硬件虚拟化扩展，用于运行嵌套虚拟机。 |
| [` public static readonly VmQueryField<bool> HardwareIsolationExtensionsEnabled `](#api-612c54fbf24f38eb) | 硬件隔离扩展：是否为虚拟机启用硬件隔离扩展。 |
| [` public static readonly VmQueryField<uint> L3CacheWays `](#api-e5de188a99e9b3fa) | 三级缓存路数：向来宾呈现的三级缓存相联路数；0 表示采用默认配置。 |
| [` public static readonly VmQueryField<L3DistributionPolicy> L3DistributionPolicy `](#api-7f7d227fb79af1fd) | 三级缓存处理器分布策略：虚拟处理器在虚拟三级缓存域之间的分布策略。 |
| [` public static readonly VmQueryField<ulong> Limit `](#api-7d047701256eae4b) | 处理器限制比例：虚拟机可使用的处理器资源上限，单位为 0.001%；100000 表示 100%。 |
| [` public static readonly VmQueryField<LpiMode> LpiMode `](#api-233e7f8a17b04416) | 本地外设中断模式：虚拟机使用的本地外设中断（LPI）模式。 |
| [` public static readonly VmQueryField<uint> MaxClusterCountPerSocket `](#api-be02ac5b81451af2) | 每插槽处理器簇上限：每个虚拟处理器插槽可包含的处理器簇数量上限。 |
| [` public static readonly VmQueryField<uint> MaxHwIsolatedGuests `](#api-99dbf0f12abe80d0) | 硬件隔离来宾数量上限：配置允许的硬件隔离来宾数量上限。 |
| [` public static readonly VmQueryField<ulong> MaxNumaNodesPerSocket `](#api-a6d84f5d95a89163) | 每插槽 NUMA 节点上限：每个虚拟处理器插槽可包含的 NUMA 节点数量上限。 |
| [` public static readonly VmQueryField<uint> MaxProcessorCountPerL3 `](#api-9c0edfec76125559) | 每三级缓存域处理器上限：每个虚拟三级缓存域可包含的虚拟处理器数量上限。 |
| [` public static readonly VmQueryField<ulong> MaxProcessorsPerNumaNode `](#api-6886f7e908319659) | 每 NUMA 节点处理器上限：每个虚拟 NUMA 节点可包含的虚拟处理器数量上限。 |
| [` public static readonly VmQueryField<PageShatterMode> PageShatterMode `](#api-82c0f5f41a88be4d) | 大页拆分策略：二级地址转换使用的大页拆分缓解策略。 |
| [` public static readonly VmQueryField<uint> PerfCpuAutonomousActivityWindow `](#api-87fed1966a792a38) | 自主活动窗口：处理器自主性能调节使用的活动窗口编码。 |
| [` public static readonly VmQueryField<uint> PerfCpuEnergyPerformancePreference `](#api-49e77b5fb5c3c8ca) | 能耗性能偏好：处理器在性能与能耗之间取舍的偏好编码。 |
| [` public static readonly VmQueryField<uint> PerfCpuFreqCapMhz `](#api-6413e1002ba615ed) | 处理器频率上限：为虚拟机配置的处理器频率上限，单位为 MHz。 |
| [` public static readonly VmQueryField<uint> PerfCpuFreqDesiredMhz `](#api-936da64e603b6013) | 处理器期望频率：为虚拟机配置的期望处理器频率，单位为 MHz。 |
| [` public static readonly VmQueryField<uint> PerfCpuFreqMinMhz `](#api-cc6cae1a2adaf234) | 处理器频率下限：为虚拟机配置的处理器频率下限，单位为 MHz。 |
| [` public static readonly VmQueryField<bool> PerfCpuIgnoreHostMaxFrequency `](#api-a3d1ef26892f4c97) | 忽略宿主最大频率：虚拟机的处理器频率策略是否忽略宿主最大频率限制。 |
| [` public static readonly VmQueryField<uint> PhysicalAddressWidth `](#api-1a26fc728b5d630c) | 物理地址宽度：向来宾呈现的物理地址宽度，单位为位。 |
| [` public static readonly VmQueryField<int> RelativeWeight `](#api-d2a54696124167d3) | 处理器相对权重：处理器资源发生竞争时，虚拟机参与资源分配的相对权重。 |
| [` public static readonly VmQueryField<ulong> Reservation `](#api-a4a7c3a0f2a21f4b) | 处理器保留比例：为虚拟机保留的处理器资源比例，单位为 0.001%；100000 表示 100%。 |
| [` public static readonly VmQueryField<SmtMode> SmtMode `](#api-3739187f27c50490) | 同时多线程模式：向来宾呈现的同时多线程模式。 |
| [` public static readonly VmQueryField<double> UsagePercent `](#api-7434f578ff5dbbe5) | 处理器使用率：本次取得的各虚拟处理器使用率的算术平均值，100 表示 100%。 |

#### 详细说明

```csharp
public static class VmProcessorFields
```

#### 字段与常量说明

<a id="api-deead2d33723fa83"></a>

##### AllowAcountMcount · 字段

```csharp
public static readonly VmQueryField<bool> AllowAcountMcount;
```

是否允许来宾访问 ACNT/MCNT 活动计数器。

**取值说明：** `true`：允许访问 ACNT/MCNT；`false`：不允许。

<a id="api-9706ef8e59c814a3"></a>

##### ApicMode · 字段

```csharp
public static readonly VmQueryField<VmApicMode> ApicMode;
```

向来宾呈现的 APIC 中断控制器模式。

<a id="api-6fbc759c309f5ee3"></a>

##### CompatibilityForMigrationEnabled · 字段

```csharp
public static readonly VmQueryField<bool> CompatibilityForMigrationEnabled;
```

是否限制向来宾公开的处理器功能，以提高跨处理器迁移的兼容性。

**取值说明：** `true`：启用迁移兼容模式；`false`：关闭。

<a id="api-547d4560e71ca43d"></a>

##### CompatibilityForMigrationMode · 字段

```csharp
public static readonly VmQueryField<VmMigrationCompatibilityMode> CompatibilityForMigrationMode;
```

迁移兼容采用的处理器功能集模式。

<a id="api-8b0a92292331ebc6"></a>

##### CompatibilityForOlderOperatingSystemsEnabled · 字段

```csharp
public static readonly VmQueryField<bool> CompatibilityForOlderOperatingSystemsEnabled;
```

是否限制来宾可见的 CPUID 功能，以兼容较旧的操作系统。

**取值说明：** `true`：启用旧系统兼容限制；`false`：关闭。

<a id="api-ceaf7a2d2850f9bb"></a>

##### Count · 字段

```csharp
public static readonly VmQueryField<int> Count;
```

分配给虚拟机的虚拟处理器数量。

**取值说明：** 正整数，例如 `4` 表示 4 个虚拟处理器。

<a id="api-bd5f865f174b18bc"></a>

##### CpuBrandString · 字段

```csharp
public static readonly VmQueryField<string> CpuBrandString;
```

向来宾公开的处理器品牌名称字符串。

<a id="api-f272a7e54caa7409"></a>

##### DisableSpeculationControls · 字段

```csharp
public static readonly VmQueryField<bool> DisableSpeculationControls;
```

是否禁用虚拟机的推测执行控制。

**取值说明：** `true`：禁用推测执行控制；`false`：不禁用。

<a id="api-90827e650cb9c32c"></a>

##### EnableHostResourceProtection · 字段

```csharp
public static readonly VmQueryField<bool> EnableHostResourceProtection;
```

是否启用宿主资源保护，以限制来宾工作负载对宿主资源的影响。

**取值说明：** `true`：启用宿主资源保护；`false`：关闭。

<a id="api-7661e50b8f81a26a"></a>

##### EnablePerfmonArchPmu · 字段

```csharp
public static readonly VmQueryField<bool> EnablePerfmonArchPmu;
```

是否向来宾提供体系结构性能监测单元。

**取值说明：** `true`：开放体系结构 PMU；`false`：不开放。

<a id="api-17818d016186987a"></a>

##### EnablePerfmonIpt · 字段

```csharp
public static readonly VmQueryField<bool> EnablePerfmonIpt;
```

是否向来宾开放 IPT 处理器指令跟踪功能。

**取值说明：** `true`：开放 IPT；`false`：不开放。

<a id="api-ce347aba41283394"></a>

##### EnablePerfmonLbr · 字段

```csharp
public static readonly VmQueryField<bool> EnablePerfmonLbr;
```

是否向来宾开放 LBR 最近分支记录功能。

**取值说明：** `true`：开放 LBR；`false`：不开放。

<a id="api-227c36bad72b2c5b"></a>

##### EnablePerfmonPebs · 字段

```csharp
public static readonly VmQueryField<bool> EnablePerfmonPebs;
```

是否向来宾开放 PEBS 精确事件采样功能。

**取值说明：** `true`：开放 PEBS；`false`：不开放。

<a id="api-db7c4ef1ab5f1cd3"></a>

##### EnablePerfmonPmu · 字段

```csharp
public static readonly VmQueryField<bool> EnablePerfmonPmu;
```

是否向来宾开放 PMU 性能监测功能。

**取值说明：** `true`：开放 PMU；`false`：不开放。

<a id="api-7eb4dbb6aee60d25"></a>

##### ExposeVirtualizationExtensions · 字段

```csharp
public static readonly VmQueryField<bool> ExposeVirtualizationExtensions;
```

是否向来宾公开硬件虚拟化扩展，用于运行嵌套虚拟机。

**取值说明：** `true`：公开硬件虚拟化扩展；`false`：不公开。

<a id="api-612c54fbf24f38eb"></a>

##### HardwareIsolationExtensionsEnabled · 字段

```csharp
public static readonly VmQueryField<bool> HardwareIsolationExtensionsEnabled;
```

是否为虚拟机启用硬件隔离扩展。

**取值说明：** `true`：启用硬件隔离扩展；`false`：关闭。

<a id="api-e5de188a99e9b3fa"></a>

##### L3CacheWays · 字段

```csharp
public static readonly VmQueryField<uint> L3CacheWays;
```

向来宾呈现的三级缓存相联路数。

**取值说明：** 整数，单位为路；`0` 表示采用默认配置。

<a id="api-7f7d227fb79af1fd"></a>

##### L3DistributionPolicy · 字段

```csharp
public static readonly VmQueryField<L3DistributionPolicy> L3DistributionPolicy;
```

虚拟处理器在虚拟三级缓存域之间的分布策略。

<a id="api-7d047701256eae4b"></a>

##### Limit · 字段

```csharp
public static readonly VmQueryField<ulong> Limit;
```

虚拟机可使用的处理器资源上限。

**取值说明：** 单位为 0.001%；`100000` 表示 100%，`50000` 表示 50%。

<a id="api-233e7f8a17b04416"></a>

##### LpiMode · 字段

```csharp
public static readonly VmQueryField<LpiMode> LpiMode;
```

虚拟机使用的本地外设中断（LPI）模式。

<a id="api-be02ac5b81451af2"></a>

##### MaxClusterCountPerSocket · 字段

```csharp
public static readonly VmQueryField<uint> MaxClusterCountPerSocket;
```

每个虚拟处理器插槽可包含的处理器簇数量上限。

**取值说明：** 整数数量，单位为个。

<a id="api-99dbf0f12abe80d0"></a>

##### MaxHwIsolatedGuests · 字段

```csharp
public static readonly VmQueryField<uint> MaxHwIsolatedGuests;
```

配置允许的硬件隔离来宾数量上限。

**取值说明：** 整数数量，单位为个。

<a id="api-a6d84f5d95a89163"></a>

##### MaxNumaNodesPerSocket · 字段

```csharp
public static readonly VmQueryField<ulong> MaxNumaNodesPerSocket;
```

每个虚拟处理器插槽可包含的 NUMA 节点数量上限。

**取值说明：** 整数数量，单位为个。

<a id="api-9c0edfec76125559"></a>

##### MaxProcessorCountPerL3 · 字段

```csharp
public static readonly VmQueryField<uint> MaxProcessorCountPerL3;
```

每个虚拟三级缓存域可包含的虚拟处理器数量上限。

**取值说明：** 整数数量，单位为个。

<a id="api-6886f7e908319659"></a>

##### MaxProcessorsPerNumaNode · 字段

```csharp
public static readonly VmQueryField<ulong> MaxProcessorsPerNumaNode;
```

每个虚拟 NUMA 节点可包含的虚拟处理器数量上限。

**取值说明：** 整数数量，单位为个。

<a id="api-82c0f5f41a88be4d"></a>

##### PageShatterMode · 字段

```csharp
public static readonly VmQueryField<PageShatterMode> PageShatterMode;
```

二级地址转换使用的大页拆分缓解策略。

<a id="api-87fed1966a792a38"></a>

##### PerfCpuAutonomousActivityWindow · 字段

```csharp
public static readonly VmQueryField<uint> PerfCpuAutonomousActivityWindow;
```

处理器自主性能调节使用的活动窗口编码。

**单位与格式：** 原始编码；时间换算待补充

<a id="api-49e77b5fb5c3c8ca"></a>

##### PerfCpuEnergyPerformancePreference · 字段

```csharp
public static readonly VmQueryField<uint> PerfCpuEnergyPerformancePreference;
```

处理器在性能与能耗之间取舍的偏好编码。

**单位与格式：** 原始编码；取值含义待补充

<a id="api-6413e1002ba615ed"></a>

##### PerfCpuFreqCapMhz · 字段

```csharp
public static readonly VmQueryField<uint> PerfCpuFreqCapMhz;
```

为虚拟机配置的处理器频率上限。

**取值说明：** 整数，单位为 MHz；例如 `2400` 表示 2.4 GHz。

<a id="api-936da64e603b6013"></a>

##### PerfCpuFreqDesiredMhz · 字段

```csharp
public static readonly VmQueryField<uint> PerfCpuFreqDesiredMhz;
```

为虚拟机配置的期望处理器频率。

**取值说明：** 整数，单位为 MHz；例如 `2400` 表示 2.4 GHz。

<a id="api-cc6cae1a2adaf234"></a>

##### PerfCpuFreqMinMhz · 字段

```csharp
public static readonly VmQueryField<uint> PerfCpuFreqMinMhz;
```

为虚拟机配置的处理器频率下限。

**取值说明：** 整数，单位为 MHz；例如 `2400` 表示 2.4 GHz。

<a id="api-a3d1ef26892f4c97"></a>

##### PerfCpuIgnoreHostMaxFrequency · 字段

```csharp
public static readonly VmQueryField<bool> PerfCpuIgnoreHostMaxFrequency;
```

虚拟机的处理器频率策略是否忽略宿主最大频率限制。

**取值说明：** `true`：忽略宿主最大频率限制；`false`：遵循限制。

<a id="api-1a26fc728b5d630c"></a>

##### PhysicalAddressWidth · 字段

```csharp
public static readonly VmQueryField<uint> PhysicalAddressWidth;
```

向来宾呈现的物理地址宽度。

**取值说明：** 整数，单位为位（bit）。

<a id="api-d2a54696124167d3"></a>

##### RelativeWeight · 字段

```csharp
public static readonly VmQueryField<int> RelativeWeight;
```

处理器资源发生竞争时，虚拟机参与资源分配的相对权重。

**取值说明：** 整数权重；数值越大，资源竞争时的相对优先级越高。

<a id="api-a4a7c3a0f2a21f4b"></a>

##### Reservation · 字段

```csharp
public static readonly VmQueryField<ulong> Reservation;
```

为虚拟机保留的处理器资源比例。

**取值说明：** 单位为 0.001%；`100000` 表示 100%，`50000` 表示 50%。

<a id="api-3739187f27c50490"></a>

##### SmtMode · 字段

```csharp
public static readonly VmQueryField<SmtMode> SmtMode;
```

向来宾呈现的同时多线程模式。

<a id="api-7434f578ff5dbbe5"></a>

##### UsagePercent · 字段

```csharp
public static readonly VmQueryField<double> UsagePercent;
```

本次取得的各虚拟处理器使用率的算术平均值；不是全宿主 CPU 的占用比例。

**取值说明：** 百分数；例如 `25` 表示 25%。

**适用条件：** 适用于运行中的虚拟机；全部预期虚拟处理器样本有效时生成平均值。

**缺测与错误：** 计数器名称对应多个同名虚拟机时返回 Ambiguous；其他缺测状态随样本返回。

<a id="api-58662fd52bc5f375"></a>

### `VmProcessorMetricFields` 类

处理器的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Processor/VmProcessorMetricFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<int> Index `](#api-89b914f0afe140db) | 虚拟处理器编号：虚拟机内的虚拟处理器编号，从 0 开始。 |
| [` public static readonly VmQueryField<double> UsagePercent `](#api-448c0c57fd4269d3) | 逐虚拟处理器使用率：单个虚拟处理器的运行时间占采样时间的比例，单位为 %。 |

#### 详细说明

```csharp
public static class VmProcessorMetricFields
```

#### 字段与常量说明

<a id="api-89b914f0afe140db"></a>

##### Index · 字段

```csharp
public static readonly VmQueryField<int> Index;
```

虚拟机内的虚拟处理器编号。

**取值说明：** 从 `0` 开始的整数编号。

<a id="api-448c0c57fd4269d3"></a>

##### UsagePercent · 字段

```csharp
public static readonly VmQueryField<double> UsagePercent;
```

单个虚拟处理器的运行时间占采样时间的比例。

**取值说明：** 百分数；例如 `25` 表示 25%。

**适用条件：** 适用于运行中的虚拟机。

**缺测与错误：** 计数器名称对应多个同名虚拟机时返回 Ambiguous。

<a id="api-5065bc52ce4b1bf9"></a>

### `VmProcessorService` 类

读取、校验和写入虚拟处理器配置及宿主相关高级选项。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 处理器；管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorService.Changes.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public static bool IsArm64Host { get; } `](#api-ac442567faaeecdc) | 当前操作系统是否为 ARM64 架构。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanEditOfflineSettings(bool isPoweredOff) `](#api-e4c8e05c638eb132) | 判断是否允许修改要求完全关机的处理器设置。 |
| [` public static bool CanTogglePerfmonDependent(bool? currentValue, bool? pmu) `](#api-a713121a602099f1) | 判断 PMU 依赖功能能否切换；已启用项仍允许关闭。 |
| [` public static bool CanTogglePerfmonPmu(bool? pmu, bool? pebs, bool? ipt) `](#api-686157bcc236e890) | 判断 PMU 能否切换，防止在 PEBS 或 IPT 仍启用时关闭 PMU。 |
| [` public static string? GetChangeError(VmProcessorSettings current, VmProcessorSettings requested, bool isPoweredOff, HostCapabilities host) `](#api-1fbfc99325fbc7f5) | 比较处理器设置，检查离线限制、宿主架构与 PMU 依赖。 |
| [` public static HostCapabilities GetHostCapabilities(HostProcessorPlatform platform, bool isolationSupported, IEnumerable<string> isolationTypes) `](#api-bc33776a025430aa) | 根据宿主平台和隔离类型计算处理器设置所需的能力标记。 |
| [` public static async Task<ApiResponse<HostCapabilities>> GetHostCapabilitiesAsync() `](#api-4bba2c7324aef3f9) | 读取宿主 CPU 平台和隔离支持后计算处理器配置能力。 |
| [` public static IReadOnlyList<int> GetProcessorCountOptions(int logicalProcessorCount) `](#api-70e78fb1432c6160) | 生成从 1 开始的二次幂选项，并包含宿主逻辑处理器总数。 |
| [` public static async Task<ApiResponse<VmProcessorSettings>> GetVmProcessorAsync(Guid vmId) `](#api-07efb27da397e312) | 读取处理器数量、资源限制和宿主支持的高级处理器属性。 |
| [` public static async Task<ApiResponse> SetVmProcessorAsync(Guid vmId, VmProcessorSettings newSettings, IReadOnlyCollection<string> changedProperties) `](#api-b0226efc49c61356) | 按当前宿主能力和虚拟机状态校验并写入处理器设置。 |

#### 详细说明

```csharp
public static partial class VmProcessorService
```

调用约定：修改入口只读取 changedProperties 中的属性；同字段使用本次请求值，不比较调用方的旧快照或版本。 CPU 与内存写入按 VM 跨 Core 进程串行，锁覆盖重新读取与本次所有修改；不阻止 PowerShell 等外部工具，也不承诺失败时整体回滚。

示例：

只把处理器数量改为 6；无需先读取整份配置，也不会把模型中的其他默认值作为修改请求。



```csharp
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
public static class SelectedProcessorChangeExample
{
    public static Task<ApiResponse> SetCountAsync(Guid vmId) =>
        VmProcessorService.SetVmProcessorAsync(vmId,
            new VmProcessorSettings { Count = 6 }, [nameof(VmProcessorSettings.Count)]);
}
```

#### 属性说明

<a id="api-ac442567faaeecdc"></a>

##### IsArm64Host · 属性

```csharp
public static bool IsArm64Host { get; }
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

当前操作系统是否为 ARM64 架构。

#### 静态方法说明

<a id="api-e4c8e05c638eb132"></a>

##### CanEditOfflineSettings · 方法

```csharp
public static bool CanEditOfflineSettings(bool isPoweredOff);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

判断是否允许修改要求完全关机的处理器设置。

| 参数 | 说明 |
| --- | --- |
| `isPoweredOff` | 是否已完全关机；保存、挂起或其他状态应传 false。 |

返回：电源状态为关机时返回 true；运行、保存及挂起状态均返回 false。

<a id="api-a713121a602099f1"></a>

##### CanTogglePerfmonDependent · 方法

```csharp
public static bool CanTogglePerfmonDependent(bool? currentValue, bool? pmu);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

判断 PMU 依赖功能能否切换；已启用项仍允许关闭。

| 参数 | 说明 |
| --- | --- |
| `currentValue` | 当前 PMU 依赖项状态；null 表示未取得当前值，不代表不支持该属性。 |
| `pmu` | 当前 PMU 状态；null 表示未取得当前值，不代表不支持该属性。 |

返回：该项已启用或 PMU 已启用时为 true。

<a id="api-686157bcc236e890"></a>

##### CanTogglePerfmonPmu · 方法

```csharp
public static bool CanTogglePerfmonPmu(bool? pmu, bool? pebs, bool? ipt);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

判断 PMU 能否切换，防止在 PEBS 或 IPT 仍启用时关闭 PMU。

| 参数 | 说明 |
| --- | --- |
| `pmu` | 当前 PMU 状态；null 表示未取得当前值，不代表不支持该属性。 |
| `pebs` | 当前 PEBS 状态；null 表示未取得当前值，不代表不支持该属性。 |
| `ipt` | 当前 IPT 状态；null 表示未取得当前值，不代表不支持该属性。 |

返回：允许切换时为 true。

<a id="api-1fbfc99325fbc7f5"></a>

##### GetChangeError · 方法

```csharp
public static string? GetChangeError(VmProcessorSettings current, VmProcessorSettings requested, bool isPoweredOff, HostCapabilities host);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

比较处理器设置，检查离线限制、宿主架构与 PMU 依赖。

| 参数 | 说明 |
| --- | --- |
| `current` | 从主机读取的当前处理器配置。 |
| `requested` | 计划应用的新处理器配置。 |
| `isPoweredOff` | 是否已完全关机；保存、挂起或其他状态应传 false。 |
| `host` | 当前宿主架构和隔离能力。 |

返回：首个限制说明；无当前检查错误时为 null。

<a id="api-bc33776a025430aa"></a>

##### GetHostCapabilities · 方法

```csharp
public static HostCapabilities GetHostCapabilities(HostProcessorPlatform platform, bool isolationSupported, IEnumerable<string> isolationTypes);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

根据宿主平台和隔离类型计算处理器设置所需的能力标记。

| 参数 | 说明 |
| --- | --- |
| `platform` | 已检测的宿主原生 CPU 平台。 |
| `isolationSupported` | 宿主是否报告硬件隔离支持。 |
| `isolationTypes` | 宿主提供的隔离类型名称；SNP 或 TDX 用于判定硬件隔离能力。 |

返回：Intel、ARM64 和 SNP/TDX 硬件隔离支持标记。

<a id="api-4bba2c7324aef3f9"></a>

##### GetHostCapabilitiesAsync · 方法

```csharp
public static async Task<ApiResponse<HostCapabilities>> GetHostCapabilitiesAsync();
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

读取宿主 CPU 平台和隔离支持后计算处理器配置能力。

返回：成功时返回当前宿主能力；查询异常返回失败原因。

<a id="api-70e78fb1432c6160"></a>

##### GetProcessorCountOptions · 方法

```csharp
public static IReadOnlyList<int> GetProcessorCountOptions(int logicalProcessorCount);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

生成从 1 开始的二次幂选项，并包含宿主逻辑处理器总数。

| 参数 | 说明 |
| --- | --- |
| `logicalProcessorCount` | 宿主逻辑处理器总数，调用者应提供正数。 |

返回：去重后升序的处理器数量选项。

<a id="api-07efb27da397e312"></a>

##### GetVmProcessorAsync · 方法

```csharp
public static async Task<ApiResponse<VmProcessorSettings>> GetVmProcessorAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 处理器。

读取处理器数量、资源限制和宿主支持的高级处理器属性。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时返回处理器设置；虚拟机或配置不存在为 NotFound，查询失败保留错误；可空高级属性表示主机未提供数据。

调用约定：可空字段未取得当前值时保留 null，不补为 false、0 或空字符串。宿主是否提供该字段由返回模型的 Supports 或 SupportedCapabilities 单独判断；字段存在不保证当前状态下允许修改。部分数值字段仍将底层未设置编码归一为 null，具体见设置模型说明。

<a id="api-b0226efc49c61356"></a>

##### SetVmProcessorAsync · 方法

```csharp
public static async Task<ApiResponse> SetVmProcessorAsync(Guid vmId, VmProcessorSettings newSettings, IReadOnlyCollection<string> changedProperties);
```

功能归属：管理层面 / 虚拟机 / 修改 / 处理器。

按当前宿主能力和虚拟机状态校验并写入处理器设置。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `newSettings` | 目标处理器配置；仅取 changedProperties 列出的属性；资源比例采用 0.001% 原始单位。 |
| `changedProperties` | 要提交的属性名集合，不能为空。例如仅修改处理器数量时包含 `nameof(VmProcessorSettings.Count)`。未选择字段保持取得虚拟机设置锁后读到的值。同字段按本次值覆盖，不比较旧值或版本。CPU 与内存设置在同一虚拟机上跨 Core 进程串行；不阻止外部工具修改。 |

返回：成功响应或包含校验/原生执行原因的失败响应。页拆分和 L3 缓存路数写入后会再次读取，无法取得实际值时返回 OutcomeUnknown，读回值不同为 Conflict；不据空值推断该字段不受支持。

调用约定：可空字段的 null 通常忽略，不写入。例外：CpuBrandString 的 null 清为空字符串；PerfCpuFreqDesiredMhz、PerfCpuEnergyPerformancePreference、PerfCpuAutonomousActivityWindow、MaxClusterCountPerSocket 和 MaxProcessorCountPerL3 的 null 写入底层未设置编码以恢复默认。未选择的字段始终不作为请求。全部选择均被忽略或与当前值相同则不提交配置。

示例：

```csharp
using System;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Services;

public static class ProcessorFieldExample
{
    public static async Task SetCountAsync(Guid vmId)
    {
        var settings = new VmProcessorSettings { Count = 4 };
        var result = await VmProcessorService.SetVmProcessorAsync(
            vmId, settings, [nameof(VmProcessorSettings.Count)]);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }
}
```

<a id="api-726a953ed0783af6"></a>

### `VmProcessorService.HostCapabilities` record 类

处理器配置规则所使用的宿主架构和硬件隔离能力。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 处理器 |
| 源文件 | ` Management/VirtualMachines/Processor/VmProcessorService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public HostCapabilities(bool IsIntel, bool IsArm64, bool HardwareIsolationSupported) `](#api-f1844dafeab1a403) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool HardwareIsolationSupported { get; init; } `](#api-f1a5b4216c0eb678) | 是否支持 SNP 或 TDX 硬件隔离。 |
| [` public bool IsArm64 { get; init; } `](#api-8cd503566716536b) | 宿主是否为 ARM64 平台。 |
| [` public bool IsIntel { get; init; } `](#api-c2c0876876686bd7) | 宿主是否为 Intel 平台。 |
| [` public bool IsX64 { get; } `](#api-a857a8bbe6bdd994) | 按当前规则由非 ARM64 推导出的 x64 能力标记。 |

#### 详细说明

```csharp
public sealed record HostCapabilities(bool IsIntel, bool IsArm64, bool HardwareIsolationSupported)
```

| 参数 | 说明 |
| --- | --- |
| `IsIntel` | 宿主是否为 Intel 平台。 |
| `IsArm64` | 宿主是否为 ARM64 平台。 |
| `HardwareIsolationSupported` | 是否支持 SNP 或 TDX 硬件隔离。 |

#### 构造函数说明

<a id="api-f1844dafeab1a403"></a>

##### HostCapabilities · 主构造器

```csharp
public HostCapabilities(bool IsIntel, bool IsArm64, bool HardwareIsolationSupported);
```

| 参数 | 说明 |
| --- | --- |
| `IsIntel` | 宿主是否为 Intel 平台。 |
| `IsArm64` | 宿主是否为 ARM64 平台。 |
| `HardwareIsolationSupported` | 是否支持 SNP 或 TDX 硬件隔离。 |

#### 属性说明

<a id="api-f1a5b4216c0eb678"></a>

##### HardwareIsolationSupported · record 属性

```csharp
public bool HardwareIsolationSupported { get; init; }
```

是否支持 SNP 或 TDX 硬件隔离。

<a id="api-8cd503566716536b"></a>

##### IsArm64 · record 属性

```csharp
public bool IsArm64 { get; init; }
```

宿主是否为 ARM64 平台。

<a id="api-c2c0876876686bd7"></a>

##### IsIntel · record 属性

```csharp
public bool IsIntel { get; init; }
```

宿主是否为 Intel 平台。

<a id="api-a857a8bbe6bdd994"></a>

##### IsX64 · 属性

```csharp
public bool IsX64 { get; }
```

按当前规则由非 ARM64 推导出的 x64 能力标记。

<a id="api-697b0c9b00903189"></a>

### `VmQueryCollection` 枚举

字段所属的记录集合；虚拟机级字段与设备级字段分开，避免将设备属性误当全局开关。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryRequest.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` BootEntries = 6 `](#api-9767f74828e4dbca) | 固件启动顺序中的条目。 |
| [` Checkpoints = 9 `](#api-e9346323c153e44a) | 检查点及当前状态节点。 |
| [` GpuAssignments = 7 `](#api-671eb7864e4c887c) | 已分配 GPU 分区适配器。 |
| [` Groups = 1 `](#api-13aacaceb1d79e9f) | 直接所属分组。 |
| [` Metadata = 2 `](#api-3e1d5d5e3f03d146) | 额外元数据的键值项。 |
| [` NetworkAdapters = 5 `](#api-ecded18b6473cc31) | 虚拟网卡。 |
| [` PcieDevices = 8 `](#api-05c5fe7e01b89a33) | 已分配 PCIe 设备。 |
| [` Processors = 3 `](#api-806e36e0534fc73f) | 虚拟处理器的逐核心采样。 |
| [` Storage = 4 `](#api-4b84baa4b68b1b42) | 存储控制器上的挂接槽位，包括空光驱。 |
| [` VirtualMachine = 0 `](#api-2732fe81225c5447) | 虚拟机本身，包括整台虚拟机的配置开关。 |

#### 详细说明

```csharp
public enum VmQueryCollection
```

#### 枚举值说明

<a id="api-9767f74828e4dbca"></a>

##### BootEntries · 枚举值

```csharp
BootEntries = 6
```

固件启动顺序中的条目。

<a id="api-e9346323c153e44a"></a>

##### Checkpoints · 枚举值

```csharp
Checkpoints = 9
```

检查点及当前状态节点。

<a id="api-671eb7864e4c887c"></a>

##### GpuAssignments · 枚举值

```csharp
GpuAssignments = 7
```

已分配 GPU 分区适配器。

<a id="api-13aacaceb1d79e9f"></a>

##### Groups · 枚举值

```csharp
Groups = 1
```

直接所属分组。

<a id="api-3e1d5d5e3f03d146"></a>

##### Metadata · 枚举值

```csharp
Metadata = 2
```

额外元数据的键值项。

<a id="api-ecded18b6473cc31"></a>

##### NetworkAdapters · 枚举值

```csharp
NetworkAdapters = 5
```

虚拟网卡。

<a id="api-05c5fe7e01b89a33"></a>

##### PcieDevices · 枚举值

```csharp
PcieDevices = 8
```

已分配 PCIe 设备。

<a id="api-806e36e0534fc73f"></a>

##### Processors · 枚举值

```csharp
Processors = 3
```

虚拟处理器的逐核心采样。

<a id="api-4b84baa4b68b1b42"></a>

##### Storage · 枚举值

```csharp
Storage = 4
```

存储控制器上的挂接槽位，包括空光驱。

<a id="api-2732fe81225c5447"></a>

##### VirtualMachine · 枚举值

```csharp
VirtualMachine = 0
```

虚拟机本身，包括整台虚拟机的配置开关。

<a id="api-f21b0baf9fec63bd"></a>

### `VmQueryCollectionResult` record 类

一个子对象集合的查询结果；Available 且 Rows 为空才表示确认没有对象。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmQueryCollectionResult(VmQueryValueState State, IReadOnlyList<VmQueryRow> Rows, string? Error, DateTimeOffset? ObservedAtUtc) `](#api-3d6d169b066bcc7f) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? Error { get; init; } `](#api-d71904e8c601d8e2) | 集合枚举失败或不适用的原因。 |
| [` public ApiFailure? Failure { get; init; } `](#api-ab793dee46ac1bae) | 集合来源读取失败的结构化错误；成功或未请求时为 null。 |
| [` public DateTimeOffset? ObservedAtUtc { get; init; } `](#api-3e6b64536c30310e) | 本集合基础清单完成枚举的 UTC 时间，空清单也有时间；未请求时为 null。 |
| [` public IReadOnlyList<VmQueryRow> Rows { get; init; } `](#api-9b5a667e11c7026a) | 本次取得的记录；还应逐字段检查读取状态。 |
| [` public VmQueryValueState State { get; init; } `](#api-dfb362ae57971b91) | 集合枚举状态。 |

#### 详细说明

```csharp
public sealed record VmQueryCollectionResult(VmQueryValueState State, IReadOnlyList<VmQueryRow> Rows, string? Error, DateTimeOffset? ObservedAtUtc)
```

| 参数 | 说明 |
| --- | --- |
| `State` | 集合枚举状态。 |
| `Rows` | 本次取得的记录；还应逐字段检查读取状态。 |
| `Error` | 集合枚举失败或不适用的原因。 |
| `ObservedAtUtc` | 本集合基础清单完成枚举的 UTC 时间，空清单也有时间；未请求时为 null。各字段可能在不同时间取得，见字段自身的 ObservedAtUtc。 |

#### 构造函数说明

<a id="api-3d6d169b066bcc7f"></a>

##### VmQueryCollectionResult · 主构造器

```csharp
public VmQueryCollectionResult(VmQueryValueState State, IReadOnlyList<VmQueryRow> Rows, string? Error, DateTimeOffset? ObservedAtUtc);
```

| 参数 | 说明 |
| --- | --- |
| `State` | 集合枚举状态。 |
| `Rows` | 本次取得的记录；还应逐字段检查读取状态。 |
| `Error` | 集合枚举失败或不适用的原因。 |
| `ObservedAtUtc` | 本集合基础清单完成枚举的 UTC 时间，空清单也有时间；未请求时为 null。各字段可能在不同时间取得，见字段自身的 ObservedAtUtc。 |

#### 属性说明

<a id="api-d71904e8c601d8e2"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

集合枚举失败或不适用的原因。

<a id="api-ab793dee46ac1bae"></a>

##### Failure · 属性

```csharp
public ApiFailure? Failure { get; init; }
```

集合来源读取失败的结构化错误；成功或未请求时为 null。

<a id="api-3e6b64536c30310e"></a>

##### ObservedAtUtc · record 属性

```csharp
public DateTimeOffset? ObservedAtUtc { get; init; }
```

本集合基础清单完成枚举的 UTC 时间，空清单也有时间；未请求时为 null。各字段可能在不同时间取得，见字段自身的 ObservedAtUtc。

<a id="api-9b5a667e11c7026a"></a>

##### Rows · record 属性

```csharp
public IReadOnlyList<VmQueryRow> Rows { get; init; }
```

本次取得的记录；还应逐字段检查读取状态。

<a id="api-dfb362ae57971b91"></a>

##### State · record 属性

```csharp
public VmQueryValueState State { get; init; }
```

集合枚举状态。

<a id="api-1bf7b2c1a1dd7920"></a>

### `VmQueryField` 类

内置可查询字段的描述；不能由调用方自行注入底层查询或执行代码。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryRequest.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Category { get; } `](#api-afd5a754e36e7c01) | 字段在查询目录中的所属领域。 |
| [` public VmQueryCollection Collection { get; } `](#api-f7e84ddcebd01c3a) | 值所属的记录集合；VirtualMachine 表示虚拟机级值。 |
| [` public string Id { get; } `](#api-960e697704551792) | 稳定字段标识，可供网页或配置文件保存查询选择。 |
| [` public string Name { get; } `](#api-63d7d29d1f08d828) | 字段中文名称。 |
| [` public Type ValueType { get; } `](#api-0529fc550fc19ce4) | 成功值的托管类型；失败状态没有有效值。 |

#### 详细说明

```csharp
public abstract class VmQueryField
```

#### 属性说明

<a id="api-afd5a754e36e7c01"></a>

##### Category · 属性

```csharp
public string Category { get; }
```

字段在查询目录中的所属领域。

<a id="api-f7e84ddcebd01c3a"></a>

##### Collection · 属性

```csharp
public VmQueryCollection Collection { get; }
```

值所属的记录集合；VirtualMachine 表示虚拟机级值。

<a id="api-960e697704551792"></a>

##### Id · 属性

```csharp
public string Id { get; }
```

稳定字段标识，可供网页或配置文件保存查询选择。

<a id="api-63d7d29d1f08d828"></a>

##### Name · 属性

```csharp
public string Name { get; }
```

字段中文名称。

<a id="api-0529fc550fc19ce4"></a>

##### ValueType · 属性

```csharp
public Type ValueType { get; }
```

成功值的托管类型；失败状态没有有效值。

<a id="api-bb675fc3332742df"></a>

### `VmQueryField<T>` 类

携带结果类型的字段；使用 VmQueryRow.Get 读取时无需强制转换。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryRequest.cs ` |

#### 详细说明

```csharp
public sealed class VmQueryField<T> : VmQueryField
```

| 类型参数 | 说明 |
| --- | --- |
| `T` | 字段成功值的托管类型。 |

<a id="api-332b2654def2cef6"></a>

### `VmQueryFields` 类

Core 当前支持的全部可查询字段；目录本身不执行任何主机查询。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryFields.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public static IReadOnlyList<VmQueryField> All { get; } = Array.AsReadOnly(new VmQueryField[][]  {  VmIdentityFields.Items,  VmGroupFields.Items,  VmMetadataFields.Items,  VmProcessorFields.Items,  VmProcessorMetricFields.Items,  VmAffinityFields.Items,  VmMemoryFields.Items,  VmStorageFields.Items,  VmStorageGlobalFields.Items,  VmNetworkFields.Items,  VmGpuFields.Items,  VmGpuPerformanceFields.Items,  VmPcieFields.Items,  VmPcieDeviceFields.Items,  VmBootFields.Items,  VmBootGlobalFields.Items,  VmSecurityFields.Items,  VmDisplayFields.Items,  VmMmioFields.Items,  VmConsoleFields.Items,  VmPowerBatteryFields.Items,  VmCheckpointFields.Items,  VmCheckpointGlobalFields.Items,  }.SelectMany(items => items).ToArray()) `](#api-f5f575943cef6d8e) | 按领域排列的完整只读目录，包含标识、名称、所属集合和结果类型；可以用于生成查询选择界面。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static VmQueryField? Find(string id) `](#api-94ae1ed25ca04185) | 根据稳定标识查找字段；不存在时返回 null，不接受任意属性名或底层查询文本。 |

#### 详细说明

```csharp
public static class VmQueryFields
```

#### 属性说明

<a id="api-f5f575943cef6d8e"></a>

##### All · 属性

```csharp
public static IReadOnlyList<VmQueryField> All { get; } = Array.AsReadOnly(new VmQueryField[][]
    {
        VmIdentityFields.Items,
        VmGroupFields.Items,
        VmMetadataFields.Items,
        VmProcessorFields.Items,
        VmProcessorMetricFields.Items,
        VmAffinityFields.Items,
        VmMemoryFields.Items,
        VmStorageFields.Items,
        VmStorageGlobalFields.Items,
        VmNetworkFields.Items,
        VmGpuFields.Items,
        VmGpuPerformanceFields.Items,
        VmPcieFields.Items,
        VmPcieDeviceFields.Items,
        VmBootFields.Items,
        VmBootGlobalFields.Items,
        VmSecurityFields.Items,
        VmDisplayFields.Items,
        VmMmioFields.Items,
        VmConsoleFields.Items,
        VmPowerBatteryFields.Items,
        VmCheckpointFields.Items,
        VmCheckpointGlobalFields.Items,
    }.SelectMany(items => items).ToArray());
```

按领域排列的完整只读目录，包含标识、名称、所属集合和结果类型；可以用于生成查询选择界面。

#### 静态方法说明

<a id="api-94ae1ed25ca04185"></a>

##### Find · 方法

```csharp
public static VmQueryField? Find(string id);
```

根据稳定标识查找字段；不存在时返回 null，不接受任意属性名或底层查询文本。

| 参数 | 说明 |
| --- | --- |
| `id` | 目录中的字段标识，区分大小写。 |

返回：内置字段或 null。

<a id="api-7dc8a4e125a28124"></a>

### `VmQueryIssue` record 类

虚拟机列表中一项可选信息的读取问题；不代表已经确认该信息为空。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmQueryIssue(string Component, string Error, ApiErrorKind ErrorKind, ApiErrorSource ErrorSource, int Code) `](#api-991242f7067e942d) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Code { get; init; } `](#api-c3a6cecc1702a89f) | 原生错误码；未提供时通常为 -1。 |
| [` public string Component { get; init; } `](#api-ba78a4fafbf5f531) | 受影响的信息类别，例如 GpuAssignments、PartitionableGpus、VideoControllerNames、SwitchNames 或 GuestIpAddresses。 |
| [` public string Error { get; init; } `](#api-79d2d2b5828d48ba) | 面向人的失败说明。 |
| [` public ApiErrorKind ErrorKind { get; init; } `](#api-0a01d0efbc10a1fa) | 业务失败类别；已确认对应可选类不存在时为 Unsupported。 |
| [` public ApiErrorSource ErrorSource { get; init; } `](#api-b7234f784567e463) | 原生错误码所属来源。 |

#### 详细说明

```csharp
public sealed record VmQueryIssue(string Component, string Error, ApiErrorKind ErrorKind, ApiErrorSource ErrorSource, int Code)
```

| 参数 | 说明 |
| --- | --- |
| `Component` | 受影响的信息类别，例如 GpuAssignments、PartitionableGpus、VideoControllerNames、SwitchNames 或 GuestIpAddresses。 |
| `Error` | 面向人的失败说明。 |
| `ErrorKind` | 业务失败类别；已确认对应可选类不存在时为 Unsupported。 |
| `ErrorSource` | 原生错误码所属来源。 |
| `Code` | 原生错误码；未提供时通常为 -1。 |

#### 构造函数说明

<a id="api-991242f7067e942d"></a>

##### VmQueryIssue · 主构造器

```csharp
public VmQueryIssue(string Component, string Error, ApiErrorKind ErrorKind, ApiErrorSource ErrorSource, int Code);
```

| 参数 | 说明 |
| --- | --- |
| `Component` | 受影响的信息类别，例如 GpuAssignments、PartitionableGpus、VideoControllerNames、SwitchNames 或 GuestIpAddresses。 |
| `Error` | 面向人的失败说明。 |
| `ErrorKind` | 业务失败类别；已确认对应可选类不存在时为 Unsupported。 |
| `ErrorSource` | 原生错误码所属来源。 |
| `Code` | 原生错误码；未提供时通常为 -1。 |

#### 属性说明

<a id="api-c3a6cecc1702a89f"></a>

##### Code · record 属性

```csharp
public int Code { get; init; }
```

原生错误码；未提供时通常为 -1。

<a id="api-ba78a4fafbf5f531"></a>

##### Component · record 属性

```csharp
public string Component { get; init; }
```

受影响的信息类别，例如 GpuAssignments、PartitionableGpus、VideoControllerNames、SwitchNames 或 GuestIpAddresses。

<a id="api-79d2d2b5828d48ba"></a>

##### Error · record 属性

```csharp
public string Error { get; init; }
```

面向人的失败说明。

<a id="api-0a01d0efbc10a1fa"></a>

##### ErrorKind · record 属性

```csharp
public ApiErrorKind ErrorKind { get; init; }
```

业务失败类别；已确认对应可选类不存在时为 Unsupported。

<a id="api-b7234f784567e463"></a>

##### ErrorSource · record 属性

```csharp
public ApiErrorSource ErrorSource { get; init; }
```

原生错误码所属来源。

<a id="api-239750cc126c8774"></a>

### `VmQueryRequest` 类

一次字段级查询请求；字段来自 VmQueryFields.All 或各领域的字段目录。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryRequest.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmQueryRequest(IEnumerable<VmQueryField> fields, IEnumerable<Guid>? vmIds = null) `](#api-244a7d7793a8c782) | 建立一次不可变的字段选择。 |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<VmQueryField> Fields { get; } `](#api-8cad5830d8559e90) | 本次选择的字段，顺序按请求去重后保留。 |
| [` public IReadOnlyList<Guid>? VmIds { get; } `](#api-6559be2262f85b38) | 目标 GUID；null 表示全部虚拟机。 |

#### 详细说明

```csharp
public sealed class VmQueryRequest
```

调用约定：构造时复制并去重字段和目标。省略目标表示枚举全部虚拟机；显式空目标表示不查询任何虚拟机。空字段集合只枚举身份。请求不包含页面预设、任意 WQL 或写入操作。

#### 构造函数说明

<a id="api-244a7d7793a8c782"></a>

##### VmQueryRequest · 构造器

```csharp
public VmQueryRequest(IEnumerable<VmQueryField> fields, IEnumerable<Guid>? vmIds = null);
```

建立一次不可变的字段选择。

| 参数 | 说明 |
| --- | --- |
| `fields` | 要返回的独立字段；相同字段重复出现只读取一次。 |
| `vmIds` | 目标 GUID；null 为全部，空集合为空结果，非空集合按身份筛选。 |

- 异常 `ArgumentException`：含空 GUID、null 字段或不属于内置目录的字段。

#### 属性说明

<a id="api-8cad5830d8559e90"></a>

##### Fields · 属性

```csharp
public IReadOnlyList<VmQueryField> Fields { get; }
```

本次选择的字段，顺序按请求去重后保留。

<a id="api-6559be2262f85b38"></a>

##### VmIds · 属性

```csharp
public IReadOnlyList<Guid>? VmIds { get; }
```

目标 GUID；null 表示全部虚拟机。

<a id="api-e685ca4677fb0596"></a>

### `VmQueryResult` record 类

自由组合查询的结果；必需的身份枚举失败由外层 ApiResponse 返回，其他失败保留在字段或集合中。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmQueryResult(IReadOnlyList<VmQueryRow> VirtualMachines, IReadOnlyList<Guid> MissingVmIds, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<VmQueryTiming> Timings) `](#api-a8f0d2daf701c765) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public DateTimeOffset CompletedAtUtc { get; init; } `](#api-d1b6e324d4846e13) | 查询结束时间。 |
| [` public IReadOnlyList<Guid> MissingVmIds { get; init; } `](#api-234b222101dfb421) | 请求中指定但本次清单没有找到的 GUID；不静默忽略。 |
| [` public DateTimeOffset StartedAtUtc { get; init; } `](#api-ccec0db0d52bc72f) | 查询开始时间。 |
| [` public IReadOnlyList<VmQueryTiming> Timings { get; init; } `](#api-c3ae0b0b5ccb6d7b) | 各读取阶段的耗时。 |
| [` public IReadOnlyList<VmQueryRow> VirtualMachines { get; init; } `](#api-997a1f01fefc8227) | 按 GUID 排序的虚拟机投影，不使用页面排序规则。 |

#### 详细说明

```csharp
public sealed record VmQueryResult(IReadOnlyList<VmQueryRow> VirtualMachines, IReadOnlyList<Guid> MissingVmIds,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<VmQueryTiming> Timings)
```

| 参数 | 说明 |
| --- | --- |
| `VirtualMachines` | 按 GUID 排序的虚拟机投影，不使用页面排序规则。 |
| `MissingVmIds` | 请求中指定但本次清单没有找到的 GUID；不静默忽略。 |
| `StartedAtUtc` | 查询开始时间。 |
| `CompletedAtUtc` | 查询结束时间。多个来源在此区间分别采集，不保证跨来源原子一致。 |
| `Timings` | 各读取阶段的耗时。 |

#### 构造函数说明

<a id="api-a8f0d2daf701c765"></a>

##### VmQueryResult · 主构造器

```csharp
public VmQueryResult(IReadOnlyList<VmQueryRow> VirtualMachines, IReadOnlyList<Guid> MissingVmIds, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<VmQueryTiming> Timings);
```

| 参数 | 说明 |
| --- | --- |
| `VirtualMachines` | 按 GUID 排序的虚拟机投影，不使用页面排序规则。 |
| `MissingVmIds` | 请求中指定但本次清单没有找到的 GUID；不静默忽略。 |
| `StartedAtUtc` | 查询开始时间。 |
| `CompletedAtUtc` | 查询结束时间。多个来源在此区间分别采集，不保证跨来源原子一致。 |
| `Timings` | 各读取阶段的耗时。 |

#### 属性说明

<a id="api-d1b6e324d4846e13"></a>

##### CompletedAtUtc · record 属性

```csharp
public DateTimeOffset CompletedAtUtc { get; init; }
```

查询结束时间。多个来源在此区间分别采集，不保证跨来源原子一致。

<a id="api-234b222101dfb421"></a>

##### MissingVmIds · record 属性

```csharp
public IReadOnlyList<Guid> MissingVmIds { get; init; }
```

请求中指定但本次清单没有找到的 GUID；不静默忽略。

<a id="api-ccec0db0d52bc72f"></a>

##### StartedAtUtc · record 属性

```csharp
public DateTimeOffset StartedAtUtc { get; init; }
```

查询开始时间。

<a id="api-c3ae0b0b5ccb6d7b"></a>

##### Timings · record 属性

```csharp
public IReadOnlyList<VmQueryTiming> Timings { get; init; }
```

各读取阶段的耗时。

<a id="api-997a1f01fefc8227"></a>

##### VirtualMachines · record 属性

```csharp
public IReadOnlyList<VmQueryRow> VirtualMachines { get; init; }
```

按 GUID 排序的虚拟机投影，不使用页面排序规则。

<a id="api-0c141aad85fe3927"></a>

### `VmQueryRow` 类

一台虚拟机或一个子对象的只读投影；仅包含请求字段。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryResult.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyDictionary<VmQueryCollection, VmQueryCollectionResult> Collections { get; } `](#api-1a4629bb4609fa92) | 本次选择的子对象集合；每个集合也具有独立读取状态。 |
| [` public string Key { get; } `](#api-bab2b04ca874552a) | 虚拟机记录为 GUID；子对象为本次来源的稳定身份或明确标注的序号。 |
| [` public IReadOnlyDictionary<string, VmQueryValue> Values { get; } `](#api-c02c51c6a0a3309e) | 按字段稳定标识索引的结果；不包含为了关联而内部读取的依赖字段。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public VmQueryCollectionResult GetCollection(VmQueryCollection collection) `](#api-5f23335810ecb483) | 取得子对象集合；没有选择时返回 NotRequested，而不是成功空列表。 |
| [` public VmQueryValue<T> Get<T>(VmQueryField<T> field) `](#api-348122994c577cdd) | 按字段描述读取强类型结果；未选择时返回 NotRequested。 |

#### 详细说明

```csharp
public sealed class VmQueryRow
```

#### 属性说明

<a id="api-1a4629bb4609fa92"></a>

##### Collections · 属性

```csharp
public IReadOnlyDictionary<VmQueryCollection, VmQueryCollectionResult> Collections { get; }
```

本次选择的子对象集合；每个集合也具有独立读取状态。

<a id="api-bab2b04ca874552a"></a>

##### Key · 属性

```csharp
public string Key { get; }
```

虚拟机记录为 GUID；子对象为本次来源的稳定身份或明确标注的序号。不可当作任意底层操作路径。

调用约定：虚拟机、所属分组使用 GUID；元数据使用 VM GUID 与元数据键的组合；引导项使用 VM GUID 与本次顺序号，重排后不能用旧 Key 关联同一引导设备。网卡、存储、GPU、PCIe 和检查点使用当前后端记录身份，移除重建或跨宿主后不保证保留。Key 仅在同一虚拟机和同类集合内关联记录；操作入口仍使用其要求的 GUID 或查询引用。

<a id="api-c02c51c6a0a3309e"></a>

##### Values · 属性

```csharp
public IReadOnlyDictionary<string, VmQueryValue> Values { get; }
```

按字段稳定标识索引的结果；不包含为了关联而内部读取的依赖字段。

#### 方法说明

<a id="api-5f23335810ecb483"></a>

##### GetCollection · 方法

```csharp
public VmQueryCollectionResult GetCollection(VmQueryCollection collection);
```

取得子对象集合；没有选择时返回 NotRequested，而不是成功空列表。

| 参数 | 说明 |
| --- | --- |
| `collection` | 需要的子对象种类。 |

返回：集合状态与记录。

<a id="api-348122994c577cdd"></a>

##### Get · 方法

```csharp
public VmQueryValue<T> Get<T>(VmQueryField<T> field);
```

按字段描述读取强类型结果；未选择时返回 NotRequested。

| 类型参数 | 说明 |
| --- | --- |
| `T` | 字段值类型，由字段描述推断。 |

| 参数 | 说明 |
| --- | --- |
| `field` | 内置字段。 |

返回：字段读取状态与值。

<a id="api-043fe4f6d9c1db33"></a>

### `VmQueryService` 类

读取虚拟机列表并持有按需创建的性能计数器；调用者负责释放实例。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryService.Memory.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmQueryService() `](#api-54083c20aa259dda) | 建立独立的查询与性能计数器拥有者；不启动自动刷新循环。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public void Dispose() `](#api-c8347aeeeba3f88b) | 同步等待异步释放完成，回收本实例的性能计数器。 |
| [` public ValueTask DisposeAsync() `](#api-4d0309be308b6bf4) | 停止接受新的性能采样，等待进行中的计数器访问结束并释放计数器。 |
| [` public async Task<Dictionary<Guid, GpuUsageData>> GetGpuPerformanceAsync(IEnumerable<VmInstance> vms) `](#api-319692fabf672c00) | 采样各虚拟机对应的 GPU 引擎占用，并检测是否出现该虚拟机的 GPU 活动实例。 |
| [` public async Task<ApiResponse<string>> GetVmConfigRootAsync(Guid vmId) `](#api-4fbd4742c0c063ad) | 按稳定身份读取虚拟机配置文件所在的根路径。 |
| [` public async Task<ApiResponse<VmInventorySnapshot>> GetVmListAsync() `](#api-2e465d73bfe57502) | 汇集虚拟机及设备配置生成列表快照，不启动持续刷新。 |
| [` public async Task<ApiResponse<Dictionary<Guid, VmDynamicMemoryData>>> GetVmRuntimeMemoryDataAsync() `](#api-ea02c278c682e728) | 按虚拟机 GUID 读取运行时已分配内存和来宾可用内存比例，分别报告两个字段的有效性。 |
| [` public async Task<ApiResponse<VmState>> GetVmStateAsync(Guid vmId) `](#api-2e4d20a997fa9624) | 按稳定身份读取虚拟机当前运行状态。 |
| [` public async Task<ApiResponse<bool>> IsVmPoweredOffAsync(Guid vmId) `](#api-fbddeaf25064539a) | 按稳定身份检查虚拟机是否处于完全关机状态。 |
| [` public async Task<ApiResponse<VmQueryResult>> QueryAsync(VmQueryRequest request, CancellationToken cancellationToken = default) `](#api-b2c9fe1c495ff468) | 按一次请求中的独立字段组合读取虚拟机；同源读取、设备身份关联和宿主数据复用由 Core 完成。 |
| [` public async Task UpdateDiskPerformanceAsync(IEnumerable<VmInstance> vms) `](#api-68108fd110ddce6f) | 采样磁盘性能计数器并原地更新传入虚拟机的磁盘读写速率。 |
| [` public async Task UpdateNetworkPerformanceAsync(IEnumerable<VmInstance> vms) `](#api-de71f96836494602) | 采样网络性能计数器并原地更新传入虚拟机的收发速率。 |

#### 详细说明

```csharp
public partial class VmQueryService
```

调用约定：列表读取是一次快照，性能更新是另行调用。计数器首次建立基线时标记 WarmingUp；仅有效样本用于更新数值。更新方法原地修改传入模型，缺测通过 PerformanceSample 等状态报告。DisposeAsync 会等待本实例进行中的计数器工作，释放后不能再开始性能采样；部分普通 WMI 查询不依赖计数器。

#### 构造函数说明

<a id="api-54083c20aa259dda"></a>

##### VmQueryService · 构造器

```csharp
public VmQueryService();
```

建立独立的查询与性能计数器拥有者；不启动自动刷新循环。

#### 方法说明

<a id="api-c8347aeeeba3f88b"></a>

##### Dispose · 方法

```csharp
public void Dispose();
```

同步等待异步释放完成，回收本实例的性能计数器。

<a id="api-4d0309be308b6bf4"></a>

##### DisposeAsync · 方法

```csharp
public ValueTask DisposeAsync();
```

停止接受新的性能采样，等待进行中的计数器访问结束并释放计数器。

<a id="api-319692fabf672c00"></a>

##### GetGpuPerformanceAsync · 方法

```csharp
public async Task<Dictionary<Guid, GpuUsageData>> GetGpuPerformanceAsync(IEnumerable<VmInstance> vms);
```

采样各虚拟机对应的 GPU 引擎占用，并检测是否出现该虚拟机的 GPU 活动实例。

| 参数 | 说明 |
| --- | --- |
| `vms` | 目标虚拟机列表，用于筛选运行中且带 GPU 的虚拟机；此方法返回字典，不原地写入 GPU 指标。 |

返回：以虚拟机 GUID 为键的本次采样状态；Sample 与 DriverSample 分别说明占用及活动检测是否有效。失败、预热或缺测不发布有效零值。

- 异常 `ObjectDisposedException`：实例已开始释放，不能再开始 GPU 性能采样。

<a id="api-4fbd4742c0c063ad"></a>

##### GetVmConfigRootAsync · 方法

```csharp
public async Task<ApiResponse<string>> GetVmConfigRootAsync(Guid vmId);
```

按稳定身份读取虚拟机配置文件所在的根路径。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |

返回：成功时返回配置根路径；配置缺失与查询失败分别返回失败。

<a id="api-2e465d73bfe57502"></a>

##### GetVmListAsync · 方法

```csharp
public async Task<ApiResponse<VmInventorySnapshot>> GetVmListAsync();
```

汇集虚拟机及设备配置生成列表快照，不启动持续刷新。

返回：成功时返回虚拟机列表及可选读取问题；必需摘要、配置、元数据、内存、存储或网卡基础读取失败时返回失败，不伪装为空列表。

调用约定：可选 GPU 能力、来宾 IP 和友好名称读取失败时保留可用列表及既有显示回退，并在 Issues 中报告原生原因；使用 IsComplete 区分完整与部分结果。

<a id="api-ea02c278c682e728"></a>

##### GetVmRuntimeMemoryDataAsync · 方法

```csharp
public async Task<ApiResponse<Dictionary<Guid, VmDynamicMemoryData>>> GetVmRuntimeMemoryDataAsync();
```

按虚拟机 GUID 读取运行时已分配内存和来宾可用内存比例，分别报告两个字段的有效性。

返回：摘要查询失败返回错误；成功时各项仍可能缺测。已分配内存单位为 MiB，可用比例按内存需求计算，负数表示需求超过分配量。

调用约定：动态内存读取摘要的 MemoryAvailable；固定内存使用 Hyper-V Dynamic Memory VM 的 Current Pressure 和 Guest Visible Physical Memory 推算需求，再换算可用比例。它不是来宾任务管理器的进程工作集或空闲页比例。固定内存计数器按名称提供，只有采样前后能唯一对应同一 GUID、运行时间未倒退且分配量一致时才采用；同名、缺测或来宾尚未报告需求时不更新。调用者应分别检查 AssignedSample 和 AvailableSample，不用缺失值更新历史曲线。动态内存配置读取失败不会丢弃已成功读取的分配量。

<a id="api-2e4d20a997fa9624"></a>

##### GetVmStateAsync · 方法

```csharp
public async Task<ApiResponse<VmState>> GetVmStateAsync(Guid vmId);
```

按稳定身份读取虚拟机当前运行状态。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |

返回：成功时返回业务状态；目标不存在与查询失败分别返回失败，不伪装成未知状态。

<a id="api-fbddeaf25064539a"></a>

##### IsVmPoweredOffAsync · 方法

```csharp
public async Task<ApiResponse<bool>> IsVmPoweredOffAsync(Guid vmId);
```

按稳定身份检查虚拟机是否处于完全关机状态。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |

返回：成功时返回是否完全关机；目标不存在或查询失败返回失败。

<a id="api-b2c9fe1c495ff468"></a>

##### QueryAsync · 方法

```csharp
public async Task<ApiResponse<VmQueryResult>> QueryAsync(VmQueryRequest request, CancellationToken cancellationToken = default);
```

按一次请求中的独立字段组合读取虚拟机；同源读取、设备身份关联和宿主数据复用由 Core 完成。

| 参数 | 说明 |
| --- | --- |
| `request` | 要返回的字段及目标 GUID；不接收任意 WQL，不包含页面预设。 |
| `cancellationToken` | 取消尚未开始的读取；已进入不可中断的本地调用会安全结束后再返回取消。 |

返回：身份枚举失败返回失败响应；成功结果只返回请求字段，部分字段失败保留在对应字段或集合的状态中。

调用约定：同一实例的组合查询串行执行，每次请求内批量 WMI 读取最多并行四个，逐虚拟机的启动顺序与亲和性读取另外限制为四个。不会启动自动刷新、启动 ARP、修改配置或读取缩略图。多个来源并非原子快照。DisposeAsync 等待已开始的组合查询；新调用在释放后被拒绝。旧 GetVmListAsync 暂时保留，尚未将下游页面自动迁移到新接口。

- 异常 `OperationCanceledException`：请求取消；已经发起的读取已收尾。
- 异常 `ObjectDisposedException`：本实例已开始释放。

<a id="api-68108fd110ddce6f"></a>

##### UpdateDiskPerformanceAsync · 方法

```csharp
public async Task UpdateDiskPerformanceAsync(IEnumerable<VmInstance> vms);
```

采样磁盘性能计数器并原地更新传入虚拟机的磁盘读写速率。

| 参数 | 说明 |
| --- | --- |
| `vms` | 要采样的虚拟机集合；更新方法会修改其中对应性能字段，调用者负责安排呈现线程。 |

调用约定：读写速率单位为字节/秒，针对后备文件或物理设备，不保证共享介质的速率按 VM 拆分。以完整文件或设备身份匹配计数器，不能唯一匹配时不更新数值。PerformanceSample 记录本次状态；明确非活动的 VM 标记 NotApplicable。虚拟盘身份另外读取全宿主存储分配，避免传入子集掩盖计数器名称编码碰撞。

<a id="api-de71f96836494602"></a>

##### UpdateNetworkPerformanceAsync · 方法

```csharp
public async Task UpdateNetworkPerformanceAsync(IEnumerable<VmInstance> vms);
```

采样网络性能计数器并原地更新传入虚拟机的收发速率。

| 参数 | 说明 |
| --- | --- |
| `vms` | 要采样的虚拟机集合；更新方法会修改其中对应性能字段，调用者负责安排呈现线程。 |

调用约定：模型中的收发速率采用字节/秒。此方法不建立自动定时器，调用者决定采样间隔。首次建立计数器标记 WarmingUp；只有收发均有效才更新两个速率及 HasNetworkSpeedSample。缺测时保留旧数值，PerformanceSample 说明本次状态。

- 异常 `ObjectDisposedException`：实例已开始释放，不能再开始网络性能采样。

<a id="api-c6acc9d5e6fe51eb"></a>

### `VmQueryService.GpuUsageData` 结构

按 GPU 引擎类别聚合的一次虚拟机采样结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public PerformanceSampleInfo? DriverSample { get; set; } `](#api-33c25606297b4985) | 本次 GPU 引擎实例检测的有效性；无效时 IsDriverBound 不代表确定结论。 |
| [` public PerformanceSampleInfo? Sample { get; set; } `](#api-9dd0e5f4e0aad5c1) | 本次引擎占用采样状态；无效时不能将各引擎的默认值视为零占用。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public double Gpu3d `](#api-23bafb99fd7fce1e) | 3D 引擎占用百分比。 |
| [` public double GpuCopy `](#api-2a3de2a5b7541246) | 复制引擎占用百分比。 |
| [` public double GpuDecode `](#api-6754836ac03e983d) | 视频解码引擎占用百分比。 |
| [` public double GpuEncode `](#api-162e02563b773931) | 视频编码引擎占用百分比。 |
| [` public bool IsDriverBound `](#api-e50aecf4841bf0c9) | 本次是否检测到该虚拟机进程的 GPU 引擎实例；使用率为零也可为 true，不证明来宾驱动状态或正在执行负载。 |

#### 详细说明

```csharp
public struct GpuUsageData
```

调用约定：同类别多个 GPU 引擎百分比直接累加，结果没有统一截断到 100；不应把它当作单一物理 GPU 的严格总利用率。

#### 属性说明

<a id="api-33c25606297b4985"></a>

##### DriverSample · 属性

```csharp
public PerformanceSampleInfo? DriverSample { get; set; }
```

本次 GPU 引擎实例检测的有效性；无效时 IsDriverBound 不代表确定结论。

<a id="api-9dd0e5f4e0aad5c1"></a>

##### Sample · 属性

```csharp
public PerformanceSampleInfo? Sample { get; set; }
```

本次引擎占用采样状态；无效时不能将各引擎的默认值视为零占用。

#### 字段与常量说明

<a id="api-23bafb99fd7fce1e"></a>

##### Gpu3d · 字段

```csharp
public double Gpu3d;
```

3D 引擎占用百分比。

<a id="api-2a3de2a5b7541246"></a>

##### GpuCopy · 字段

```csharp
public double GpuCopy;
```

复制引擎占用百分比。

<a id="api-6754836ac03e983d"></a>

##### GpuDecode · 字段

```csharp
public double GpuDecode;
```

视频解码引擎占用百分比。

<a id="api-162e02563b773931"></a>

##### GpuEncode · 字段

```csharp
public double GpuEncode;
```

视频编码引擎占用百分比。

<a id="api-e50aecf4841bf0c9"></a>

##### IsDriverBound · 字段

```csharp
public bool IsDriverBound;
```

本次是否检测到该虚拟机进程的 GPU 引擎实例；使用率为零也可为 true，不证明来宾驱动状态或正在执行负载。仅 DriverSample.IsValid 时有本次结论。

<a id="api-71bb74d4d3230055"></a>

### `VmQueryService.VmDynamicMemoryData` 结构

虚拟机当前内存占用与可用百分比。

| 项目 | 内容 |
| --- | --- |
| 类型 | 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryService.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public PerformanceSampleInfo? AssignedSample { get; init; } `](#api-0df00ffb287af5ca) | 已分配内存的本次读取状态及时间。 |
| [` public PerformanceSampleInfo? AvailableSample { get; init; } `](#api-e93c4005ce0adee5) | 来宾可用百分比的本次读取状态及时间；可能独立于已分配内存缺测。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public long AssignedMiB `](#api-8d28112b07bb3d9d) | 当前分配内存，单位 MiB；仅 AssignedSample.IsValid 时表示本次有效值。 |
| [` public int AvailablePercent `](#api-205e8652409c0c56) | 来宾可用内存百分比；仅 AvailableSample.IsValid 时有效。 |

#### 详细说明

```csharp
public struct VmDynamicMemoryData
```

#### 属性说明

<a id="api-0df00ffb287af5ca"></a>

##### AssignedSample · 属性

```csharp
public PerformanceSampleInfo? AssignedSample { get; init; }
```

已分配内存的本次读取状态及时间。

<a id="api-e93c4005ce0adee5"></a>

##### AvailableSample · 属性

```csharp
public PerformanceSampleInfo? AvailableSample { get; init; }
```

来宾可用百分比的本次读取状态及时间；可能独立于已分配内存缺测。

#### 字段与常量说明

<a id="api-8d28112b07bb3d9d"></a>

##### AssignedMiB · 字段

```csharp
public long AssignedMiB;
```

当前分配内存，单位 MiB；仅 AssignedSample.IsValid 时表示本次有效值。

<a id="api-205e8652409c0c56"></a>

##### AvailablePercent · 字段

```csharp
public int AvailablePercent;
```

来宾可用内存百分比；仅 AvailableSample.IsValid 时有效。负数表示内存需求超过分配量，保留原值，不把缺失值截断成零。

<a id="api-ebac90479a0ebe98"></a>

### `VmQueryTiming` record 类

查询引擎执行阶段的耗时，用于比较来源成本；并行阶段耗时不能直接相加作为总时间。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryResult.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmQueryTiming(string Name, TimeSpan Elapsed) `](#api-c7d6082b76bba86c) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public TimeSpan Elapsed { get; init; } `](#api-2621095181876b0b) | 该阶段实际执行耗时。 |
| [` public string Name { get; init; } `](#api-eb94250e87bbdf77) | 读取阶段标识，不是供调用方执行的命令。 |

#### 详细说明

```csharp
public sealed record VmQueryTiming(string Name, TimeSpan Elapsed)
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 读取阶段标识，不是供调用方执行的命令。 |
| `Elapsed` | 该阶段实际执行耗时。 |

#### 构造函数说明

<a id="api-c7d6082b76bba86c"></a>

##### VmQueryTiming · 主构造器

```csharp
public VmQueryTiming(string Name, TimeSpan Elapsed);
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 读取阶段标识，不是供调用方执行的命令。 |
| `Elapsed` | 该阶段实际执行耗时。 |

#### 属性说明

<a id="api-2621095181876b0b"></a>

##### Elapsed · record 属性

```csharp
public TimeSpan Elapsed { get; init; }
```

该阶段实际执行耗时。

<a id="api-eb94250e87bbdf77"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

读取阶段标识，不是供调用方执行的命令。

<a id="api-6ab0c100b6d64513"></a>

### `VmQueryValue` 类

供动态调用方枚举的字段结果基类。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryResult.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? Error { get; } `](#api-e5f80f1a74cd351f) | 缺测、归属冲突或读取失败的说明。 |
| [` public ApiFailure? Failure { get; } `](#api-e7337c7908788fa0) | 来源读取失败的结构化错误；未请求、暂缺值等没有原始错误时为 null。 |
| [` public bool HasValue { get; } `](#api-13fe155b0d393f2a) | 是否取得本次有效数据。 |
| [` public DateTimeOffset ObservedAtUtc { get; } `](#api-b5ae8f6ccde83147) | 该来源完成读取的 UTC 时间；不是全局原子快照时间。 |
| [` public VmQueryValueState State { get; } `](#api-60b6c369c60eb840) | 本次字段读取状态。 |
| [` public abstract object? UntypedValue { get; } `](#api-26b7546b84238eb5) | 动态类型的有效值；无效状态时为 null。 |

#### 详细说明

```csharp
public abstract class VmQueryValue
```

#### 属性说明

<a id="api-e5f80f1a74cd351f"></a>

##### Error · 属性

```csharp
public string? Error { get; }
```

缺测、归属冲突或读取失败的说明。

<a id="api-e7337c7908788fa0"></a>

##### Failure · 属性

```csharp
public ApiFailure? Failure { get; }
```

来源读取失败的结构化错误；未请求、暂缺值等没有原始错误时为 null。

<a id="api-13fe155b0d393f2a"></a>

##### HasValue · 属性

```csharp
public bool HasValue { get; }
```

是否取得本次有效数据。

<a id="api-b5ae8f6ccde83147"></a>

##### ObservedAtUtc · 属性

```csharp
public DateTimeOffset ObservedAtUtc { get; }
```

该来源完成读取的 UTC 时间；不是全局原子快照时间。

<a id="api-60b6c369c60eb840"></a>

##### State · 属性

```csharp
public VmQueryValueState State { get; }
```

本次字段读取状态。

<a id="api-26b7546b84238eb5"></a>

##### UntypedValue · 属性

```csharp
public abstract object? UntypedValue { get; }
```

动态类型的有效值；无效状态时为 null。

<a id="api-78b4a43e7de4c00e"></a>

### `VmQueryValue<T>` 类

带类型的字段结果；读取 Value 前必须检查 HasValue。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryResult.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public override object? UntypedValue { get; } `](#api-a8c1811d73152d0b) | 供动态调用方读取的值；无效时为 null。 |
| [` public T? Value { get; } `](#api-ff8d70c03f80b493) | 仅 HasValue 为 true 时有效；其他状态下的 default 不是实际采样值。 |

#### 详细说明

```csharp
public sealed class VmQueryValue<T> : VmQueryValue
```

| 类型参数 | 说明 |
| --- | --- |
| `T` | 字段值的托管类型。 |

#### 属性说明

<a id="api-a8c1811d73152d0b"></a>

##### UntypedValue · 属性

```csharp
public override object? UntypedValue { get; }
```

供动态调用方读取的值；无效时为 null。

<a id="api-ff8d70c03f80b493"></a>

##### Value · 属性

```csharp
public T? Value { get; }
```

仅 HasValue 为 true 时有效；其他状态下的 default 不是实际采样值。

<a id="api-4c2beb579a79ea91"></a>

### `VmQueryValueState` 枚举

一次读取的状态；只有 Available 的 Value 才是本次有效数据。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 |
| 源文件 | ` Management/VirtualMachines/Query/VmQueryResult.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Ambiguous = 7 `](#api-ce09938223099b39) | 对象或采样不能唯一归属。 |
| [` Available = 0 `](#api-60c44be774f12dba) | 成功读到有效值；false、0 和空集合也可以是有效值。 |
| [` Failed = 5 `](#api-be0b587e1353c612) | 读取操作失败，Error 提供原因。 |
| [` NotApplicable = 3 `](#api-823476c1d36700cd) | 当前状态不适用，例如未运行虚拟机的性能采样。 |
| [` NotRequested = 1 `](#api-c443b6ea673827fa) | 调用方没有选择该字段或集合。 |
| [` Unavailable = 4 `](#api-6ce9cf99b6c0bdc5) | 字段存在但没有取得有效值；不推断为零或关闭。 |
| [` Unsupported = 2 `](#api-2ac08f500549e8c6) | 当前提供者明确缺少所需类或属性。 |
| [` WarmingUp = 6 `](#api-284df5366d5e817a) | 计数器正在建立基线，本次还没有有效速率。 |

#### 详细说明

```csharp
public enum VmQueryValueState
```

#### 枚举值说明

<a id="api-ce09938223099b39"></a>

##### Ambiguous · 枚举值

```csharp
Ambiguous = 7
```

对象或采样不能唯一归属。

<a id="api-60c44be774f12dba"></a>

##### Available · 枚举值

```csharp
Available = 0
```

成功读到有效值；false、0 和空集合也可以是有效值。

<a id="api-be0b587e1353c612"></a>

##### Failed · 枚举值

```csharp
Failed = 5
```

读取操作失败，Error 提供原因。

<a id="api-823476c1d36700cd"></a>

##### NotApplicable · 枚举值

```csharp
NotApplicable = 3
```

当前状态不适用，例如未运行虚拟机的性能采样。

<a id="api-c443b6ea673827fa"></a>

##### NotRequested · 枚举值

```csharp
NotRequested = 1
```

调用方没有选择该字段或集合。

<a id="api-6ce9cf99b6c0bdc5"></a>

##### Unavailable · 枚举值

```csharp
Unavailable = 4
```

字段存在但没有取得有效值；不推断为零或关闭。

<a id="api-2ac08f500549e8c6"></a>

##### Unsupported · 枚举值

```csharp
Unsupported = 2
```

当前提供者明确缺少所需类或属性。

<a id="api-284df5366d5e817a"></a>

##### WarmingUp · 枚举值

```csharp
WarmingUp = 6
```

计数器正在建立基线，本次还没有有效速率。

<a id="api-eb65900437449cfd"></a>

### `VmRegistrationState` 枚举

操作结束时已确认的虚拟机注册状态；未知不等于仍在册或已经注销。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 实例 |
| 源文件 | ` Management/VirtualMachines/Instances/VmRegistrationState.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Registered = 1 `](#api-eb185291eee5f5b6) | 已确认虚拟机仍注册在主机上。 |
| [` Unknown = 0 `](#api-b9748cc05b700875) | 无法确认当前是否仍有注册的虚拟机。 |
| [` Unregistered = 2 `](#api-2be5ee3a853c7a60) | 已确认虚拟机没有注册，或操作尚未提交创建。 |

#### 详细说明

```csharp
public enum VmRegistrationState
```

#### 枚举值说明

<a id="api-eb185291eee5f5b6"></a>

##### Registered · 枚举值

```csharp
Registered = 1
```

已确认虚拟机仍注册在主机上。

<a id="api-b9748cc05b700875"></a>

##### Unknown · 枚举值

```csharp
Unknown = 0
```

无法确认当前是否仍有注册的虚拟机。

<a id="api-2be5ee3a853c7a60"></a>

##### Unregistered · 枚举值

```csharp
Unregistered = 2
```

已确认虚拟机没有注册，或操作尚未提交创建。

<a id="api-8ef86a18b6b1159d"></a>

### `VmScreenshotSource` 类

不建立连接，通过 Hyper-V WMI 获取虚拟机基本控制台的一张截图。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 业务层面 / 视频 |
| 源文件 | ` Business/Video/VmScreenshotSource.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static async Task<VmScreenshotImage?> CaptureAsync(Guid vmId, int desiredWidth, int desiredHeight) `](#api-f254cefc45d55cc1) | 获取基本控制台截图，并将尺寸限制在指定的宽、高范围内。 |

#### 详细说明

```csharp
public static class VmScreenshotSource
```

调用约定：适合列表缩略图，返回 RGB565 图像；不读取增强会话的 RDP 用户桌面。 对已建立的基本或增强连接截图，应使用该连接的 IConsoleVideoSource.CaptureFrame；持续画面使用 FrameReceived。

#### 静态方法说明

<a id="api-f254cefc45d55cc1"></a>

##### CaptureAsync · 方法

```csharp
public static async Task<VmScreenshotImage?> CaptureAsync(Guid vmId, int desiredWidth, int desiredHeight);
```

获取基本控制台截图，并将尺寸限制在指定的宽、高范围内。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 本机虚拟机的 GUID。 |
| `desiredWidth` | 截图允许的最大宽度，1～65535 像素。 |
| `desiredHeight` | 截图允许的最大高度，1～65535 像素。 |

返回：包含实际尺寸和 RGB565 像素的截图。没有可用画面、参数无效或读取失败时返回 null。

调用约定：能读取来宾分辨率时，会保持其宽高比。例如 1920 &#215; 1080 的桌面，指定 400 &#215; 300，可得到 400 &#215; 225 的截图。 有多个显示头时使用首个有效分辨率；无法读取分辨率时按请求的宽高获取。



无法确定当前配置、图像数据不完整或图像过大时返回 null，与没有画面使用同一返回值。

示例：

获取缩略图并转换为 JPEG 字节，随后可写文件或作为网页响应。



```csharp
using ExHyperV.Services;
public static class ScreenshotExample
{
    public static async Task<byte[]?> CaptureJpegAsync(Guid vmId)
    {
        var image = await VmScreenshotSource.CaptureAsync(vmId, 400, 300);
        return image?.EncodeJpeg();
    }
}
```

<a id="api-75f6ad683307ea45"></a>

### `VmSecurityFields` 类

安全的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 安全 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Security/VmSecurityFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> EncryptEnabled `](#api-06df1f9055056625) | 保存状态及迁移流量加密：是否对虚拟机的保存状态和迁移流量启用加密。 |
| [` public static readonly VmQueryField<bool> SecureBootEnabled `](#api-28e557e48799bcb7) | 安全启动：是否启用安全启动，在启动过程中验证引导组件的签名。 |
| [` public static readonly VmQueryField<string> SecureBootTemplateId `](#api-7f6bd57a186d1944) | 安全启动模板：安全启动所使用的信任模板标识。 |
| [` public static readonly VmQueryField<bool> Shielded `](#api-2a3449d9451ad422) | 屏蔽保护请求：虚拟机配置是否请求屏蔽保护。 |
| [` public static readonly VmQueryField<bool> TpmEnabled `](#api-8f8a2903c0a49b71) | 虚拟 TPM：是否为虚拟机启用虚拟 TPM。 |

#### 详细说明

```csharp
public static class VmSecurityFields
```

#### 字段与常量说明

<a id="api-06df1f9055056625"></a>

##### EncryptEnabled · 字段

```csharp
public static readonly VmQueryField<bool> EncryptEnabled;
```

是否对虚拟机的保存状态和迁移流量启用加密。

**取值说明：** `true`：启用保存状态和迁移流量加密；`false`：关闭。

<a id="api-28e557e48799bcb7"></a>

##### SecureBootEnabled · 字段

```csharp
public static readonly VmQueryField<bool> SecureBootEnabled;
```

是否启用安全启动，在启动过程中验证引导组件的签名。

**取值说明：** `true`：启用安全启动；`false`：关闭。

<a id="api-7f6bd57a186d1944"></a>

##### SecureBootTemplateId · 字段

```csharp
public static readonly VmQueryField<string> SecureBootTemplateId;
```

安全启动所使用的信任模板标识。

<a id="api-2a3449d9451ad422"></a>

##### Shielded · 字段

```csharp
public static readonly VmQueryField<bool> Shielded;
```

ShieldingRequested 配置标记；不证明来宾已通过证明或保护已生效。

**取值说明：** `true`：请求屏蔽保护；`false`：未请求。

<a id="api-8f8a2903c0a49b71"></a>

##### TpmEnabled · 字段

```csharp
public static readonly VmQueryField<bool> TpmEnabled;
```

是否为虚拟机启用虚拟 TPM。

**取值说明：** `true`：启用虚拟 TPM；`false`：关闭。

<a id="api-4b2f79ff06e9f37a"></a>

### `VmSecurityService` 类

读取和修改第 2 代虚拟机的安全启动、虚拟 TPM、保存状态及迁移流量加密设置。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 安全；管理层面 / 虚拟机 / 修改 / 安全 |
| 源文件 | ` Management/VirtualMachines/Security/VmSecurityService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static bool CanEdit(int generation, VmState state) `](#api-ad615863e3b71955) | 判断虚拟机是否为第 2 代且已经完全关机。 |
| [` public static Capabilities GetCapabilities(int generation, VmState state, bool secureBoot, bool tpm, bool shielded) `](#api-9c93ddc3d326d62c) | 合并代数、运行状态和安全开关互锁规则。 |
| [` public static Capabilities GetInterlocks(bool secureBoot, bool tpm, bool shielded) `](#api-b49c2d8f03d0a4d7) | 仅按安全启动、TPM 和屏蔽状态计算各安全开关之间的互锁关系。 |
| [` public static async Task<ApiResponse<IReadOnlyList<SecureBootTemplate>>> GetSecureBootTemplatesAsync() `](#api-438e700cde99ca50) | 读取宿主提供的安全启动模板，查询失败与成功空列表分别返回。 |
| [` public static async Task<ApiResponse<VmSecurityInfo>> GetSecuritySettingsAsync(Guid vmId) `](#api-dbe9199815b280f4) | 读取安全启动模板、TPM、状态加密及屏蔽状态；任何必要子查询失败时返回失败。 |
| [` public static string? GetSettingError(int generation, VmState state, VmSecurityInfo current, Setting setting) `](#api-980880659014513c) | 校验指定安全设置是否满足当前代数、运行状态和互锁规则。 |
| [` public static async Task<ApiResponse> SetEncryptionAsync(Guid vmId, bool enabled) `](#api-7c7f5ddfdae4bb2a) | 修改保存状态和迁移流量加密，遵守 TPM 与屏蔽互锁规则。 |
| [` public static async Task<ApiResponse> SetSecureBootAsync(Guid vmId, bool enabled) `](#api-ead4e6953adecf9e) | 校验当前状态后启用或关闭安全启动。 |
| [` public static async Task<ApiResponse> SetSecureBootTemplateAsync(Guid vmId, Guid templateId) `](#api-61172b9c24452be0) | 设置安全启动信任模板。 |
| [` public static async Task<ApiResponse> SetShieldingAsync(Guid vmId, bool enabled) `](#api-aca34d7baf5f06d6) | 启用屏蔽时先确保 TPM，再启用加密与屏蔽；关闭时只清除屏蔽标记。 |
| [` public static async Task<ApiResponse> SetTpmAsync(Guid vmId, bool enabled) `](#api-b33af35bb12380eb) | 配置虚拟 TPM，启用前按需建立本地密钥保护器。 |

#### 详细说明

```csharp
public static class VmSecurityService
```

调用约定：修改前要求虚拟机完全关机；启用虚拟 TPM 时由 Core 准备所需的密钥保护配置。具体能力和互锁限制通过查询及校验接口读取。

#### 静态方法说明

<a id="api-ad615863e3b71955"></a>

##### CanEdit · 方法

```csharp
public static bool CanEdit(int generation, VmState state);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

判断虚拟机是否为第 2 代且已经完全关机。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `state` | 当前运行状态快照；只有 PoweredOff 满足关机要求。保存、挂起、休眠、过渡及未知状态均不满足。 |

返回：第 2 代且 state 为 PoweredOff 时返回 true；各安全开关的互锁规则另由 GetCapabilities 检查。

调用约定：此方法只判断传入的快照，不查询宿主。实际修改入口会重新读取状态；返回 true 不锁定后续状态。

<a id="api-9c93ddc3d326d62c"></a>

##### GetCapabilities · 方法

```csharp
public static Capabilities GetCapabilities(int generation, VmState state, bool secureBoot, bool tpm, bool shielded);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

合并代数、运行状态和安全开关互锁规则。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `state` | 当前运行状态快照；只有 PoweredOff 允许修改。此方法不自行刷新宿主状态。 |
| `secureBoot` | 当前安全启动状态。 |
| `tpm` | 当前虚拟 TPM 状态。 |
| `shielded` | 当前屏蔽状态。 |

返回：当前上下文下各安全设置的编辑能力。

<a id="api-b49c2d8f03d0a4d7"></a>

##### GetInterlocks · 方法

```csharp
public static Capabilities GetInterlocks(bool secureBoot, bool tpm, bool shielded);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

仅按安全启动、TPM 和屏蔽状态计算各安全开关之间的互锁关系。

| 参数 | 说明 |
| --- | --- |
| `secureBoot` | 当前安全启动状态。 |
| `tpm` | 当前虚拟 TPM 状态。 |
| `shielded` | 当前屏蔽状态。 |

返回：每个安全设置是否允许编辑的标记；不包含运行状态及代数限制。

<a id="api-438e700cde99ca50"></a>

##### GetSecureBootTemplatesAsync · 方法

```csharp
public static async Task<ApiResponse<IReadOnlyList<SecureBootTemplate>>> GetSecureBootTemplatesAsync();
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

读取宿主提供的安全启动模板，查询失败与成功空列表分别返回。

返回：模板名称及稳定 GUID 列表；不会把查询错误当作没有模板。

<a id="api-dbe9199815b280f4"></a>

##### GetSecuritySettingsAsync · 方法

```csharp
public static async Task<ApiResponse<VmSecurityInfo>> GetSecuritySettingsAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 安全。

读取安全启动模板、TPM、状态加密及屏蔽状态；任何必要子查询失败时返回失败。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：安全配置快照或查询失败。此模型把 null 或非 bool 的属性值折为 false；需要区分缺测与关闭时使用相应 VmSecurityFields 查询结果。

<a id="api-980880659014513c"></a>

##### GetSettingError · 方法

```csharp
public static string? GetSettingError(int generation, VmState state, VmSecurityInfo current, Setting setting);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

校验指定安全设置是否满足当前代数、运行状态和互锁规则。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `state` | 当前运行状态快照；保存、挂起、休眠、过渡及未知状态均返回需要完全关机。 |
| `current` | 当前成功读取的安全设置，含互锁所需开关。 |
| `setting` | 准备修改的安全设置项。 |

返回：不允许修改的说明；允许继续时为 null。

<a id="api-7c7f5ddfdae4bb2a"></a>

##### SetEncryptionAsync · 方法

```csharp
public static async Task<ApiResponse> SetEncryptionAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

修改保存状态和迁移流量加密，遵守 TPM 与屏蔽互锁规则。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | 是否启用加密。 |

返回：当前状态校验及底层写入结果。

<a id="api-ead4e6953adecf9e"></a>

##### SetSecureBootAsync · 方法

```csharp
public static async Task<ApiResponse> SetSecureBootAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

校验当前状态后启用或关闭安全启动。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | 是否启用安全启动。 |

返回：写入结果或当前状态限制。

<a id="api-61172b9c24452be0"></a>

##### SetSecureBootTemplateAsync · 方法

```csharp
public static async Task<ApiResponse> SetSecureBootTemplateAsync(Guid vmId, Guid templateId);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

设置安全启动信任模板。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `templateId` | 宿主安全启动模板查询结果中的 GUID。 |

返回：写入结果；无效 GUID 在提交前拒绝。

<a id="api-aca34d7baf5f06d6"></a>

##### SetShieldingAsync · 方法

```csharp
public static async Task<ApiResponse> SetShieldingAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

启用屏蔽时先确保 TPM，再启用加密与屏蔽；关闭时只清除屏蔽标记。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | 是否启用屏蔽。 |

返回：最终写入结果；Steps 记录按需执行的 Guardian、GenerateKeyProtector、SetKeyProtector、Tpm 和 Shielding。后续失败保留此前已完成的设置。

<a id="api-b33af35bb12380eb"></a>

##### SetTpmAsync · 方法

```csharp
public static async Task<ApiResponse> SetTpmAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 安全。

配置虚拟 TPM，启用前按需建立本地密钥保护器。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | 是否启用虚拟 TPM。 |

返回：写入结果；Steps 记录按需执行的 Guardian、GenerateKeyProtector、SetKeyProtector 和 Tpm。失败保留已完成步骤；OutcomeUnknown 表示最终状态尚未确认。不需要或未开始的步骤不列出。

<a id="api-3901bdc4dc40e883"></a>

### `VmSecurityService.Capabilities` record 结构

当前上下文下整组及各项安全设置的编辑许可。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 安全 |
| 源文件 | ` Management/VirtualMachines/Security/VmSecurityService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public Capabilities(bool CanEdit, bool SecureBoot, bool SecureBootTemplate, bool Tpm, bool Encryption, bool Shielding) `](#api-c4934d9dff33c571) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool CanEdit { get; init; } `](#api-c3cb1d7a436f1b4e) | 整组安全配置是否允许编辑。 |
| [` public bool Encryption { get; init; } `](#api-610229c4df87fc7e) | 是否允许切换加密。 |
| [` public bool SecureBoot { get; init; } `](#api-c65ab8cfcb63e978) | 是否允许编辑安全启动。 |
| [` public bool SecureBootTemplate { get; init; } `](#api-db1b36b57dbbbf15) | 是否允许选择安全启动模板。 |
| [` public bool Shielding { get; init; } `](#api-856231ed45880ba1) | 是否允许切换屏蔽。 |
| [` public bool Tpm { get; init; } `](#api-c083740c65786721) | 是否允许切换 TPM。 |

#### 详细说明

```csharp
public readonly record struct Capabilities(bool CanEdit, bool SecureBoot, bool SecureBootTemplate, bool Tpm, bool Encryption, bool Shielding)
```

| 参数 | 说明 |
| --- | --- |
| `CanEdit` | 整组安全配置是否允许编辑。 |
| `SecureBoot` | 是否允许编辑安全启动。 |
| `SecureBootTemplate` | 是否允许选择安全启动模板。 |
| `Tpm` | 是否允许切换 TPM。 |
| `Encryption` | 是否允许切换加密。 |
| `Shielding` | 是否允许切换屏蔽。 |

#### 构造函数说明

<a id="api-c4934d9dff33c571"></a>

##### Capabilities · 主构造器

```csharp
public Capabilities(bool CanEdit, bool SecureBoot, bool SecureBootTemplate, bool Tpm, bool Encryption, bool Shielding);
```

| 参数 | 说明 |
| --- | --- |
| `CanEdit` | 整组安全配置是否允许编辑。 |
| `SecureBoot` | 是否允许编辑安全启动。 |
| `SecureBootTemplate` | 是否允许选择安全启动模板。 |
| `Tpm` | 是否允许切换 TPM。 |
| `Encryption` | 是否允许切换加密。 |
| `Shielding` | 是否允许切换屏蔽。 |

#### 属性说明

<a id="api-c3cb1d7a436f1b4e"></a>

##### CanEdit · record 属性

```csharp
public bool CanEdit { get; init; }
```

整组安全配置是否允许编辑。

<a id="api-610229c4df87fc7e"></a>

##### Encryption · record 属性

```csharp
public bool Encryption { get; init; }
```

是否允许切换加密。

<a id="api-c65ab8cfcb63e978"></a>

##### SecureBoot · record 属性

```csharp
public bool SecureBoot { get; init; }
```

是否允许编辑安全启动。

<a id="api-db1b36b57dbbbf15"></a>

##### SecureBootTemplate · record 属性

```csharp
public bool SecureBootTemplate { get; init; }
```

是否允许选择安全启动模板。

<a id="api-856231ed45880ba1"></a>

##### Shielding · record 属性

```csharp
public bool Shielding { get; init; }
```

是否允许切换屏蔽。

<a id="api-c083740c65786721"></a>

##### Tpm · record 属性

```csharp
public bool Tpm { get; init; }
```

是否允许切换 TPM。

<a id="api-fd164ffe192c3a62"></a>

### `VmSecurityService.SecureBootTemplate` record 类

宿主提供的安全启动信任模板。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 安全 |
| 源文件 | ` Management/VirtualMachines/Security/VmSecurityService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public SecureBootTemplate(string Name, Guid Id) `](#api-f623932bb5b12fc3) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public Guid Id { get; init; } `](#api-3de726ab4713c856) | 用于选择模板的稳定 GUID。 |
| [` public string Name { get; init; } `](#api-daf97913e07038c1) | 模板的显示名称。 |

#### 详细说明

```csharp
public sealed record SecureBootTemplate(string Name, Guid Id)
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 模板的显示名称。 |
| `Id` | 用于选择模板的稳定 GUID。 |

#### 构造函数说明

<a id="api-f623932bb5b12fc3"></a>

##### SecureBootTemplate · 主构造器

```csharp
public SecureBootTemplate(string Name, Guid Id);
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 模板的显示名称。 |
| `Id` | 用于选择模板的稳定 GUID。 |

#### 属性说明

<a id="api-3de726ab4713c856"></a>

##### Id · record 属性

```csharp
public Guid Id { get; init; }
```

用于选择模板的稳定 GUID。

<a id="api-daf97913e07038c1"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

模板的显示名称。

<a id="api-5a0351ede3d222c3"></a>

### `VmSecurityService.Setting` 枚举

需要进行互锁及状态校验的安全设置项。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 安全 |
| 源文件 | ` Management/VirtualMachines/Security/VmSecurityService.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Encryption = 3 `](#api-1685e267e228fbd4) | 保存状态和迁移流量加密开关。 |
| [` SecureBoot = 0 `](#api-2b14d00ebf0320fb) | 安全启动开关。 |
| [` SecureBootTemplate = 1 `](#api-3fbfd5aa04175a1a) | 安全启动信任模板。 |
| [` Shielding = 4 `](#api-483ec4d41595ee6d) | 虚拟机屏蔽开关。 |
| [` Tpm = 2 `](#api-b3e75b596a0f448c) | 虚拟可信平台模块开关。 |

#### 详细说明

```csharp
public enum Setting
```

#### 枚举值说明

<a id="api-1685e267e228fbd4"></a>

##### Encryption · 枚举值

```csharp
Encryption = 3
```

保存状态和迁移流量加密开关。

<a id="api-2b14d00ebf0320fb"></a>

##### SecureBoot · 枚举值

```csharp
SecureBoot = 0
```

安全启动开关。

<a id="api-3fbfd5aa04175a1a"></a>

##### SecureBootTemplate · 枚举值

```csharp
SecureBootTemplate = 1
```

安全启动信任模板。

<a id="api-483ec4d41595ee6d"></a>

##### Shielding · 枚举值

```csharp
Shielding = 4
```

虚拟机屏蔽开关。

<a id="api-b3e75b596a0f448c"></a>

##### Tpm · 枚举值

```csharp
Tpm = 2
```

虚拟可信平台模块开关。

<a id="api-86e6f74c910a8e21"></a>

### `VmSecurityService.VmSecurityInfo` record 结构

成功读取的安全设置快照；读取失败由外层 ApiResponse 表示。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 结构 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 安全 |
| 源文件 | ` Management/VirtualMachines/Security/VmSecurityService.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmSecurityInfo(bool SecureBootEnabled, string SecureBootTemplateId, bool TpmEnabled, bool EncryptEnabled, bool Shielded) `](#api-2b9c817de92f2767) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public bool EncryptEnabled { get; init; } `](#api-193c15e7791ed52c) | 保存状态及迁移流量加密是否启用。 |
| [` public bool SecureBootEnabled { get; init; } `](#api-74cd339a6dc4dcaf) | 安全启动是否启用。 |
| [` public string SecureBootTemplateId { get; init; } `](#api-3370dfebfef4e9e2) | 当前安全启动模板 GUID 字符串。 |
| [` public bool Shielded { get; init; } `](#api-87fe8e0917911350) | ShieldingRequested 配置标记，表示请求屏蔽保护；不证明来宾已通过证明或保护已生效。 |
| [` public bool TpmEnabled { get; init; } `](#api-a2a39ae8943176b4) | 虚拟 TPM 是否启用。 |

#### 详细说明

```csharp
public readonly record struct VmSecurityInfo(bool SecureBootEnabled, string SecureBootTemplateId, bool TpmEnabled, bool EncryptEnabled, bool Shielded)
```

| 参数 | 说明 |
| --- | --- |
| `SecureBootEnabled` | 安全启动是否启用。 |
| `SecureBootTemplateId` | 当前安全启动模板 GUID 字符串。 |
| `TpmEnabled` | 虚拟 TPM 是否启用。 |
| `EncryptEnabled` | 保存状态及迁移流量加密是否启用。 |
| `Shielded` | ShieldingRequested 配置标记，表示请求屏蔽保护；不证明来宾已通过证明或保护已生效。 |

#### 构造函数说明

<a id="api-2b9c817de92f2767"></a>

##### VmSecurityInfo · 主构造器

```csharp
public VmSecurityInfo(bool SecureBootEnabled, string SecureBootTemplateId, bool TpmEnabled, bool EncryptEnabled, bool Shielded);
```

| 参数 | 说明 |
| --- | --- |
| `SecureBootEnabled` | 安全启动是否启用。 |
| `SecureBootTemplateId` | 当前安全启动模板 GUID 字符串。 |
| `TpmEnabled` | 虚拟 TPM 是否启用。 |
| `EncryptEnabled` | 保存状态及迁移流量加密是否启用。 |
| `Shielded` | ShieldingRequested 配置标记，表示请求屏蔽保护；不证明来宾已通过证明或保护已生效。 |

#### 属性说明

<a id="api-193c15e7791ed52c"></a>

##### EncryptEnabled · record 属性

```csharp
public bool EncryptEnabled { get; init; }
```

保存状态及迁移流量加密是否启用。

<a id="api-74cd339a6dc4dcaf"></a>

##### SecureBootEnabled · record 属性

```csharp
public bool SecureBootEnabled { get; init; }
```

安全启动是否启用。

<a id="api-3370dfebfef4e9e2"></a>

##### SecureBootTemplateId · record 属性

```csharp
public string SecureBootTemplateId { get; init; }
```

当前安全启动模板 GUID 字符串。

<a id="api-87fe8e0917911350"></a>

##### Shielded · record 属性

```csharp
public bool Shielded { get; init; }
```

ShieldingRequested 配置标记，表示请求屏蔽保护；不证明来宾已通过证明或保护已生效。

<a id="api-a2a39ae8943176b4"></a>

##### TpmEnabled · record 属性

```csharp
public bool TpmEnabled { get; init; }
```

虚拟 TPM 是否启用。

<a id="api-2b7baa8a8ea0e2bc"></a>

### `VmSpacetimeService` 类

管理检查点、回退及历史磁盘临时挂接；相关操作可能修改配置和磁盘文件。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 检查点；管理层面 / 虚拟机 / 修改 / 检查点 |
| 源文件 | ` Management/VirtualMachines/Checkpoints/VmSpacetimeService.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<ApiResponse> AnnihilateAsync(Guid vmId, SpacetimeNode node) `](#api-14255e699db3a71f) | 删除目标检查点及其全部后代检查点。 |
| [` public static bool CanCreateMoment(bool checkpointsEnabled, IEnumerable<SpacetimeNode>? nodes) `](#api-b14757a9dfd6ac57) | 检查是否允许创建检查点，要求已启用检查点且没有打开的虫洞。 |
| [` public static bool CanMutateHistoricalNode(SpacetimeNode? node, IEnumerable<SpacetimeNode>? nodes) `](#api-501503fc23323a0a) | 判断历史检查点是否能执行修改，要求节点有效且没有打开的虫洞。 |
| [` public static bool CanOperateHistoricalNode(SpacetimeNode? node) `](#api-0c5a1fddbe95fa8c) | 判断节点是否代表可操作的历史检查点。 |
| [` public static Task<ApiResponse> CaptureMomentAsync(Guid vmId, SpacetimeMode mode, VmScreenshotImage? externalThumb = null, Func<VmScreenshotImage, byte[]>? encodeThumbnail = null) `](#api-20cbc9fb4959abe9) | 创建检查点并按需缓存缩略图；离线目标采用标准检查点。 |
| [` public static Task<ApiResponse> CloseWormholeAsync(Guid vmId, SpacetimeNode node) `](#api-b2269ed4e50933e1) | 移除历史磁盘挂接、删除临时盘并恢复历史磁盘名称。 |
| [` public static Task<ApiResponse> ConvergeAsync(Guid vmId, SpacetimeNode node) `](#api-24ca250382fac3e4) | 删除并合并单个历史检查点，保留后继检查点关系。 |
| [` public static async Task<ApiResponse<bool>> GetCheckpointsEnabledAsync(Guid vmId) `](#api-c085c1456fbb0dd4) | 按稳定身份读取虚拟机检查点是否启用。 |
| [` public static async Task<ApiResponse<List<SpacetimeNode>>> GetSpacetimeNodesAsync(Guid vmId, Func<VmScreenshotImage, byte[]>? encodeThumbnail = null) `](#api-c53de2ebe0dc12ab) | 按稳定身份读取检查点树、当前状态及历史磁盘挂接信息。 |
| [` public static bool HasOpenWormhole(IEnumerable<SpacetimeNode>? nodes) `](#api-fe99c3f3519670dc) | 检查节点集合中是否存在已打开的历史磁盘挂接。 |
| [` public static Task<ApiResponse> OpenWormholeAsync(Guid vmId, SpacetimeNode targetNode) `](#api-872492e48a171d3a) | 将指定历史节点的磁盘临时挂接到同一虚拟机。 |
| [` public static Task<ApiResponse> RenameSnapshotAsync(Guid vmId, SpacetimeNode node, string newName) `](#api-e53cd949791f2cbd) | 修改属于目标虚拟机的检查点名称。 |
| [` public static Task<ApiResponse> SetCheckpointsEnabledAsync(Guid vmId, bool enabled) `](#api-1496418acc73bbde) | 按稳定身份设置虚拟机是否允许使用检查点。 |
| [` public static Task<ApiResponse> TeleportAsync(SpacetimeNode node, Guid vmId, IEnumerable<SpacetimeNode>? nodes = null) `](#api-f3267d42a777e344) | 关闭当前历史磁盘挂接，应用检查点，并完成流程要求的重新启动。 |

#### 详细说明

```csharp
public static class VmSpacetimeService
```

#### 静态方法说明

<a id="api-14255e699db3a71f"></a>

##### AnnihilateAsync · 方法

```csharp
public static Task<ApiResponse> AnnihilateAsync(Guid vmId, SpacetimeNode node);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

删除目标检查点及其全部后代检查点。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `node` | 要删除的历史节点；执行前核对归属及历史磁盘挂接互锁。 |

返回：删除结果；删除成功不能通过本响应撤销。

<a id="api-b14757a9dfd6ac57"></a>

##### CanCreateMoment · 方法

```csharp
public static bool CanCreateMoment(bool checkpointsEnabled, IEnumerable<SpacetimeNode>? nodes);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

检查是否允许创建检查点，要求已启用检查点且没有打开的虫洞。

| 参数 | 说明 |
| --- | --- |
| `checkpointsEnabled` | 当前是否允许使用检查点。 |
| `nodes` | 检查点和当前状态节点的快照；允许 null 的规则入口按没有节点处理。 |

返回：当前输入满足条件时为 true；不执行主机检查。

<a id="api-501503fc23323a0a"></a>

##### CanMutateHistoricalNode · 方法

```csharp
public static bool CanMutateHistoricalNode(SpacetimeNode? node, IEnumerable<SpacetimeNode>? nodes);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

判断历史检查点是否能执行修改，要求节点有效且没有打开的虫洞。

| 参数 | 说明 |
| --- | --- |
| `node` | 目标历史检查点或已打开的历史磁盘挂接节点。 |
| `nodes` | 检查点和当前状态节点的快照；允许 null 的规则入口按没有节点处理。 |

返回：当前输入满足条件时为 true。

<a id="api-0c5a1fddbe95fa8c"></a>

##### CanOperateHistoricalNode · 方法

```csharp
public static bool CanOperateHistoricalNode(SpacetimeNode? node);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

判断节点是否代表可操作的历史检查点。

| 参数 | 说明 |
| --- | --- |
| `node` | 目标历史检查点或已打开的历史磁盘挂接节点。 |

返回：仅非空且 NodeType 为 Snapshot 时返回 true；Genesis 和 Current 均不满足。

<a id="api-20cbc9fb4959abe9"></a>

##### CaptureMomentAsync · 方法

```csharp
public static Task<ApiResponse> CaptureMomentAsync(Guid vmId, SpacetimeMode mode, VmScreenshotImage? externalThumb = null, Func<VmScreenshotImage, byte[]>? encodeThumbnail = null);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

创建检查点并按需缓存缩略图；离线目标采用标准检查点。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `mode` | Continuous 请求标准检查点；Still 仅在 Running 状态请求生产检查点，其他状态请求标准检查点。生产请求失败时保留失败结果。 |
| `externalThumb` | 可选外部截图；未提供时尝试捕获当前截图。 |
| `encodeThumbnail` | 可选缩略图编码器。 |

返回：检查点创建结果；截图缓存缺失不否定已完成的检查点创建。临时切换检查点策略后恢复原值；OutcomeUnknown 时保留现场及原策略值，须核对原作业后再恢复。

<a id="api-b2269ed4e50933e1"></a>

##### CloseWormholeAsync · 方法

```csharp
public static Task<ApiResponse> CloseWormholeAsync(Guid vmId, SpacetimeNode node);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

移除历史磁盘挂接、删除临时盘并恢复历史磁盘名称。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `node` | 要关闭挂接的历史节点；执行前重新核对当前实际挂接。 |

返回：清理结果；失败时保留可供重试的节点状态。

<a id="api-24ca250382fac3e4"></a>

##### ConvergeAsync · 方法

```csharp
public static Task<ApiResponse> ConvergeAsync(Guid vmId, SpacetimeNode node);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

删除并合并单个历史检查点，保留后继检查点关系。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `node` | 要合并的历史节点；执行前核对归属及历史磁盘挂接互锁。 |

返回：合并结果；成功不能通过本响应撤销。

<a id="api-c085c1456fbb0dd4"></a>

##### GetCheckpointsEnabledAsync · 方法

```csharp
public static async Task<ApiResponse<bool>> GetCheckpointsEnabledAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 检查点。

按稳定身份读取虚拟机检查点是否启用。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |

返回：成功时返回实际设置；配置缺失或查询失败返回错误，不推断为启用。

<a id="api-c53de2ebe0dc12ab"></a>

##### GetSpacetimeNodesAsync · 方法

```csharp
public static async Task<ApiResponse<List<SpacetimeNode>>> GetSpacetimeNodesAsync(Guid vmId, Func<VmScreenshotImage, byte[]>? encodeThumbnail = null);
```

功能归属：管理层面 / 虚拟机 / 查询 / 检查点。

按稳定身份读取检查点树、当前状态及历史磁盘挂接信息。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `encodeThumbnail` | 可选缩略图编码器；提供时可能更新缩略图缓存。 |

返回：查询成功的完整节点列表，或明确的读取失败；失败不会伪装成没有检查点。

<a id="api-fe99c3f3519670dc"></a>

##### HasOpenWormhole · 方法

```csharp
public static bool HasOpenWormhole(IEnumerable<SpacetimeNode>? nodes);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

检查节点集合中是否存在已打开的历史磁盘挂接。

| 参数 | 说明 |
| --- | --- |
| `nodes` | 检查点和当前状态节点的快照；允许 null 的规则入口按没有节点处理。 |

返回：存在打开的虫洞时为 true；null 集合为 false。

<a id="api-872492e48a171d3a"></a>

##### OpenWormholeAsync · 方法

```csharp
public static Task<ApiResponse> OpenWormholeAsync(Guid vmId, SpacetimeNode targetNode);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

将指定历史节点的磁盘临时挂接到同一虚拟机。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `targetNode` | 来自该虚拟机的历史节点；执行前重新核对身份和当前磁盘关系。 |

返回：挂接结果；失败可能发生在部分文件和配置变更之后。

调用约定：只处理节点 VhdPath 指定的一个磁盘，不挂接该检查点的全部磁盘。流程可能将 .avhdx 改名为 _renamed.vhdx，再创建同目录 _wormhole_tmp.vhdx 差分盘并挂到 SCSI；关闭时移除临时盘并恢复文件名。当前运行磁盘父链上的节点不能这样挂接。



临时盘或改名目标已存在时返回冲突并保留文件，不凭固定文件名认定旧文件属于本次操作。创建结果尚未确认时保留现场；失败清理只删除本次已确认创建的临时盘。

<a id="api-e53cd949791f2cbd"></a>

##### RenameSnapshotAsync · 方法

```csharp
public static Task<ApiResponse> RenameSnapshotAsync(Guid vmId, SpacetimeNode node, string newName);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

修改属于目标虚拟机的检查点名称。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `node` | 目标历史节点；执行前重新核对归属。 |
| `newName` | 非空的新显示名称。 |

返回：重命名结果。

<a id="api-1496418acc73bbde"></a>

##### SetCheckpointsEnabledAsync · 方法

```csharp
public static Task<ApiResponse> SetCheckpointsEnabledAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

按稳定身份设置虚拟机是否允许使用检查点。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `enabled` | true 启用检查点，false 禁用。 |

返回：设置结果；错误包含实际查询或提交原因。

<a id="api-f3267d42a777e344"></a>

##### TeleportAsync · 方法

```csharp
public static Task<ApiResponse> TeleportAsync(SpacetimeNode node, Guid vmId, IEnumerable<SpacetimeNode>? nodes = null);
```

功能归属：管理层面 / 虚拟机 / 修改 / 检查点。

关闭当前历史磁盘挂接，应用检查点，并完成流程要求的重新启动。

| 参数 | 说明 |
| --- | --- |
| `node` | 要应用的历史节点。 |
| `vmId` | 目标虚拟机的非空 GUID。 |
| `nodes` | 可选的调用方节点快照，用于同步本次清理后的挂接状态。 |

返回：整个应用及必要重启结果；Steps 记录 CloseWormhole（ResourceId 为节点 ID）、Save、Apply、Start 及失败的状态检查 StateBeforeApply/StateBeforeRestart。未执行的步骤不列出。后续失败不会撤销已经完成的步骤。

调用约定：开始时处于运行或暂停状态的虚拟机在应用后会被启动；暂停状态不会原样恢复。其他初始状态不要求重启。

<a id="api-dbd3558483ca1d26"></a>

### `VmStorageAddPreparation` record 类

存储请求的准备结果，区分可执行请求与校验或目录操作错误。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmStorageService.Planning.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmStorageAddPreparation(VmStorageAddRequest? Request, string? Error = null, bool IsError = false) `](#api-3f2737c8fc914abf) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? Error { get; init; } `](#api-a9af7275602f252d) | 错误说明；没有报告错误时为 null。 |
| [` public bool IsError { get; init; } `](#api-b8da95c3de82a28a) | 是否属于准备目录等执行错误，而非普通输入校验提示。 |
| [` public VmStorageAddRequest? Request { get; init; } `](#api-a52a148ab82f32d8) | 校验后可执行的存储请求；准备失败时为 null。 |
| [` public bool Success { get; } `](#api-d2350f8dc2a2c283) | Request 不为 null，表示准备完成。 |

#### 详细说明

```csharp
public sealed record VmStorageAddPreparation(VmStorageAddRequest? Request, string? Error = null, bool IsError = false)
```

| 参数 | 说明 |
| --- | --- |
| `Request` | 校验后可执行的存储请求；准备失败时为 null。 |
| `Error` | 错误说明；没有报告错误时为 null。 |
| `IsError` | 是否属于准备目录等执行错误，而非普通输入校验提示。 |

#### 构造函数说明

<a id="api-3f2737c8fc914abf"></a>

##### VmStorageAddPreparation · 主构造器

```csharp
public VmStorageAddPreparation(VmStorageAddRequest? Request, string? Error = null, bool IsError = false);
```

| 参数 | 说明 |
| --- | --- |
| `Request` | 校验后可执行的存储请求；准备失败时为 null。 |
| `Error` | 错误说明；没有报告错误时为 null。 |
| `IsError` | 是否属于准备目录等执行错误，而非普通输入校验提示。 |

#### 属性说明

<a id="api-a9af7275602f252d"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

错误说明；没有报告错误时为 null。

<a id="api-b8da95c3de82a28a"></a>

##### IsError · record 属性

```csharp
public bool IsError { get; init; }
```

是否属于准备目录等执行错误，而非普通输入校验提示。

<a id="api-a52a148ab82f32d8"></a>

##### Request · record 属性

```csharp
public VmStorageAddRequest? Request { get; init; }
```

校验后可执行的存储请求；准备失败时为 null。

<a id="api-d2350f8dc2a2c283"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

Request 不为 null，表示准备完成。

<a id="api-889fbbb37ca39d32"></a>

### `VmStorageAddRequest` record 类

新增存储设备的完整请求；可先准备校验，再交给 AddDriveAsync 执行。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmStorageService.Planning.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public uint? BlockSizeBytes { get; init; } `](#api-36dbecf89029991d) | 新虚拟磁盘的块大小，单位为字节；null 使用后端默认值，指定时必须大于零。 |
| [` public int ControllerNumber { get; init; } `](#api-0da4752c9dd03602) | 从零开始的控制器编号；IDE 为 0 至 1，SCSI 为 0 至 3。 |
| [` public string ControllerType { get; init; } = "SCSI" `](#api-5c0014cffc268583) | 控制器类型字符串，默认 SCSI；也支持 IDE。 |
| [` public string DriveType { get; init; } = "HardDisk" `](#api-871c671f6cf72b23) | 驱动类型字符串 HardDisk 或 DvdDrive，默认 HardDisk。 |
| [` public bool IsNew { get; init; } `](#api-97462253184ba3a6) | 是否先创建新 VHD 或 ISO；物理设备来源不创建新介质。 |
| [` public bool IsPhysical { get; init; } `](#api-bc2edba6e7954ff5) | 是否挂接宿主物理设备。 |
| [` public string? IsoOutputPath { get; init; } `](#api-57cf2c8f85805930) | 新建 ISO 的输出文件路径；其他模式可为 null。 |
| [` public string? IsoSourcePath { get; init; } `](#api-112ecbd03c4e65e7) | 制作 ISO 时的源目录；非新建光盘模式可为 null。 |
| [` public string? IsoVolumeLabel { get; init; } `](#api-0c976de18eb04a85) | 新 ISO 的卷标；null 使用制作器默认值，其他来源必须为 null。 |
| [` public int Location { get; init; } `](#api-fdf92506efb42f98) | 控制器内从零开始的槽位；IDE 为 0 至 1，SCSI 为 0 至 63。 |
| [` public string? ParentPath { get; init; } `](#api-e09fce4f2b87d2f0) | 新建差分磁盘的父磁盘路径；其他模式可为 null。 |
| [` public string? PathOrNumber { get; init; } `](#api-8f95b1b405fece14) | 介质文件路径或物理设备编号字符串；现有虚拟光驱可为空表示无介质。 |
| [` public HostDiskInfo? PhysicalDisk { get; init; } `](#api-9d66098f29435d6a) | GetHostDisksAsync 取得的物理硬盘快照；必须包含编号、正数字节容量及唯一标识或序列号。 |
| [` public string SectorFormat { get; init; } = "Default" `](#api-337d1b1b04ab0532) | 新虚拟磁盘扇区格式：Default、512n、512e 或 4kn；非新建虚拟磁盘仅允许 Default。 |
| [` public int? SizeGiB { get; init; } `](#api-602751760bb56101) | 新建非差分虚拟磁盘的容量，单位为 GiB（1024&#179; 字节）；其余来源必须为 null。 |
| [` public string VhdType { get; init; } = "Dynamic" `](#api-760986605d426a74) | 新虚拟磁盘类型，默认 Dynamic；差分磁盘使用 Differencing。 |

#### 详细说明

```csharp
public sealed record VmStorageAddRequest
```

调用约定：现有虚拟磁盘或 ISO 使用 PathOrNumber；空虚拟光驱允许空路径。新 VHD 使用容量、格式及可选父盘，新 ISO 使用来源目录和输出路径。物理硬盘必须提供 PhysicalDisk，物理光驱使用查询所得 PNP 标识；互斥来源参数会被拒绝。

#### 属性说明

<a id="api-36dbecf89029991d"></a>

##### BlockSizeBytes · 属性

```csharp
public uint? BlockSizeBytes { get; init; }
```

新虚拟磁盘的块大小，单位为字节；null 使用后端默认值，指定时必须大于零。

<a id="api-0da4752c9dd03602"></a>

##### ControllerNumber · 属性

```csharp
public int ControllerNumber { get; init; }
```

从零开始的控制器编号；IDE 为 0 至 1，SCSI 为 0 至 3。

<a id="api-5c0014cffc268583"></a>

##### ControllerType · 属性

```csharp
public string ControllerType { get; init; } = "SCSI";
```

控制器类型字符串，默认 SCSI；也支持 IDE。

<a id="api-871c671f6cf72b23"></a>

##### DriveType · 属性

```csharp
public string DriveType { get; init; } = "HardDisk";
```

驱动类型字符串 HardDisk 或 DvdDrive，默认 HardDisk。

<a id="api-97462253184ba3a6"></a>

##### IsNew · 属性

```csharp
public bool IsNew { get; init; }
```

是否先创建新 VHD 或 ISO；物理设备来源不创建新介质。

<a id="api-bc2edba6e7954ff5"></a>

##### IsPhysical · 属性

```csharp
public bool IsPhysical { get; init; }
```

是否挂接宿主物理设备。

<a id="api-57cf2c8f85805930"></a>

##### IsoOutputPath · 属性

```csharp
public string? IsoOutputPath { get; init; }
```

新建 ISO 的输出文件路径；其他模式可为 null。

<a id="api-112ecbd03c4e65e7"></a>

##### IsoSourcePath · 属性

```csharp
public string? IsoSourcePath { get; init; }
```

制作 ISO 时的源目录；非新建光盘模式可为 null。

<a id="api-0c976de18eb04a85"></a>

##### IsoVolumeLabel · 属性

```csharp
public string? IsoVolumeLabel { get; init; }
```

新 ISO 的卷标；null 使用制作器默认值，其他来源必须为 null。

<a id="api-fdf92506efb42f98"></a>

##### Location · 属性

```csharp
public int Location { get; init; }
```

控制器内从零开始的槽位；IDE 为 0 至 1，SCSI 为 0 至 63。

<a id="api-e09fce4f2b87d2f0"></a>

##### ParentPath · 属性

```csharp
public string? ParentPath { get; init; }
```

新建差分磁盘的父磁盘路径；其他模式可为 null。

<a id="api-8f95b1b405fece14"></a>

##### PathOrNumber · 属性

```csharp
public string? PathOrNumber { get; init; }
```

介质文件路径或物理设备编号字符串；现有虚拟光驱可为空表示无介质。

<a id="api-9d66098f29435d6a"></a>

##### PhysicalDisk · 属性

```csharp
public HostDiskInfo? PhysicalDisk { get; init; }
```

GetHostDisksAsync 取得的物理硬盘快照；必须包含编号、正数字节容量及唯一标识或序列号。执行时重新核对所有已提供的身份字段和容量，不信任 CanPassthrough 显示标记。

<a id="api-337d1b1b04ab0532"></a>

##### SectorFormat · 属性

```csharp
public string SectorFormat { get; init; } = "Default";
```

新虚拟磁盘扇区格式：Default、512n、512e 或 4kn；非新建虚拟磁盘仅允许 Default。

<a id="api-602751760bb56101"></a>

##### SizeGiB · 属性

```csharp
public int? SizeGiB { get; init; }
```

新建非差分虚拟磁盘的容量，单位为 GiB（1024&#179; 字节）；其余来源必须为 null。

<a id="api-760986605d426a74"></a>

##### VhdType · 属性

```csharp
public string VhdType { get; init; } = "Dynamic";
```

新虚拟磁盘类型，默认 Dynamic；差分磁盘使用 Differencing。

<a id="api-e23425c9bb2d0bc5"></a>

### `VmStorageFields` 类

存储的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 存储 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Storage/VmStorageFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<string> AttachmentId `](#api-d0730c38c1a22ea6) | 挂接身份：存储挂接槽位的标识，用于关联本次查询中的设备记录。 |
| [` public static readonly VmQueryField<long> CapacityBytes `](#api-4b1182beb09d2a44) | 磁盘虚拟容量：磁盘向来宾提供的容量，单位为字节；物理直通盘返回对应宿主磁盘容量。 |
| [` public static readonly VmQueryField<int> ControllerLocation `](#api-ae813c2650489917) | 控制器槽位：设备在所属控制器上的槽位编号，从 0 开始。 |
| [` public static readonly VmQueryField<int> ControllerNumber `](#api-6dec1c0b93401211) | 控制器编号：同类存储控制器中的编号，从 0 开始。 |
| [` public static readonly VmQueryField<string> ControllerType `](#api-a9cb687d9fa61d81) | 控制器类型：挂接槽位所属的控制器类型，例如 IDE 或 SCSI。 |
| [` public static readonly VmQueryField<string> DiskModel `](#api-d9e0b68e39f5d623) | 宿主磁盘型号：物理直通盘对应的宿主磁盘型号。 |
| [` public static readonly VmQueryField<int> DiskNumber `](#api-ef04025f80055eb9) | 宿主物理磁盘编号：物理直通盘对应的宿主磁盘编号。 |
| [` public static readonly VmQueryField<string> DiskType `](#api-718dccc22f411245) | 挂接来源类型：槽位的介质来源类型：虚拟磁盘、物理磁盘或空槽位。 |
| [` public static readonly VmQueryField<string> DriveType `](#api-bd6c9f99df57f35f) | 驱动器类型：挂接槽位的驱动器类型：硬盘或光驱。 |
| [` public static readonly VmQueryField<long> FileSizeBytes `](#api-4be25331f6b1240c) | 磁盘文件占用：虚拟磁盘映像文件当前的长度，单位为字节。 |
| [` public static readonly VmQueryField<bool> IsPassthroughStale `](#api-0b703edfa9822c90) | 物理磁盘挂接失效：当前物理磁盘挂接目标是否已从 Hyper-V 物理磁盘清单中消失。 |
| [` public static readonly VmQueryField<string> Path `](#api-deb0ab5b07658929) | 挂接目标路径：槽位当前挂接的映像文件路径或宿主设备引用。 |
| [` public static readonly VmQueryField<string> PnpDeviceId `](#api-a620c20cce4cdaff) | 宿主磁盘设备身份：物理直通盘对应的宿主 PnP 设备标识。 |
| [` public static readonly VmQueryField<long> ReadBytesPerSecond `](#api-537997a327a2db1c) | 磁盘读取速率：磁盘当前的读取速率，单位为字节每秒。 |
| [` public static readonly VmQueryField<string> SerialNumber `](#api-f136f8ff301b9625) | 宿主磁盘序列号：物理直通盘对应的宿主磁盘序列号。 |
| [` public static readonly VmQueryField<string> VirtualDiskType `](#api-4b7c31cc73cad6ba) | 虚拟磁盘格式类型：虚拟磁盘映像的格式类型名称，例如固定、动态或差分。 |
| [` public static readonly VmQueryField<long> WriteBytesPerSecond `](#api-d5e61ae4a382ef8a) | 磁盘写入速率：磁盘当前的写入速率，单位为字节每秒。 |

#### 详细说明

```csharp
public static class VmStorageFields
```

#### 字段与常量说明

<a id="api-d0730c38c1a22ea6"></a>

##### AttachmentId · 字段

```csharp
public static readonly VmQueryField<string> AttachmentId;
```

存储挂接槽位的标识，用于关联本次查询中的设备记录。

<a id="api-4b1182beb09d2a44"></a>

##### CapacityBytes · 字段

```csharp
public static readonly VmQueryField<long> CapacityBytes;
```

磁盘向来宾提供的容量，单位为字节；物理直通盘返回对应宿主磁盘容量。

**取值说明：** 单位为字节；例如 `1073741824` 表示 1 GiB。

**适用条件：** 适用于虚拟磁盘和物理直通盘；空槽位返回 NotApplicable。

<a id="api-ae813c2650489917"></a>

##### ControllerLocation · 字段

```csharp
public static readonly VmQueryField<int> ControllerLocation;
```

设备在所属控制器上的槽位编号。

**取值说明：** 从 `0` 开始的整数编号。

<a id="api-6dec1c0b93401211"></a>

##### ControllerNumber · 字段

```csharp
public static readonly VmQueryField<int> ControllerNumber;
```

同类存储控制器中的编号。

**取值说明：** 从 `0` 开始的整数编号。

<a id="api-a9cb687d9fa61d81"></a>

##### ControllerType · 字段

```csharp
public static readonly VmQueryField<string> ControllerType;
```

挂接槽位所属的控制器类型。

**取值说明：** `IDE` 或 `SCSI`。

<a id="api-d9e0b68e39f5d623"></a>

##### DiskModel · 字段

```csharp
public static readonly VmQueryField<string> DiskModel;
```

物理直通盘对应的宿主磁盘型号。

**适用条件：** 适用于物理直通盘。

<a id="api-ef04025f80055eb9"></a>

##### DiskNumber · 字段

```csharp
public static readonly VmQueryField<int> DiskNumber;
```

物理直通盘对应的宿主磁盘编号。

**取值说明：** 非负整数，例如 `2` 对应宿主的磁盘 2。

**适用条件：** 适用于物理直通盘。目标当前是否仍存在可读取 IsPassthroughStale。

<a id="api-718dccc22f411245"></a>

##### DiskType · 字段

```csharp
public static readonly VmQueryField<string> DiskType;
```

槽位的介质来源类型：虚拟磁盘、物理磁盘或空槽位。

**取值说明：** Virtual：文件映像；Physical：物理设备；Empty：没有介质。

<a id="api-bd6c9f99df57f35f"></a>

##### DriveType · 字段

```csharp
public static readonly VmQueryField<string> DriveType;
```

挂接槽位的驱动器类型：硬盘或光驱。

**取值说明：** HardDisk：硬盘；DvdDrive：光驱。光驱可以没有挂载介质。

<a id="api-4be25331f6b1240c"></a>

##### FileSizeBytes · 字段

```csharp
public static readonly VmQueryField<long> FileSizeBytes;
```

映像文件的当前长度，可用于展示文件大小；文件系统实际分配空间需另行查询。

**取值说明：** 单位为字节；例如 `1073741824` 表示 1 GiB。

**适用条件：** 适用于虚拟磁盘文件。

<a id="api-0b703edfa9822c90"></a>

##### IsPassthroughStale · 字段

```csharp
public static readonly VmQueryField<bool> IsPassthroughStale;
```

当前物理磁盘挂接目标是否已从 Hyper-V 物理磁盘清单中消失。

**取值说明：** `true`：目标磁盘已不在宿主清单中；`false`：仍在清单中。

**适用条件：** 适用于物理直通盘。

**缺测与错误：** 无法读取宿主物理磁盘清单时返回该来源的失败状态。

<a id="api-deb0ab5b07658929"></a>

##### Path · 字段

```csharp
public static readonly VmQueryField<string> Path;
```

槽位当前挂接的映像文件路径或宿主设备引用。

**取值说明：** 空槽位返回空字符串。

<a id="api-a620c20cce4cdaff"></a>

##### PnpDeviceId · 字段

```csharp
public static readonly VmQueryField<string> PnpDeviceId;
```

物理直通盘对应的宿主 PnP 设备标识。

**适用条件：** 适用于物理直通盘。

<a id="api-537997a327a2db1c"></a>

##### ReadBytesPerSecond · 字段

```csharp
public static readonly VmQueryField<long> ReadBytesPerSecond;
```

磁盘当前的读取速率。

**取值说明：** 单位为字节/秒；例如 `1048576` 表示 1 MiB/s。

<a id="api-f136f8ff301b9625"></a>

##### SerialNumber · 字段

```csharp
public static readonly VmQueryField<string> SerialNumber;
```

物理直通盘对应的宿主磁盘序列号。

**适用条件：** 适用于物理直通盘；返回值已去除首尾空白。

<a id="api-4b7c31cc73cad6ba"></a>

##### VirtualDiskType · 字段

```csharp
public static readonly VmQueryField<string> VirtualDiskType;
```

虚拟磁盘映像的格式类型名称，例如固定、动态或差分。

**适用条件：** 适用于虚拟磁盘文件。

<a id="api-d5e61ae4a382ef8a"></a>

##### WriteBytesPerSecond · 字段

```csharp
public static readonly VmQueryField<long> WriteBytesPerSecond;
```

磁盘当前的写入速率。

**取值说明：** 单位为字节/秒；例如 `1048576` 表示 1 MiB/s。

<a id="api-7599034e4f313f52"></a>

### `VmStorageGlobalFields` 类

存储的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 存储 |
| 源文件 | ` Management/VirtualMachines/Query/Fields/Storage/VmStorageGlobalFields.cs ` |

#### 成员概览

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly VmQueryField<bool> AllowFullScsiCommandSet `](#api-f34a88079ea3232d) | 完整 SCSI 命令集：是否为整台虚拟机开放完整 SCSI 命令集。 |

#### 详细说明

```csharp
public static class VmStorageGlobalFields
```

#### 字段与常量说明

<a id="api-f34a88079ea3232d"></a>

##### AllowFullScsiCommandSet · 字段

```csharp
public static readonly VmQueryField<bool> AllowFullScsiCommandSet;
```

是否为整台虚拟机开放完整 SCSI 命令集。

**取值说明：** `true`：开放完整 SCSI 命令集；`false`：不开放。

**适用条件：** 虚拟机级全局配置，通过虚拟机记录读取。

<a id="api-7974d06edc0418d4"></a>

### `VmStorageService` 类

读取与配置虚拟机存储挂接，处理虚拟磁盘、光驱和物理磁盘。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 查询 / 存储；管理层面 / 虚拟机 / 修改 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmStorageService.Planning.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static Task<ApiResponse<VmStorageSlot>> AddDriveAsync(Guid vmId, VmStorageAddRequest request) `](#api-baa4e2878599d828) | 按完整请求创建或挂接虚拟磁盘、光驱或物理磁盘，并返回实际挂接位置。 |
| [` public static bool CanEditStoragePath(VmStorageAttachment? item) `](#api-e8e3fc55c8135b30) | 判断该存储挂接是否支持更换文件路径。 |
| [` public static async Task<ApiResponse> CompactDiskAsync(string vhdPath) `](#api-ad2ff3fe6a3fba22) | 请求 Hyper-V 压缩虚拟磁盘文件的物理占用。 |
| [` public static async Task<ApiResponse<VmStorageSlot>> DetachPhysicalDiskAsync(Guid vmId, HostDiskInfo disk) `](#api-3baa807ff7202cde) | 按物理磁盘查询快照核对身份，并从指定虚拟机移除其直通挂接。 |
| [` public static async Task<ApiResponse<List<VmStorageAttachment>>> FindStalePassthroughDisksAsync(Guid vmId) `](#api-bda4c2d7782ca304) | 查找主机物理磁盘身份或编号已变化的直通挂接，供启动修复流程处理。 |
| [` public static string? GetAddSlotError(int generation, bool isRunning, VmStorageAddRequest request, IEnumerable<VmStorageAttachment> items) `](#api-e4e83234f4558d6c) | 校验新增设备请求的控制器类型、编号、槽位及运行状态限制。 |
| [` public static IReadOnlyList<int> GetAvailableLocations(string type, int number, IEnumerable<VmStorageAttachment> items) `](#api-5be5567371bf61be) | 从已有挂接快照中排除占用位置，计算指定控制器的空闲槽位。 |
| [` public static VmStorageSlotPlan GetBestSlot(int generation, bool isRunning, string driveType, IEnumerable<VmStorageAttachment> items) `](#api-88765d16d58e5609) | 按当前规则和已有设备快照选择首个适合新增驱动的空闲槽位。 |
| [` public static string? GetControllerFullError(string type, int number, IEnumerable<VmStorageAttachment> items) `](#api-36bd6baf81450e53) | 检查指定控制器是否仍有可用槽位。 |
| [` public static IReadOnlyList<int> GetControllerNumbers(string type) `](#api-5c99c297a915c534) | 返回指定控制器类型可选择的从零开始的编号。 |
| [` public static IReadOnlyList<string> GetControllerTypes(int generation, bool isRunning, string driveType) `](#api-02528d20642aad87) | 根据代数、运行状态和驱动类型列出允许添加的控制器类型。 |
| [` public static string? GetEditStoragePathError(VmStorageAttachment item, bool isRunning) `](#api-598076cb947f091d) | 检查指定设备在当前运行状态下能否更换介质路径。 |
| [` public static string? GetMediaPathError(string? newPath) `](#api-87444f817bed9ba9) | 校验介质路径是否存在；空路径按弹出介质处理。 |
| [` public static int GetNewDiskSize(string? input) `](#api-57998fd260a9bee9) | 将新磁盘容量文本解析为正整数 GiB。 |
| [` public static VmStorageSourceChoice GetSourceChoice(int generation, bool physical, string driveType, bool isNew) `](#api-3e54a0ff198bf9c4) | 按代数、物理磁盘标记和介质类型规范化新增设备来源选项。 |
| [` public static Task<IReadOnlyDictionary<string, long>> GetVirtualDiskSizesAsync(IEnumerable<string> paths) `](#api-173f032235ecbf39) | 批量读取磁盘文件当前的文件长度，不是虚拟磁盘声明的最大容量。 |
| [` public static async Task<ApiResponse<List<VmStorageAttachment>>> GetVmStorageItemsAsync(Guid vmId) `](#api-a36775377d1b6bb8) | 读取虚拟机控制器上的虚拟磁盘、物理直通磁盘及光驱挂接。 |
| [` public static Task<ApiResponse> ModifyDvdDrivePathAsync(Guid vmId, VmStorageAttachment drive, string newIsoPath) `](#api-f8e5cfee1814c647) | 替换查询时选中的虚拟光驱介质；空路径弹出介质，保留驱动器。 |
| [` public static Task<ApiResponse> ModifyHardDrivePathAsync(Guid vmId, VmStorageAttachment drive, string newPath) `](#api-c58ebeff6db40a24) | 替换查询时选中的虚拟硬盘驱动器所引用的介质路径。 |
| [` public static VmStorageAddPreparation PrepareAddRequest(VmStorageAddRequest request, int generation, bool isRunning, IEnumerable<VmStorageAttachment> items) `](#api-6df673aadf44ba9d) | 规范化并验证新增设备请求；ISO 制作路径需要时会创建输出目录。 |
| [` public static async Task<ApiResponse> RemoveDriveAsync(Guid vmId, VmStorageAttachment drive) `](#api-ac191a2f4bce7362) | 从指定虚拟机移除查询时选中的存储挂接和介质；不删除虚拟磁盘文件。 |
| [` public static Task<ApiResponse> SetFullScsiCommandSetEnabledAsync(Guid vmId, bool enabled) `](#api-23305d2d162e210d) | 设置整台虚拟机是否允许完整 SCSI 命令集；此开关不属于单个存储设备。 |

#### 详细说明

```csharp
public static partial class VmStorageService
```

#### 静态方法说明

<a id="api-baa4e2878599d828"></a>

##### AddDriveAsync · 方法

```csharp
public static Task<ApiResponse<VmStorageSlot>> AddDriveAsync(Guid vmId, VmStorageAddRequest request);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

按完整请求创建或挂接虚拟磁盘、光驱或物理磁盘，并返回实际挂接位置。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID；执行和槽位回查均按此身份定位。 |
| `request` | 完整来源、创建选项及目标槽位。物理硬盘必须携带查询所得身份和字节容量，提交时重新核对。 |

返回：成功时 Data 为实际槽位；失败时 ErrorKind 区分输入错误、对象缺失、身份冲突或执行失败。

调用约定：不会仅凭物理磁盘编号挂接；系统盘和启动盘始终拒绝。失败清理只接受本次创建回执确认的资源，不按数量变化或槽位推断归属。作业结果或资源归属不明时保留资源并返回 OutcomeUnknown；已确认失败但存在外来子设备或清理未完成时返回 CleanupRequired。两种情况下都不将可能仍在用的物理盘恢复联机。设备查询与系统调用不构成原子硬件锁。

<a id="api-e8e3fc55c8135b30"></a>

##### CanEditStoragePath · 方法

```csharp
public static bool CanEditStoragePath(VmStorageAttachment? item);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

判断该存储挂接是否支持更换文件路径。

| 参数 | 说明 |
| --- | --- |
| `item` | 目标存储挂接；允许 null 的规则入口会返回不允许编辑。 |

返回：非空且不是物理磁盘等不可换路径项时为 true。

<a id="api-ad2ff3fe6a3fba22"></a>

##### CompactDiskAsync · 方法

```csharp
public static async Task<ApiResponse> CompactDiskAsync(string vhdPath);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

请求 Hyper-V 压缩虚拟磁盘文件的物理占用。

| 参数 | 说明 |
| --- | --- |
| `vhdPath` | 待压缩的虚拟磁盘文件路径。 |

返回：压缩结果；操作改变磁盘文件，能否执行取决于主机和磁盘当前占用状态。

<a id="api-3baa807ff7202cde"></a>

##### DetachPhysicalDiskAsync · 方法

```csharp
public static async Task<ApiResponse<VmStorageSlot>> DetachPhysicalDiskAsync(Guid vmId, HostDiskInfo disk);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

按物理磁盘查询快照核对身份，并从指定虚拟机移除其直通挂接。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `disk` | GetHostDisksAsync 取得的物理硬盘快照；必须提供身份和容量，执行前重新核对。 |

返回：成功时包含原控制器类型、编号和槽位，供重新挂接使用；失败不返回默认槽位。

<a id="api-bda4c2d7782ca304"></a>

##### FindStalePassthroughDisksAsync · 方法

```csharp
public static async Task<ApiResponse<List<VmStorageAttachment>>> FindStalePassthroughDisksAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 存储。

查找主机物理磁盘身份或编号已变化的直通挂接，供启动修复流程处理。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：成功时为需要核对或重新挂接的磁盘列表；查询失败与成功空列表分开表示，此方法本身不执行修复。

调用约定：宿主磁盘映射读取成功后才判断失效；读取失败直接返回错误。失效表示当前挂接引用不再匹配，不等于已经证明物理设备被拔出。

<a id="api-e4e83234f4558d6c"></a>

##### GetAddSlotError · 方法

```csharp
public static string? GetAddSlotError(int generation, bool isRunning, VmStorageAddRequest request, IEnumerable<VmStorageAttachment> items);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

校验新增设备请求的控制器类型、编号、槽位及运行状态限制。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |
| `request` | 待检查的新增存储设备请求。 |
| `items` | 当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。 |

返回：首个错误说明；符合当前快照时为 null。执行时需重新核对槽位。

<a id="api-5be5567371bf61be"></a>

##### GetAvailableLocations · 方法

```csharp
public static IReadOnlyList<int> GetAvailableLocations(string type, int number, IEnumerable<VmStorageAttachment> items);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

从已有挂接快照中排除占用位置，计算指定控制器的空闲槽位。

| 参数 | 说明 |
| --- | --- |
| `type` | 控制器类型 IDE 或 SCSI。 |
| `number` | 从零开始的控制器编号。 |
| `items` | 当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。 |

返回：从零开始的槽位列表；不会预留位置。

<a id="api-88765d16d58e5609"></a>

##### GetBestSlot · 方法

```csharp
public static VmStorageSlotPlan GetBestSlot(int generation, bool isRunning, string driveType, IEnumerable<VmStorageAttachment> items);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

按当前规则和已有设备快照选择首个适合新增驱动的空闲槽位。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |
| `driveType` | 驱动类型 HardDisk 或 DvdDrive。 |
| `items` | 当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。 |

返回：可用槽位，或没有槽位时的错误说明。

<a id="api-36bd6baf81450e53"></a>

##### GetControllerFullError · 方法

```csharp
public static string? GetControllerFullError(string type, int number, IEnumerable<VmStorageAttachment> items);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

检查指定控制器是否仍有可用槽位。

| 参数 | 说明 |
| --- | --- |
| `type` | 控制器类型 IDE 或 SCSI。 |
| `number` | 从零开始的控制器编号。 |
| `items` | 当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。 |

返回：已满时返回说明，否则为 null。

<a id="api-5c99c297a915c534"></a>

##### GetControllerNumbers · 方法

```csharp
public static IReadOnlyList<int> GetControllerNumbers(string type);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

返回指定控制器类型可选择的从零开始的编号。

| 参数 | 说明 |
| --- | --- |
| `type` | 控制器类型 IDE 或 SCSI。 |

返回：IDE 为 0、1，SCSI 为 0 至 3。

<a id="api-02528d20642aad87"></a>

##### GetControllerTypes · 方法

```csharp
public static IReadOnlyList<string> GetControllerTypes(int generation, bool isRunning, string driveType);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

根据代数、运行状态和驱动类型列出允许添加的控制器类型。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |
| `driveType` | 驱动类型 HardDisk 或 DvdDrive。 |

返回：IDE 或 SCSI 类型列表；仅计算规则，不创建控制器。

<a id="api-598076cb947f091d"></a>

##### GetEditStoragePathError · 方法

```csharp
public static string? GetEditStoragePathError(VmStorageAttachment item, bool isRunning);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

检查指定设备在当前运行状态下能否更换介质路径。

| 参数 | 说明 |
| --- | --- |
| `item` | 目标存储挂接；允许 null 的规则入口会返回不允许编辑。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |

返回：限制说明；允许继续时为 null。

<a id="api-87444f817bed9ba9"></a>

##### GetMediaPathError · 方法

```csharp
public static string? GetMediaPathError(string? newPath);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

校验介质路径是否存在；空路径按弹出介质处理。

| 参数 | 说明 |
| --- | --- |
| `newPath` | 要引用的介质文件路径；允许空值的光驱校验入口将其视为无介质。 |

返回：非空但不存在的路径返回说明，否则为 null。

<a id="api-57998fd260a9bee9"></a>

##### GetNewDiskSize · 方法

```csharp
public static int GetNewDiskSize(string? input);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

将新磁盘容量文本解析为正整数 GiB。

| 参数 | 说明 |
| --- | --- |
| `input` | 以 GiB（1024&#179; 字节）为单位的容量文本；null 或无效输入使用 128。 |

返回：有效正数；为空、格式错误或非正数时返回 128。

<a id="api-3e54a0ff198bf9c4"></a>

##### GetSourceChoice · 方法

```csharp
public static VmStorageSourceChoice GetSourceChoice(int generation, bool physical, string driveType, bool isNew);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

按代数、物理磁盘标记和介质类型规范化新增设备来源选项。

| 参数 | 说明 |
| --- | --- |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `physical` | 是否选择宿主物理设备来源。 |
| `driveType` | 驱动类型 HardDisk 或 DvdDrive。 |
| `isNew` | 是否先创建新 VHD 或 ISO；默认 false 表示复用现有来源。 |

返回：允许的驱动类型、是否创建新介质以及是否支持光驱。

<a id="api-173f032235ecbf39"></a>

##### GetVirtualDiskSizesAsync · 方法

```csharp
public static Task<IReadOnlyDictionary<string, long>> GetVirtualDiskSizesAsync(IEnumerable<string> paths);
```

功能归属：管理层面 / 虚拟机 / 查询 / 存储。

批量读取磁盘文件当前的文件长度，不是虚拟磁盘声明的最大容量。

| 参数 | 说明 |
| --- | --- |
| `paths` | 待查询文件长度的磁盘路径集合。 |

返回：路径到文件字节数的映射；未读取成功的路径不在结果中，不应将缺少的项视为零字节。

<a id="api-a36775377d1b6bb8"></a>

##### GetVmStorageItemsAsync · 方法

```csharp
public static async Task<ApiResponse<List<VmStorageAttachment>>> GetVmStorageItemsAsync(Guid vmId);
```

功能归属：管理层面 / 虚拟机 / 查询 / 存储。

读取虚拟机控制器上的虚拟磁盘、物理直通磁盘及光驱挂接。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |

返回：挂接列表或查询错误；控制器编号与槽位使用从零开始的索引。

调用约定：存在物理磁盘挂接时，宿主磁盘映射或身份读取失败会返回查询错误，不把缺测当成直通盘失效。

<a id="api-f8e5cfee1814c647"></a>

##### ModifyDvdDrivePathAsync · 方法

```csharp
public static Task<ApiResponse> ModifyDvdDrivePathAsync(Guid vmId, VmStorageAttachment drive, string newIsoPath);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

替换查询时选中的虚拟光驱介质；空路径弹出介质，保留驱动器。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `drive` | 存储查询返回的光驱，原样回传 DeviceToken；不以显示槽位重新定位。 |
| `newIsoPath` | 新的 ISO 文件路径；空字符串用于弹出介质。 |

返回：更新结果；设备身份变化时返回 Conflict，未知底层结果不自动重试。

<a id="api-c58ebeff6db40a24"></a>

##### ModifyHardDrivePathAsync · 方法

```csharp
public static Task<ApiResponse> ModifyHardDrivePathAsync(Guid vmId, VmStorageAttachment drive, string newPath);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

替换查询时选中的虚拟硬盘驱动器所引用的介质路径。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `drive` | 存储查询返回的硬盘驱动器，原样回传 DeviceToken。 |
| `newPath` | 新的虚拟磁盘路径；沿用介质入口的空路径移除介质行为，不删除文件。 |

返回：更新结果；只修改原驱动器，不改同槽位的替代设备。

调用约定：设备身份摘要不比较旧介质路径。同一驱动器仍采用本次提交覆盖；WMI 不提供跨查询和提交的原子身份锁，完全相同身份的外部重建无法区分。

<a id="api-6df673aadf44ba9d"></a>

##### PrepareAddRequest · 方法

```csharp
public static VmStorageAddPreparation PrepareAddRequest(VmStorageAddRequest request, int generation, bool isRunning, IEnumerable<VmStorageAttachment> items);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

规范化并验证新增设备请求；ISO 制作路径需要时会创建输出目录。

| 参数 | 说明 |
| --- | --- |
| `request` | 待检查的新增存储设备请求。 |
| `generation` | 虚拟机代数，使用 1 或 2。 |
| `isRunning` | 当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。 |
| `items` | 当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。 |

返回：可执行请求或错误、提示状态；不会创建虚拟机设备，但可能创建宿主目录。

<a id="api-ac191a2f4bce7362"></a>

##### RemoveDriveAsync · 方法

```csharp
public static async Task<ApiResponse> RemoveDriveAsync(Guid vmId, VmStorageAttachment drive);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

从指定虚拟机移除查询时选中的存储挂接和介质；不删除虚拟磁盘文件。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID，必须与产生选择摘要的查询一致。 |
| `drive` | 存储查询返回的设备，须原样回传 SelectionToken；可序列化后回传。位置及显示字段不作为替代定位依据。 |

返回：移除结果；旧摘要、设备或介质已变化时返回冲突。失败可能发生在介质已移除但槽位仍保留之后。

调用约定：缺少身份摘要不会退回按槽位移除。执行前再次核对原对象；WMI 不提供原子比较删除，不能保证识别外部在最终核对后替换或按完全相同配置重建的对象。

<a id="api-23305d2d162e210d"></a>

##### SetFullScsiCommandSetEnabledAsync · 方法

```csharp
public static Task<ApiResponse> SetFullScsiCommandSetEnabledAsync(Guid vmId, bool enabled);
```

功能归属：管理层面 / 虚拟机 / 修改 / 存储。

设置整台虚拟机是否允许完整 SCSI 命令集；此开关不属于单个存储设备。

| 参数 | 说明 |
| --- | --- |
| `vmId` | 目标虚拟机的稳定 GUID。 |
| `enabled` | true 启用，false 关闭。 |

返回：配置提交结果；属性不受支持或执行条件不满足时返回失败。

<a id="api-cc76a9e383bed2e6"></a>

### `VmStorageSlot` record 类

控制器类型、从零开始的控制器编号和槽位。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmStorageService.Planning.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmStorageSlot(string ControllerType, int ControllerNumber, int Location) `](#api-4d5af89550ffb9b7) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int ControllerNumber { get; init; } `](#api-8a2b7348dbfa1acd) | 从零开始的控制器编号。 |
| [` public string ControllerType { get; init; } `](#api-b65b0b179b01f44c) | 控制器类型，IDE 或 SCSI。 |
| [` public int Location { get; init; } `](#api-4d624de2f6f1c00d) | 从零开始的控制器槽位。 |

#### 详细说明

```csharp
public sealed record VmStorageSlot(string ControllerType, int ControllerNumber, int Location)
```

| 参数 | 说明 |
| --- | --- |
| `ControllerType` | 控制器类型，IDE 或 SCSI。 |
| `ControllerNumber` | 从零开始的控制器编号。 |
| `Location` | 从零开始的控制器槽位。 |

#### 构造函数说明

<a id="api-4d5af89550ffb9b7"></a>

##### VmStorageSlot · 主构造器

```csharp
public VmStorageSlot(string ControllerType, int ControllerNumber, int Location);
```

| 参数 | 说明 |
| --- | --- |
| `ControllerType` | 控制器类型，IDE 或 SCSI。 |
| `ControllerNumber` | 从零开始的控制器编号。 |
| `Location` | 从零开始的控制器槽位。 |

#### 属性说明

<a id="api-8a2b7348dbfa1acd"></a>

##### ControllerNumber · record 属性

```csharp
public int ControllerNumber { get; init; }
```

从零开始的控制器编号。

<a id="api-b65b0b179b01f44c"></a>

##### ControllerType · record 属性

```csharp
public string ControllerType { get; init; }
```

控制器类型，IDE 或 SCSI。

<a id="api-4d624de2f6f1c00d"></a>

##### Location · record 属性

```csharp
public int Location { get; init; }
```

从零开始的控制器槽位。

<a id="api-2cd3bdb239cc100c"></a>

### `VmStorageSlotPlan` record 类

新增设备的槽位选择结果；无可用槽位时携带说明。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmStorageService.Planning.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmStorageSlotPlan(VmStorageSlot? Slot, string? Error = null) `](#api-1bdb7e70c3aa0e43) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? Error { get; init; } `](#api-6a0b90ae7c24dc92) | 错误说明；没有报告错误时为 null。 |
| [` public VmStorageSlot? Slot { get; init; } `](#api-3bd4b1b8105a7c44) | 选定槽位；无法分配时为 null。 |

#### 详细说明

```csharp
public sealed record VmStorageSlotPlan(VmStorageSlot? Slot, string? Error = null)
```

| 参数 | 说明 |
| --- | --- |
| `Slot` | 选定槽位；无法分配时为 null。 |
| `Error` | 错误说明；没有报告错误时为 null。 |

#### 构造函数说明

<a id="api-1bdb7e70c3aa0e43"></a>

##### VmStorageSlotPlan · 主构造器

```csharp
public VmStorageSlotPlan(VmStorageSlot? Slot, string? Error = null);
```

| 参数 | 说明 |
| --- | --- |
| `Slot` | 选定槽位；无法分配时为 null。 |
| `Error` | 错误说明；没有报告错误时为 null。 |

#### 属性说明

<a id="api-6a0b90ae7c24dc92"></a>

##### Error · record 属性

```csharp
public string? Error { get; init; }
```

错误说明；没有报告错误时为 null。

<a id="api-3bd4b1b8105a7c44"></a>

##### Slot · record 属性

```csharp
public VmStorageSlot? Slot { get; init; }
```

选定槽位；无法分配时为 null。

<a id="api-50d2022f49af22c1"></a>

### `VmStorageSourceChoice` record 类

按代数与物理设备限制规范化后的介质选择。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 存储 |
| 源文件 | ` Management/VirtualMachines/Storage/VmStorageService.Planning.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmStorageSourceChoice(string DriveType, bool IsNew, bool SupportsOptical) `](#api-1c3293bf556cfed0) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DriveType { get; init; } `](#api-998fa8a2f7cbe7e8) | 规范化后的 HardDisk 或 DvdDrive。 |
| [` public bool IsNew { get; init; } `](#api-71a9b3418343f250) | 是否先创建新介质。 |
| [` public bool SupportsOptical { get; init; } `](#api-cf3441c4b0e9abbe) | 该来源和代数是否支持光驱选项。 |

#### 详细说明

```csharp
public sealed record VmStorageSourceChoice(string DriveType, bool IsNew, bool SupportsOptical)
```

| 参数 | 说明 |
| --- | --- |
| `DriveType` | 规范化后的 HardDisk 或 DvdDrive。 |
| `IsNew` | 是否先创建新介质。 |
| `SupportsOptical` | 该来源和代数是否支持光驱选项。 |

#### 构造函数说明

<a id="api-1c3293bf556cfed0"></a>

##### VmStorageSourceChoice · 主构造器

```csharp
public VmStorageSourceChoice(string DriveType, bool IsNew, bool SupportsOptical);
```

| 参数 | 说明 |
| --- | --- |
| `DriveType` | 规范化后的 HardDisk 或 DvdDrive。 |
| `IsNew` | 是否先创建新介质。 |
| `SupportsOptical` | 该来源和代数是否支持光驱选项。 |

#### 属性说明

<a id="api-998fa8a2f7cbe7e8"></a>

##### DriveType · record 属性

```csharp
public string DriveType { get; init; }
```

规范化后的 HardDisk 或 DvdDrive。

<a id="api-71a9b3418343f250"></a>

##### IsNew · record 属性

```csharp
public bool IsNew { get; init; }
```

是否先创建新介质。

<a id="api-cf3441c4b0e9abbe"></a>

##### SupportsOptical · record 属性

```csharp
public bool SupportsOptical { get; init; }
```

该来源和代数是否支持光驱选项。

<a id="api-406890c926a27d9e"></a>

### `VmmsImageInfo` record 类

VMMS 程序文件的路径与存在状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmmsImageInfo(VmmsImageState State, string? ResolvedPath, ApiFailure? Failure = null) `](#api-7824943cb68b5733) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ApiFailure? Failure { get; init; } `](#api-0b2286b6783ae330) | 读取失败的原始信息；路径无法解析且没有系统错误时为 null。 |
| [` public string? ResolvedPath { get; init; } `](#api-abaf3c9b5b5544dd) | VMMS 程序文件在宿主上的完整路径；未能解析时为 null。 |
| [` public VmmsImageState State { get; init; } `](#api-e667643b7f77bccf) | 路径检查结论。 |

#### 详细说明

```csharp
public sealed record VmmsImageInfo(VmmsImageState State, string? ResolvedPath, ApiFailure? Failure = null)
```

| 参数 | 说明 |
| --- | --- |
| `State` | 路径检查结论。 |
| `ResolvedPath` | VMMS 程序文件在宿主上的完整路径；未能解析时为 null。 |
| `Failure` | 读取失败的原始信息；路径无法解析且没有系统错误时为 null。 |

#### 构造函数说明

<a id="api-7824943cb68b5733"></a>

##### VmmsImageInfo · 主构造器

```csharp
public VmmsImageInfo(VmmsImageState State, string? ResolvedPath, ApiFailure? Failure = null);
```

| 参数 | 说明 |
| --- | --- |
| `State` | 路径检查结论。 |
| `ResolvedPath` | VMMS 程序文件在宿主上的完整路径；未能解析时为 null。 |
| `Failure` | 读取失败的原始信息；路径无法解析且没有系统错误时为 null。 |

#### 属性说明

<a id="api-0b2286b6783ae330"></a>

##### Failure · record 属性

```csharp
public ApiFailure? Failure { get; init; }
```

读取失败的原始信息；路径无法解析且没有系统错误时为 null。

<a id="api-abaf3c9b5b5544dd"></a>

##### ResolvedPath · record 属性

```csharp
public string? ResolvedPath { get; init; }
```

VMMS 程序文件在宿主上的完整路径；未能解析时为 null。

<a id="api-e667643b7f77bccf"></a>

##### State · record 属性

```csharp
public VmmsImageState State { get; init; }
```

路径检查结论。

<a id="api-897673c08040ffe4"></a>

### `VmmsImageState` 枚举

VMMS 程序路径的诊断状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Exists = 1 `](#api-1322bd4fd0b1f208) | 已解析为本机绝对路径，并确认该路径是文件。 |
| [` Missing = 2 `](#api-e17b8d5a70c76759) | 文件系统明确报告文件或所在目录不存在。 |
| [` NotChecked = 0 `](#api-c789775c88cb86dd) | 本次没有检查程序路径。 |
| [` Unknown = 3 `](#api-68331d864647ce76) | 路径含糊、格式无效或读取失败，不能认定文件缺失。 |

#### 详细说明

```csharp
public enum VmmsImageState
```

#### 枚举值说明

<a id="api-1322bd4fd0b1f208"></a>

##### Exists · 枚举值

```csharp
Exists = 1
```

已解析为本机绝对路径，并确认该路径是文件。

<a id="api-e17b8d5a70c76759"></a>

##### Missing · 枚举值

```csharp
Missing = 2
```

文件系统明确报告文件或所在目录不存在。

<a id="api-c789775c88cb86dd"></a>

##### NotChecked · 枚举值

```csharp
NotChecked = 0
```

本次没有检查程序路径。

<a id="api-68331d864647ce76"></a>

##### Unknown · 枚举值

```csharp
Unknown = 3
```

路径含糊、格式无效或读取失败，不能认定文件缺失。

<a id="api-7f0ea6f367307b55"></a>

### `VmmsServiceInfo` record 类

VMMS Windows 服务的状态、启动配置与程序信息。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Services ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 宿主 / 查询 / Hyper-V |
| 源文件 | ` Management/Host/HyperV/HyperVEnvironmentStatus.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmmsServiceInfo(string? State, string? StartMode, uint? ExitCode, uint? ServiceSpecificExitCode, uint? ProcessId, string? PathName, VmmsImageInfo Image) `](#api-646a62ae88cd9085) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public uint? ExitCode { get; init; } `](#api-9183c4c4e75c0be4) | Win32\_Service.ExitCode 原始值；未上报时为 null。 |
| [` public VmmsImageInfo Image { get; init; } `](#api-a3d1b0d95ed74a60) | 程序路径解析及文件查询结果。 |
| [` public string? PathName { get; init; } `](#api-e0eacebe04f64745) | VMMS 服务配置的程序命令行，包含程序路径及启动参数。 |
| [` public uint? ProcessId { get; init; } `](#api-327d8285176a95c2) | VMMS 进程编号；未取得时为 null。 |
| [` public uint? ServiceSpecificExitCode { get; init; } `](#api-36b6a5de1d3e3095) | 服务特定退出码；未上报时为 null。 |
| [` public string? StartMode { get; init; } `](#api-9f1a87a0897acaf6) | 启动类型，例如 Auto、Manual、Disabled。 |
| [` public string? State { get; init; } `](#api-efa1c3d700396342) | Windows 服务状态，例如 Running、Stopped；未取得时为 null。 |

#### 详细说明

```csharp
public sealed record VmmsServiceInfo(string? State, string? StartMode, uint? ExitCode,
    uint? ServiceSpecificExitCode, uint? ProcessId, string? PathName, VmmsImageInfo Image)
```

| 参数 | 说明 |
| --- | --- |
| `State` | Windows 服务状态，例如 Running、Stopped；未取得时为 null。 |
| `StartMode` | 启动类型，例如 Auto、Manual、Disabled。 |
| `ExitCode` | Win32_Service.ExitCode 原始值；未上报时为 null。 |
| `ServiceSpecificExitCode` | 服务特定退出码；未上报时为 null。 |
| `ProcessId` | VMMS 进程编号；未取得时为 null。 |
| `PathName` | VMMS 服务配置的程序命令行，包含程序路径及启动参数。 |
| `Image` | 程序路径解析及文件查询结果。 |

#### 构造函数说明

<a id="api-646a62ae88cd9085"></a>

##### VmmsServiceInfo · 主构造器

```csharp
public VmmsServiceInfo(string? State, string? StartMode, uint? ExitCode, uint? ServiceSpecificExitCode, uint? ProcessId, string? PathName, VmmsImageInfo Image);
```

| 参数 | 说明 |
| --- | --- |
| `State` | Windows 服务状态，例如 Running、Stopped；未取得时为 null。 |
| `StartMode` | 启动类型，例如 Auto、Manual、Disabled。 |
| `ExitCode` | Win32_Service.ExitCode 原始值；未上报时为 null。 |
| `ServiceSpecificExitCode` | 服务特定退出码；未上报时为 null。 |
| `ProcessId` | VMMS 进程编号；未取得时为 null。 |
| `PathName` | VMMS 服务配置的程序命令行，包含程序路径及启动参数。 |
| `Image` | 程序路径解析及文件查询结果。 |

#### 属性说明

<a id="api-9183c4c4e75c0be4"></a>

##### ExitCode · record 属性

```csharp
public uint? ExitCode { get; init; }
```

Win32_Service.ExitCode 原始值；未上报时为 null。

<a id="api-a3d1b0d95ed74a60"></a>

##### Image · record 属性

```csharp
public VmmsImageInfo Image { get; init; }
```

程序路径解析及文件查询结果。

<a id="api-e0eacebe04f64745"></a>

##### PathName · record 属性

```csharp
public string? PathName { get; init; }
```

VMMS 服务配置的程序命令行，包含程序路径及启动参数。

<a id="api-327d8285176a95c2"></a>

##### ProcessId · record 属性

```csharp
public uint? ProcessId { get; init; }
```

VMMS 进程编号；未取得时为 null。

<a id="api-36b6a5de1d3e3095"></a>

##### ServiceSpecificExitCode · record 属性

```csharp
public uint? ServiceSpecificExitCode { get; init; }
```

服务特定退出码；未上报时为 null。

<a id="api-9f1a87a0897acaf6"></a>

##### StartMode · record 属性

```csharp
public string? StartMode { get; init; }
```

启动类型，例如 Auto、Manual、Disabled。

<a id="api-efa1c3d700396342"></a>

##### State · record 属性

```csharp
public string? State { get; init; }
```

Windows 服务状态，例如 Running、Stopped；未取得时为 null。

<a id="api-972639a130eb0cc2"></a>

### `ApiErrorKind` 枚举

与底层错误码来源无关的失败类别；调用方可据此处理业务分支。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiResponse.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` AccessDenied = 7 `](#api-d15596b7521e4dc5) | 当前调用方缺少完成操作的权限。 |
| [` AmbiguousTarget = 3 `](#api-43f2af5c671191c3) | 名称或选择条件匹配多个对象，无法确定唯一目标。 |
| [` Cancelled = 11 `](#api-16ab72c26271c17f) | 操作已取消或在提交前被取消；仅停止等待且无法确认底层结果时使用 OutcomeUnknown。 |
| [` CleanupRequired = 9 `](#api-9e6b9889c75f950e) | 操作产生了尚未清理的资源或状态，需要先处理清理结果。 |
| [` Conflict = 5 `](#api-ac9e9663edfb4ded) | 对象身份或状态已变化，与本次请求冲突。 |
| [` InvalidArgument = 2 `](#api-e42dc6d9dda74822) | 请求缺少必需信息，或参数组合不合法。 |
| [` InvalidState = 6 `](#api-0fff1aa06e727c90) | 对象当前状态不允许执行请求的操作。 |
| [` None = 0 `](#api-20e4be046ac21141) | 操作成功，没有错误。 |
| [` NotFound = 4 `](#api-d35fb89b7937c3d9) | 请求的对象不存在或已不可查询。 |
| [` OperationFailed = 10 `](#api-d1957b6b975a71ae) | 执行操作失败；不表示此前步骤没有产生副作用。 |
| [` OutcomeUnknown = 12 `](#api-090555baed70183a) | 请求已经提交或可能已提交，但最终结果或资源状态尚未确认；底层可能仍在执行，不得据此直接重试或清理。 |
| [` Unspecified = 1 `](#api-d2ae60112dbe461f) | 失败尚未归入更具体的类别，不应按错误文案猜测。 |
| [` Unsupported = 8 `](#api-e854c90f87820512) | 当前宿主或目标对象不支持请求的能力。 |

#### 详细说明

```csharp
public enum ApiErrorKind
```

#### 枚举值说明

<a id="api-d15596b7521e4dc5"></a>

##### AccessDenied · 枚举值

```csharp
AccessDenied = 7
```

当前调用方缺少完成操作的权限。

<a id="api-43f2af5c671191c3"></a>

##### AmbiguousTarget · 枚举值

```csharp
AmbiguousTarget = 3
```

名称或选择条件匹配多个对象，无法确定唯一目标。

<a id="api-16ab72c26271c17f"></a>

##### Cancelled · 枚举值

```csharp
Cancelled = 11
```

操作已取消或在提交前被取消；仅停止等待且无法确认底层结果时使用 OutcomeUnknown。

<a id="api-9e6b9889c75f950e"></a>

##### CleanupRequired · 枚举值

```csharp
CleanupRequired = 9
```

操作产生了尚未清理的资源或状态，需要先处理清理结果。

<a id="api-ac9e9663edfb4ded"></a>

##### Conflict · 枚举值

```csharp
Conflict = 5
```

对象身份或状态已变化，与本次请求冲突。

<a id="api-e42dc6d9dda74822"></a>

##### InvalidArgument · 枚举值

```csharp
InvalidArgument = 2
```

请求缺少必需信息，或参数组合不合法。

<a id="api-0fff1aa06e727c90"></a>

##### InvalidState · 枚举值

```csharp
InvalidState = 6
```

对象当前状态不允许执行请求的操作。

<a id="api-20e4be046ac21141"></a>

##### None · 枚举值

```csharp
None = 0
```

操作成功，没有错误。

<a id="api-d35fb89b7937c3d9"></a>

##### NotFound · 枚举值

```csharp
NotFound = 4
```

请求的对象不存在或已不可查询。

<a id="api-d1957b6b975a71ae"></a>

##### OperationFailed · 枚举值

```csharp
OperationFailed = 10
```

执行操作失败；不表示此前步骤没有产生副作用。

<a id="api-090555baed70183a"></a>

##### OutcomeUnknown · 枚举值

```csharp
OutcomeUnknown = 12
```

请求已经提交或可能已提交，但最终结果或资源状态尚未确认；底层可能仍在执行，不得据此直接重试或清理。

<a id="api-d2ae60112dbe461f"></a>

##### Unspecified · 枚举值

```csharp
Unspecified = 1
```

失败尚未归入更具体的类别，不应按错误文案猜测。

<a id="api-e854c90f87820512"></a>

##### Unsupported · 枚举值

```csharp
Unsupported = 8
```

当前宿主或目标对象不支持请求的能力。

<a id="api-acee855ddcf0dd3d"></a>

### `ApiErrorSource` 枚举

操作响应中数值错误码所属的来源，需结合具体方法解释。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiResponse.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Com = 4 `](#api-0c753437c3cb89f1) | COM/WinRT 调用失败。 |
| [` Hcs = 3 `](#api-291b4a85c1d92094) | Host Compute Service 接口失败。 |
| [` None = 0 `](#api-4b07c5f901d7b76a) | 未指定原生来源，通常为参数、业务或托管失败。 |
| [` Win32 = 2 `](#api-dd74d22d9cb33bc1) | Windows 原生接口错误；具体方法可能使用 Win32、配置管理器或 DISM 码。 |
| [` Wmi = 1 `](#api-3122618ee2c61a4b) | WMI/CIM 查询或方法调用失败。 |

#### 详细说明

```csharp
public enum ApiErrorSource
```

#### 枚举值说明

<a id="api-0c753437c3cb89f1"></a>

##### Com · 枚举值

```csharp
Com = 4
```

COM/WinRT 调用失败。

<a id="api-291b4a85c1d92094"></a>

##### Hcs · 枚举值

```csharp
Hcs = 3
```

Host Compute Service 接口失败。

<a id="api-4b07c5f901d7b76a"></a>

##### None · 枚举值

```csharp
None = 0
```

未指定原生来源，通常为参数、业务或托管失败。

<a id="api-dd74d22d9cb33bc1"></a>

##### Win32 · 枚举值

```csharp
Win32 = 2
```

Windows 原生接口错误；具体方法可能使用 Win32、配置管理器或 DISM 码。

<a id="api-3122618ee2c61a4b"></a>

##### Wmi · 枚举值

```csharp
Wmi = 1
```

WMI/CIM 查询或方法调用失败。

<a id="api-ed6980c68f413cc1"></a>

### `ApiFailure` record 类

可序列化的失败信息；分类和原始错误码用于程序判断，Message 用于显示。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiFailure.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public ApiFailure(ApiErrorKind Kind, int Code, ApiErrorSource Source, string Message) `](#api-1306a5ccc8b6b459) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Code { get; init; } `](#api-22dddabd6b7fe853) | 原始接口错误码；没有原始码时为 -1。 |
| [` public ApiErrorKind Kind { get; init; } `](#api-8bd98aa8be804067) | 业务错误类别。 |
| [` public string Message { get; init; } `](#api-421a4c5dfb2403bc) | 面向人的说明，不作为稳定匹配条件。 |
| [` public ApiErrorSource Source { get; init; } `](#api-ca9aad6ebc85d4fa) | 错误码所属接口。 |

#### 详细说明

```csharp
public sealed record ApiFailure(ApiErrorKind Kind, int Code, ApiErrorSource Source, string Message)
```

| 参数 | 说明 |
| --- | --- |
| `Kind` | 业务错误类别。 |
| `Code` | 原始接口错误码；没有原始码时为 -1。 |
| `Source` | 错误码所属接口。 |
| `Message` | 面向人的说明，不作为稳定匹配条件。 |

#### 构造函数说明

<a id="api-1306a5ccc8b6b459"></a>

##### ApiFailure · 主构造器

```csharp
public ApiFailure(ApiErrorKind Kind, int Code, ApiErrorSource Source, string Message);
```

| 参数 | 说明 |
| --- | --- |
| `Kind` | 业务错误类别。 |
| `Code` | 原始接口错误码；没有原始码时为 -1。 |
| `Source` | 错误码所属接口。 |
| `Message` | 面向人的说明，不作为稳定匹配条件。 |

#### 属性说明

<a id="api-22dddabd6b7fe853"></a>

##### Code · record 属性

```csharp
public int Code { get; init; }
```

原始接口错误码；没有原始码时为 -1。

<a id="api-8bd98aa8be804067"></a>

##### Kind · record 属性

```csharp
public ApiErrorKind Kind { get; init; }
```

业务错误类别。

<a id="api-421a4c5dfb2403bc"></a>

##### Message · record 属性

```csharp
public string Message { get; init; }
```

面向人的说明，不作为稳定匹配条件。

<a id="api-ca9aad6ebc85d4fa"></a>

##### Source · record 属性

```csharp
public ApiErrorSource Source { get; init; }
```

错误码所属接口。

<a id="api-422efc6c1478ebfe"></a>

### `ApiResponse` 类

表示不携带数据的操作结果，提供成功标志及失败信息。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiResponse.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Code { get; } `](#api-38efe1de89d31e9e) | 原生或业务错误码；含义由 ErrorSource 和具体方法决定，未指定时通常为 -1。 |
| [` public string Error { get; } = string.Empty `](#api-94a85ddbff6b6faa) | 面向人的失败说明，可能本地化，不宜按文本判断错误类别。 |
| [` public ApiErrorKind ErrorKind { get; } `](#api-2248bf934335f9fb) | 业务失败类别；成功时为 None，尚未分类的失败为 Unspecified。 |
| [` public ApiErrorSource ErrorSource { get; } `](#api-8440bfb969ffd704) | 错误码所属接口来源。 |
| [` public Exception? Exception { get; } `](#api-966a961f0a8cf7a6) | 失败时保留的原始异常；未提供时为 null。 |
| [` public bool RequiresRestart { get; } `](#api-0087ba1268ce915f) | 本次操作已确认需要重启宿主；失败响应也可能保留此前成功步骤的重启要求。 |
| [` public IReadOnlyList<RestartReason> RestartReasons { get; } = Array.Empty<RestartReason>() `](#api-af1755b963651324) | 本次操作及已记录步骤明确报告的重启原因；按首次出现顺序保存，只读且去重。 |
| [` public IReadOnlyList<OperationStepResult> Steps { get; } = Array.Empty<OperationStepResult>() `](#api-c0a7bebd2c4b1238) | 接口已记录的修改步骤，按执行顺序排列；空列表表示该入口不提供步骤记录，不表示没有副作用。 |
| [` public bool Success { get; } `](#api-b3895a620b9929fd) | 当前操作是否报告成功；具体成功条件见对应方法。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static ApiResponse Fail(string error, int code = -1, ApiErrorSource source = ApiErrorSource.None, Exception? exception = null, ApiErrorKind kind = ApiErrorKind.Unspecified) `](#api-0a0e41c001f1d02c) | 创建失败响应并写入调试输出，不抛出传入异常。 |
| [` public static ApiResponse Ok() `](#api-308387f87df9c37a) | 创建不携带数据的成功响应。 |
| [` public static implicit operator bool (ApiResponse? response) `](#api-8b7b70e45ccc66eb) | 将响应按是否成功转换为布尔值；响应对象为 null 时为 false。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public override string ToString() `](#api-8481a5cc2fd10c46) | 生成用于诊断的响应摘要，不是稳定的数据交换格式。 |

#### 详细说明

```csharp
public sealed class ApiResponse
```

调用约定：使用 Success 判断操作结果；失败时读取 ErrorKind 和 Error。Steps 提供入口记录的执行步骤，RequiresRestart 与 RestartReasons 提供本次操作报告的宿主重启要求。

#### 属性说明

<a id="api-38efe1de89d31e9e"></a>

##### Code · 属性

```csharp
public int Code { get; }
```

原生或业务错误码；含义由 ErrorSource 和具体方法决定，未指定时通常为 -1。

<a id="api-94a85ddbff6b6faa"></a>

##### Error · 属性

```csharp
public string Error { get; } = string.Empty;
```

面向人的失败说明，可能本地化，不宜按文本判断错误类别。

<a id="api-2248bf934335f9fb"></a>

##### ErrorKind · 属性

```csharp
public ApiErrorKind ErrorKind { get; }
```

业务失败类别；成功时为 None，尚未分类的失败为 Unspecified。

<a id="api-8440bfb969ffd704"></a>

##### ErrorSource · 属性

```csharp
public ApiErrorSource ErrorSource { get; }
```

错误码所属接口来源。

<a id="api-966a961f0a8cf7a6"></a>

##### Exception · 属性

```csharp
public Exception? Exception { get; }
```

失败时保留的原始异常；未提供时为 null。

<a id="api-0087ba1268ce915f"></a>

##### RequiresRestart · 属性

```csharp
public bool RequiresRestart { get; }
```

本次操作已确认需要重启宿主；失败响应也可能保留此前成功步骤的重启要求。

调用约定：仅提供重启状态的接口设置此标志；false 不代表对整个系统不存在待重启事项作出保证。本对象不会触发重启。

<a id="api-af1755b963651324"></a>

##### RestartReasons · 属性

```csharp
public IReadOnlyList<RestartReason> RestartReasons { get; } = Array.Empty<RestartReason>();
```

本次操作及已记录步骤明确报告的重启原因；按首次出现顺序保存，只读且去重。

调用约定：只有重启布尔标记的旧入口可能没有原因。本列表不会推断系统其他操作的重启需求。

<a id="api-c0a7bebd2c4b1238"></a>

##### Steps · 属性

```csharp
public IReadOnlyList<OperationStepResult> Steps { get; } = Array.Empty<OperationStepResult>();
```

接口已记录的修改步骤，按执行顺序排列；空列表表示该入口不提供步骤记录，不表示没有副作用。

调用约定：步骤标识及范围见具体方法；未开始的步骤可能不在列表中。此列表不代替重新查询实际资源。

<a id="api-b3895a620b9929fd"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

当前操作是否报告成功；具体成功条件见对应方法。

#### 静态方法说明

<a id="api-0a0e41c001f1d02c"></a>

##### Fail · 方法

```csharp
public static ApiResponse Fail(string error, int code = -1, ApiErrorSource source = ApiErrorSource.None, Exception? exception = null, ApiErrorKind kind = ApiErrorKind.Unspecified);
```

创建失败响应并写入调试输出，不抛出传入异常。

| 参数 | 说明 |
| --- | --- |
| `error` | 可供人阅读的错误说明。 |
| `code` | 接口或业务错误码；默认 -1。 |
| `source` | 错误码来源；默认未分类。 |
| `exception` | 可选原始异常，保存在响应中供诊断使用。 |
| `kind` | 失败类别；None 或未定义值按 Unspecified 处理。 |

返回：保存错误信息的失败响应。

<a id="api-308387f87df9c37a"></a>

##### Ok · 方法

```csharp
public static ApiResponse Ok();
```

创建不携带数据的成功响应。

返回：新的成功响应。

<a id="api-8b7b70e45ccc66eb"></a>

##### op_Implicit · 方法

```csharp
public static implicit operator bool (ApiResponse? response);
```

将响应按是否成功转换为布尔值；响应对象为 null 时为 false。

| 参数 | 说明 |
| --- | --- |
| `response` | 待判断响应，可为 null。 |

返回：响应非 null 且 Success=true 时为 true。

#### 方法说明

<a id="api-8481a5cc2fd10c46"></a>

##### ToString · 方法

```csharp
public override string ToString();
```

生成用于诊断的响应摘要，不是稳定的数据交换格式。

返回：包含成功状态或错误来源、码和说明的文本。

<a id="api-dd3269fca7449cd0"></a>

### `ApiResponse<T>` 类

表示带数据操作的结果，区分失败、成功空结果和成功数据。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiResponse.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Code { get; } `](#api-36b99e7ac53eca9c) | 原生或业务错误码；含义由 ErrorSource 和具体方法决定，未指定时通常为 -1。 |
| [` public T? Data { get; } `](#api-09aba646ac904898) | 响应数据；使用前先检查 HasData，失败/空结果时可能为默认值。 |
| [` public string Error { get; } = string.Empty `](#api-63a4a29a2aba472e) | 面向人的失败说明，可能本地化，不宜按文本判断错误类别。 |
| [` public ApiErrorKind ErrorKind { get; } `](#api-082ece6ab08fa1f1) | 业务失败类别；成功时为 None，尚未分类的失败为 Unspecified。 |
| [` public ApiErrorSource ErrorSource { get; } `](#api-9da73b15a78ab23e) | 错误码所属接口来源。 |
| [` public Exception? Exception { get; } `](#api-bbf70dc4b44782a5) | 失败时保留的原始异常；未提供时为 null。 |
| [` public bool HasData { get; } `](#api-237f5f5d1eefe59d) | 仅在成功、非空结果且 Data 非 null 时为 true。 |
| [` public bool IsEmpty { get; } `](#api-01fa8ecc3156be15) | 查询是否以成功但没有匹配结果结束，与失败区分。 |
| [` public bool Success { get; } `](#api-72d76820028394ac) | 操作响应是否成功；成功的空结果也为 true，数据存在性由 HasData 判断。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static ApiResponse<T> Empty() `](#api-4da81e85512bf4cd) | 创建成功但没有查询匹配项的响应。 |
| [` public static ApiResponse<T> Fail(string error, int code = -1, ApiErrorSource source = ApiErrorSource.None, Exception? exception = null, ApiErrorKind kind = ApiErrorKind.Unspecified) `](#api-49f48ccb175ec724) | 创建失败响应并写入调试输出，不抛出传入异常。 |
| [` public static ApiResponse<T> Ok(T? data) `](#api-ca7826aa38f9355f) | 创建成功响应并保存给定数据。 |
| [` public static implicit operator bool (ApiResponse<T>? response) `](#api-d476883b504758e1) | 将响应按是否成功转换为布尔值；空响应对象为 false，成功空结果为 true。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public override string ToString() `](#api-27458ff1456674cc) | 生成用于诊断的响应摘要，不是稳定的数据交换格式。 |

#### 详细说明

```csharp
public sealed class ApiResponse<T>
```

| 类型参数 | 说明 |
| --- | --- |
| `T` | 成功响应携带的数据类型。 |

调用约定：使用 HasData 判断是否有数据，IsEmpty 表示成功但没有结果。失败信息由 ErrorKind、ErrorSource、Code 和 Error 提供。

#### 属性说明

<a id="api-36b99e7ac53eca9c"></a>

##### Code · 属性

```csharp
public int Code { get; }
```

原生或业务错误码；含义由 ErrorSource 和具体方法决定，未指定时通常为 -1。

<a id="api-09aba646ac904898"></a>

##### Data · 属性

```csharp
public T? Data { get; }
```

响应数据；使用前先检查 HasData，失败/空结果时可能为默认值。

<a id="api-63a4a29a2aba472e"></a>

##### Error · 属性

```csharp
public string Error { get; } = string.Empty;
```

面向人的失败说明，可能本地化，不宜按文本判断错误类别。

<a id="api-082ece6ab08fa1f1"></a>

##### ErrorKind · 属性

```csharp
public ApiErrorKind ErrorKind { get; }
```

业务失败类别；成功时为 None，尚未分类的失败为 Unspecified。

<a id="api-9da73b15a78ab23e"></a>

##### ErrorSource · 属性

```csharp
public ApiErrorSource ErrorSource { get; }
```

错误码所属接口来源。

<a id="api-bbf70dc4b44782a5"></a>

##### Exception · 属性

```csharp
public Exception? Exception { get; }
```

失败时保留的原始异常；未提供时为 null。

<a id="api-237f5f5d1eefe59d"></a>

##### HasData · 属性

```csharp
public bool HasData { get; }
```

仅在成功、非空结果且 Data 非 null 时为 true。

<a id="api-01fa8ecc3156be15"></a>

##### IsEmpty · 属性

```csharp
public bool IsEmpty { get; }
```

查询是否以成功但没有匹配结果结束，与失败区分。

<a id="api-72d76820028394ac"></a>

##### Success · 属性

```csharp
public bool Success { get; }
```

操作响应是否成功；成功的空结果也为 true，数据存在性由 HasData 判断。

#### 静态方法说明

<a id="api-4da81e85512bf4cd"></a>

##### Empty · 方法

```csharp
public static ApiResponse<T> Empty();
```

创建成功但没有查询匹配项的响应。

返回：Success=true、IsEmpty=true、Data=default 的响应；HasData=false。

<a id="api-49f48ccb175ec724"></a>

##### Fail · 方法

```csharp
public static ApiResponse<T> Fail(string error, int code = -1, ApiErrorSource source = ApiErrorSource.None, Exception? exception = null, ApiErrorKind kind = ApiErrorKind.Unspecified);
```

创建失败响应并写入调试输出，不抛出传入异常。

| 参数 | 说明 |
| --- | --- |
| `error` | 可供人阅读的错误说明。 |
| `code` | 接口或业务错误码；默认 -1。 |
| `source` | 错误码来源；默认未分类。 |
| `exception` | 可选原始异常，保存在响应中供诊断使用。 |
| `kind` | 失败类别；None 或未定义值按 Unspecified 处理。 |

返回：保存错误信息的失败响应。

<a id="api-ca7826aa38f9355f"></a>

##### Ok · 方法

```csharp
public static ApiResponse<T> Ok(T? data);
```

创建成功响应并保存给定数据。

| 参数 | 说明 |
| --- | --- |
| `data` | 成功响应要携带的数据；不复制数据，null 统一表示成功空结果。 |

返回：数据非 null 时为成功数据响应；null 时与 Empty() 等价。

<a id="api-d476883b504758e1"></a>

##### op_Implicit · 方法

```csharp
public static implicit operator bool (ApiResponse<T>? response);
```

将响应按是否成功转换为布尔值；空响应对象为 false，成功空结果为 true。

| 参数 | 说明 |
| --- | --- |
| `response` | 待判断响应，可为 null。 |

返回：响应非 null 且 Success=true 时为 true；不检查 Data。

#### 方法说明

<a id="api-27458ff1456674cc"></a>

##### ToString · 方法

```csharp
public override string ToString();
```

生成用于诊断的响应摘要，不是稳定的数据交换格式。

返回：包含成功/空结果或错误来源、码和说明的文本。

<a id="api-d508b5ee4db3a2d0"></a>

### `CoreOperationException` 类

Core 操作抛出的失败异常；Result 保留错误类别和执行结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/CoreOperationException.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ApiResponse Result { get; } `](#api-c479a19f939348b7) | 保留错误类别、来源、错误码与原始异常的失败响应。 |

#### 详细说明

```csharp
public class CoreOperationException : InvalidOperationException
```

调用约定：OutcomeUnknown 表示已提交操作的结果尚未确认，不代表操作未发生或已停止，不应直接重试或清理。

#### 属性说明

<a id="api-c479a19f939348b7"></a>

##### Result · 属性

```csharp
public ApiResponse Result { get; }
```

保留错误类别、来源、错误码与原始异常的失败响应。

<a id="api-9f824e626b7d8430"></a>

### `DiskParser` 类

读取块设备上的 GPT/MBR 分区结构，用于筛选可供来宾部署的数据分区。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / 磁盘分区解析 |
| 源文件 | ` Management/Tools/Partitions/DiskParser.cs ` |

#### 成员概览

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public List<DiskPartitionInfo> GetPartitions(string devicePath) `](#api-cc0ae6d5227b0f65) | 读取磁盘并筛选达到容量阈值的 Windows/Linux 相关分区。 |

#### 详细说明

```csharp
public class DiskParser
```

#### 方法说明

<a id="api-cc0ae6d5227b0f65"></a>

##### GetPartitions · 方法

```csharp
public List<DiskPartitionInfo> GetPartitions(string devicePath);
```

读取磁盘并筛选达到容量阈值的 Windows/Linux 相关分区。

| 参数 | 说明 |
| --- | --- |
| `devicePath` | 原始块设备路径，或直接按磁盘扇区排列的镜像文件路径。VHD/VHDX 应先挂载，再传入块设备路径。 |

返回：识别的分区列表，容量以字节表示；不是包含所有分区的完整磁盘清单。

调用约定：返回容量至少 1 GiB、被识别为 Windows 或 Linux 用途的分区。打开设备失败会抛异常；分区表解析失败时保留已经取得的记录，当前结果不含完整性标记。物理设备从系统读取扇区大小；原始镜像文件按 512 或 4096 字节扇区定位 GPT。

<a id="api-824369b51a27da0a"></a>

### `OperationStepResult` record 类

组合操作中一个步骤的结果。

| 项目 | 内容 |
| --- | --- |
| 类型 | record 类 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiFailure.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public OperationStepResult(string Name, OperationStepState State, ApiFailure? Failure = null) `](#api-0d4271411581772e) |  |

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public ApiFailure? Failure { get; init; } `](#api-0f3ef4bdc4d10e86) | 失败详情；未执行或成功时为 null。 |
| [` public string Name { get; init; } `](#api-607bd34bd2237c7a) | 稳定的步骤标识，具体值见使用此类型的接口说明。 |
| [` public bool RequiresRestart { get; } `](#api-cafb8cfd44349217) | 本步骤是否明确报告需要重启；步骤失败也可能保留此前已完成修改的重启要求。 |
| [` public string? ResourceId { get; init; } `](#api-5c82818beaa34e7d) | 本步骤针对的资源标识；同一步骤处理多个资源时用于区分。 |
| [` public IReadOnlyList<RestartReason> RestartReasons { get; } = Array.Empty<RestartReason>() `](#api-7ad6e177a3261994) | 本步骤已确认的重启原因；只读且去重。 |
| [` public OperationStepState State { get; init; } `](#api-2475a7bd8ed9ae1f) | 执行状态。 |

#### 详细说明

```csharp
public sealed record OperationStepResult(string Name, OperationStepState State, ApiFailure? Failure = null)
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 稳定的步骤标识，具体值见使用此类型的接口说明。 |
| `State` | 执行状态。 |
| `Failure` | 失败详情；未执行或成功时为 null。 |

#### 构造函数说明

<a id="api-0d4271411581772e"></a>

##### OperationStepResult · 主构造器

```csharp
public OperationStepResult(string Name, OperationStepState State, ApiFailure? Failure = null);
```

| 参数 | 说明 |
| --- | --- |
| `Name` | 稳定的步骤标识，具体值见使用此类型的接口说明。 |
| `State` | 执行状态。 |
| `Failure` | 失败详情；未执行或成功时为 null。 |

#### 属性说明

<a id="api-0f3ef4bdc4d10e86"></a>

##### Failure · record 属性

```csharp
public ApiFailure? Failure { get; init; }
```

失败详情；未执行或成功时为 null。

<a id="api-607bd34bd2237c7a"></a>

##### Name · record 属性

```csharp
public string Name { get; init; }
```

稳定的步骤标识，具体值见使用此类型的接口说明。

<a id="api-cafb8cfd44349217"></a>

##### RequiresRestart · 属性

```csharp
public bool RequiresRestart { get; }
```

本步骤是否明确报告需要重启；步骤失败也可能保留此前已完成修改的重启要求。

<a id="api-5c82818beaa34e7d"></a>

##### ResourceId · 属性

```csharp
public string? ResourceId { get; init; }
```

本步骤针对的资源标识；同一步骤处理多个资源时用于区分。未提供时为 null。

<a id="api-7ad6e177a3261994"></a>

##### RestartReasons · 属性

```csharp
public IReadOnlyList<RestartReason> RestartReasons { get; } = Array.Empty<RestartReason>();
```

本步骤已确认的重启原因；只读且去重。旧入口只有布尔标记时可能为空。

<a id="api-2475a7bd8ed9ae1f"></a>

##### State · record 属性

```csharp
public OperationStepState State { get; init; }
```

执行状态。

<a id="api-0c0612bad12dd636"></a>

### `OperationStepState` 枚举

一个修改步骤的执行情况，区分未执行、明确结束和结果不明。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiFailure.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Failed = 2 `](#api-34eb060abeb68cf1) | 已报告失败，之前步骤的副作用不因此撤销。 |
| [` NotStarted = 0 `](#api-c48bc52113615706) | 未选择或尚未开始此步骤。 |
| [` OutcomeUnknown = 3 `](#api-094d76c0af1348e7) | 请求可能仍在执行，不能据此重试或回滚。 |
| [` Succeeded = 1 `](#api-1484803469125625) | 已确认成功完成。 |

#### 详细说明

```csharp
public enum OperationStepState
```

#### 枚举值说明

<a id="api-34eb060abeb68cf1"></a>

##### Failed · 枚举值

```csharp
Failed = 2
```

已报告失败，之前步骤的副作用不因此撤销。

<a id="api-c48bc52113615706"></a>

##### NotStarted · 枚举值

```csharp
NotStarted = 0
```

未选择或尚未开始此步骤。

<a id="api-094d76c0af1348e7"></a>

##### OutcomeUnknown · 枚举值

```csharp
OutcomeUnknown = 3
```

请求可能仍在执行，不能据此重试或回滚。

<a id="api-1484803469125625"></a>

##### Succeeded · 枚举值

```csharp
Succeeded = 1
```

已确认成功完成。

<a id="api-69a3d065d000a98e"></a>

### `OsImages` 类

维护固定操作系统类型名称及其标准写法。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 虚拟机 / 修改 / 额外元数据 |
| 源文件 | ` Management/VirtualMachines/Metadata/OsImages.cs ` |

#### 成员概览

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static string Canonical(string? osType) `](#api-a7feffe5e688d28b) | 按不区分大小写的方式匹配内置操作系统类型。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public static readonly IReadOnlyList<string> SupportedTypes `](#api-6af71dddb7b4af51) | 内置只读的操作系统类型白名单；名称可用于对应的显示资源键。 |

#### 详细说明

```csharp
public static class OsImages
```

#### 静态方法说明

<a id="api-a7feffe5e688d28b"></a>

##### Canonical · 方法

```csharp
public static string Canonical(string? osType);
```

按不区分大小写的方式匹配内置操作系统类型。

| 参数 | 说明 |
| --- | --- |
| `osType` | 待规范化的操作系统类型名称。 |

返回：标准类型名称；未知或 null 输入回退为 Windows。

#### 字段与常量说明

<a id="api-6af71dddb7b4af51"></a>

##### SupportedTypes · 字段

```csharp
public static readonly IReadOnlyList<string> SupportedTypes;
```

内置只读的操作系统类型白名单；名称可用于对应的显示资源键。

<a id="api-ec6d13ee818c895b"></a>

### `RestartReason` 枚举

宿主操作已确认的重启原因；仅说明需要重启，不会触发重启。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Tools ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 使用说明 / 结果、取消和资源释放 |
| 源文件 | ` Common/Results/ApiFailure.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` BootConfiguration = 2 `](#api-bb39e18ba381e05e) | 已确认的启动配置尚需通过重启采用。 |
| [` PackageRegistration = 0 `](#api-2c4fe500de5b72b4) | 本机 Windows 组件包登记返回了重启要求。 |
| [` WindowsFeatureChange = 1 `](#api-44b900e8438e018d) | Windows 可选功能操作或其映像会话收尾返回了重启要求。 |

#### 详细说明

```csharp
public enum RestartReason
```

#### 枚举值说明

<a id="api-bb39e18ba381e05e"></a>

##### BootConfiguration · 枚举值

```csharp
BootConfiguration = 2
```

已确认的启动配置尚需通过重启采用。

<a id="api-2c4fe500de5b72b4"></a>

##### PackageRegistration · 枚举值

```csharp
PackageRegistration = 0
```

本机 Windows 组件包登记返回了重启要求。

<a id="api-44b900e8438e018d"></a>

##### WindowsFeatureChange · 枚举值

```csharp
WindowsFeatureChange = 1
```

Windows 可选功能操作或其映像会话收尾返回了重启要求。

<a id="api-a52eae53275a8dfa"></a>

### `VmcxDevice` 类

由 manifest 读取的设备描述。无效 GUID 保留原文，由 Validate 报告。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string DeviceType { get; } `](#api-e0cc29be17ee9c66) | 设备类型 GUID 文本。 |
| [` public string Instance { get; } `](#api-82ded115c114367d) | 设备实例 GUID 文本，通常对应 configuration 下的同名数据容器。 |
| [` public string Name { get; } `](#api-8529cfc4d89ac09b) | manifest 中的设备名称。 |
| [` public int Number { get; } `](#api-656d5621ed64b888) | manifest 中的 vdev 编号。 |

#### 详细说明

```csharp
public sealed class VmcxDevice
```

#### 属性说明

<a id="api-e0cc29be17ee9c66"></a>

##### DeviceType · 属性

```csharp
public string DeviceType { get; }
```

设备类型 GUID 文本。

<a id="api-82ded115c114367d"></a>

##### Instance · 属性

```csharp
public string Instance { get; }
```

设备实例 GUID 文本，通常对应 configuration 下的同名数据容器。

<a id="api-8529cfc4d89ac09b"></a>

##### Name · 属性

```csharp
public string Name { get; }
```

manifest 中的设备名称。

<a id="api-656d5621ed64b888"></a>

##### Number · 属性

```csharp
public int Number { get; }
```

manifest 中的 vdev 编号。

<a id="api-70be9275005910c6"></a>

### `VmcxDeviceKind` 枚举

可向现有配置添加的设备定义种类。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` GpuPartition = 2 `](#api-df99b7c473dc8d85) | GPU 分区，可使用通用池或指定 HostResource。 |
| [` NetworkAdapter = 0 `](#api-b52ee65ebcdc40eb) | 合成网卡，初始不连接虚拟交换机。 |
| [` PcieDevice = 3 `](#api-26de041c7ff9338e) | 离散设备分配（DDA）的配置定义。 |
| [` ScsiController = 1 `](#api-b863684e5a181b14) | 合成 SCSI 控制器。 |

#### 详细说明

```csharp
public enum VmcxDeviceKind
```

#### 枚举值说明

<a id="api-df99b7c473dc8d85"></a>

##### GpuPartition · 枚举值

```csharp
GpuPartition = 2
```

GPU 分区，可使用通用池或指定 HostResource。

<a id="api-b52ee65ebcdc40eb"></a>

##### NetworkAdapter · 枚举值

```csharp
NetworkAdapter = 0
```

合成网卡，初始不连接虚拟交换机。

<a id="api-26de041c7ff9338e"></a>

##### PcieDevice · 枚举值

```csharp
PcieDevice = 3
```

离散设备分配（DDA）的配置定义。

<a id="api-b863684e5a181b14"></a>

##### ScsiController · 枚举值

```csharp
ScsiController = 1
```

合成 SCSI 控制器。

<a id="api-bf0a3c9cc5591d9f"></a>

### `VmcxDocument` 类

VMCX 配置文件的只读快照与编辑入口。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<VmcxEntry> Entries { get; } `](#api-ccb3afc17d0920ff) | 根节点以及所有活动节点的不可变快照。 |
| [` public string FilePath { get; } `](#api-5267fcff84034e29) | 源文件绝对路径。 |
| [` public VmcxEntry Root { get; } `](#api-13d25c257292c603) | 合成根节点，可用于访问顶层容器。 |
| [` public long SourceLength { get; } `](#api-ab3ec99b3d1b44f0) | 源文件字节数。 |
| [` public string SourceSha256 { get; } `](#api-f356a6e29b4b6ee9) | 读取时整份源文件的 SHA-256。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static VmcxDocument Read(string path) `](#api-8da6e8eca2da9bd3) | 由本机 VmDataStore 读取已有 VMCX 的副本，并验证源文件读取前后哈希一致。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public VmcxEditSession BeginEdit() `](#api-9f5cf056c585b5cf) | 开始一批内存编辑；调用 Commit 前不写文件。 |
| [` public IReadOnlyList<VmcxEntry> Find(string path) `](#api-d0f27296f996e297) | 按展示路径查找全部匹配节点。 |
| [` public IReadOnlyList<VmcxEntry> GetChildren(VmcxEntry parent) `](#api-fec17503fd2a034a) | 读取指定节点的直接子节点，保留同名条目的边界。 |
| [` public IReadOnlyList<VmcxDevice> GetDevices() `](#api-3852fafc6545d18f) | 从 manifest 读取设备定义。 |
| [` public IReadOnlyList<string> Validate() `](#api-1648858c98d627a5) | 检查 manifest、设备结构和已知互斥规则。 |

#### 详细说明

```csharp
public sealed class VmcxDocument
```

调用约定：通过本机 VmDataStore.dll 读取文件副本，使用 BeginEdit 创建编辑批次。 文件上限为 256 MiB；提交时需要独占访问源文件。 原生接口按本机 DLL 的接口约定调用，尚未提供覆盖所有 Windows 版本的兼容性保证；缺少 DLL 或接口时直接失败。

示例：

```csharp
using ExHyperV.Vmcx;
public static class VmcxFileExample
{
    public static VmcxEditResult SetExistingString(string exportedVmcxPath, string nodePath, string text)
    {
        var document = VmcxDocument.Read(exportedVmcxPath);
        var edit = document.BeginEdit();
        var entry = document.Find(nodePath).Single();
        edit.SetValue(entry, VmcxValue.String(text));
        return edit.Commit(); // 备份后由 DLL 提交修改
    }
}
```

#### 属性说明

<a id="api-ccb3afc17d0920ff"></a>

##### Entries · 属性

```csharp
public IReadOnlyList<VmcxEntry> Entries { get; }
```

根节点以及所有活动节点的不可变快照。

<a id="api-5267fcff84034e29"></a>

##### FilePath · 属性

```csharp
public string FilePath { get; }
```

源文件绝对路径。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 读取的 VMCX 配置文件在宿主上的完整路径。 |
| 取值与默认 | Windows 文件路径，扩展名为 `.vmcx`。 |

<a id="api-13d25c257292c603"></a>

##### Root · 属性

```csharp
public VmcxEntry Root { get; }
```

合成根节点，可用于访问顶层容器。

<a id="api-ab3ec99b3d1b44f0"></a>

##### SourceLength · 属性

```csharp
public long SourceLength { get; }
```

源文件字节数。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 读取时源文件的大小。 |
| 取值与默认 | 单位为字节；读取入口支持最大 256 MiB 的文件。 |

<a id="api-f356a6e29b4b6ee9"></a>

##### SourceSha256 · 属性

```csharp
public string SourceSha256 { get; }
```

读取时整份源文件的 SHA-256。提交必须仍匹配此值。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 读取时计算的文件指纹。提交修改前用它检查源文件是否发生变化。 |
| 取值与默认 | 64 个大写十六进制字符（`0–9`、`A–F`）。 |

#### 静态方法说明

<a id="api-8da6e8eca2da9bd3"></a>

##### Read · 方法

```csharp
public static VmcxDocument Read(string path);
```

由本机 VmDataStore 读取已有 VMCX 的副本，并验证源文件读取前后哈希一致。

| 参数 | 说明 |
| --- | --- |
| `path` | 已有 .vmcx 文件路径。 |

返回：独立的内存快照，不持有文件句柄。

- 异常 `IOException`：读取失败、文件损坏或读取期间发生修改。
- 异常 `NotSupportedException`：文件种类、节点类型或大小超出支持范围。
- 异常 `VmcxException`：VmDataStore 返回原生错误，例如文件锁定或无效存储。

#### 方法说明

<a id="api-9f5cf056c585b5cf"></a>

##### BeginEdit · 方法

```csharp
public VmcxEditSession BeginEdit();
```

开始一批内存编辑；调用 Commit 前不写文件。

返回：独立编辑批次；同一源快照的多个批次提交时仍执行并发检查。

<a id="api-d0f27296f996e297"></a>

##### Find · 方法

```csharp
public IReadOnlyList<VmcxEntry> Find(string path);
```

按展示路径查找全部匹配节点。

| 参数 | 说明 |
| --- | --- |
| `path` | 完整逻辑路径，按不区分大小写匹配。 |

返回：匹配节点的只读集合；未命中时为空。

<a id="api-fec17503fd2a034a"></a>

##### GetChildren · 方法

```csharp
public IReadOnlyList<VmcxEntry> GetChildren(VmcxEntry parent);
```

读取指定节点的直接子节点，保留同名条目的边界。

| 参数 | 说明 |
| --- | --- |
| `parent` | 本快照的容器节点。 |

返回：直接子节点的只读集合；无子节点时为空。

<a id="api-3852fafc6545d18f"></a>

##### GetDevices · 方法

```csharp
public IReadOnlyList<VmcxDevice> GetDevices();
```

从 manifest 读取设备定义。

返回：按 manifest 编号排列的设备描述。

<a id="api-1648858c98d627a5"></a>

##### Validate · 方法

```csharp
public IReadOnlyList<string> Validate();
```

检查 manifest、设备结构和已知互斥规则。

返回：已发现的问题；未发现已知问题时为空。

<a id="api-c115085593ccde9c"></a>

### `VmcxEditException` 类

文件提交失败，包含可用于人工恢复的路径和状态。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? BackupPath { get; } `](#api-6e95c55a2b10345a) | 提交前的完整备份；尚未创建备份时为 null。 |
| [` public bool OriginalIntact { get; } `](#api-a0b6819c5b2818a3) | 失败后核对目标哈希是否仍与读取时的源文件一致。 |
| [` public IReadOnlyList<string> RecoveryPaths { get; } `](#api-f4145f90364d37bf) | 保留的恢复材料路径。 |
| [` public string? RestoreError { get; } `](#api-2f8d66cb48a59e31) | 状态核对失败的说明；能够核对时为 null。 |
| [` public bool TargetWasModified { get; } `](#api-6d7f22065d4b50ef) | 目标可能已被原生提交或恢复改写；读取失败无法核对时也为 true。 |

#### 详细说明

```csharp
public sealed class VmcxEditException : Exception
```

#### 属性说明

<a id="api-6e95c55a2b10345a"></a>

##### BackupPath · 属性

```csharp
public string? BackupPath { get; }
```

提交前的完整备份；尚未创建备份时为 null。

<a id="api-a0b6819c5b2818a3"></a>

##### OriginalIntact · 属性

```csharp
public bool OriginalIntact { get; }
```

失败后核对目标哈希是否仍与读取时的源文件一致。

<a id="api-f4145f90364d37bf"></a>

##### RecoveryPaths · 属性

```csharp
public IReadOnlyList<string> RecoveryPaths { get; }
```

保留的恢复材料路径。调用方应核对目标和备份后再决定是否恢复。

<a id="api-2f8d66cb48a59e31"></a>

##### RestoreError · 属性

```csharp
public string? RestoreError { get; }
```

状态核对失败的说明；能够核对时为 null。

<a id="api-6d7f22065d4b50ef"></a>

##### TargetWasModified · 属性

```csharp
public bool TargetWasModified { get; }
```

目标可能已被原生提交或恢复改写；读取失败无法核对时也为 true。

<a id="api-955a4f3832150289"></a>

### `VmcxEditResult` 类

一次 VMCX 文件提交的结果；备份由调用方保留或自行归档。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string? BackupPath { get; } `](#api-d59292b5d9139c44) | 提交前完整备份的路径；无修改时为 null。 |
| [` public bool Changed { get; } `](#api-16f2e1670c44de3b) | 是否实际写入了文件。 |
| [` public string FilePath { get; } `](#api-fad486b58d947d0d) | 被编辑文件的绝对路径。 |
| [` public string OriginalSha256 { get; } `](#api-6c646a74b253ec38) | 提交前源文件 SHA-256。 |
| [` public string ResultSha256 { get; } `](#api-6c0659b5a3112856) | 提交后文件 SHA-256。 |

#### 详细说明

```csharp
public sealed class VmcxEditResult
```

#### 属性说明

<a id="api-d59292b5d9139c44"></a>

##### BackupPath · 属性

```csharp
public string? BackupPath { get; }
```

提交前完整备份的路径；无修改时为 null。

<a id="api-16f2e1670c44de3b"></a>

##### Changed · 属性

```csharp
public bool Changed { get; }
```

是否实际写入了文件。

<a id="api-fad486b58d947d0d"></a>

##### FilePath · 属性

```csharp
public string FilePath { get; }
```

被编辑文件的绝对路径。

<a id="api-6c646a74b253ec38"></a>

##### OriginalSha256 · 属性

```csharp
public string OriginalSha256 { get; }
```

提交前源文件 SHA-256。

<a id="api-6c0659b5a3112856"></a>

##### ResultSha256 · 属性

```csharp
public string ResultSha256 { get; }
```

提交后文件 SHA-256。

<a id="api-0dd8fc721201a6a9"></a>

### `VmcxEditSession` 类

针对现有 VMCX 快照的单次编辑批次。修改先保存在内存，Commit 才写文件。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public IReadOnlyList<VmcxEntry> Entries { get; } `](#api-5bed3913db66b5bd) | 当前批次的节点快照；可包含尚未保存的新节点。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public VmcxEntry AddContainer(VmcxEntry parent, string key) `](#api-ba60f70a1ff2c8bc) | 新增容器。 |
| [` public Guid AddDevice(VmcxDeviceKind kind, string? hostResource = null) `](#api-f403dd0fb67518c2) | 在现有配置中新增设备定义及 manifest 条目。 |
| [` public VmcxEntry AddScsiDrive(Guid scsiInstance, string mediaPath, bool optical = false, int controller = 0) `](#api-8ba8b3b050e4d3ac) | 向指定 SCSI 设备定义添加磁盘或光驱，选择该逻辑控制器下第一个空闲 drive 编号。 |
| [` public VmcxEntry AddValue(VmcxEntry parent, string key, VmcxValue value, bool isSelector = false) `](#api-fae00fe745217e19) | 在容器中新增类型值；同父同名的值拒绝重复。 |
| [` public VmcxEditResult Commit() `](#api-31e722814f116d84) | 校验整批修改，备份后通过本机 VmDataStore 一次提交到原文件。 |
| [` public IReadOnlyList<VmcxEntry> Find(string path) `](#api-93cba0b6f2634990) | 按路径查找当前批次的全部匹配项。 |
| [` public IReadOnlyList<VmcxEntry> GetChildren(VmcxEntry parent) `](#api-e9233f91a82cadd0) | 读取当前批次中指定节点的直接子节点。 |
| [` public IReadOnlyList<VmcxDevice> GetDevices() `](#api-6741b25328481b20) | 读取当前批次的设备清单。 |
| [` public void RemoveDevice(Guid instance) `](#api-834ebc7ceb1a2afb) | 移除指定设备的数据子树及 manifest 条目，并连续重编号其余条目。 |
| [` public void RemoveSubtree(VmcxEntry entry) `](#api-e67b1ddf6c260c02) | 删除节点及其整个子树；不能删除合成根节点。 |
| [` public void Rename(VmcxEntry entry, string key) `](#api-65e6afb800041b20) | 修改节点的单段键名，不改变父节点或类型。 |
| [` public void SetValue(VmcxEntry entry, VmcxValue value) `](#api-684beb43ff3c3b64) | 按节点身份修改值，要求类型与原节点相同。 |
| [` public IReadOnlyList<string> Validate() `](#api-02ee5848c56e15f6) | 检查本批次当前的 manifest、设备结构和已知互斥规则。 |

#### 详细说明

```csharp
public sealed class VmcxEditSession
```

调用约定：实例不支持并发调用。丢弃实例即可取消未提交改动；无需释放文件句柄。 Commit 无论成功或失败均结束此批次，后续操作应重新读取文件。

#### 属性说明

<a id="api-5bed3913db66b5bd"></a>

##### Entries · 属性

```csharp
public IReadOnlyList<VmcxEntry> Entries { get; }
```

当前批次的节点快照；可包含尚未保存的新节点。

#### 方法说明

<a id="api-ba60f70a1ff2c8bc"></a>

##### AddContainer · 方法

```csharp
public VmcxEntry AddContainer(VmcxEntry parent, string key);
```

新增容器。允许同父同名容器，返回的节点身份用于后续精确编辑。

| 参数 | 说明 |
| --- | --- |
| `parent` | 源文档或本批次的父容器。 |
| `key` | 单段键名，不含路径运算符或空字符，UTF-8 最长 254 字节。 |

返回：新增容器，可用于同一批次的后续修改。

<a id="api-f403dd0fb67518c2"></a>

##### AddDevice · 方法

```csharp
public Guid AddDevice(VmcxDeviceKind kind, string? hostResource = null);
```

在现有配置中新增设备定义及 manifest 条目。

| 参数 | 说明 |
| --- | --- |
| `kind` | 网卡、SCSI 控制器、GPU 分区或 DDA。 |
| `hostResource` | DDA 必填的宿主设备路径；GPU 可选固定卡路径；其他种类必须为 null。 |

返回：新增设备的实例 GUID，供同一批次后续操作使用。

调用约定：网卡默认断开；GPU 默认使用通用池。

<a id="api-8ba8b3b050e4d3ac"></a>

##### AddScsiDrive · 方法

```csharp
public VmcxEntry AddScsiDrive(Guid scsiInstance, string mediaPath, bool optical = false, int controller = 0);
```

向指定 SCSI 设备定义添加磁盘或光驱，选择该逻辑控制器下第一个空闲 drive 编号。

| 参数 | 说明 |
| --- | --- |
| `scsiInstance` | 已有 SCSI 控制器的实例 GUID。 |
| `mediaPath` | 磁盘路径；光驱可为空以表示空介质。 |
| `optical` | true 为 ISO 光驱，false 为 VHD/VHDX 磁盘。 |
| `controller` | 数据节点中的 controller 编号，必须非负。 |

返回：新增 drive 容器，可通过通用字段编辑和 RemoveSubtree 继续操作。

调用约定：介质路径按配置值写入；介质文件保持原状。

<a id="api-fae00fe745217e19"></a>

##### AddValue · 方法

```csharp
public VmcxEntry AddValue(VmcxEntry parent, string key, VmcxValue value, bool isSelector = false);
```

在容器中新增类型值；同父同名的值拒绝重复。

| 参数 | 说明 |
| --- | --- |
| `parent` | 源文档或本批次的父容器。 |
| `key` | 单段键名。 |
| `value` | 新值。 |
| `isSelector` | 是否用此值限定父容器。目前每个容器支持一个字符串选择键，其名称须以 ASCII 字母或数字开头。 |

返回：新增值节点，可用于同一批次的后续修改。

<a id="api-31e722814f116d84"></a>

##### Commit · 方法

```csharp
public VmcxEditResult Commit();
```

校验整批修改，备份后通过本机 VmDataStore 一次提交到原文件。

返回：是否修改、保留的备份路径以及前后哈希。无改动不创建备份。

调用约定：提交前核对源文件并取得原生排他锁。失败时保留备份和当前文件，恢复材料通过 VmcxEditException 返回；不会自动回滚，也不承诺断电或进程终止时的原子提交。

- 异常 `InvalidDataException`：批次含已知结构错误。
- 异常 `IOException`：源文件已变化或无法访问。
- 异常 `VmcxEditException`：文件事务失败；异常包含备份和恢复状态。
- 异常 `NotSupportedException`：节点名称、类型与 selector 形态无法由原生接口无损重建；原文件不会被写入。

<a id="api-93cba0b6f2634990"></a>

##### Find · 方法

```csharp
public IReadOnlyList<VmcxEntry> Find(string path);
```

按路径查找当前批次的全部匹配项。重复路径必须由调用方进一步区分。

| 参数 | 说明 |
| --- | --- |
| `path` | 完整逻辑路径，匹配不区分大小写。 |

返回：当前批次的全部匹配节点；未命中时为空。

<a id="api-e9233f91a82cadd0"></a>

##### GetChildren · 方法

```csharp
public IReadOnlyList<VmcxEntry> GetChildren(VmcxEntry parent);
```

读取当前批次中指定节点的直接子节点。

| 参数 | 说明 |
| --- | --- |
| `parent` | 源文档或本批次的容器节点。 |

返回：当前批次的直接子节点快照。

<a id="api-6741b25328481b20"></a>

##### GetDevices · 方法

```csharp
public IReadOnlyList<VmcxDevice> GetDevices();
```

读取当前批次的设备清单。

返回：按 manifest 编号排列的设备描述，包含尚未提交的修改。

<a id="api-834ebc7ceb1a2afb"></a>

##### RemoveDevice · 方法

```csharp
public void RemoveDevice(Guid instance);
```

移除指定设备的数据子树及 manifest 条目，并连续重编号其余条目。

| 参数 | 说明 |
| --- | --- |
| `instance` | 设备实例 GUID。 |

调用约定：删除设备的配置定义及 manifest 条目；数据节点缺失时仍可删除条目。宿主资源和磁盘文件保持原状。

<a id="api-e67b1ddf6c260c02"></a>

##### RemoveSubtree · 方法

```csharp
public void RemoveSubtree(VmcxEntry entry);
```

删除节点及其整个子树；不能删除合成根节点。

| 参数 | 说明 |
| --- | --- |
| `entry` | 源文档或本批次的目标节点。 |

<a id="api-65e6afb800041b20"></a>

##### Rename · 方法

```csharp
public void Rename(VmcxEntry entry, string key);
```

修改节点的单段键名，不改变父节点或类型。

| 参数 | 说明 |
| --- | --- |
| `entry` | 源文档或本批次的目标节点。 |
| `key` | 新键名；允许同名容器，不允许同名值。 |

<a id="api-684beb43ff3c3b64"></a>

##### SetValue · 方法

```csharp
public void SetValue(VmcxEntry entry, VmcxValue value);
```

按节点身份修改值，要求类型与原节点相同。

| 参数 | 说明 |
| --- | --- |
| `entry` | 源文档或本批次的值节点。 |
| `value` | 新值。 |

- 异常 `ArgumentException`：类型不匹配、节点来自其他批次或已删除。

<a id="api-02ee5848c56e15f6"></a>

##### Validate · 方法

```csharp
public IReadOnlyList<string> Validate();
```

检查本批次当前的 manifest、设备结构和已知互斥规则。

返回：当前批次中已发现的结构问题。

<a id="api-d80d4fe6c5be0ca7"></a>

### `VmcxEntry` 类

现有 VMCX 中的一个节点快照。同名同路径节点仍各自具有独立身份。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public int Id { get; } `](#api-1d11ee3e6e8c164d) | 节点在当前文档内的唯一标识。 |
| [` public bool IsContainer { get; } `](#api-a2b82066b617aa85) | 是否为容器。 |
| [` public bool IsSelector { get; } `](#api-b595979699865424) | 是否为限定父容器身份的 selector 值。 |
| [` public string Key { get; } `](#api-e123aba642340ff7) | 原始单段键名，不含 selector 谓词。 |
| [` public int? ParentId { get; } `](#api-2ecd9ca8ee77ff94) | 父节点标识；根节点为 null。 |
| [` public string Path { get; } `](#api-cb105f8580cb5735) | 用于展示和搜索的路径；同名容器或值可使路径不唯一。 |
| [` public VmcxValue? Value { get; } `](#api-e2239934656a5019) | 值节点的类型和值；容器为 null。 |

#### 详细说明

```csharp
public sealed class VmcxEntry
```

调用约定：Id 只在本次文档及其编辑批次内有效；保存后请重新读取，不能跨文件复用。

#### 属性说明

<a id="api-1d11ee3e6e8c164d"></a>

##### Id · 属性

```csharp
public int Id { get; }
```

节点在当前文档内的唯一标识。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 节点在当前文档及其编辑批次中的身份，用于精确定位同名节点。 |
| 取值与默认 | 整数；仅在当前文档及编辑批次内有效。 |

<a id="api-a2b82066b617aa85"></a>

##### IsContainer · 属性

```csharp
public bool IsContainer { get; }
```

是否为容器。

<a id="api-b595979699865424"></a>

##### IsSelector · 属性

```csharp
public bool IsSelector { get; }
```

是否为限定父容器身份的 selector 值。

<a id="api-e123aba642340ff7"></a>

##### Key · 属性

```csharp
public string Key { get; }
```

原始单段键名，不含 selector 谓词。

<a id="api-2ecd9ca8ee77ff94"></a>

##### ParentId · 属性

```csharp
public int? ParentId { get; }
```

父节点标识；根节点为 null。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 直接父节点的身份。 |
| 取值与默认 | 与父节点的 `Id` 相同；根节点为 `null`。 |

<a id="api-cb105f8580cb5735"></a>

##### Path · 属性

```csharp
public string Path { get; }
```

用于展示和搜索的路径；同名容器或值可使路径不唯一。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 节点在 VMCX 内部的逻辑路径，用于展示和搜索。同名节点可以具有相同路径。 |
| 取值与默认 | 以 `/` 分隔层级，例如 `/configuration/manifest`。 |

<a id="api-e2239934656a5019"></a>

##### Value · 属性

```csharp
public VmcxValue? Value { get; }
```

值节点的类型和值；容器为 null。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 值节点保存的数据。 |
| 取值与默认 | 值节点返回 `VmcxValue`；容器节点为 `null`。 |

<a id="api-3156373219b7a844"></a>

### `VmcxException` 类

表示 VMCX 原生存储操作失败，保留其返回码供诊断。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**构造函数**

| 成员 | 说明 |
| --- | --- |
| [` public VmcxException(string msg, int hr) `](#api-8fc14d4f5fac567e) | 创建带原生 HRESULT 的 VMCX 失败异常。 |

**字段与常量**

| 成员 | 说明 |
| --- | --- |
| [` public int HResultCode `](#api-1c8568a89cb2055b) | 原生调用返回的 HRESULT；与异常继承的 HResult 属性分开保存。 |

#### 详细说明

```csharp
public sealed class VmcxException : Exception
```

#### 构造函数说明

<a id="api-8fc14d4f5fac567e"></a>

##### VmcxException · 构造器

```csharp
public VmcxException(string msg, int hr);
```

创建带原生 HRESULT 的 VMCX 失败异常。

| 参数 | 说明 |
| --- | --- |
| `msg` | 失败操作说明。 |
| `hr` | 原生 HRESULT 或封装定义的失败码。 |

#### 字段与常量说明

<a id="api-1c8568a89cb2055b"></a>

##### HResultCode · 字段

```csharp
public int HResultCode;
```

原生调用返回的 HRESULT；与异常继承的 HResult 属性分开保存。

<a id="api-727cf80a4878b3e2"></a>

### `VmcxValue` 类

不可变的 VMCX 类型值。用于现有配置文件的字段读取和编辑。

| 项目 | 内容 |
| --- | --- |
| 类型 | 类 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**属性**

| 成员 | 说明 |
| --- | --- |
| [` public string Text { get; } `](#api-6e6fbf148b6f3739) | 不随区域设置变化的文本表示；Raw 为 Base64，Boolean 为 True/False。 |
| [` public VmcxValueType Type { get; } `](#api-dfdc3182e9b498bc) | 存储类型。 |

**静态方法**

| 成员 | 说明 |
| --- | --- |
| [` public static VmcxValue Boolean(bool value) `](#api-cb12b98653bbaeb9) | 构造布尔值。 |
| [` public static VmcxValue Double(double value) `](#api-99c708ad5bde37c7) | 构造有限双精度值；拒绝 NaN 和无穷大。 |
| [` public static VmcxValue Integer(long value) `](#api-2975120629cfdd93) | 构造有符号整数值。 |
| [` public static VmcxValue Raw(ReadOnlySpan<byte> value) `](#api-df5ef0596a3ac845) | 构造独立的二进制值；不会保留调用方的可变缓冲区。 |
| [` public static VmcxValue String(string value) `](#api-65624121721725fe) | 构造字符串值，保留换行、空格和大小写。 |
| [` public static VmcxValue UnsignedInteger(ulong value) `](#api-2d100af9b82f68d9) | 构造无符号整数值。 |

**方法**

| 成员 | 说明 |
| --- | --- |
| [` public bool GetBoolean() `](#api-67a3fe29eb321613) | 读取布尔值；类型不匹配时抛出 InvalidOperationException。 |
| [` public double GetDouble() `](#api-7b779e17f332a44b) | 读取双精度值；类型不匹配时抛出 InvalidOperationException。 |
| [` public long GetInteger() `](#api-817e9c3670e9a2f0) | 读取有符号整数；类型不匹配时抛出 InvalidOperationException。 |
| [` public byte[] GetRaw() `](#api-c84528efac7cb838) | 读取独立的字节数组；类型不匹配时抛出 InvalidOperationException。 |
| [` public string GetString() `](#api-46c417f2e2601ae4) | 读取字符串；类型不匹配时抛出 InvalidOperationException。 |
| [` public ulong GetUnsignedInteger() `](#api-c1787e65fd08f48f) | 读取无符号整数；类型不匹配时抛出 InvalidOperationException。 |

#### 详细说明

```csharp
public sealed class VmcxValue
```

#### 属性说明

<a id="api-6e6fbf148b6f3739"></a>

##### Text · 属性

```csharp
public string Text { get; }
```

不随区域设置变化的文本表示；Raw 为 Base64，Boolean 为 True/False。

**字段说明**

| 项目 | 说明 |
| --- | --- |
| 详细含义 | 当前类型值的文本表示，格式不随区域设置变化。 |
| 取值与默认 | 整数和浮点数使用十进制；字符串保留原文；二进制数据使用 Base64；布尔值为 `True` 或 `False`。 |

<a id="api-dfdc3182e9b498bc"></a>

##### Type · 属性

```csharp
public VmcxValueType Type { get; }
```

存储类型。

#### 静态方法说明

<a id="api-cb12b98653bbaeb9"></a>

##### Boolean · 方法

```csharp
public static VmcxValue Boolean(bool value);
```

构造布尔值。

| 参数 | 说明 |
| --- | --- |
| `value` | 待保存的值。 |

返回：布尔类型值。

<a id="api-99c708ad5bde37c7"></a>

##### Double · 方法

```csharp
public static VmcxValue Double(double value);
```

构造有限双精度值；拒绝 NaN 和无穷大。

| 参数 | 说明 |
| --- | --- |
| `value` | 待保存的值。 |

返回：双精度类型值。

- 异常 `ArgumentOutOfRangeException`：值不是有限数。

<a id="api-2975120629cfdd93"></a>

##### Integer · 方法

```csharp
public static VmcxValue Integer(long value);
```

构造有符号整数值。

| 参数 | 说明 |
| --- | --- |
| `value` | 待保存的值。 |

返回：有符号整数类型值。

<a id="api-df5ef0596a3ac845"></a>

##### Raw · 方法

```csharp
public static VmcxValue Raw(ReadOnlySpan<byte> value);
```

构造独立的二进制值；不会保留调用方的可变缓冲区。

| 参数 | 说明 |
| --- | --- |
| `value` | 待保存的字节。 |

返回：保存独立数据的二进制类型值。

<a id="api-65624121721725fe"></a>

##### String · 方法

```csharp
public static VmcxValue String(string value);
```

构造字符串值，保留换行、空格和大小写。

| 参数 | 说明 |
| --- | --- |
| `value` | 非 null 字符串。 |

返回：保存原文的字符串类型值。

- 异常 `ArgumentException`：字符串包含无效 UTF-16。

<a id="api-2d100af9b82f68d9"></a>

##### UnsignedInteger · 方法

```csharp
public static VmcxValue UnsignedInteger(ulong value);
```

构造无符号整数值。

| 参数 | 说明 |
| --- | --- |
| `value` | 待保存的值。 |

返回：无符号整数类型值。

#### 方法说明

<a id="api-67a3fe29eb321613"></a>

##### GetBoolean · 方法

```csharp
public bool GetBoolean();
```

读取布尔值；类型不匹配时抛出 InvalidOperationException。

返回：布尔值。

<a id="api-7b779e17f332a44b"></a>

##### GetDouble · 方法

```csharp
public double GetDouble();
```

读取双精度值；类型不匹配时抛出 InvalidOperationException。

返回：双精度浮点数。

<a id="api-817e9c3670e9a2f0"></a>

##### GetInteger · 方法

```csharp
public long GetInteger();
```

读取有符号整数；类型不匹配时抛出 InvalidOperationException。

返回：64 位有符号整数。

<a id="api-c84528efac7cb838"></a>

##### GetRaw · 方法

```csharp
public byte[] GetRaw();
```

读取独立的字节数组；类型不匹配时抛出 InvalidOperationException。

返回：由调用方拥有的独立字节数组。

<a id="api-46c417f2e2601ae4"></a>

##### GetString · 方法

```csharp
public string GetString();
```

读取字符串；类型不匹配时抛出 InvalidOperationException。

返回：字符串原文。

<a id="api-c1787e65fd08f48f"></a>

##### GetUnsignedInteger · 方法

```csharp
public ulong GetUnsignedInteger();
```

读取无符号整数；类型不匹配时抛出 InvalidOperationException。

返回：64 位无符号整数。

<a id="api-1dcabda321eccb67"></a>

### `VmcxValueType` 枚举

VMCX 值节点支持的存储类型；容器不是值。

| 项目 | 内容 |
| --- | --- |
| 类型 | 枚举 |
| 命名空间 | ` ExHyperV.Vmcx ` |
| 程序集 | `ExHyperV.Core.dll` |
| 功能归属 | 管理层面 / 工具 / VMCX 文件 |
| 源文件 | ` Management/Tools/Vmcx/Vmcx.cs ` |

#### 成员概览

**枚举值**

| 成员 | 说明 |
| --- | --- |
| [` Boolean = 8 `](#api-c4e6b478477d90ff) | 布尔值。 |
| [` Double = 5 `](#api-82ee7725b49b24e9) | 有限双精度浮点数。 |
| [` Integer = 3 `](#api-b4e03f1837926fab) | 64 位有符号整数。 |
| [` Raw = 7 `](#api-1f4fb3605c3a579d) | 字节数组。 |
| [` String = 6 `](#api-d2979928dc3afe09) | UTF-16 字符串。 |
| [` UnsignedInteger = 4 `](#api-2063562f87e9d555) | 64 位无符号整数。 |

#### 详细说明

```csharp
public enum VmcxValueType
```

#### 枚举值说明

<a id="api-c4e6b478477d90ff"></a>

##### Boolean · 枚举值

```csharp
Boolean = 8
```

布尔值。

<a id="api-82ee7725b49b24e9"></a>

##### Double · 枚举值

```csharp
Double = 5
```

有限双精度浮点数。

<a id="api-b4e03f1837926fab"></a>

##### Integer · 枚举值

```csharp
Integer = 3
```

64 位有符号整数。

<a id="api-1f4fb3605c3a579d"></a>

##### Raw · 枚举值

```csharp
Raw = 7
```

字节数组。

<a id="api-d2979928dc3afe09"></a>

##### String · 枚举值

```csharp
String = 6
```

UTF-16 字符串。

<a id="api-2063562f87e9d555"></a>

##### UnsignedInteger · 枚举值

```csharp
UnsignedInteger = 4
```

64 位无符号整数。

<!-- api-reference:end -->
