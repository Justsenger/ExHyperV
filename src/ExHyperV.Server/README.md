# ExHyperV.Server 接入指南

ExHyperV.Server 在 Windows Hyper-V 宿主机上提供 HTTP 管理 API、实时遥测和原生虚拟机控制台。WoW、桌面程序和独立网页共用同一协议。运行实例的 Windows 账户决定 WMI、性能计数器、Packet Monitor 和虚拟机控制台的实际权限。

## 部署与运行

在安装有 VS 2026 MSBuild 和 .NET 11 SDK 的 Windows 开发机上，从仓库根目录运行以下命令。默认发布包含运行时。将对应架构的**整个发布目录**复制到宿主机。

```powershell
.\src\ExHyperV.Server\Build.ps1 -Runtime win-arm64
.\src\ExHyperV.Server\Build.ps1 -Runtime win-x64
```

产物位于 `.cache/server/win-arm64` 和 `.cache/server/win-x64`。交互使用时，在发布目录运行 `.\ExHyperV.Server.exe`，用 WoW 或自己的前端连接默认地址 `http://127.0.0.1:5191`。前端自行部署。Ctrl+C 会停止服务，重定向标准输入时也可以单独发送一行 `shutdown`。

无头使用时可以直接运行同一程序，或在管理员 PowerShell 中注册服务。以下示例保存本机监听配置，并允许来自指定 HTTPS 站点的前端访问。先在宿主机设置访问口令。

```powershell
.\ExHyperV.Server.exe --set-password
.\ExHyperV.Server.exe --url http://127.0.0.1:5191 --cors-origins https://wow.example.org --save-config
.\ExHyperV.Server.exe --install-service --service-name ExHyperV.Server
Start-Service ExHyperV.Server
```

SCM 将当前可执行文件的绝对路径注册为自动启动服务，启动参数包含 `--service`、服务名及绝对状态目录。默认以 LocalSystem 身份运行。请将发布目录放在只有管理员可写的位置。服务名首位为 ASCII 字母或数字，全名可含字母、数字、点、下划线和连字符，最多 80 位。卸载前先停止服务并等待活动会话退出。

```powershell
Stop-Service ExHyperV.Server
.\ExHyperV.Server.exe --uninstall-service --service-name ExHyperV.Server
```

状态目录优先取 `--state-dir`，其次取 `EXHYPERV_SERVER_STATE_DIRECTORY`，默认是 `%ProgramData%\ExHyperV.Server`。默认位置检查重解析点、所有者和写入 ACL。自定义位置由部署方设置目录和文件权限。运行设置、口令哈希分别存入 `server.json` 和 `auth.json`。以下是一个完整运行配置。

```json
{
  "url": "http://127.0.0.1:5191",
  "corsOrigins": ["https://wow.example.org"],
  "readOnly": false,
  "toolsets": ["core", "host", "vm", "devices", "console", "utilities"]
}
```

显式 CLI 参数覆盖文件的对应字段。列表整体替换。`--read-only` 和 `--read-write` 可显式选择访问模式。运行 `--save-config` 会校验并原子保存合并结果。损坏的文件须先修复。修改后重启服务。安装命令中显式提供的 URL、工具组、访问模式及 Origin 会成为服务启动参数，之后仍优先于文件。需要随文件变更生效的字段，请通过 `--save-config` 设置。

## 监听、鉴权与浏览器连接

