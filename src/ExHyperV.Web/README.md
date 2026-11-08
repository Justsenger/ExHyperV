# ExHyperV.Web

Windows 上的虚拟机列表和浏览器控制台。图像、键鼠能力统一来自 Core；Web 负责视频编码、传输和呈现，没有虚拟机电源或配置操作。

虚拟机列表调用 Core 的 `VmQueryService.GetVmListAsync()`，沿用返回模型的状态文本和运行判定；Web 只保留列表字段、排序和短时缓存。查询服务由 Web 宿主管理生命周期，退出时释放，不启动性能采样。

`GET /api/vms` 成功返回 `{ virtualMachines, issues }`：清单来自 Core 的 `VmInventorySnapshot.VirtualMachines`，可选信息读取问题来自 `Issues`，页面保留清单并显示提示。Core 查询失败时返回 HTTP 503 及错误说明、类别、来源和错误码，不作为空清单缓存或显示。控制台连接前也通过同一入口查询目标。

## 运行

宿主需要 .NET 10 ASP.NET Core Runtime、对应架构的 Microsoft Visual C++ v14 运行库和 Hyper-V 管理权限，浏览器使用新版 Edge 或 Chrome。以管理员身份运行发布目录中的 `ExHyperV.Web.exe`，打开 `http://127.0.0.1:5197/`。`--Port 5200` 可修改端口。保留整个发布目录，包括 `wwwroot`、`media` 和 Core 的原生依赖。

当前仅监听本机 IPv4 回环，校验 Host 和 WebSocket Origin。远程入口尚未提供登录、TLS、STUN/TURN 和端口规划；不能直接把此版本反向代理到公网。视频传输已经使用 WebRTC，但这不代表远程部署已完成。

## 图像和控制

```text
Core 的 IConsoleConnection.Video
  → Web 的最新帧槽 → 缩放 / 色彩转换 → H.264 编码
  → WebRTC（DTLS/SRTP）→ 浏览器原生 video

浏览器键鼠 → WebSocket → Web 输入适配 → IConsoleConnection.Input
```

默认使用 x264 软件编码，配置为低延迟、无 B 帧和帧内并行。编码和 WebRTC 来自 GStreamer，`external/gstreamer/web_media.c` 只做调用适配。没有自写视频编解码器，也不把 Web 的媒体依赖放进 Core。

`--VideoEncoder software` 为默认值。`hardware` 强制使用 Windows Media Foundation 硬件 H.264；`auto` 优先硬件，在初始化失败或协商阶段未输出时改用 x264。硬件路径保留用于设备兼容验证；当前测试宿主曾出现编码停顿和驱动挂起，不能仅凭检测到硬件编码器就认定它更快或稳定。

三档参数为编码上限，不承诺源帧率或恒定实际流量：

| 档位 | 最大画面 | 帧率上限 | 目标码率 |
| --- | --- | --- | --- |
| 节省 | 1280 × 720 | 30 | 2 Mbps |
| 均衡 | 1920 × 1080 | 60 | 4 Mbps |
| 清晰 | 1920 × 1080 | 60 | 8 Mbps |

保持纵横比，不放大小画面。切换档位需要断开后重连。H.264 是有损压缩，色度采样为 4:2:0；文字锐度不等同于原来的无损像素通道。目标码率也不是包含 RTP、重传等开销的严格网络限速。目前不自动按 REMB 调整码率：静态桌面恢复时的估计偏低问题尚需专项弱网验证。

每个连接最多保留一个待处理 Core 帧和一个供关键帧重发的最新帧租约；原生输入池最多三块缓冲，输入队列只容纳一帧。慢消费者丢弃旧帧，不无限排队。由于 Core 的公开帧也可能使用托管内存，送入异步编码器时需要复制到原生池；这里不是零拷贝。源尺寸改变时重建编码连接，用代际编号隔离旧的 SDP 和 ICE。

