# ExHyperV 网站

基于 Astro 的静态网站，包含 Windows 和 Ubuntu 系统图鉴。各版本共用按架构切换的图片与描述模板，描述暂时留空。首页和兼容保留的更新日志页面，其主体内容仍为空。API 页面直接使用主仓库 ExHyperV.Core 生成的文档。

全站采用深色与薄荷绿配色，同时保留跟随系统的浅色模式。顶部导航为「首页」「系统图鉴」「文档」；文档页内提供 API 大卡片，链接到 `/api/`。顶部语言选择器暂时隐藏，界面和镜像语言优先级跟随浏览器，不使用旧的手动语言偏好覆盖。界面支持简体中文、繁體中文和 English，其他语种的界面暂时回退英文。API 文档保留 Core 自身的中文内容、搜索、导航和主题设置。

字标使用 Comfortaa 700，英文界面使用 Comfortaa 600，中文使用资源圆体 CN 500，缺字由系统字体回退。中文字体直接取自作者发布的 Resource Han Rounded CN 0.990 Medium 原版 TTF，仅转换为一个完整 WOFF2 文件，保留全部 30,887 个 Unicode 映射。字体许可证及原版来源信息随网站提供。

## 本地开发

运行环境：Node.js 24.19 及以上的 24.x 版本、pnpm 11.25.0、.NET 10 SDK 和 Git，均需加入 PATH。网站与 Core 在同一仓库中维护。

最简单的入口：在主仓库双击 `启动文档.cmd`，或执行 `node website/scripts/start.mjs`。首次自动安装网站依赖，随后生成 API 文档并启动完整网站。首页、图鉴 `/os/`、文档 `/docs/` 和 API `/api/` 共用 `http://localhost:5189/`。服务监听 `0.0.0.0`，内网可通过本机 IP 的 5189 端口访问。

也可以在本目录使用以下命令：

```sh
pnpm install --frozen-lockfile
pnpm dev
pnpm build
pnpm verify
pnpm preview
```

各命令的用途：

- `pnpm install --frozen-lockfile`：按锁定版本安装依赖。
- `pnpm dev`：自动更新 Core `API.md` 的生成区块、生成 API 网页并启动网站；监听 Core 源码、示例和模板变化，成功后刷新页面。
- `pnpm build`：直接从同仓库的 Core 生成并校验 API 文档，再生成整站到 `dist/`；文档过期或生成失败时停止构建。
- `pnpm api:sync`：只生成 API 网页。加 `--write` 可先更新 Core `API.md` 的生成区块；提交前审核文档差异。
- `pnpm api:verify`：校验已导入 API 文档的版本、数量和 SHA-256。
- `pnpm verify`：检查已生成的页面、目录、架构选项、字体和许可证文件，并运行镜像语言选择、来源黑名单及具体文件链接的回归测试。
- `pnpm preview`：启动 Astro 构建预览服务器。

开发与构建预览都使用 5189 端口，二选一启动。日常只需开发入口，不必再启动 Core 的独立 HTML 预览器。文档生成器只读取源码，不运行 Core 或操作虚拟机。

本地审核可在 `.cache/api-review.json` 指定意见服务，例如 `{"origin":"http://127.0.0.1:5186"}`。开发网站会在 API 页面显示该服务的意见小窗，沿用它的记录与处理状态。配置仅接受本机回环地址；未配置时不显示，静态构建也不包含小窗。该配置与意见记录不提交到仓库。

系统图鉴使用 `/os/` 路径；旧 `/wiki/` 页面及跳转均已删除。

## 系统页面与图片

`src/data/system-previews.json` 按版本和架构保存系统页面内容。

架构选项保留在大图上方，Build、WDDM、XDDM 放在图片右侧的文字区；窄屏时文字区顺排到图片下方。描述仍然留空。驱动模型数据表示系统平台支持的模型，不是 Hyper-V 当前显示适配器的实测结果。Windows 10 及更早桌面版的映射依据和推断范围见 `src/data/windows-display-model-evidence.json`；XDDM 不编造数字版本，更早的 Windows、PE、IoT 不按年代或相同 Build 自动套用桌面模型。

没有截图时，将记录的 `image` 设为 `null`。页面共用 `public/images/system-placeholder.svg` 作为背景，系统名称、版本和架构由 HTML 文字渲染，无需按版本生成图片。

