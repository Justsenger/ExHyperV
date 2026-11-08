# Core 测试

独立测试项目，通过项目引用同步编译当前 Core。与 `ExHyperV.Core`、`ExHyperV.Wpf` 并列，入口已加入 `src/ExHyperV.sln`。

## 打开测试页面

在仓库根目录运行，兼容 Windows PowerShell 5.1 和 PowerShell 7：

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File src/ExHyperV.Core.Tests/Inspect-Resources.ps1
```

需要 .NET 10 SDK、Visual Studio 2026 / Build Tools 及 .NET 桌面工作负载。普通终端启动页面时，脚本会请求管理员权限；确认 Windows 提示后自动编译、打开 `http://127.0.0.1:5187/`，后续更新沿用同一权限。提权启动日志保存在 `.artifacts/resource-inspection/launcher-*.log`。`-BuildOnly` 和 `-ReportOnly` 保持调用者身份，便于自动化运行；Runner 使用已具备权限的账户执行。

1. **资源检查**：读取宿主、虚拟机和设备；选择一个共用虚拟机模板、PCIe 设备、物理测试盘或光驱。工作目录默认位于当前账户的本地缓存。普通虚拟机、隔离交换机、测试磁盘、ISO 和导入样本由执行器自行创建。
2. **选择测试项目**：按 Core API 文档的中文分类显示检查点、通过标准和资源条件。条件齐备的项目默认勾选，其余项目显示具体原因；可按分类、功能或单项取消。
3. **执行与结果**：逐项显示状态、断言和证据，汇总已记录的 API 调用次数及累计执行时间。清单中的检查点均已登记执行代码，覆盖规则、模型、宿主、虚拟机配置与生命周期、存储、网络、GPU、PCIe、控制台、性能采样和宿主重启。系统修改使用 PowerShell、WMI、注册表或实际文件独立核验；事件、错误和生命周期边界使用受控后端。独立检查最多并行四项，宿主资源操作串行，重启项最后执行。

执行代码登记、编译通过和实机通过分别记录。新接入执行器仍需逐项实测；页面中的“条件满足”只代表可以开始该项。执行阶段还会核验实际设备、登录、容量和运行状态，缺项记录具体跳过原因。

创建虚拟机前先保存归属记录；下一轮启动时检查遗留记录。已确认完成的操作按归属记录收尾；超时、原生结果未知、进程异常退出或清理失败时保留现场，暂停后续宿主操作。临时对象按 GUID 和配置目录双重核对，现有模板保持原样。宿主配置、设备状态和当前账户配置均先保存原值，逐项恢复并核验。

报告同时列出本轮省略的全部项目及具体原因，区分资源缺项、平台限制、已知故障隔离和执行代码缺口。每项开始和结束时原子保存进度，进程异常退出后可核对已完成项及最后执行目标；恢复前先核对在途操作的实际结果。每轮保存 Windows Build、架构、Core 版本及 DLL 的 SHA-256、资源快照、逐项证据，供跨 Build 对照。高级配置的属性存在仅代表可读取，成功写入仍需实测；已复现的宿主崩溃项按 Build 和架构隔离，原失败报告保留。

模板按虚拟机 ID 保存。普通测试和 GPU 测试共用这一选择，使用完整副本和独立磁盘，结束后清理副本。复制前核验模板处于关机状态、磁盘链完整，并核验缓存容量；导出当前状态后生成独立磁盘，源模板的检查点保留。GPU 部署在完整副本上执行；普通检查点测试另建空白虚拟机。

资源登记默认位于 `%LOCALAPPDATA%/ExHyperV.Core.Tests/resources.json`；同账户 Runner 可复用，其他账户可用 `-Configuration <路径>` 指定。需要进入虚拟机的项目，通过当前账户保存的 `%LOCALAPPDATA%/ExHyperV.Core.Tests/Credentials/<模板 GUID>.clixml` 获取凭据。使用本机 `Get-Credential | Export-Clixml -LiteralPath <路径>` 创建该文件，凭据受 Windows DPAPI 保护；报告仅记录其就绪状态。Windows 使用 PowerShell Direct，Linux 使用 SSH 并核对副本 BIOS 身份。