键鼠坐标按原始桌面尺寸换算。失焦或断开时释放按键和鼠标按钮。绿色 FPS 按浏览器合成器提交的帧数计数，不等于显示器扫描率；“源更新”统计 Core 回调次数，包含局部画面和鼠标更新，不代表来宾刷新率，静态画面也可以为零。码率单位为 Mbps；控制往返只测 WebSocket 往返，不是端到端操作延迟。编码耗时包含原生输入排队、缩放和编码；最大帧间隔是呈现回调间隔，受浏览器后台限速、主线程调度和源画面静止影响。

持续收到源更新而编码器停止输出时，连接会报告超时。原生释放放在后台执行，超过三秒仍未完成时拒绝新媒体连接并提示重启 Web 服务，避免再次进入挂起的驱动。超时不强制释放仍在使用的原生内存，也不重启虚拟机；这不是驱动自动恢复机制。

Web 使用 `ConsoleService.GetBackends()` 填充连接方式，并通过 `CreateConnection` 获取中立接口。默认选择 `RdpBasic`；`--ConsoleSource <标识>` 设置默认选项，例如 `RdpEnhanced`。基本与增强会话均由 Core 内部的 FreeRDP 实现，不再使用 `Enhanced` 参数。未实现或不提供画面的后端不能连接，没有输入能力的后端仅查看。Web 不引用后端的具体类或 WPF。

`GET /api/console/backends` 返回默认选项及 Core 登记的后端目录；控制台请求使用 `?backend=<标识>`，大小写与 Core 一致，未知或未实现的后端不自动回退。失焦、断开时调用 Core 的 `ReleaseAllInputs()`，由 Core 管理已按住的输入与释放重试；Ctrl+Alt+Del 通过 `SendKeys()` 整组提交。

## 构建媒体依赖

下载官方 GStreamer 1.28.8 Windows MSVC SDK（选择与目标一致的 ARM64 或 x86_64 架构），安装开发组件。适配层可用 LLVM-MinGW 编译：

```powershell
./src/ExHyperV.Web/external/gstreamer/build.ps1 `
  -GStreamerRoot C:/SDK/gstreamer-arm64 `
  -Compiler C:/SDK/llvm-mingw/bin/aarch64-w64-mingw32-clang.exe `
  -ReadObj C:/SDK/llvm-mingw/bin/llvm-readobj.exe `
  -RuntimeIdentifier win-arm64 -OutputRoot C:/build/web-media-arm64
```

脚本校验架构，按实际 DLL 导入关系收集运行依赖，保留上游许可证，生成二进制 SHA256 清单；输出目录必须是新目录。x64 使用对应 SDK、`x86_64-w64-mingw32-clang.exe` 和 `win-x64`。运行包包含 x264 及其 GStreamer 插件，不打包 FFmpeg 或 SDK 的其他无关插件。

Core 含 COM 引用，使用 Visual Studio 的完整 MSBuild 发布：

```powershell
msbuild src/ExHyperV.Web/ExHyperV.Web.csproj /restore /t:Publish `
  /p:Configuration=Release /p:RuntimeIdentifier=win-arm64 /p:SelfContained=false `
  /p:MediaRuntimeRoot=C:/build/web-media-arm64
```

媒体 DLL 动态加载、独立存放，可替换；`--MediaRuntime <目录>` 可指定其他同架构运行目录。GStreamer 核心与所用插件的具体许可证见随包 `media/licenses`；本构建所用 x264 源码声明 GPL-2.0-or-later。正式对外发布时还需同时提供对应版本的第三方源码、构建说明和本项目源码；许可证文本和二进制清单不能替代源码交付。GStreamer 官方下载与源码入口：<https://gstreamer.freedesktop.org/download/>、<https://gstreamer.freedesktop.org/src/>。H.264 专利授权与开源许可证是不同事项。

## 当前范围

- 基本会话和增强会话沿用 Core；增强会话要求来宾通道就绪。
- 尚未接入音频、剪贴板、USB、来宾分辨率控制或浏览器输入法组合文本。
- 每台虚拟机同时允许一个 Web 控制台，总计最多四个；断开不改变虚拟机电源。
- ARM64 已作本机浏览器验证，x64 构建不等于 x64 实机验收；跨网络丢包、限速和端到端延迟仍需独立验证。
