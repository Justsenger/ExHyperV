# 网站与 Core 文档联动

网站与 Core 在同一仓库维护，共用一个本地网站入口和一份部署产物。

```text
ExHyperV/
├─ src/ExHyperV.Core/        Core 源码与接口 XML 注释
├─ website/                 Astro 首页、系统图鉴、文档入口与静态资源
│  ├─ public/api/           本地生成的 API HTML、Markdown 和 manifest（不提交）
│  └─ dist/                 含 /api/ 的完整网站（不提交）
├─ 启动文档.cmd             Windows 一键启动完整网站
└─ .github/workflows/website.yml
```

## 日常使用

需要 Node.js 24.19+（24.x）、pnpm 11.25.0、.NET 10 SDK 和 Git。在主仓库双击 `启动文档.cmd`，或运行：

```sh
node website/scripts/start.mjs
```

首次启动自动安装网站依赖。随后自动更新 Core `API.md` 的生成区块，导出 API 网页，启动 `http://localhost:5189/`。在其他平台也可使用上述 Node 命令。Node.js、pnpm、.NET SDK 和 Git 需要已安装并加入 PATH。

首页 `/`、系统图鉴 `/os/`、文档 `/docs/` 和 API `/api/` 全部由同一个服务提供。访问 API 是读取站内页面，不会跳转到 Core 仓库。导航使用当前主机和端口。`/sdk/` 已删除。

服务监听所有 IPv4 网卡，内网通过 `http://本机IP:5189/` 访问；防火墙需允许此端口。修改网站页面后 Astro 自动更新；修改 Core 源码注释、doc/api/guide.md、示例、样式或模板后，文档会重新生成并刷新。生成失败时终端显示原因，保留最后一次成功的 API 页面；修正并保存后自动重试。生成过程只分析源码，不运行 Core 或示例。

首页、图鉴和文档入口改 `website/src/`；API 说明改 Core 注释和 `doc/api/`；网页模板与样式改 `website/src/api/`，具体见 [Core 文档维护说明](../../doc/api/README.md)。生成区块不手工编辑。提交时检查 `doc/api/API.md` 的差异，与源码一起保存。

## 构建和校验

在 `website/` 执行：

```sh
pnpm install --frozen-lockfile
pnpm api:sync --write
pnpm build
pnpm verify
```

`api:sync --write` 用于本地更新 Markdown；开发服务已自动完成这一步。`pnpm build` 只检查并导出，不修补过期文档；CI 同样要求文档已经更新并提交。构建总是读取相邻的 `../src/ExHyperV.Core`，不设置本机绝对路径、不下载另一仓库、不依赖文档产物分支。

文档包校验 HTML/Markdown 的 SHA-256、文件长度、版本和接口数量后才替换 `public/api/`。`/api/manifest.json` 记录源码提交和内容摘要；本地未提交变化标记为 `dirty: true`，CI 禁止发布这种产物。CI 的 detached checkout 和功能分支均可构建本提交的文档。

`pnpm preview` 可查看 `dist/` 的部署效果，也使用 5189 端口；需先停止开发服务。平时无需同时运行两种预览。

## Cloudflare 接入

主仓库 `.github/workflows/website.yml` 在 `2.0.0` 的网站或 Core 变更时构建，PR 也会校验。完整产物保存为 `exhyperv-website`。默认不会发布上线。

上线时只需一次配置：

1. 确保网站源码、Core 文档和工作流已提交到待部署分支。
2. 在主仓库 Actions Secrets 设置 `CLOUDFLARE_API_TOKEN`（目标账户的 Workers 编辑权限）和 `CLOUDFLARE_ACCOUNT_ID`。不要将凭据写入仓库。
3. 在 Actions Variables 设置 `CLOUDFLARE_WORKER_NAME`，填写实际要更新的 Worker 名称。工作流使用此名称覆盖 `wrangler.jsonc` 的默认值，不猜测测试或生产项目。
4. 如果该 Worker 已配置其他自动构建流程，停用重复的部署入口。由此仓库的 GitHub Actions 构建并直接部署，Cloudflare 无需重新构建源码。
5. 手动运行 **Website and API documentation**，选择 `2.0.0` 并勾选 `deploy`；校验和构建通过才会部署。
6. 首次部署成功后，将仓库变量 `WEBSITE_DEPLOY_ENABLED` 设为 `true`，之后推送 `2.0.0` 的相关改动就会自动更新整站。保持未设置或 `false` 时，只构建、保存产物。

此流程使用 [Cloudflare 官方 GitHub Actions 集成](https://developers.cloudflare.com/workers/ci-cd/external-cicd/github-actions/)。.NET 只在 GitHub 构建环境运行，Cloudflare 提供 `website/dist/` 静态文件。Core 推送后需要等待构建和部署完成，线上才会更新。