页面文件修改后自动刷新并保留选择。C# 修改后，用下面的命令请求现有启动器重新编译、重启，浏览器自动重连：

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File src/ExHyperV.Core.Tests/Update-Inspection.ps1
```

执行中的任务结束后才允许更新。构建失败时恢复上一版；结果文件保留。启动参数还包括 `-ReportOnly`（只保存资源报告）、`-BuildOnly`（只编译）、`-NoBrowser`、`-Port`。

资源及 API 执行报告保存到 `.artifacts/resource-inspection/reports/`，含本机资源信息，已由 Git 忽略。报告中的资源状态只代表本次检查身份与时间；实际测试结论记录在独立执行结果中。

## 从命令行执行页面中的 API 测试

先启动资源检查程序并保存资源选择，再运行以下命令。脚本兼容 PowerShell 5.1，使用与页面“下一步”相同的执行器，默认提交条件齐备的全部检查点：

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File src/ExHyperV.Core.Tests/Run-ApiTests.ps1
# 收集当前轮次，适合终端重连或由自动化接管进度
powershell -NoProfile -ExecutionPolicy RemoteSigned -File src/ExHyperV.Core.Tests/Run-ApiTests.ps1 -Attach
```

`-CheckId <编号>` 可只执行指定回归项，`-OutputDirectory <目录>` 指定机器可读报告位置。终端显示状态变化，并保存 `results.json`、`summary.json`；退出码 0 表示完成且有通过项，1 表示失败、取消或执行异常，2 表示零通过项。省略和跳过原因随结果保留，测试失败也能取得完整报告。退出收集脚本后页面中的任务继续运行，可用 `-Attach` 接回。

此入口要求测试页面服务已经运行。宿主重启项先正常关闭运行中的虚拟机，保存阶段和原配置，再在页面和命令行显示“第 N 次宿主重启等待你确认”。每次都由操作者确认并手动重启，包含恢复原配置所需的重启。等待期间可以关闭页面；实际开机后，计划任务使用同一版本继续本轮测试。结束后恢复虚拟机原来的运行或暂停状态，正常关闭失败则跳过重启项。中断发生在原生提交过程中时保留恢复材料，等待核对终态。

直接在 Actions 作业进程内运行的页面入口会跳过宿主重启项。正式自动化入口使用独立的开机任务，配合测试机之外的采集端回传结果。

## 部署后的自动运行

首次部署由调度仓库的 `Install-TestMachine.ps1` 打开资源检查页。选择允许使用的虚拟机模板、物理盘、光驱和 PCIe 设备，保存检查后点“完成部署”。基础环境就绪才登记开机接单；可选设备空缺时，相关项目保留跳过原因。后续每轮仍会重新核验当前资源。

`Start-AutomatedTests.ps1` 是 Runner 调用的正式入口，兼容 PowerShell 5.1，要求管理员身份、Git、.NET 10 SDK 和 VS 2026 MSBuild。源码须为已提交的干净版本：

```powershell
$revision = git rev-parse HEAD
$run = [guid]::NewGuid().ToString('N')
powershell -NoProfile -ExecutionPolicy RemoteSigned -File src/ExHyperV.Core.Tests/Start-AutomatedTests.ps1 `
    -Revision $revision -RunId $run -RunDirectory "C:\ProgramData\ExHyperV.TestLab\runs\$run"