- `--url` 接受 HTTP 或 HTTPS 的 IP 地址或 localhost。使用 IP 或 localhost 作为请求 Host。绑定通配 IP 时，客户端应使用实际宿主 IP。跨机器访问需要预先设置口令，或为服务进程设置 `EXHYPERV_SERVER_TOKEN`。根令牌由 32 至 512 位字母、数字或 `-._~+/=` 字符组成。远程部署应使用 HTTPS 与 WSS，可通过 `Kestrel__Certificates__Default__Path` 及 `Kestrel__Certificates__Default__Password` 为进程配置服务器证书。
- 仅回环监听且未配置凭证时，本机进程可以直接调用。配置凭证后，除健康检查和登录外均要求鉴权。使用 `Authorization: Bearer <token>` 发送根令牌或登录取得的会话令牌。会话有效期为 24 小时。连续登录失败触发 HTTP 429，按 `Retry-After` 重试。
- 服务在 WebSocket 升级前校验 Host、Origin 和凭证。同源请求可直接访问。跨源前端通过 `--cors-origins` 指定逗号分隔的精确 HTTP 或 HTTPS Origin，含协议、主机和可选端口。服务处理 OPTIONS 预检。来源或主机被拒返回 403，缺少有效凭证返回 401 并附 `WWW-Authenticate: Bearer`，已鉴权的普通 GET 访问 WebSocket 路由返回 400。
- 浏览器先调用 `POST /api/v1/auth/ws-ticket`，再把响应的 `ticket` 放入 WebSocket URL。每张票仅供一次握手，30 秒后过期。每条连接及每次重连分别领票。会话吊销后，其未使用票据同步失效。长期令牌、根令牌和口令只经请求头或登录请求体传递。反向代理和访问日志应隐藏票据查询参数。

以下 JavaScript 使用前端已持有的会话令牌连接。部署时将 origin 替换为服务实际地址。

```js
const origin = "https://192.0.2.10:5191"
async function openStream(path, token) {
  const response = await fetch(origin + "/api/v1/auth/ws-ticket", {
    method: "POST", headers: { Authorization: "Bearer " + token }
  })
  if (!response.ok) throw new Error("领取票据失败 " + response.status)
  const url = new URL("/ws/v1/" + path, origin)
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:"
  url.searchParams.set("ticket", (await response.json()).ticket)
  const socket = new WebSocket(url)
  socket.binaryType = "arraybuffer"
  return socket
}
```

## HTTP 契约

下表路径均相对于 `/api/v1`。请求体和普通返回体使用 JSON。无内容响应为 204。传输或参数错误通常含 `error`。Core 调用还要检查 `success`、`errorKind` 和 `error`。具体参数和返回模式由运行实例的 `GET /api/v1/openapi.json` 提供。

| 方法与路径 | 请求和返回 |
| --- | --- |
| GET /health | 返回 status、version、authRequired |
| GET /server/info | 返回管理员状态、鉴权模式、工具组、只读模式、监听地址、架构、进程号和 Core 契约指纹 |
| GET /openapi.json | OpenAPI 3.0.3 路由文档 |
| GET /meta/guide | Core 原始 Markdown 指南 |
| GET /meta/field-help | Core 字段帮助 JSON |
| POST /auth/login | 请求提供 password 或 token，可加 clientName。响应含 sessionId、token、expiresAt、clientName |
| GET /auth/sessions | 有效会话元数据列表 |
| DELETE /auth/sessions/{sessionId} | 吊销该会话及未使用票据 |
| POST /auth/logout | 结束当前会话 |
| POST /auth/password | 请求含 password，保存新口令并吊销会话和票据。需要写入权限 |
| POST /auth/ws-ticket | 空请求体，返回 ticket 和 expiresAt |
| GET /core/catalog | 按 search、group、kind、includeAuxiliary、readOnlyOnly 筛选。offset 从 0 起，limit 为 1 至 100，默认 30。返回 total、offset、limit、entries |
| GET /core/describe?apiId=… | 返回成员签名、参数、输入或值的 Schema、字段说明及异常 |
| POST /core/call | apiId 选择方法或构造函数。arguments 按参数名传值。实例调用提供 targetObjectId。可选 typeArguments、methodArguments、retainResult、background。同步返回 operationId 和结果，后台返回 202 和 operationId |
| POST /core/get | apiId 选择属性或字段，可带 targetObjectId、typeArguments 及索引器 arguments。返回 value |
| POST /core/set | 在 get 参数基础上提供 value，写入后返回 success |
| GET /objects | 返回保留对象的 handle、type、owned 和 activeCalls |
| DELETE /objects/{objectId} | 等待对象释放并返回结果。对象有活动调用或订阅时可能返回 409 |
| GET /binaries/{objectId} | 返回二进制数据、PNG 画面或 JPEG 截图，按 Content-Type 解码 |
| GET /operations | 返回操作摘要列表 |
| GET /operations/{operationId} | 获取状态、result、pendingCallbacks 及事件批次。eventLimit 为 1 至 128，默认 30 |
| POST /operations/{operationId}/reply | 请求体含 requestId 和符合回调类型的 value |
| POST /operations/{operationId}/cancel | 请求取消，终态以操作状态为准 |
| DELETE /operations/{operationId} | 删除已结束操作的记录 |
| POST /events/subscribe | apiId 选择事件，实例事件提供 targetObjectId。返回 subscriptionId |
| GET /events/{subscriptionId} | 取出事件批次。limit 为 1 至 128，默认 30 |
| DELETE /events/{subscriptionId} | 解绑事件并释放尚在队列中的租约 |

