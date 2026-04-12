#!/bin/bash
# @Name: Ubuntu-24.04-Official
# @Description: CUDA√Mesa×
# @Author: Justsenger
# @Version: 1.0.2

set -e

# ==========================================================
# 0. 辅助函数定义
# ==========================================================
# 更新 /etc/environment 环境变量
update_env() {
    local key=$1
    local val=$2
    sudo sed -i "/^$key=/d" /etc/environment
    sudo sed -i "/^export $key=/d" /etc/environment
    echo "$key=$val" | sudo tee -a /etc/environment > /dev/null
}

# 重试机制
retry_cmd() {
    local n=1
    local max=5
    local delay=5
    while true; do
        if "$@"; then
            break
        else
            if [[ $n -lt $max ]]; then
                echo " -> [WARNING] 命令执行失败，等待 $delay 秒后进行第 $n 次重试: $*"
                sleep $delay
                ((n++))
            else
                echo " -> [ERROR] 已达到最大重试次数 ($max)，执行失败: $*"
                return 1
            fi
        fi
    done
}

# ==========================================================
# 1. 初始化与参数解析
# ==========================================================
ACTION=${1:-"deploy"}
ENABLE_GRAPHICS=${2:-"true"}
PROXY_URL=${3:-""}

# --- 自动识别架构 ---
MACHINE_ARCH=$(uname -m)
case "$MACHINE_ARCH" in
    x86_64)
        ARCH_DIR="x64"
        ;;
    aarch64|arm64)
        ARCH_DIR="arm64"
        ;;
    *)
        echo "[ERROR] Unsupported architecture: $MACHINE_ARCH"
        exit 1
        ;;
esac
echo "[+] Detected architecture: $MACHINE_ARCH (Using repo dir: $ARCH_DIR)"


DEPLOY_DIR="$(dirname $(realpath $0))"
LIB_DIR="$DEPLOY_DIR/lib"
# 补丁与库文件的远程仓库基准地址
PATCH_BASE_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/patches"
GITHUB_LIB_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/lib/$ARCH_DIR"

if [ -n "$PROXY_URL" ]; then
    export http_proxy="$PROXY_URL"
    export https_proxy="$PROXY_URL"
    echo "[+] Using proxy: $PROXY_URL"
fi

# ==========================================================
# 2. 依赖安装
# [适配建议]: 若修改为其他发行版，请替换 apt-get 指令。
# 必须依赖: git, curl, dkms, wget, build-essential(或 gcc/make), unzip, aria2
# ==========================================================
echo "[STEP: Installing basic dependencies...]"
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git curl dkms wget build-essential software-properties-common unzip aria2

# ==========================================================
# 3. 内核检查与头文件
# [适配建议]: 不同发行版的内核头文件包名不同。
# Ubuntu: linux-headers-$(uname -r)
# CentOS/Fedora: kernel-devel-$(uname -r)
# Arch: linux-headers
# ==========================================================
echo "[STEP: Checking Kernel Headers...]"
TARGET_KERNEL_VERSION=$(uname -r)

if [ ! -e "/lib/modules/$TARGET_KERNEL_VERSION/build" ]; then
    echo " -> Kernel headers not found for $TARGET_KERNEL_VERSION. Attempting installation..."
    if ! sudo apt-get install -y -qq "linux-headers-$TARGET_KERNEL_VERSION"; then
        echo " -> Failed to find headers for current kernel. Installing a standard generic kernel instead..."
        # 兜底逻辑：若无法匹配当前微版本，尝试安装最新通用版内核及对应头文件
        NEW_KERNEL_IMAGE=$(apt-cache search "^linux-image-[0-9]" | awk '{print $1}' | grep -E "generic$" | sort -V | tail -1)
        NEW_KERNEL_VERSION=$(echo "$NEW_KERNEL_IMAGE" | sed 's/linux-image-//')
        NEW_KERNEL_HEADERS="linux-headers-$NEW_KERNEL_VERSION"
        sudo apt-get install -y -qq "$NEW_KERNEL_IMAGE" "$NEW_KERNEL_HEADERS"
        echo "[STATUS: REBOOT_REQUIRED]"
        exit 0
    fi
fi

# ==========================================================
# 4. dxgkrnl 模块编译与验证 (核心逻辑 - 自动化 Packaging Layer 适配版)
# [说明]: 
# 1. 根据当前内核版本自动分流，下载对应的“全熟”预制包。
# 2. 预制包已在云端完成所有 API 适配补丁，无需本地 apply_patch。
# 3. 统一使用 DKMS 进行独立模块构建。
# ==========================================================
if lsmod | grep -q "dxgkrnl" || dkms status | grep -q "dxgkrnl"; then
    echo " -> dxgkrnl is already installed or loaded."