真实截图放在 `public/screenshots/`，在对应记录的 `image` 中填写站内路径，并通过 `image_width`、`image_height` 记录实际尺寸。文件名包含产品、版本和架构，例如 `Windows-11-26H2-ARM64.png`；Ubuntu 文件名同时注明 Server 或 Desktop。图片信息只维护在 `src/data/system-previews.json`，新增截图不需要另建图片清单。

## 镜像来源

`src/data/image-sources.json` 按版本和架构保存候选镜像来源。来源显示在大图下方，同一版本、同一架构下，每个来源站点只显示一个入口。

来源记录区分具体版本下载页、组件包、历史合集和检索候选，并保留页面访问、HTTP HEAD 响应及归档元数据的核对结果。页面只显示来源按钮，不显示核对备注提示。

2026-10-24 已撤下不忘初心、HelloWindows 的全部来源，并在 `src/data/image-source-policy.js` 中持久排除这两个站点及迅雷网盘（包括已记录的跳转目标）。Bob Pony、Massgrave 的入口已改为具体安装介质文件或单文件下载页，不再指向首页或总目录。当前保留 113 个来源组、504 个语言变体；同一版本、同一架构、同一来源仍只显示一个链接。未选用明确标注的精简修改版、激活工具、安装器、DVP 差分补丁及不完整多盘套装。

先正确匹配版本和架构，再选择镜像语言。中文界面默认按中文、英文、其他已确认语言、未知语言排序；用户选择英文时优先英文，自动识别其他语言时优先该语言，然后回退中文、英文、其他语言。网页显示语言与镜像语言是独立字段：其他浏览器语言的界面暂回退英文，镜像仍优先匹配用户语言。系统和版本名称保持原样。语言变体保存在来源的 `variants` 中，由构建和浏览器共用的选择函数挑选，切换语言不会生成重复来源按钮。

新来源核对了公开文件名、版本、架构和语言。当前 12 个来源组有中文文件，其余按已确认的英文或其他语言回退。Bob Pony 的 9 个小量文件探测共读取 1,152 字节，其余部分遇到限流或服务错误；这些都不等于完整下载、散列校验或启动验证。当前准确文件与语言变体记录见 `docs/specific-mirror-links-2026-10-24.json`；`docs/alternative-image-sources-2026-10-24.json` 是已被替代的上一轮调研记录。

这些检查不能证明镜像能够完整下载、内容真实完整或能够启动。系统描述仍然留空。

## 源码结构与目录规则

- `src/data/catalog.json`：产品名称、版本 ID、分类、排序及依据。导航调整时，版本 ID 保持稳定。
- `src/data/windows-build-evidence.json`：Windows Build 的来源依据，区分安装研究资料与厂商直接说明。所有 Windows 记录都有 `build_number` 字段：通常为基础系统 Build 字符串；若同一产品系列有多个已记录的基础 Build，则使用数组。目前 Windows 1.x、2.x、3.0 中的 10 条记录仍为 `null`，并标记 `build_status: unverified`，没有用产品版本号冒充 Build。即使主 Build 相同，Service Pack 仍保留各自的目录入口。
- Windows PE 10.0 按 15 个实际 ADK 基础 Build 分组，范围为 10240–28000，每个 Build 分别提供架构选项。10.1.x 等 ADK 工具包版本号及补丁修订号不作为新的 WinPE 系统基础 Build。旧地址 `/os/windows-pe-10-0/` 对应 10240；旧的宽泛 ARM64 别名对应 16299，即当前选录范围中已核对的最早 ARM64 ADK Build。这不代表最早的 ARM64 开发版本。
- `src/data/ubuntu-architecture-evidence.json`：Ubuntu 各 Desktop、Server 版本的架构依据，来自官方镜像归档和 Canonical 关于撤回、重发镜像的公告。界面将 i386 显示为 x86、amd64 显示为 x64。目录范围为通用 x86、x64、ARM64 安装介质，不包含板卡专用镜像、ARM32 和其他处理器架构。记录某架构存在，不代表已在 Hyper-V 中实测启动。
- 每个版本通过 `architectures` 和 `default_architecture` 声明可选架构及默认架构。左侧目录只列版本；架构在版本页面内选择，地址参数为 `?arch=x86`、`?arch=x64` 或 `?arch=arm64`。ARM32 选项和路由已移除。保留的旧架构专用地址作为兼容别名，仍选中同一个版本，不增加独立目录入口。Windows RT、RT 8.1、Phone、Mobile 不在目录中。
- 产品版本类型（例如 Home、Enterprise）不作为筛选项或独立入口。已知 `build_number` 保留在对应版本记录上；原有 36 个版本类型地址通过 `version_aliases` 对应到相同 Build。旧的 `edition` 参数会被丢弃。Windows 10 Team 已删除。页面切换时保留侧栏滚动位置。
- `src/content/wiki/<codename>.md`：可为已有版本添加补充文章；没有对应 Markdown 文件时，补充文章区域保持为空。
- `src/components/`：共用导航、侧栏及页面组件。桌面侧栏固定为 360px。子项标签省略与父级重复的前缀，完整名称仍用于搜索、无障碍名称和名称提示。
- `src/styles/site.css`：全站样式，不使用原始参考网站的 CSS 或 Materialize 打包文件。
- `public/licenses/`：第三方声明和字体许可证。网站所需字体在本地提供，相关依赖版本已锁定。