```

入口构建当前提交的 Core 与测试项目，先运行离线回归，再将固定载荷交给独立工作进程。工作进程读取登记资源，自动选择条件齐备的 API 项，持续保存 `status.json` 和逐项报告。若已选模板正在运行，先正常关闭；测试结束后恢复原运行状态。正常关闭失败、写入结果未知或恢复失败时保留记录，暂停后续写入。

`-Configuration` 指定本机资源文件；`-CheckId` 限定检查点；`-SkipTemplatePreparation` 保留模板当前电源状态。默认范围为常规测试，`-IncludeRebootTests` 加入宿主重启项，每次仍由操作者确认并手动重启。重复提交相同轮次只返回现有目录，恢复中的记录会阻止另一轮启动。

开机任务使用原账户和固定版本，按持久身份接回同一轮。已结束的轮次直接返回原结果；普通步骤异常中断时报告“等待恢复”，供核对现场。分发端和采集端可反复读取结果，采集操作仅负责回传。

多机分发与 GitHub 汇总由独立的测试调度仓库提供。每台机器先保存资源选择、注册 Runner，随后调用此入口。各机器报告记录源码提交、架构、Windows Build 和 Core 哈希，云端按轮次合并。

## 运行自动回归

以下脚本使用 PowerShell 7，先核对 API 清单，再以 Visual Studio MSBuild 构建 Core 与测试项目，最后运行 MSTest：

```powershell
pwsh -File src/ExHyperV.Core.Tests/Test-Core.ps1
pwsh -File src/ExHyperV.Core.Tests/Test-Core.ps1 -Suite All -LockedRestore
```

| 套件 | 测试范围 | 使用的资源 |
| --- | --- | --- |
| `Regression`（默认） | API 约定、查询字段、规则、流程确认与错误恢复、对象生命周期、控制台帧与输入、资源检查与页面筛选 | 替身后端、独立临时目录、专属测试子进程 |
| `Windows` | FreeRDP 原生离线自检、COM STA 调度、JPEG 编码、IMAPI2 ISO 生成 | 系统组件、原生库和临时 ISO |
| `All` | 以上两组 | CI 使用此入口 |

可用 `-Runtime win-arm64` 或 `win-x64` 指定架构，`-Filter 'FullyQualifiedName~QueryContractTests'` 筛选用例，`-NoCoverage` 关闭覆盖率。原生测试在 Visual Studio 中默认标记未执行，通过脚本 `-Suite Windows` / `All` 显式启用。

结果保存在 `.artifacts/1x/core-tests/results/<架构>/<本轮编号>/`：

- `summary.json`：状态、用例数量、覆盖率。
- `tests.trx`：逐项结果与失败位置。
- `coverage.cobertura.*.xml`：托管 Core 的行与分支覆盖率。
- `build.log`、`test.log`：构建与执行日志。

同一架构串行运行脚本；不同架构使用独立输出目录。测试失败、零用例、选中但未执行、缺失结果或空覆盖率报告均返回失败。MSTest 套件与页面 API 实机执行器分别统计，真实硬件和重启恢复以对应实机报告为准。

## 跟进 Core API

手写测试计划位于 `TestPlanning/TestCatalog.json`，公共条件位于 `TestPlanning/TestConditions.cs`。分类与排序直接读取 Core 文档生成器输出的元数据，包含方法级归属；框架自有项目归入“测试框架”。

```powershell
# 核对公开方法、计划和分类
dotnet run --project src/ExHyperV.Core.Tests/Tools/Catalog -- --audit-tests

# 同步分类与排序，保留检查点、编号、条件和通过标准
dotnet run --project src/ExHyperV.Core.Tests/Tools/Catalog -- --sync-categories
```

接口约定以 [Core API 文档](../ExHyperV.Core/API.md) 为准。源码核对按方法名合并重载；编译后的 `ApiCatalogTests` 反向检查计划中的公开接口是否仍存在。计划登记数量与实际执行覆盖率分别统计。

编译后的测试程序支持 `--audit-catalog`，只核对执行器登记与资源条件定义，输出 JSON；此入口仅作静态检查。新增清单项缺少执行器或条件定义时返回失败。

新增自动用例使用 `[TestMethod]`。`Diagnostics/` 保存复用场景和独立诊断入口；主测试程序集只编入规则与替身场景。历史操作标签 `OperationQuery`、`OperationCommand`、`OperationWorkflow`、`OperationResource` 可与 `Regression` / `WindowsIntegration` 一起筛选。

`.github/workflows/core-tests.yml` 对相关 push、PR 及手动触发运行 `All`，分别构建 ARM64 / x64，上传报告。依赖锁定文件固定 MSTest 与覆盖率组件版本；云端结果在推送后由 Actions 记录。