先检索目录并读取目标成员的 Schema，再调用成员。使用 `{"$handle":"对象 GUID"}` 引用保留对象。抽象模型和接口参数也可用 `$type` 指定可赋给该参数的 Core 具体类型，并提供其构造参数。对于含 `ref` 或 `out` 的方法，响应的 `result.result` 是方法返回值，`result.refArguments` 按参数名给出调用后的值。终态包括 `Completed`、`Failed`、`Cancelled` 和 `RequiresVerification`。出现 `RequiresVerification` 时，先核对实际宿主状态再决定后续操作。

二进制输入可用 Base64。默认情况下，至多 65536 字节的字节数组结果以 Base64 返回。更大的数组返回含 `$handle`、`length` 和 `href` 的对象。通过 `GET /api/v1/binaries/{objectId}` 可下载字节数组、可读且可定位的流、PNG 控制台画面或 JPEG 截图。下载结束后调用 `DELETE /api/v1/objects/{objectId}` 释放对象。

对于默认值为 `null` 的 Core 决策回调，省略该参数会沿用默认值。在 `/core/call` 的 `arguments` 中显式传入 `{"$callback": true}`，并设置 `background: true`。Core 请求决定时，操作进入 `AwaitingInput`。读取 `pendingCallbacks` 中的 `requestId`，向 `POST /api/v1/operations/{operationId}/reply` 提交该编号和符合回调返回类型的 `value`，操作会继续执行。

导入、导出、挂载 ISO 和管理 VHDX 时，文件路径指宿主机本地路径，或运行服务的 Windows 账户可访问的路径。HTTP 单请求体上限为 16 MiB。服务不提供大文件分片上传接口，请先将镜像或归档放到宿主机可访问的位置，再在调用参数中传入路径。

## 实时事件

连接 `GET /ws/v1/events`。每个文本消息是一个 JSON 对象。三个 WebSocket 端点均支持 `{"type":"ping"}` 并返回 `{"type":"pong"}`。无效指令返回 `{"type":"error","error":"说明"}`。

| 客户端 type | 其余字段 | 服务端推送 |
| --- | --- | --- |
| subscribeOperation | operationId | type 为 operation，含 operationId 和 status。内容与 HTTP 操作状态相同 |
| subscribeEvent | subscriptionId | type 为 event，含 subscriptionId 和 batch |
| unsubscribe | operationId 或 subscriptionId | type 为 unsubscribed，含 id |

订阅后先收到当前快照，后续由变化唤醒。相邻状态可能合并，使用最新 status 渲染。新增回调位于 `status.pendingCallbacks`，使用 HTTP reply 回答。操作监听结束后操作本身继续执行。一个 Core 事件编号可由一条 WebSocket 接管，退订或断线时服务解绑该事件，重连时重新创建订阅。

批次包含 `events`、`dropped`、`remaining`、`resourceLimitReached`。每条记录含 sequence、time、name、arguments 和可空的 requestId。队列通常保留最近 128 项，画面事件只保留最新帧，dropped 是该队列累计替换数量。HTTP 读取和 WebSocket 推送共同消费同一队列，多个独立事件消费者应各自创建订阅。WebSocket 推送的帧句柄可在监听期间读取，结束监听时服务回收该监听仍保留的帧。资源受限时，主动释放已读对象可恢复推送。

## 遥测

连接 `GET /ws/v1/telemetry` 并发送订阅。每次成功 subscribe 替换该连接原有订阅。收到 subscribed 时，旧采样器已释放。

