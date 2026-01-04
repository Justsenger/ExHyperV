
# 完整手动执行指南

## 第一部分：宿主机（Windows）操作

### 步骤 1：配置虚拟机参数

打开 **PowerShell（管理员）**，执行：

```powershell
# 替换 '你的虚拟机名称' 为实际名称
$VMName = '你的虚拟机名称'

# 配置内存映射
Set-VM -GuestControlledCacheTypes $true -VMName $VMName
Set-VM -HighMemoryMappedIoSpace 64GB -VMName $VMName
Set-VM -LowMemoryMappedIoSpace 1GB -VMName $VMName
```

### 步骤 2：获取可用的 GPU 路径

```powershell
# Windows 11 / Server 2022+
Get-VMHostPartitionableGpu | Select-Object Name

# Windows 10 / Server 2019
# Get-VMPartitionableGpu | Select-Object Name
```

输出类似：
```
\\?\PCI#VEN_10DE&DEV_2684&SUBSYS_...
```

### 步骤 3：添加 GPU 分区

```powershell
# Windows 11（指定GPU路径）
Add-VMGpuPartitionAdapter -VMName $VMName -InstancePath '上一步获取的GPU路径'

# Windows 10（不需要指定路径）
# Add-VMGpuPartitionAdapter -VMName $VMName
```

### 步骤 4：启动虚拟机

```powershell
Start-VM -Name $VMName
```

---

## 第二部分：Linux 虚拟机操作

通过 SSH 连接到虚拟机。

### ⚠️ Arch Linux 用户特别说明：内核版本兼容性

**重要提示**：根据项目兼容性测试，Ubuntu 22.04（内核 6.8.x）可以成功运行，而 Ubuntu 24.04（内核 6.14.x）失败。Arch Linux 的滚动更新特性意味着内核版本必然较新（2026-01-01已经是 6.18），我个人测试下来是不行的。

想必各位都非要用ArchLinux了，自行在AUR上拉一个内核编译替换也不是什么很难的事情，各位arch玩家也可以自行探索一切可能可行的内核方案，我采用的是linux-lts66，参考文件：
https://wiki.archlinux.org.cn/title/Kernel
https://linuxkernel.org.cn/
https://aur.archlinux.org/packages/linux-lts66


### 步骤 1：安装基础依赖

**Debian/Ubuntu:**
```bash
sudo apt update
sudo apt install -y git curl dkms build-essential linux-headers-$(uname -r)
```

**Arch Linux:**
```bash
sudo pacman -Sy
sudo pacman -S --noconfirm git curl dkms base-devel
```

### 步骤 2：创建工作目录

```bash
mkdir -p ~/gpu-pv && cd ~/gpu-pv
```

### 步骤 3：下载并编译 dxgkrnl 模块

```bash
# 下载脚本
wget --no-check-certificate -O install_dxgkrnl.sh https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/install_dxgkrnl.sh
chmod +x install_dxgkrnl.sh

# 执行（忽略最后的检查错误）
sudo ./install_dxgkrnl.sh
```

> ⚠️ 如果脚本输出 `STATUS: REBOOT_REQUIRED`，需要重启后再次运行此脚本。
> 
> ⚠️ 如果最后报错但日志显示 `Installing to /lib/modules/...`，说明实际成功了，继续下一步。

### 步骤 4：验证模块安装

```bash
# 检查模块文件（使用 modinfo，兼容所有发行版）
modinfo dxgkrnl

# 或者手动检查文件位置：
# Debian/Ubuntu:
#   ls -la /lib/modules/$(uname -r)/updates/dkms/dxgkrnl.ko
# Arch Linux (注意 .ko.zst 压缩格式):
#   ls -la /usr/lib/modules/$(uname -r)/updates/dkms/dxgkrnl.ko.zst

# 加载模块
sudo modprobe dxgkrnl

# 验证模块已加载
lsmod | grep dxgkrnl

# 验证设备已创建
ls -la /dev/dxg
```

### 步骤 5：从宿主机复制驱动文件

