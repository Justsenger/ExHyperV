# FreeRDP 原生适配

此 DLL 是 `ExHyperV.Core` 的实现依赖。它直接在调用进程中使用 FreeRDP，不启动 2.0 的 Service、SDK、WebSocket 或 ConsoleWorker。每个 `exhv_create` 仅代表一条到指定 VM 的控制台连接，不是 Core 全局运行会话。

- 使用当前进程的 Windows 身份连接本机 VMMS `127.0.0.1:2179`，PCB 携带 VM GUID；增强模式添加 `;EnhancedMode=1`。证书例外仅限经过 VMMS 进程身份核对的本机端点。每条连接设置系统 `secur32.dll` 为 SSPI 模块，不修改宿主环境变量。
- 基本模式复用 2.0 的 Hyper-V Frame Buffer DVC，增强模式使用 FreeRDP 软件 GDI/图形通道；交付完整、从上到下、Alpha 不透明的 BGRA8 帧，并合成远端鼠标。图像线程按更新事件唤醒，只保留最新帧，不在每次输出后额外等待固定时间。FBR 在 VMMS 共享互斥锁内复制画面，释放锁后再处理 Alpha 和发布；消费者回调在本地输出锁之外执行。连接时只读获取本机 Terminal Server 的 `InstanceID` 作为 RDP 数字产品标识，让 VMMS 正确认出同机连接并提供 FBR；不会写注册表。
- 键盘、鼠标、分辨率请求由有界队列交给连接线程。队列满明确拒绝，不覆盖已接受的按键抬起。增强分辨率仅在 `disp` 通道可用时接受；后续协商失败通过回调 6 报告，宽度按显示通道要求取偶数。
- 增强剪贴板通过 `cliprdr` 传递 Unicode 文本、DIB 图像和 HTML Format；不访问宿主剪贴板，不启用文件传输。单格式最多 16 MiB，完整快照最多 32 MiB。远端格式顺序读取；新广告或本地更新使旧回复失效。协议没有回复请求 ID，因此等待旧回复结束后才能请求新代，不能把迟到回复用于新内容。失败格式返回空缺，不中止控制台。
- 单个远端格式请求超过 30 秒未获应答，报告剪贴板不可用；保留未完成请求的位置，防止迟到数据串入新内容。迟到应答丢弃后恢复读取最新内容，始终未获应答则等通道或连接重建。视频和输入不受此超时影响。仅等待应答时设置截止时间，闲置时不增加定时轮询。
- 图像回调 7 借出 `exhv_frame` 描述符及只读像素，异步使用须先 `exhv_frame_retain`，结束后 `exhv_frame_release`；已有租约在连接销毁后仍有效。Core 用 `ConsoleFrame` 管理租约，不复制整帧；其他回调载荷仍在调用期间复制。剪贴板、能力和异步命令失败回调由输出线程发出，所有托管回调都在原生锁之外。`exhv_stop` 可由任意线程请求；等待 `exhv_run` 返回后，停止输入调用，再 `exhv_destroy`。不能在回调里同步等待连接关闭。
- 直接使用 FreeRDP 核心 context 分配器，避免 CLI helper 每次创建连接时覆盖全局 FBR 通道注册；不同控制台可并发连接。`Connected` 只在完整连接及通道初始化成功后发出，画面、剪贴板和登录通知不会先于它暴露。
- `LoginComplete` 来自服务器 `SaveSessionInfo` 的实际登录通知，不将图形激活或出现登录画面冒充用户已登录；连接完成前收到的通知保留到 `Connected` 后投递一次。

公开 C ABI 见 `exhv_freerdp.h`。控制台的窗口显示、全屏热键和系统剪贴板适配由 WPF 承担；图像、RDP 输入和剪贴板通道由 Core 提供。

`exhv_capture_frame` 按需保留本连接最近合成的完整帧，不等待新画面、不调用 WMI，也不复制像素。输出端在独立锁内检查引用计数、复用或替换当前帧；取图在同一锁内增加引用，防止与原地光标更新竞争。调用方用 `exhv_frame_release` 释放截图，停止后不能再取图，已有租约仍有效。未请求截图时，不额外保留托管帧或增加整帧复制；截图租约仍在使用时则按原有帧池规则保护像素。销毁连接前必须等待正在进行的取图调用退出。

## 构建

固定 FreeRDP 3.32.2（`97eff6ddfbbfaa8e42a126d362686bd001ff8bdc`）、OpenSSL 3.5.9、zlib 1.3.1；使用 LLVM MinGW UCRT 20261020 与 CMake 4.4.4。当前脚本使用 Windows ARM64 工具链，交叉生成 ARM64 和 x64 包。构建脚本读取依赖目录，在单独的工作目录重建 OpenSSL、包含 `cliprdr` 的 FreeRDP 和本项目适配层。

`DependencyRoot` 应包含 `upstream/`（上述 FreeRDP 提交）、`openssl-openssl-3.5.9/`，以及 `tools/` 下的 LLVM MinGW、CMake 和 Strawberry Perl。`DependencyPrefix` 是已编译的匹配架构依赖前缀，包含 zlib 的头文件和库；工作目录必须在依赖目录之外。具体目录名见 `build.ps1` 开头的工具路径。

```powershell
./src/ExHyperV.Core/external/freerdp/build.ps1 `
  -DependencyRoot '<依赖目录>' `
  -DependencyPrefix '<arm64 依赖前缀>' `
  -WorkDirectory '.artifacts/freerdp-native-arm64' -Architecture arm64
```

x64 使用匹配架构的依赖前缀，传入独立工作目录并指定 `-Architecture x64`。不能混用架构。构建产物采用固定的程序安装目录作为原生库默认搜索路径，实际编译和安装仍在工作目录中完成，避免将开发机路径带入发布包。

脚本递归核对 DLL 导入与 PE 架构，运行离线自测，将 DLL、许可证和 SHA256 清单打包到 `runtimes/win-<architecture>/console-native.zip`，供 Core 嵌入。自测验证输入边界/满队列、像素方向与行距、慢消费者下的帧所有权和最新帧合并、FBR 名称、剪贴板格式/请求代、停止先于运行；不会连接 VM 或读写宿主剪贴板。编译和离线自测不能替代基本/增强会话的实机画面、输入、剪贴板及刷新流畅度验收。

FBR 协议实现从 2.0 原生参考保留，其上游 IronRDP MIT 许可证随源码和包保留；FreeRDP、OpenSSL、zlib 许可证同时随包分发。