else
    echo "[STEP: Preparing Pre-patched Source...]"
    # 获取内核大版本号进行分流
    KERNEL_MAJOR=$(echo $TARGET_KERNEL_VERSION | cut -d. -f1)
    KERNEL_MINOR=$(echo $TARGET_KERNEL_VERSION | cut -d. -f2)
    
    # --- 版本分流决策 ---
    if [[ "$KERNEL_MAJOR" -eq 5 ]]; then
        PKG_VER="5.15"
    elif [[ "$KERNEL_MAJOR" -eq 6 ]]; then
        if [[ "$KERNEL_MINOR" -le 6 ]]; then
            PKG_VER="6.6"
        else
            # 6.7 及其以上版本（如 6.12, 6.17, 6.19）统一使用 6.12 适配包
            PKG_VER="6.12"
        fi
    else
        # 兼容未来的 7.x+
        PKG_VER="6.12"
    fi

    PKG="dxgkrnl-${PKG_VER}-patched"
    echo " -> Detected Kernel $TARGET_KERNEL_VERSION, using pre-patched assets: $PKG"

    # 清理旧工作空间
    rm -rf /tmp/dxg-src /tmp/kernel_src.tar.gz /tmp/$PKG
    
    # 下载云端提取好的全熟包
    ZIP_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/kernel-assets/$PKG.tar.gz"
    echo " -> Downloading from: $ZIP_URL"
    retry_cmd aria2c -x 4 -s 4 --dir=/tmp --out=kernel_src.tar.gz "$ZIP_URL" --allow-overwrite
    
    # 解压
    tar -xzf /tmp/kernel_src.tar.gz -C /tmp/
    
    VERSION="custom"
    sudo rm -rf /usr/src/dxgkrnl-$VERSION
    # 将源码移至 DKMS 目录 (tar包内应包含 dxgkrnl-xx-patched 文件夹)
    sudo cp -r /tmp/$PKG /usr/src/dxgkrnl-$VERSION

    echo "[STEP: Compiling and Installing DXG Module...]"
    
    # 配置独立编译 Makefile
    # 强制修改 Makefile 以支持独立模块编译，并包含内部 include 路径
    sudo bash -c "cat > /usr/src/dxgkrnl-$VERSION/Makefile <<EOF
obj-m := dxgkrnl.o
dxgkrnl-y := dxgmodule.o hmgr.o misc.o dxgadapter.o ioctl.o dxgvmbus.o dxgprocess.o dxgsyncfile.o
ccflags-y := -I\\\$(src)/include -D_MAIN_KERNEL_

all:
	make -C /lib/modules/\\\$(shell uname -r)/build M=\\\$(PWD) modules
clean:
	make -C /lib/modules/\\\$(shell uname -r)/build M=\\\$(PWD) clean
EOF"
    
    # 生成 DKMS 配置文件
    sudo tee /usr/src/dxgkrnl-$VERSION/dkms.conf > /dev/null <<EOF
PACKAGE_NAME="dxgkrnl"
PACKAGE_VERSION="$VERSION"
BUILT_MODULE_NAME="dxgkrnl"
DEST_MODULE_LOCATION="/kernel/drivers/hv/dxgkrnl/"
AUTOINSTALL="yes"
EOF

    # DKMS 构建流程
    sudo dkms add dxgkrnl/$VERSION
    sudo dkms build dxgkrnl/$VERSION
    sudo dkms install dxgkrnl/$VERSION --force
fi

echo "[STEP: Testing module load...]"
if ! sudo modprobe dxgkrnl; then
    echo " -> [WARNING] dxgkrnl could not be loaded. Check Secure Boot status."
fi

# ==========================================================
# 5. 图形栈配置
# [适配建议]: Ubuntu 24.04 的官方源 Mesa 版本已足够新，直接安装官方包。
# ==========================================================
if [ "$ENABLE_GRAPHICS" == "true" ]; then
    echo "[STEP: Configuring Graphics Stack...]"
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
        libgl1-mesa-dri libglapi-mesa libglx-mesa0 libgbm1 \
        mesa-vulkan-drivers mesa-utils vulkan-tools mesa-va-drivers vainfo
fi

# ==========================================================
# 6. 系统配置与 WSL 库部署
# [说明]: 将 D3D12/DXCore 用户态库映射到 WSL 标准路径，以便 3D 应用加载。
# ==========================================================
echo "[STEP: Deploying WSL Core Libraries...]"
LIBS=("libd3d12.so" "libd3d12core.so" "libdxcore.so")
mkdir -p "$LIB_DIR"
for lib in "${LIBS[@]}"; do
    if [ ! -f "$LIB_DIR/$lib" ]; then
        echo " -> $lib not found locally, downloading for $ARCH_DIR..."
        if ! retry_cmd aria2c -x 4 -s 4 --dir="$LIB_DIR" --out="$lib" "$GITHUB_LIB_URL/$lib" --allow-overwrite; then
            echo " -> [ERROR] Failed to download $lib from $GITHUB_LIB_URL/$lib"
            exit 1
        fi
    fi