**在 Windows 宿主机上**，找到 GPU 驱动目录：
```
C:\Windows\System32\DriverStore\FileRepository\nv_dispsi.inf_amd64_xxxxxxxx\
```

**方法 A**：使用 SCP 命令（在 Windows PowerShell 中执行）

```powershell
# 找到驱动目录
$DriverPath = Get-ChildItem "C:\Windows\System32\DriverStore\FileRepository" -Filter "nv_dispsi.inf_amd64_*" | Select-Object -First 1 -ExpandProperty FullName

# 复制到虚拟机（替换 用户名@IP）
scp -r $DriverPath 用户名@虚拟机IP:~/gpu-pv/drivers/
```

**方法 B**：使用 WinSCP 等图形化工具复制

### 步骤 6：复制核心库文件

**如果宿主机安装了 WSL**，从 `C:\Windows\System32\lxss\lib\` 复制所有文件到虚拟机的 `~/gpu-pv/lib/`

**如果没有 WSL**，在虚拟机中下载：

```bash
mkdir -p ~/gpu-pv/lib && cd ~/gpu-pv/lib

wget --no-check-certificate https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/lib/libd3d12.so
wget --no-check-certificate https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/lib/libd3d12core.so
wget --no-check-certificate https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/lib/libdxcore.so
```

### 步骤 7：部署驱动和库文件

```bash
# 创建系统目录
sudo mkdir -p /usr/lib/wsl/drivers /usr/lib/wsl/lib

# 复制驱动
sudo cp -r ~/gpu-pv/drivers/* /usr/lib/wsl/drivers/

# 复制库文件
sudo cp -a ~/gpu-pv/lib/* /usr/lib/wsl/lib/

# 创建符号链接
sudo ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so

# 设置权限
sudo chmod -R 0555 /usr/lib/wsl
sudo chown -R root:root /usr/lib/wsl

# 配置动态库路径
echo "/usr/lib/wsl/lib" | sudo tee /etc/ld.so.conf.d/ld.wsl.conf
sudo ldconfig
```

### 步骤 8：配置内核模块自动加载

```bash
# vgem 模块
echo "vgem" | sudo tee /etc/modules-load.d/vgem.conf
sudo modprobe vgem

# dxgkrnl 延迟加载（避免启动冲突）
echo "blacklist dxgkrnl" | sudo tee /etc/modprobe.d/blacklist-dxgkrnl.conf

# 创建延迟加载脚本
sudo tee /usr/local/bin/load_dxg_driver.sh > /dev/null << 'EOF'
#!/bin/bash
modprobe dxgkrnl
if [ -e /dev/dxg ]; then
    chmod 666 /dev/dxg
fi
EOF
sudo chmod +x /usr/local/bin/load_dxg_driver.sh

# 创建 systemd 服务
sudo tee /etc/systemd/system/load-dxg-late.service > /dev/null << 'EOF'
[Unit]
Description=Late load dxgkrnl
After=multi-user.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/load_dxg_driver.sh

[Install]
WantedBy=multi-user.target
EOF

# 启用服务
sudo systemctl daemon-reload
sudo systemctl enable load-dxg-late.service

# 更新 initramfs
# Debian/Ubuntu:
sudo update-initramfs -u
# Arch Linux:
# sudo mkinitcpio -P
```

### 步骤 9：重启

```bash
sudo reboot
```

---

## 第三部分：验证

重启后 SSH 连接，执行：

```bash
# 检查模块
lsmod | grep dxgkrnl

# 检查设备
ls -la /dev/dxg

# 检查驱动目录
ls -la /usr/lib/wsl/drivers/ | head -20
ls -la /usr/lib/wsl/lib/

# 测试 nvidia-smi（如果有）
nvidia-smi

# 测试 PyTorch CUDA
python3 << 'EOF'
import torch
print(f"CUDA available: {torch.cuda.is_available()}")
if torch.cuda.is_available():
    print(f"Device name: {torch.cuda.get_device_name(0)}")
    print(f"Device count: {torch.cuda.device_count()}")
EOF
```

