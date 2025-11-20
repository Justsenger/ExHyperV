#!/bin/bash -e

# --------------------------------------------------------
# install_dxgkrnl.sh
# --------------------------------------------------------

WORKDIR="$(dirname $(realpath $0))"
LINUX_DISTRO="$(cat /etc/*-release)"
LINUX_DISTRO=${LINUX_DISTRO,,}

# 基础 URL
PATCH_BASE_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/patches"

KERNEL_6_6_NEWER_REGEX="^(6\.[6-9]\.|6\.[0-9]{2,}\.)"
KERNEL_5_15_NEWER_REGEX="^(5\.1[5-9]+\.|6\.[0-5]\.)"

install_dependencies() {
    NEED_TO_INSTALL=""
    if [ ! -e "/bin/git" ] && [ ! -e "/usr/bin/git" ]; then
        NEED_TO_INSTALL="git"; 
    fi
    if [ ! -e "/usr/bin/curl" ] && [ ! -e "/bin/curl" ]; then
        NEED_TO_INSTALL="$NEED_TO_INSTALL curl"
    fi
    if [ ! -e "/sbin/dkms" ] && [ ! -e "/bin/dkms" ] && [ ! -e "/usr/bin/dkms" ]; then
        NEED_TO_INSTALL="$NEED_TO_INSTALL dkms"
    fi

    if [[ ! -z "$NEED_TO_INSTALL" ]]; then
        echo "Installing basic dependencies: $NEED_TO_INSTALL"
        if [[ "$LINUX_DISTRO" == *"debian"* ]]; then
            apt update;
            apt install -y $NEED_TO_INSTALL;
        elif [[ "$LINUX_DISTRO" == *"fedora"* ]]; then
            yum -y install $NEED_TO_INSTALL;
        else
            echo "Fatal: The system distro is unsupported";
            exit 1;
        fi
    else
        echo "Basic dependencies (git, curl, dkms) are already installed."
    fi
}