done


if [ -f "$LIB_DIR/nvidia-smi" ]; then
    echo "[+] Found nvidia-smi uploaded from host, deploying to /usr/bin..."
    sudo cp "$LIB_DIR/nvidia-smi" /usr/bin/nvidia-smi
    sudo chmod 755 /usr/bin/nvidia-smi
fi


sudo mkdir -p /usr/lib/wsl/drivers /usr/lib/wsl/lib
sudo rm -rf /usr/lib/wsl/drivers/* /usr/lib/wsl/lib/*
if [ -d "$DEPLOY_DIR/drivers" ]; then
    sudo cp -r "$DEPLOY_DIR/drivers"/* /usr/lib/wsl/drivers/
fi
sudo cp -a "$LIB_DIR"/*.so* /usr/lib/wsl/lib/
sudo ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so
sudo chmod -R 0555 /usr/lib/wsl
sudo chown -R root:root /usr/lib/wsl
echo "/usr/lib/wsl/lib" | sudo tee /etc/ld.so.conf.d/ld.wsl.conf > /dev/null
sudo ldconfig

# ==========================================================
# 7. 内核模块延迟加载策略
# ==========================================================
echo "[STEP: Configuring Kernel Modules Strategy (vgem & dxgkrnl)...]"

# 1. 配置 vgem 自动加载
echo "vgem" | sudo tee /etc/modules-load.d/vgem.conf > /dev/null
sudo modprobe vgem

# 2. 将 dxgkrnl 加入黑名单防止启动冲突
echo "blacklist dxgkrnl" | sudo tee /etc/modprobe.d/blacklist-dxgkrnl.conf > /dev/null

# 3. 更新 initramfs
echo " -> Updating initramfs..."
sudo update-initramfs -u

# 4. 创建加载脚本
echo " -> Creating late-load script..."
sudo tee /usr/local/bin/load_dxg_driver.sh > /dev/null << 'EOF'
#!/bin/bash
modprobe dxgkrnl
if [ -e /dev/dxg ]; then
    chmod 666 /dev/dxg
fi
EOF
sudo chmod +x /usr/local/bin/load_dxg_driver.sh

sudo systemctl stop load-dxg-late.service 2>/dev/null || true
sudo systemctl disable load-dxg-late.service 2>/dev/null || true
sudo rm -f /etc/systemd/system/load-dxg-late.service

sudo tee /etc/systemd/system/load-dxg-late.service > /dev/null << 'EOF'
[Unit]
Description=Late load dxgkrnl
After=graphical.target

[Service]
Type=simple
User=root
ExecStart=/usr/local/bin/load_dxg_driver.sh
Restart=on-failure
RestartSec=5

[Install]
WantedBy=graphical.target
EOF

sudo systemctl daemon-reload
sudo systemctl unmask load-dxg-late.service
sudo systemctl enable load-dxg-late.service
sudo systemctl start load-dxg-late.service

# ==========================================================
# 8. 环境变量与权限
# ==========================================================
if [ "$ENABLE_GRAPHICS" == "true" ]; then
    echo "[STEP: Finalizing environment variables...]"
    # Gallium D3D12 后端配置
    update_env "GALLIUM_DRIVER" "d3d12"
    update_env "DRI_PRIME" "1"
    update_env "LIBVA_DRIVER_NAME" "d3d12"
    
    if ! grep -q "GALLIUM_DRIVE=d3d12" ~/.bashrc; then
        cat >> ~/.bashrc <<EOF
# GPU-PV Configuration
export GALLIUM_DRIVER=d3d12
export DRI_PRIME=1
export LIBVA_DRIVER_NAME=d3d12
EOF
    fi
    
    # 授予当前用户渲染权限
    sudo usermod -a -G video,render $USER
    
    # Hyper-V 的虚拟显卡通常是 card1，但很多旧程序只认 card0
    echo " -> Fix permissions and symlinks for /dev/dri..."
    sudo chmod 666 /dev/dri/* || true
    if [ -e /dev/dri/card1 ]; then
        sudo ln -sf /dev/dri/card1 /dev/dri/card0
    fi
fi

# ==========================================================
# 9. 清理并退出
# ==========================================================
echo "[STEP: Cleaning up deployment files...]"
cd /
sudo rm -rf "$DEPLOY_DIR"

echo "[STATUS: SUCCESS]"