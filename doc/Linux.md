# Linux 部署 GPU-PV 参考（ExHyperV）


## 1. 适用范围

- 适用于 Hyper-V 虚拟机内 Linux 的 GPU-PV 部署。

## 2. ExHyperV 自动部署真实执行链

无论是否勾选“安装 OpenGL/Vulkan 图形驱动”，都会执行：

1. `install_dxgkrnl.sh`（编译/安装 dxgkrnl）
2. `configure_system.sh`（部署 `/usr/lib/wsl`、配置延迟加载服务等）

勾选“安装 OpenGL/Vulkan 图形驱动”时，额外执行：

1. `setup_graphics.sh`
2. `configure_system.sh enable_graphics`

未勾选时执行：

1. `configure_system.sh no_graphics`

说明：
- `setup_graphics.sh` 只在勾选图形驱动时运行。
- `configure_system.sh` 始终运行，但图形相关配置（如 Arch 下 Xorg `kmsdev` 修复、`GALLIUM_DRIVER`、`LIBVA_DRIVER_NAME`）仅在 `enable_graphics` 分支处理。

## 3. Arch Linux 内核建议（LTS66）

Arch 滚动内核可能出现兼容性波动。建议优先使用 LTS66 方案。  
已编译可复用版本：

- https://github.com/Micro-ATP/hyperv-gpupv-kernel/releases/tag/Arch-x860Lts66-1



## 4. 宿主机（Windows）准备

管理员 PowerShell：

```powershell
$VMName = "你的虚拟机名称"

Set-VM -GuestControlledCacheTypes $true -VMName $VMName
Set-VM -HighMemoryMappedIoSpace 64GB -VMName $VMName
Set-VM -LowMemoryMappedIoSpace 1GB -VMName $VMName

# Windows 11 / Server 2022+
$GpuPath = (Get-VMHostPartitionableGpu | Select-Object -First 1 -ExpandProperty Name)
Add-VMGpuPartitionAdapter -VMName $VMName -InstancePath $GpuPath

# Windows 10 / Server 2019（可改用）
# Add-VMGpuPartitionAdapter -VMName $VMName
```

## 5. 手动部署（与脚本保持一致）

### 5.1 下载脚本

```bash
mkdir -p ~/exhyperv_deploy
cd ~/exhyperv_deploy

curl -fL -o install_dxgkrnl.sh https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/install_dxgkrnl.sh
curl -fL -o setup_graphics.sh https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/setup_graphics.sh
curl -fL -o configure_system.sh https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/configure_system.sh
chmod +x *.sh
```

### 5.2 准备驱动目录

将宿主机 `DriverStore/FileRepository` 对应显卡目录复制到：

```bash
~/exhyperv_deploy/drivers/
```

### 5.3 执行部署

```bash
sudo ./install_dxgkrnl.sh
```

若输出 `STATUS: REBOOT_REQUIRED`，先重启，再执行一次 `install_dxgkrnl.sh`。

仅当你需要 OpenGL/Vulkan 时：

```bash
sudo ./setup_graphics.sh
sudo ./configure_system.sh enable_graphics
```

若不需要 OpenGL/Vulkan：

```bash
sudo ./configure_system.sh no_graphics
```

最后重启：

```bash
sudo reboot
```

## 6. Arch 图形栈（勾选图形驱动时）

`setup_graphics.sh` 在 Arch 分支会做：

- 安装 `mesa mesa-utils vulkan-tools vulkan-icd-loader libva-utils libva-mesa-driver`
- 优先安装 `vulkan-dzn`，否则尝试 `vulkan-swrast`
- 检测 DZN ICD（包括 `dzn_icd.json`）并写入 `VK_ICD_FILENAMES`

## 7. 验证命令

基础验证：

```bash
lsmod | grep dxgkrnl
ls -l /dev/dxg
systemctl status load-dxg-late.service --no-pager
```

Vulkan 验证：

```bash
vulkaninfo --summary | grep -E "driverName|deviceName|driverID"
```

常见成功特征：

- `driverName = Dozen`
- `deviceName = Microsoft Direct3D12 (...)`

## 8. 常见问题

### 8.1 `nvidia-smi` 报 `GPU access blocked by the operating system`

优先检查 `/dev/dxg` 和 `load-dxg-late.service` 状态。  
缺少 `/dev/dxg` 说明 dxg 通道未就绪。

### 8.2 SDDM 报 `Failed to read display number from pipe`

通常是 Xorg `kmsdev` 选错。  
当前脚本在 Arch + `enable_graphics` 且存在 `/dev/dri/card1` 时，会写入：

```text
/etc/X11/xorg.conf.d/20-exhyperv-modesetting.conf
```

### 8.3 Vulkan 报 `Found no drivers`

通常是 ICD 选错（只命中 `nvidia_icd.json`）。检查：

```bash
ls /usr/share/vulkan/icd.d
echo "$VK_ICD_FILENAMES"
```

### 8.4 SSH 下 `glxinfo` 报 `unable to open display`

这是无图形会话下 `DISPLAY` 未设置导致的正常现象，不代表 Vulkan/D3D12 路径失效。