```json
{"type":"subscribe","host":true,"vmIds":["00112233-4455-6677-8899-aabbccddeeff"],"metrics":["cpu","memory","disk","network","gpu","hostThreads"],"intervalMs":1000}
```

宿主目标需要 host 工具组，VM 目标需要 vm 工具组。vmIds 最多 256 项，hostThreads 仅用于宿主机。至少选择一个目标和一个指标。默认间隔 1000 毫秒，服务端将输入限制到 500 至 10000 毫秒，在 subscribed.intervalMs 中返回实际值。每轮完成后等待该间隔，慢采样不会重叠。退订发送 `{"type":"unsubscribe"}`，服务等待当前本地读取与释放完成后回复 unsubscribed。离开监控页时退订或关闭连接。

推送形如 `{"type":"telemetry","timestamp":"2026-01-01T00:00:00Z","host":{"memory":{"availableMiB":4096,"totalMiB":8192}},"vms":{}}`。host 为未订阅时的 null 或按指标名索引的对象。vms 以 VM GUID 为键，内部再按指标名索引。

- 宿主 cpu 按逻辑核心给出使用率和样本状态。memory 使用 MiB。disk 按物理盘号给出活动比例和读写字节速率。network 按接口 ID 给出吞吐及采样状态。gpu 按设备实例给出可用引擎的占用百分比。hostThreads 包含线程和消耗者归属报告。
- VM 指标使用 Core 字段投影，values 以字段 ID 为键，collections 包含子设备行。值中有 state、observedAtUtc、value 和 issue。网络适配器可附带按 MAC 观察到的 IPv4 地址及 ARP 捕获问题。
- 样本级失败返回 state 和 issue，其他指标继续推送。绘图时仅使用有效读数。预热、无观测值、空集合和有效的零读数具有不同含义。

## 原生虚拟机控制台

连接 `GET /ws/v1/console/{vmId}`，vmId 是非空 GUID。需要 console 工具组及写入权限。默认同时允许 16 条控制台连接，已满时升级前返回 429。

| 查询参数 | 默认值 | 接受值 |
| --- | --- | --- |
| backend | RdpBasic | RdpBasic 或 RdpEnhanced |
| width、height | 1024、768 | 各 200 至 8192，宽 × 高 × 4 至多 64 MiB 减 64 字节 |
| desktopScale | 100 | 整数 100 至 500 |
| connectTimeoutMs | 2000 | 整数 1 至 120000，表示原生连接超时 |
| frameEncoding | png | png、bgra、deflate |
| hostCursor | false | true、false（1 或 0），true 时分离指针形状与视频帧 |
| clipboard | true | true、false（1 或 0），是否在增强会话中启用剪贴板重定向 |
| audioOutput | true | true、false（1 或 0），是否在增强会话中启用声音输出重定向 |
| audioInput | true | true、false（1 或 0），是否在增强会话中启用麦克风输入重定向 |

### 文本控制消息

服务端 state 消息含 state、errorCode 和 error。典型顺序为 Connecting、Connected、LoginCompleted、Disconnected。capabilities 消息报告 backendId、supportsResize、clipboardAvailable、audioOutputAvailable、audioInputAvailable、audioInputFormat。能力可能在连接建立后变化。开启 hostCursor 时，指针变更推送 cursor 消息（含 visible，可见时另含 width、height、hotX、hotY 和 PNG Base64）。resizeRejected 含 errorCode，本地拒绝为 0。clipboard 与客户端同形。error 含错误说明。

| 客户端 type | 字段和行为 |
| --- | --- |
| key | scanCode 为 UInt16，down 为布尔值，可选 extended，默认 false |
| keys | events 数组，每项采用 key 的按键字段，一批 1 至 256 项 |
| pause、releaseAllInputs | 分别发送 Pause 键、释放全部输入。前端失焦时应释放按键 |
| syncKeyboard | flags 为锁定键位图，ScrollLock=1，NumLock=2，CapsLock=4，KanaLock=8 |
| pointer | x、y 是来宾桌面像素坐标，UInt16。button 可为 None、Left、Right、Middle，默认 None。down 默认 false，wheel 为有符号 Int16，默认 0 |
| click | x、y、可选 button，默认 Left |
| resize | width、height 和可选 desktopScale，限制同握手参数 |
| clipboard | formats 数组，最多 3 项。每项含 id、可空 name 和 Base64 编码的 data |
| captureFrame | 请求最近画面，成功后下发完整视频帧，可用来重建客户端画布 |