check_and_install_kernel() {
    echo ""
    echo "========================================"
    echo "  Checking Kernel Headers"
    echo "========================================"
    echo "Target kernel version: ${TARGET_KERNEL_VERSION}"
    
    # 检查精确版本的 headers 是否存在
    if [ -e "/usr/src/linux-headers-${TARGET_KERNEL_VERSION}" ] && [ -e "/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
        echo "✓ Kernel headers found for ${TARGET_KERNEL_VERSION}"
        return 0
    fi
    
    echo "✗ Kernel headers not found for ${TARGET_KERNEL_VERSION}"
    echo ""
    echo "Will install a new kernel from standard repository..."
    echo ""
    
    if [[ "$LINUX_DISTRO" == *"debian"* ]]; then
        # 更新软件包列表
        echo "Updating package list..."
        apt update
        
        # 获取可用的最新内核版本
        echo "Searching for available kernels..."
        AVAILABLE_KERNELS=$(apt-cache search "^linux-image-[0-9]" | grep -E "linux-image-[0-9]+\.[0-9]+\.[0-9]+-[0-9]+-amd64" | awk '{print $1}' | sort -V | tail -5)
        
        echo "Available kernels:"
        echo "$AVAILABLE_KERNELS" | nl
        echo ""
        
        # 选择最新的内核
        NEW_KERNEL_IMAGE=$(echo "$AVAILABLE_KERNELS" | tail -1)
        NEW_KERNEL_VERSION=$(echo "$NEW_KERNEL_IMAGE" | sed 's/linux-image-//')
        NEW_KERNEL_HEADERS="linux-headers-${NEW_KERNEL_VERSION}"
        
        echo "Selected kernel: $NEW_KERNEL_VERSION"
        echo ""
        
        # 检查是否已经安装
        if dpkg -l | grep -q "^ii.*$NEW_KERNEL_IMAGE"; then
            echo "✓ Kernel $NEW_KERNEL_VERSION is already installed"
        else
            echo "Installing kernel and headers..."
            echo "  - $NEW_KERNEL_IMAGE"
            echo "  - $NEW_KERNEL_HEADERS"
            echo ""
            
            # 安装内核和 headers
            apt install -y "$NEW_KERNEL_IMAGE" "$NEW_KERNEL_HEADERS"
            
            echo ""
            echo "========================================"
            echo "  Kernel Installation Completed"
            echo "========================================"
            echo "New kernel installed: $NEW_KERNEL_VERSION"
            echo ""
            echo "System will reboot in 5 seconds..."
            echo "After reboot, please run this script again:"
            echo "  sudo $0"
            echo ""
            
            # 创建标记文件，重启后继续执行
            echo "$NEW_KERNEL_VERSION" > /tmp/dxgkrnl_install_pending
            
            sleep 5
            reboot
            exit 0
        fi
        
        # 更新 TARGET_KERNEL_VERSION 为新内核版本
        echo ""
        echo "Updating target kernel version to: $NEW_KERNEL_VERSION"
        TARGET_KERNEL_VERSION="$NEW_KERNEL_VERSION"
        
        # 验证 headers 是否存在
        if [ ! -e "/usr/src/linux-headers-${TARGET_KERNEL_VERSION}" ]; then
            echo "ERROR: Headers not found after installation!"
            exit 1
        fi
        
        if [ ! -e "/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo "ERROR: /lib/modules/${TARGET_KERNEL_VERSION}/build not found!"
            exit 1
        fi
        
        echo "✓ Kernel headers verified for ${TARGET_KERNEL_VERSION}"
        
    elif [[ "$LINUX_DISTRO" == *"fedora"* ]]; then
        echo "Installing latest kernel..."
        yum -y install kernel kernel-devel
        
        # 获取新安装的内核版本
        NEW_KERNEL_VERSION=$(rpm -q kernel --last | head -1 | awk '{print $1}' | sed 's/kernel-//')
        echo "New kernel version: $NEW_KERNEL_VERSION"
        
        echo ""
        echo "========================================"
        echo "  Kernel Installation Completed"
        echo "========================================"
        echo "System will reboot in 5 seconds..."
        echo "After reboot, please run this script again:"
        echo "  sudo $0"
        echo ""
        
        # 创建标记文件
        echo "$NEW_KERNEL_VERSION" > /tmp/dxgkrnl_install_pending
        
        sleep 5
        reboot
        exit 0
    else
        echo "Fatal: The system distro is unsupported";
        exit 1;
    fi
}

update_git() {
    # 提取内核主版本号和次版本号
    KERNEL_MAJOR=$(echo ${TARGET_KERNEL_VERSION} | grep -oP '^\d+')
    KERNEL_MINOR=$(echo ${TARGET_KERNEL_VERSION} | grep -oP '^\d+\.(\d+)' | grep -oP '\d+$')
    
    echo ""
    echo "========================================"
    echo "  Preparing Source Code"
    echo "========================================"
    echo "Detected kernel version: ${KERNEL_MAJOR}.${KERNEL_MINOR}"
    
    # 根据版本选择分支
    if [[ "$KERNEL_MAJOR" -eq 6 && "$KERNEL_MINOR" -ge 6 ]] || [[ "$KERNEL_MAJOR" -gt 6 ]]; then
        TARGET_BRANCH="linux-msft-wsl-6.6.y"
        echo "Using branch: linux-msft-wsl-6.6.y (for kernel 6.6+)"
    elif [[ "$KERNEL_MAJOR" -eq 5 && "$KERNEL_MINOR" -ge 15 ]] || [[ "$KERNEL_MAJOR" -eq 6 && "$KERNEL_MINOR" -lt 6 ]]; then
        TARGET_BRANCH="linux-msft-wsl-5.15.y"
        echo "Using branch: linux-msft-wsl-5.15.y (for kernel 5.15 - 6.5)"
    else
        echo "Fatal: Unsupported kernel version ${TARGET_KERNEL_VERSION}"
        echo "Supported versions: 5.15+ or 6.0+"
        exit 1
    fi

    if [ ! -e "/tmp/WSL2-Linux-Kernel" ]; then
        echo "Cloning WSL2-Linux-Kernel repository..."
        git clone --branch=$TARGET_BRANCH --no-checkout --depth=1 https://github.com/microsoft/WSL2-Linux-Kernel.git /tmp/WSL2-Linux-Kernel
    fi

    cd /tmp/WSL2-Linux-Kernel;

    if [ "`git branch -a | grep -o $TARGET_BRANCH`" == "" ]; then
        git fetch --depth=1 origin $TARGET_BRANCH:$TARGET_BRANCH;
    fi

    git sparse-checkout set --no-cone /drivers/hv/dxgkrnl /include/uapi/misc/d3dkmthk.h
    git checkout -f $TARGET_BRANCH
}

get_version() {
    cd /tmp/WSL2-Linux-Kernel
    CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
    VERSION=$(git rev-parse --short HEAD)
}

install() {
    echo ""
    echo "========================================"
    echo "  Applying Patches"
    echo "========================================"
    
    cd /tmp/WSL2-Linux-Kernel

    case $CURRENT_BRANCH in
        "linux-msft-wsl-5.15.y")
            echo "Applying patches for 5.15 branch..."
            PATCHES="0001-Add-a-gpu-pv-support.patch \
                     0002-Add-a-multiple-kernel-version-support.patch";
            if [[ "$TARGET_KERNEL_VERSION" != *"azure"* ]]; then
                    PATCHES="$PATCHES 0003-Fix-gpadl-has-incomplete-type-error.patch";
            fi
            
            for PATCH in $PATCHES; do
                echo "  - $PATCH"
                curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/$PATCH" | git apply -v;
            done
            ;;
            
        "linux-msft-wsl-6.6.y")
            echo "Applying patches for 6.6 branch..."
            echo "  - 0001-Add-a-gpu-pv-support.patch (Shared)"
            curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/0001-Add-a-gpu-pv-support.patch" | git apply -v;

            PATCHES="";
            if [[ "$TARGET_KERNEL_VERSION" != *"truenas"* ]]; then
                PATCHES="0002-Fix-eventfd_signal.patch";
            fi

            for PATCH in $PATCHES; do
                echo "  - $PATCH"
                curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-6.6.y/$PATCH" | git apply -v;
            done
            ;;
        *)
            echo "Fatal: \"$CURRENT_BRANCH\" is not available";
            exit 1;;
    esac

    echo ""
    echo "========================================"
    echo "  Installing Module Files"
    echo "========================================"
    
    echo "Copying dxgkrnl driver..."
    cp -r ./drivers/hv/dxgkrnl /usr/src/dxgkrnl-$VERSION
    echo "Copying include files..."
    cp -r ./include /usr/src/dxgkrnl-$VERSION/include

    echo "Configuring Makefile..."
    sed -i 's/\$(CONFIG_DXGKRNL)/m/' /usr/src/dxgkrnl-$VERSION/Makefile
    echo "EXTRA_CFLAGS=-I\$(PWD)/include -D_MAIN_KERNEL_" >> /usr/src/dxgkrnl-$VERSION/Makefile

    # 根据实际使用的分支设置 BUILD_EXCLUSIVE_KERNEL
    if [[ "$CURRENT_BRANCH" == "linux-msft-wsl-6.6.y" ]]; then
        BUILD_EXCLUSIVE_KERNEL=$KERNEL_6_6_NEWER_REGEX
    else
        BUILD_EXCLUSIVE_KERNEL=$KERNEL_5_15_NEWER_REGEX
    fi

    echo "Creating DKMS configuration..."
    cat > /usr/src/dxgkrnl-$VERSION/dkms.conf << EOF
