#!/bin/bash
# @Name: Ubuntu-22.04-Official
# @Description: 针对 Ubuntu 22.04 的官方推荐部署脚本。
# @Author: Justsenger
# @Version: 1.0.7

set -e

# ==========================================================
# 0. 辅助函数定义
# ==========================================================
update_env() {
    local key=$1
    local val=$2
    sudo sed -i "/^$key=/d" /etc/environment
    sudo sed -i "/^export $key=/d" /etc/environment
    echo "$key=$val" | sudo tee -a /etc/environment > /dev/null
}

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

DEPLOY_DIR="$(dirname $(realpath $0))"
LIB_DIR="$DEPLOY_DIR/lib"
PATCH_BASE_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/patches"
GITHUB_LIB_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/lib"

if [ -n "$PROXY_URL" ]; then
    export http_proxy="$PROXY_URL"
    export https_proxy="$PROXY_URL"
    echo "[+] Using proxy: $PROXY_URL"
fi

# ==========================================================
# 2. 依赖安装
# ==========================================================
echo "[STEP: Installing basic dependencies...]"
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git curl dkms wget build-essential software-properties-common unzip aria2

# ==========================================================
# 3. 内核检查与头文件
# ==========================================================
echo "[STEP: Checking Kernel Headers...]"
TARGET_KERNEL_VERSION=$(uname -r)

if [ ! -e "/lib/modules/$TARGET_KERNEL_VERSION/build" ]; then
    echo " -> Kernel headers not found for $TARGET_KERNEL_VERSION. Attempting installation..."
    if ! sudo apt-get install -y -qq "linux-headers-$TARGET_KERNEL_VERSION"; then
        echo " -> Failed to find headers for current kernel. Installing a standard generic kernel instead..."
        NEW_KERNEL_IMAGE=$(apt-cache search "^linux-image-[0-9]" | awk '{print $1}' | grep -E "generic$" | sort -V | tail -1)
        NEW_KERNEL_VERSION=$(echo "$NEW_KERNEL_IMAGE" | sed 's/linux-image-//')
        NEW_KERNEL_HEADERS="linux-headers-$NEW_KERNEL_VERSION"
        sudo apt-get install -y -qq "$NEW_KERNEL_IMAGE" "$NEW_KERNEL_HEADERS"
        echo "[STATUS: REBOOT_REQUIRED]"
        exit 0
    fi
fi

# ==========================================================
# 4. dxgkrnl 模块编译与验证
# ==========================================================
if lsmod | grep -q "dxgkrnl" || dkms status | grep -q "dxgkrnl"; then
    echo " -> dxgkrnl is already installed or loaded."