剪贴板支持 ID 13 的 UTF-16LE 文本（长度为偶数且以两个零字节终止）、ID 8 的 DIB、ID 0xC000 至 0xFFFF 且 name 为 `HTML Format` 的 HTML。两种标准格式的 name 为空。每种格式只能出现一次，单格式最多 16 MiB。计入 Core 格式描述和名称后的总量最多 32 MiB。

### 二进制消息

一个完整 WebSocket 消息承载一个视频或音频包。所有多字节字段均为小端序。解析 64 位序号时在 JavaScript 中可用 `DataView.getBigInt64(offset, true)`。

| 偏移 | 字节 | 公共字段 |
| ---: | ---: | --- |
| 0 | 2 | 固定 0x45、0x58，即 EX |
| 2 | 1 | Type，1 表示视频，2 表示服务端音频输出，3 表示客户端音频输入 |
| 3 | 1 | Flags，视频 bit 0 为整帧标志，其余位为 0。音频为 0 |
| 4 | 1 | Format，视频 0=BGRA32、1=PNG、2=DeflateBGRA。音频 1=Signed16LE、2=Float32LE |
| 5 | 1 | Channels，视频为 0，音频为 1 至 8 |

| 视频偏移 | 字节 | 字段 |
| ---: | ---: | --- |
| 6 | 8 | Sequence，Int64 |
| 14 | 8 | BaseSequence，Int64 |
| 22 | 2 | TotalWidth，UInt16 |
| 24 | 2 | TotalHeight，UInt16 |
| 26 | 2 | RectX，UInt16 |
| 28 | 2 | RectY，UInt16 |
| 30 | 2 | RectWidth，UInt16 |
| 32 | 2 | RectHeight，UInt16 |
| 34 | 至消息末尾 | 矩形的图像载荷 |

整帧重设画布尺寸并覆盖画布。局部帧只更新指定矩形，左上角为原点。BGRA 每像素 4 字节，自上向下逐行紧密排列。Deflate 是同一字节布局的 raw DEFLATE，可使用 `DecompressionStream("deflate-raw")` 解码。PNG 可直接解码为矩形位图。前端应保持解码和绘制顺序。当尺寸变化或 Core 的 BaseSequence 与上一已发 Sequence 不连续时，服务自动发送整帧。客户端自行跳过消息后，可通过 captureFrame 请求恢复画面。

| 音频偏移 | 字节 | 字段 |
| ---: | ---: | --- |
| 6 | 4 | SampleRate，Int32，每秒采样帧数 |
| 10 | 8 | Timestamp，Int64，流内相对时间，单位 100 纳秒 |
| 18 | 至消息末尾 | 声道交错排列的小端 PCM 数据 |

上行使用 Type 3、非负时间戳和 capabilities 消息中 audioInputFormat 对应的格式、声道数与采样率。audioInputAvailable 变为 false 时暂停麦克风，恢复为 true 时按新的协商格式发送。采样率为 8000 至 192000。载荷非空，至多 1 MiB，长度须为声道数乘以每样本字节数的整数倍。输出使用 Type 2。处理音频时按包头读取格式。下行发送队列保留最近 8 个待发音频包，积压时丢弃最旧的包。

## 验证发布

```powershell
.\ExHyperV.Server.exe --help
.\ExHyperV.Server.exe --verify-contracts
.\ExHyperV.Server.exe --api-catalog
```

契约校验成功返回 0。运行 `src/ExHyperV.Server.Tests/Test-Server.ps1` 可验证全部公开成员的描述和 Schema，并通过真实回环 HTTP 与 WebSocket 检查登录、管理回调、事件、遥测和控制台。测试使用模拟数据和独立状态目录。服务运维测试核对 SCM 命令参数，并按生成的启动命令运行服务模式子进程。
