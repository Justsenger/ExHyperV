# Linux GPU-PV 部署

Core 通过 SSH 将所选 GPU 的宿主驱动文件和 Windows/WSL 库上传到虚拟机，再执行所选发行版的部署脚本。

## 准备文件

Windows/WSL 图形库由用户自己的 Windows 安装提供。本仓库保存部署脚本，运行库使用各自的软件许可。

需要的三个文件：

- `libd3d12.so`
- `libd3d12core.so`
- `libdxcore.so`

Core 沿用 `%SystemRoot%\System32\lxss\lib` 作为上传来源。部署前先确认三个文件存在且与虚拟机架构一致。若系统中缺少这些文件，先通过 Windows 的正式安装或更新渠道准备对应组件。

微软对这些组件的说明：[DirectX on Linux](https://devblogs.microsoft.com/directx/directx-heart-linux/)。项目根目录的 GPL 许可适用于本项目代码，Windows/WSL 二进制的使用遵循其各自许可。

## 通过 ExHyperV 部署

1. 在 GPU 部署流程中选择 Linux 虚拟机，填写 SSH 地址、端口和登录信息。
2. Core 上传宿主 `DriverStore/FileRepository` 到 `/tmp/exhyperv_deploy/drivers/`，上传宿主 WSL 库到 `/tmp/exhyperv_deploy/lib/`。
3. Core 从本项目 `2.0.0` 分支的 `src/ExHyperV.Core/Linux/script/` 获取所选脚本并通过 SSH 执行。
4. 脚本先检查运行库，再安装依赖和内核模块、配置图形环境。缺少运行库时退出并显示缺少的文件名。
5. 脚本要求重启时，Core 按已有部署流程重启虚拟机并继续执行。

## 手动执行

选择与发行版相符的脚本，在虚拟机中准备以下目录：

```text
exhyperv_deploy/
├─ Ubuntu-24.04-Official.sh
├─ drivers/                 宿主 GPU 驱动文件
└─ lib/                     对应架构的 Windows/WSL 库
```

以 Ubuntu 24.04 脚本为例：

```bash
cd ~/exhyperv_deploy
sudo bash Ubuntu-24.04-Official.sh deploy true
```

第二个参数 `true` 表示安装图形栈，`false` 表示跳过该部分；第三个参数可传代理地址。具体兼容范围以所选脚本顶部说明为准。

## 验证

部署结束后，在虚拟机中检查 `/dev/dxg`、`/usr/lib/wsl/lib` 和对应图形工具的输出。按显卡与发行版选择 `nvidia-smi`、`glxinfo -B` 或 `vulkaninfo --summary`。内核、宿主驱动和发行版的组合需要分别验证。

