#!/bin/bash
# @Name: Ubuntu-22.04-Micro-ATP
# @Description: Micro-ATP reference Ubuntu 22.04 GPU-PV deployment script (local variant).
# @Author: Micro-ATP
# @Version: 0.1.1

set -e
trap 'echo " -> [ERROR] Command failed at line $LINENO: $BASH_COMMAND"' ERR

# ==========================================================
# 0. Helper functions
# ==========================================================
update_env() {
    local key=$1
    local val=$2
    sudo sed -i "/^$key=/d" /etc/environment
    sudo sed -i "/^export $key=/d" /etc/environment
    echo "$key=$val" | sudo tee -a /etc/environment > /dev/null
}

remove_env() {
    local key=$1
    sudo sed -i "/^$key=/d" /etc/environment
    sudo sed -i "/^export $key=/d" /etc/environment
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
                echo " -> [WARNING] Command failed. Retrying in $delay seconds (attempt $n): $*"
                sleep $delay
                ((n++))
            else
                echo " -> [ERROR] Reached maximum retry count ($max). Command failed: $*"
                return 1
            fi
        fi
    done
}

# ==========================================================
# 1. Initialization and arguments
# ==========================================================
ACTION=${1:-"deploy"}
ENABLE_GRAPHICS=${2:-"true"}
PROXY_URL=${3:-""}
GPU_VENDOR_RAW=${4:-""}

DEPLOY_DIR="$(dirname "$(realpath "$0")")"
LIB_DIR="$DEPLOY_DIR/lib"
PATCH_BASE_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/patches"
GITHUB_LIB_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/lib"
GPU_VENDOR_UPPER=$(printf '%s' "$GPU_VENDOR_RAW" | tr '[:lower:]' '[:upper:]')
IS_NVIDIA=false

if [[ "$GPU_VENDOR_UPPER" == *"NVIDIA"* ]]; then
    IS_NVIDIA=true
fi

if [ -n "$PROXY_URL" ]; then
    export http_proxy="$PROXY_URL"
    export https_proxy="$PROXY_URL"
    echo "[+] Using proxy: $PROXY_URL"
fi

echo "[+] GPU vendor hint: ${GPU_VENDOR_RAW:-unknown}"

# ==========================================================
# 2. Install dependencies
# ==========================================================
echo "[STEP: Installing basic dependencies...]"
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git curl dkms wget build-essential software-properties-common unzip aria2

# ==========================================================
# 3. Check kernel headers
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
# 4. Build and validate dxgkrnl
# ==========================================================
if lsmod | grep -q "dxgkrnl" || dkms status | grep -q "dxgkrnl"; then
    echo " -> dxgkrnl is already installed or loaded."