PACKAGE_NAME="dxgkrnl"
PACKAGE_VERSION="$VERSION"
BUILT_MODULE_NAME="dxgkrnl"
DEST_MODULE_LOCATION="/kernel/drivers/hv/dxgkrnl/"
AUTOINSTALL="yes"
BUILD_EXCLUSIVE_KERNEL="$BUILD_EXCLUSIVE_KERNEL"
EOF
    
    echo "✓ Module files installed to /usr/src/dxgkrnl-$VERSION"
}

install_dkms() {
    echo ""
    echo "========================================"
    echo "  Building and Installing DKMS Module"
    echo "========================================"
    
    # 确认 kernel headers 路径存在
    echo "Verifying kernel headers..."
    if [ ! -e "/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
        echo "ERROR: /lib/modules/${TARGET_KERNEL_VERSION}/build not found!"
        exit 1
    fi
    echo "✓ Headers path: $(readlink -f /lib/modules/${TARGET_KERNEL_VERSION}/build)"
    
    if dkms status | grep -q "dxgkrnl/$VERSION"; then
        echo "Removing existing dxgkrnl/$VERSION..."
        dkms remove dxgkrnl/$VERSION --all
    fi
    
    echo ""
    echo "Adding module to DKMS..."
    dkms -k ${TARGET_KERNEL_VERSION} add dxgkrnl/$VERSION
    
    echo ""
    echo "Building module (this may take a few minutes)..."
    dkms -k ${TARGET_KERNEL_VERSION} build dxgkrnl/$VERSION
    
    echo ""
    echo "Installing module..."
    dkms -k ${TARGET_KERNEL_VERSION} install dxgkrnl/$VERSION
    
    echo ""
    echo "✓ DKMS module installed successfully"
    
    # 清理标记文件
    rm -f /tmp/dxgkrnl_install_pending
}

all() {
    TARGET_KERNEL_VERSION="$1";
    if [ "$TARGET_KERNEL_VERSION" == "" ]; then
        TARGET_KERNEL_VERSION=`uname -r`
    fi

    echo "========================================"
    echo "  dxgkrnl Installation Script"
    echo "========================================"
    echo "Initial target kernel: ${TARGET_KERNEL_VERSION}"
    echo "Current running kernel: $(uname -r)"
    
    # 检查是否有待处理的安装（重启后）
    if [ -f /tmp/dxgkrnl_install_pending ]; then
        PENDING_VERSION=$(cat /tmp/dxgkrnl_install_pending)
        echo "Resuming installation for kernel: $PENDING_VERSION"
        TARGET_KERNEL_VERSION=$PENDING_VERSION
    fi
    
    echo ""
    
    install_dependencies
    check_and_install_kernel  # 这个函数会检查并可能更新 TARGET_KERNEL_VERSION
    update_git
    get_version
    
    echo ""
    echo "========================================"
    echo "  Build Information"
    echo "========================================"
    echo "Target kernel: ${TARGET_KERNEL_VERSION}"
    echo "Module version: ${VERSION}"
    echo "Source branch: ${CURRENT_BRANCH}"
    echo ""
    
    install
    install_dkms
}

if [ -z $1 ]; then
    all `uname -r`
elif [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+.+$ ]]; then
    all $1
else
    echo "Usage: $0 [kernel_version]"
    echo ""
    echo "Examples:"
    echo "  $0                    # Use current running kernel"
    echo "  $0 6.1.0-41-amd64     # Use specific kernel version"
    exit 1
fi

echo ""
echo "========================================"
echo "  Installation Completed Successfully!"
echo "========================================"
echo "Kernel version: ${TARGET_KERNEL_VERSION}"
echo "Module installed: dxgkrnl/${VERSION}"
echo ""

if [ "$(uname -r)" != "$TARGET_KERNEL_VERSION" ]; then
    echo "⚠ WARNING: The module was built for kernel ${TARGET_KERNEL_VERSION}"
    echo "           but you are currently running $(uname -r)"
    echo ""
    echo "Please reboot to use the new kernel:"
    echo "  sudo reboot"
else
    echo "✓ Module is ready to use!"
    echo ""
    echo "You can load the module with:"
    echo "  sudo modprobe dxgkrnl"
    echo ""
    echo "To verify:"
    echo "  lsmod | grep dxgkrnl"
fi

echo ""