## Git 版本管理

本目录是 `Justsenger/ExHyperV` 主仓库中的 `website/`，和 Core 一起提交、审核和构建。

源码、许可证、占位图片和 `pnpm-lock.yaml` 纳入 Git。依赖目录、生成的 `dist/`、本地缓存、环境变量密钥和临时托管信息通过 `.gitignore` 排除。

## 镜像来源核对进度

2026-10-25 已按 B 审核记录导入 Windows 来源变更。391 项中落实 389 项，另 2 项新增 26H2 Microsoft 来源因当前收录规则而跳过；新增 106 条候选，6 条候选归入审核指定的正确版本。65 条隐藏 Server 记录继续隐藏，未改动系统目录、架构或系统参数。

原始审核保存在 `docs/reviewed-windows-sources-2026-10-24.json`，逐项导入记录见 `docs/windows-source-import-2026-10-25.json`。`review_import` 保留变更编号与依据；取消默认资格的候选保留记录并设置 `display_eligible: false`，不再出现在来源按钮中。临时 HTTP 429 不自动删除来源。

来源按钮优先使用审核给出的 `source_page_url`，具体介质直链和 P2P/IPFS 信息独立留档；没有对应说明页的旧候选保留原入口。Microsoft 名称统一使用英文品牌名。语言变体仍按浏览器偏好选择，同一来源只显示一个入口，来源排序未另行改为固定站点顺序。

目前的来源集合是调研草稿。2026-10-24 的检查发现，部分候选包含架构错配、驱动或 SDK 下载、第三方修改的救援盘，以及设备专用恢复介质。

`docs/image-source-review.json` 保存了具体问题，供后续逐项修正。记录问题不会自动修改网站当前的来源数据。链接存在、文件响应正常或归档元数据可读取，都不能证明能够安装或兼容 Hyper-V。

## Cloudflare 部署

现有 `wrangler.jsonc` 配置使用 Workers Static Assets 提供 `dist/` 中的静态文件。

主仓库的 `.github/workflows/website.yml` 安装 Node.js 和 .NET SDK，从同一个提交构建整站、校验并保存产物；启用部署后通过 Wrangler 直接上传到指定的 Cloudflare Worker。Cloudflare 运行时只提供静态文件，不运行 Core 或 .NET。

默认只构建，不发布。上线时在主仓库配置 Cloudflare 凭据和 Worker 名称，首次手动部署成功后再开启自动部署。如果 Worker 已配置其他自动构建流程，应停用重复的部署入口。步骤见 [网站与 Core 文档联动](docs/core-api-sync.md)。

`public/api/` 和 `dist/` 都是生成产物，不提交；`/api/manifest.json` 记录 Core 提交、版本、内容哈希和是否包含本地未提交改动。网站不拉取远程文档分支，也不需要 Deploy Hook。

## 字体与品牌素材

字体许可证与产品商标属于不同事项。Windows 和 Ubuntu 图标用于标识图鉴条目，其展示方式需要按各自权利人的品牌政策评估。本仓库不表示获得 Microsoft 或 Canonical 的授权或背书。

相关说明见 `public/licenses/THIRD_PARTY_NOTICES.txt`。