else
    echo "[STEP: Preparing Source & Patching...]"
    KERNEL_MAJOR=$(echo $TARGET_KERNEL_VERSION | cut -d. -f1)
    KERNEL_MINOR=$(echo $TARGET_KERNEL_VERSION | cut -d. -f2)
    
    if [[ "$KERNEL_MAJOR" -eq 6 && "$KERNEL_MINOR" -ge 6 ]] || [[ "$KERNEL_MAJOR" -gt 6 ]]; then
        TARGET_BRANCH="linux-msft-wsl-6.6.y"
    else
        TARGET_BRANCH="linux-msft-wsl-5.15.y"
    fi

    # 清理旧文件
    rm -rf /tmp/dxg-src /tmp/kernel_src.tar.gz /tmp/extract-tmp
    
    # 下载轻量包
    PKG="dxgkrnl-${TARGET_BRANCH#linux-msft-wsl-}"
    PKG="${PKG%.y}-patched"
    echo " -> Downloading Lightweight Kernel Source using Aria2..."
    ZIP_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/kernel-assets/$PKG.tar.gz"
    
    retry_cmd aria2c -x 4 -s 4 --dir=/tmp --out=kernel_src.tar.gz "$ZIP_URL" --allow-overwrite
    
    echo " -> Recreating 1:1 Kernel Tree Structure..."
    mkdir -p /tmp/extract-tmp
    tar -xzf /tmp/kernel_src.tar.gz -C /tmp/extract-tmp --strip-components=1
    
    # 【核心修正】：人工重建 1.0.6 的目录结构，确保 patch -p1 和 cp -r 逻辑一丝不差
    mkdir -p /tmp/dxg-src/drivers/hv/dxgkrnl
    mv /tmp/extract-tmp/include /tmp/dxg-src/include
    # 将其余所有驱动文件移入深层目录
    cp -r /tmp/extract-tmp/* /tmp/dxg-src/drivers/hv/dxgkrnl/
    
    cd /tmp/dxg-src
    VERSION="custom"

    apply_patch() {
        local patch_url=$1
        local patch_file=$(basename "$patch_url")
        retry_cmd curl -fsSL --retry 3 "$patch_url" -o "$patch_file"
        # 此时在 /tmp/dxg-src 下，-p1 正好匹配 drivers/hv/dxgkrnl/...
        patch -p1 < "$patch_file"
    }

    echo "[STEP: Patching Kernel Source...]"
    apply_patch "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/0001-Add-a-gpu-pv-support.patch"
    
    if [ "$TARGET_BRANCH" == "linux-msft-wsl-5.15.y" ]; then
        apply_patch "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/0002-Add-a-multiple-kernel-version-support.patch"
        apply_patch "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/0003-Fix-gpadl-has-incomplete-type-error.patch"
    else
        retry_cmd curl -fsSL --retry 3 "$PATCH_BASE_URL/linux-msft-wsl-6.6.y/0002-Fix-eventfd_signal.patch" -o patch_6.6.patch
        patch -p1 --ignore-whitespace < patch_6.6.patch
    fi

    echo "[STEP: Compiling and Installing DXG Module...]"
    # 这里的命令现在和 1.0.6 完全一模一样了
    sudo cp -r ./drivers/hv/dxgkrnl /usr/src/dxgkrnl-$VERSION
    sudo cp -r ./include /usr/src/dxgkrnl-$VERSION/include
    DXGMODULE_FILE="/usr/src/dxgkrnl-$VERSION/dxgmodule.c"
    
    if grep -q "eventfd_signal.*struct eventfd_ctx.*__u64" /lib/modules/$TARGET_KERNEL_VERSION/build/include/linux/eventfd.h 2>/dev/null; then
        sed -i 's/eventfd_signal(event->cpu_event);/eventfd_signal(event->cpu_event, 1);/g' "$DXGMODULE_FILE"
    fi

    echo "Configuring Makefile..."
    sudo sed -i 's/\$(CONFIG_DXGKRNL)/m/' /usr/src/dxgkrnl-$VERSION/Makefile
    echo "EXTRA_CFLAGS=-I\$(PWD)/include -D_MAIN_KERNEL_" | sudo tee -a /usr/src/dxgkrnl-$VERSION/Makefile
    
    sudo tee /usr/src/dxgkrnl-$VERSION/dkms.conf > /dev/null <<EOF
PACKAGE_NAME="dxgkrnl"
PACKAGE_VERSION="$VERSION"
BUILT_MODULE_NAME="dxgkrnl"
DEST_MODULE_LOCATION="/kernel/drivers/hv/dxgkrnl/"
AUTOINSTALL="yes"
EOF

    sudo dkms add dxgkrnl/$VERSION
    sudo dkms build dxgkrnl/$VERSION
    sudo dkms install dxgkrnl/$VERSION --force
fi

echo "[STEP: Testing module load...]"
if ! sudo modprobe dxgkrnl; then
    echo " -> [WARNING] dxgkrnl could not be loaded. This is usually caused by Secure Boot."
    echo " -> Please disable Secure Boot in Hyper-V settings or sign the module manually."
fi

# ==========================================================
# 5. 图形栈配置 (1:1 还原 1.0.6)
# ==========================================================
if [ "$ENABLE_GRAPHICS" == "true" ]; then
    echo "[STEP: Configuring Graphics Stack (Kisak PPA)...]"
    sudo apt-get install -y -qq ppa-purge
    sudo ppa-purge -y ppa:kisak/turtle || true
    sudo ppa-purge -y ppa:kisak/kisak-mesa || true
    sudo rm -f /etc/apt/preferences.d/99-mesa-pinning /etc/apt/preferences.d/00-mesa-hold-gl

    sudo bash -c 'cat > /etc/apt/preferences.d/00-mesa-hold-gl <<EOF
Package: libgl1-mesa-dri libglapi-mesa libglx-mesa0 libgbm1
Pin: release o=Ubuntu
Pin-Priority: 1001
EOF'
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --allow-downgrades libgl1-mesa-dri libglapi-mesa libglx-mesa0 libgbm1

    sudo add-apt-repository ppa:kisak/turtle -y
    sudo apt-get update -qq
    sudo bash -c 'cat > /etc/apt/preferences.d/99-mesa-pinning <<EOF
Package: mesa-vulkan-drivers
Pin: version *kisak*
Pin-Priority: 900
EOF'
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mesa-vulkan-drivers mesa-utils vulkan-tools mesa-va-drivers vainfo
fi

# ==========================================================
# 6. 系统配置与 WSL 库部署 (1:1 还原 1.0.6)
# ==========================================================
echo "[STEP: Deploying WSL Core Libraries...]"
LIBS=("libd3d12.so" "libd3d12core.so" "libdxcore.so")
mkdir -p "$LIB_DIR"
for lib in "${LIBS[@]}"; do
    if [ ! -f "$LIB_DIR/$lib" ]; then
        echo " -> $lib not found locally, attempting download..."
        if ! retry_cmd aria2c -x 4 -s 4 --dir="$LIB_DIR" --out="$lib" "$GITHUB_LIB_URL/$lib"; then
            echo " -> [ERROR] Failed to download $lib. Please check network."
            exit 1
        fi
    fi
done

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
# 7. 内核模块延迟加载策略 (1:1 还原 1.0.6)
# ==========================================================
echo "[STEP: Configuring systemd late-loader...]"
echo "vgem" | sudo tee /etc/modules-load.d/vgem.conf > /dev/null
echo "blacklist dxgkrnl" | sudo tee /etc/modprobe.d/blacklist-dxgkrnl.conf > /dev/null
sudo update-initramfs -u

sudo tee /usr/local/bin/load_dxg_driver.sh > /dev/null << 'EOF'
#!/bin/bash
modprobe dxgkrnl
if [ -e /dev/dxg ]; then chmod 666 /dev/dxg; fi
EOF
sudo chmod +x /usr/local/bin/load_dxg_driver.sh

sudo tee /etc/systemd/system/load-dxg-late.service > /dev/null << 'EOF'
[Unit]
Description=Late load dxgkrnl for ExHyperV
After=multi-user.target

[Service]
Type=simple
ExecStart=/usr/local/bin/load_dxg_driver.sh

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable load-dxg-late.service

# ==========================================================
# 8. 环境变量与权限 (1:1 还原 1.0.6)
# ==========================================================
if [ "$ENABLE_GRAPHICS" == "true" ]; then
    echo "[STEP: Finalizing environment variables...]"
    update_env "GALLIUM_DRIVER" "d3d12"
    update_env "DRI_PRIME" "1"
    update_env "LIBVA_DRIVER_NAME" "d3d12"
    sudo usermod -a -G video,render $USER
fi

# ==========================================================
# 9. 清理并退出 (1:1 还原 1.0.6)
# ==========================================================
echo "[STEP: Cleaning up deployment files...]"
sudo rm -rf "$DEPLOY_DIR"

echo "[STATUS: SUCCESS]"