else
    echo "[STEP: Preparing WSL Kernel Source & Patching...]"
    KERNEL_MAJOR=$(echo "$TARGET_KERNEL_VERSION" | cut -d. -f1)
    KERNEL_MINOR=$(echo "$TARGET_KERNEL_VERSION" | cut -d. -f2)

    if [[ "$KERNEL_MAJOR" -eq 6 && "$KERNEL_MINOR" -ge 6 ]] || [[ "$KERNEL_MAJOR" -gt 6 ]]; then
        TARGET_BRANCH="linux-msft-wsl-6.6.y"
    else
        TARGET_BRANCH="linux-msft-wsl-5.15.y"
    fi

    rm -rf /tmp/WSL2-Linux-Kernel /tmp/kernel_src.zip

    echo " -> Downloading Kernel Source ZIP using Aria2..."
    ZIP_URL="https://github.com/microsoft/WSL2-Linux-Kernel/archive/refs/heads/$TARGET_BRANCH.zip"

    download_kernel() {
        aria2c -x 4 -s 4 -k 1M --file-allocation=none --dir=/tmp --out=kernel_src.zip "$ZIP_URL"
    }

    MAX_RETRIES=5
    COUNT=0
    SUCCESS=false

    while [ $COUNT -lt $MAX_RETRIES ]; do
        if download_kernel; then
            echo " -> Download finished. Verifying ZIP integrity..."
            if unzip -tq /tmp/kernel_src.zip; then
                echo " -> ZIP is valid."
                SUCCESS=true
                break
            else
                echo " -> [WARNING] ZIP file corrupted. Deleting and retrying..."
                rm -f /tmp/kernel_src.zip
            fi
        else
            echo " -> [WARNING] Aria2 download failed. Retrying..."
        fi
        COUNT=$((COUNT+1))
        sleep 3
    done

    if [ "$SUCCESS" = false ]; then
        echo " -> [ERROR] Failed to download a valid kernel source package."
        exit 1
    fi

    echo " -> Unzipping kernel source..."
    unzip -q /tmp/kernel_src.zip -d /tmp/
    mv "/tmp/WSL2-Linux-Kernel-$TARGET_BRANCH" /tmp/WSL2-Linux-Kernel

    cd /tmp/WSL2-Linux-Kernel
    VERSION="custom"

    apply_patch() {
        local patch_url=$1
        local patch_file
        patch_file=$(basename "$patch_url")
        retry_cmd curl -fsSL --retry 3 "$patch_url" -o "$patch_file"
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
    sudo cp -r ./drivers/hv/dxgkrnl "/usr/src/dxgkrnl-$VERSION"
    sudo cp -r ./include "/usr/src/dxgkrnl-$VERSION/include"
    DXGMODULE_FILE="/usr/src/dxgkrnl-$VERSION/dxgmodule.c"

    if grep -q "eventfd_signal.*struct eventfd_ctx.*__u64" "/lib/modules/$TARGET_KERNEL_VERSION/build/include/linux/eventfd.h" 2>/dev/null; then
        sed -i 's/eventfd_signal(event->cpu_event);/eventfd_signal(event->cpu_event, 1);/g' "$DXGMODULE_FILE"
    fi

    echo "Configuring Makefile..."
    sudo sed -i 's/\$(CONFIG_DXGKRNL)/m/' "/usr/src/dxgkrnl-$VERSION/Makefile"
    echo "EXTRA_CFLAGS=-I\$(PWD)/include -D_MAIN_KERNEL_" | sudo tee -a "/usr/src/dxgkrnl-$VERSION/Makefile"

    sudo tee "/usr/src/dxgkrnl-$VERSION/dkms.conf" > /dev/null <<EOF
PACKAGE_NAME="dxgkrnl"
PACKAGE_VERSION="$VERSION"
BUILT_MODULE_NAME="dxgkrnl"
DEST_MODULE_LOCATION="/kernel/drivers/hv/dxgkrnl/"
AUTOINSTALL="yes"
EOF

    sudo dkms add "dxgkrnl/$VERSION"
    sudo dkms build "dxgkrnl/$VERSION"
    sudo dkms install "dxgkrnl/$VERSION" --force
fi

echo "[STEP: Testing module load...]"
if ! sudo modprobe dxgkrnl; then
    echo " -> [WARNING] dxgkrnl could not be loaded. This is usually caused by Secure Boot."
    echo " -> Please disable Secure Boot in Hyper-V settings or sign the module manually."
fi

# ==========================================================
# 5. Configure graphics stack
# ==========================================================
if [ "$ENABLE_GRAPHICS" == "true" ]; then
    if [ "$IS_NVIDIA" == "true" ]; then
        echo "[STEP: Configuring Graphics Stack (safe NVIDIA mode)...]"
        sudo apt-get update -qq
        sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mesa-utils vulkan-tools vainfo xserver-xorg-core xserver-xorg-video-fbdev
        echo " -> NVIDIA detected. Skipping Kisak PPA and global Mesa overrides to reduce black-screen risk."
    else
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
fi

# ==========================================================
# 6. Deploy WSL userspace libraries
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
if [ -f "$LIB_DIR/nvidia-smi" ]; then
    sudo install -m 0755 "$LIB_DIR/nvidia-smi" /usr/lib/wsl/lib/nvidia-smi
    sudo ln -sf /usr/lib/wsl/lib/nvidia-smi /usr/bin/nvidia-smi
    echo " -> Installed nvidia-smi from uploaded WSL userspace files."
else
    echo " -> [WARNING] nvidia-smi was not uploaded from the host WSL library directory."
fi
sudo ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so
sudo chmod -R 0555 /usr/lib/wsl
sudo chown -R root:root /usr/lib/wsl
echo "/usr/lib/wsl/lib" | sudo tee /etc/ld.so.conf.d/ld.wsl.conf > /dev/null
sudo ldconfig

echo "[STEP: Validating deployed GPU userspace...]"
DRIVER_FILE_COUNT=$(find /usr/lib/wsl/drivers -type f 2>/dev/null | wc -l)
LIB_FILE_COUNT=$(find /usr/lib/wsl/lib -maxdepth 1 -type f 2>/dev/null | wc -l)
echo " -> Driver files deployed: $DRIVER_FILE_COUNT"
echo " -> WSL library files deployed: $LIB_FILE_COUNT"

if [ "$DRIVER_FILE_COUNT" -eq 0 ]; then
    echo " -> [ERROR] No files were copied to /usr/lib/wsl/drivers."
    exit 1
fi

if [ "$IS_NVIDIA" == "true" ]; then
    NVIDIA_MARKERS=$(find /usr/lib/wsl/drivers /usr/lib/wsl/lib -type f \( -iname 'libcuda.so*' -o -iname 'libnvidia-ml.so*' -o -iname 'nvidia-smi' \) 2>/dev/null | head -n 5 || true)
    if [ -z "$NVIDIA_MARKERS" ]; then
        echo " -> [ERROR] NVIDIA userspace files were not found under /usr/lib/wsl after deployment."
        echo " -> Expected files such as libcuda.so, libnvidia-ml.so, or nvidia-smi."
        exit 1
    fi
    echo " -> NVIDIA userspace markers detected:"
    echo "$NVIDIA_MARKERS" | sed 's/^/    /'
fi

if [ -e /dev/dxg ]; then
    echo " -> /dev/dxg is present."
else
    echo " -> [WARNING] /dev/dxg is not present yet; it should appear after the late-load service runs on reboot."
fi

# ==========================================================
# 7. Configure delayed dxgkrnl loading
# ==========================================================
echo "[STEP: Configuring systemd late-loader...]"
echo "vgem" | sudo tee /etc/modules-load.d/vgem.conf > /dev/null
echo "blacklist dxgkrnl" | sudo tee /etc/modprobe.d/blacklist-dxgkrnl.conf > /dev/null
sudo update-initramfs -u

sudo tee /usr/local/bin/load_dxg_driver.sh > /dev/null << 'EOF'
#!/bin/bash
set -e
modprobe dxgkrnl
udevadm settle || true
if [ -e /dev/dxg ]; then chmod 666 /dev/dxg; fi
EOF
sudo chmod +x /usr/local/bin/load_dxg_driver.sh

sudo tee /etc/systemd/system/load-dxg-late.service > /dev/null << 'EOF'
[Unit]
Description=Late load dxgkrnl for ExHyperV
After=multi-user.target systemd-modules-load.service
Before=display-manager.service graphical.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/load_dxg_driver.sh
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable load-dxg-late.service

# ==========================================================
# 8. Finalize environment variables and permissions
# ==========================================================
if [ "$ENABLE_GRAPHICS" == "true" ]; then
    remove_env "GALLIUM_DRIVER"
    remove_env "DRI_PRIME"
    remove_env "LIBVA_DRIVER_NAME"

    if [ "$IS_NVIDIA" == "true" ]; then
        echo "[STEP: Finalizing environment variables (safe NVIDIA mode)...]"
        echo " -> NVIDIA detected. Leaving Mesa D3D12 environment variables unset to avoid desktop black screens."
    else
        echo "[STEP: Finalizing environment variables...]"
        update_env "GALLIUM_DRIVER" "d3d12"
        update_env "DRI_PRIME" "1"
        update_env "LIBVA_DRIVER_NAME" "d3d12"
    fi

    sudo usermod -a -G video,render "$USER"
fi

# ==========================================================
# 9. Configure Ubuntu VMConnect display compatibility
# ==========================================================
if [ "$ENABLE_GRAPHICS" == "true" ] && [ "$IS_NVIDIA" == "true" ]; then
    echo "[STEP: Configuring VMConnect display compatibility for Ubuntu...]"

    if [ -f /etc/gdm3/custom.conf ]; then
        sudo sed -i 's/^#\?WaylandEnable=.*/WaylandEnable=false/' /etc/gdm3/custom.conf
        if ! grep -q '^WaylandEnable=false' /etc/gdm3/custom.conf; then
            echo 'WaylandEnable=false' | sudo tee -a /etc/gdm3/custom.conf > /dev/null
        fi
        echo " -> Disabled GDM Wayland to keep the login session on Xorg."
    else
        echo " -> GDM custom.conf not found; skipping Wayland change."
    fi

    if [ -e /dev/fb0 ]; then
        sudo mkdir -p /etc/X11/xorg.conf.d
        sudo tee /etc/X11/xorg.conf.d/20-exhyperv-hyperv-fbdev.conf > /dev/null <<'EOF'
Section "Device"
    Identifier "HyperVFramebuffer"
    Driver "fbdev"
    Option "fbdev" "/dev/fb0"
EndSection
EOF
        echo " -> Wrote Xorg fbdev fallback for Hyper-V VMConnect."
    else
        echo " -> /dev/fb0 not present during deployment; skipping fbdev Xorg fallback."
    fi
fi

# ==========================================================
# 10. Cleanup and exit
# ==========================================================
echo "[STEP: Cleaning up deployment files...]"
sudo rm -rf "$DEPLOY_DIR"

echo "[STATUS: SUCCESS]"