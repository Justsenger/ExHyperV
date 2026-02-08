#!/bin/bash -e
# install_dxgkrnl.sh
WORKDIR="$(dirname $(realpath $0))"
LINUX_DISTRO="$(cat /etc/*-release)"
LINUX_DISTRO=${LINUX_DISTRO,,}

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
        if [[ "$LINUX_DISTRO" == *"debian"* || "$LINUX_DISTRO" == *"ubuntu"* ]]; then
            apt update;
            apt install -y $NEED_TO_INSTALL;
        elif [[ "$LINUX_DISTRO" == *"fedora"* ]]; then
            yum -y install $NEED_TO_INSTALL;
        elif [[ "$LINUX_DISTRO" == *"arch"* ]]; then
            pacman -Sy --noconfirm $NEED_TO_INSTALL;
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
    
    # Arch Linux uses different paths for kernel headers
    if [[ "$LINUX_DISTRO" == *"arch"* ]]; then
        if [ -e "/usr/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo "✓ Kernel headers found for ${TARGET_KERNEL_VERSION}"
            return 0
        fi
    else
        if [ -e "/usr/src/linux-headers-${TARGET_KERNEL_VERSION}" ] && [ -e "/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo "✓ Kernel headers found for ${TARGET_KERNEL_VERSION}"
            return 0
        fi
    fi
    
    echo "✗ Kernel headers not found for ${TARGET_KERNEL_VERSION}"
    echo ""
    echo "Will install a new kernel from standard repository..."
    echo ""
    
    if [[ "$LINUX_DISTRO" == *"debian"* || "$LINUX_DISTRO" == *"ubuntu"* ]]; then
        echo "Updating package list..."
        apt update
        
        echo "Searching for available kernels..."
        
        AVAILABLE_KERNELS=$(apt-cache search "^linux-image-[0-9]" | \
            awk '{print $1}' | \
            grep -E "^linux-image-[0-9]+\.[0-9]+\.[0-9]+-[0-9]+-(amd64|generic)$" | \
            grep -v -- "-unsigned" | \
            grep -v -- "-dbg" | \
            grep -v -- "-cloud" | \
            sort -V | tail -5)
        
        if [ -z "$AVAILABLE_KERNELS" ]; then
            echo "ERROR: No standard kernel images found in repository!"
            echo ""
            echo "Debugging information:"
            echo "All available kernel images:"
            apt-cache search "^linux-image-[0-9]" | head -20
            exit 1
        fi
        
        echo "Available standard kernels:"
        echo "$AVAILABLE_KERNELS" | nl
        echo ""
        
        NEW_KERNEL_IMAGE=$(echo "$AVAILABLE_KERNELS" | tail -1)
        NEW_KERNEL_VERSION=$(echo "$NEW_KERNEL_IMAGE" | sed 's/linux-image-//')
        NEW_KERNEL_HEADERS="linux-headers-${NEW_KERNEL_VERSION}"
        
        echo "Selected packages:"
        echo "  Image:   $NEW_KERNEL_IMAGE"
        echo "  Version: $NEW_KERNEL_VERSION"
        echo "  Headers: $NEW_KERNEL_HEADERS"
        echo ""
        
        # 验证包是否存在
        echo "Verifying packages availability..."
        if ! apt-cache show "$NEW_KERNEL_IMAGE" >/dev/null 2>&1; then
            echo "ERROR: Kernel image package '$NEW_KERNEL_IMAGE' not found!"
            exit 1
        fi
        
        if ! apt-cache show "$NEW_KERNEL_HEADERS" >/dev/null 2>&1; then
            echo "ERROR: Headers package '$NEW_KERNEL_HEADERS' not found!"
            exit 1
        fi
        
        echo "✓ Both packages verified in repository"
        echo ""
        
        # 检查是否已安装
        if dpkg -l | grep -q "^ii.*$NEW_KERNEL_IMAGE\s"; then
            echo "✓ Kernel image $NEW_KERNEL_VERSION is already installed"
        else
            echo "Installing kernel image and headers..."
            echo ""
            
            apt install -y "$NEW_KERNEL_IMAGE" "$NEW_KERNEL_HEADERS"
            
            echo ""
            echo "========================================"
            echo "  ACTION REQUIRED: REBOOT"
            echo "========================================"
            echo "A new kernel (${NEW_KERNEL_VERSION}) has been installed."
            echo "Please reboot and run this script again."
            echo ""
            echo "After reboot, run:"
            echo "  sudo $0"
            echo ""
            echo "STATUS: REBOOT_REQUIRED"
            exit 0
        fi
        
        # 检查 headers 是否已安装
        if ! dpkg -l | grep -q "^ii.*$NEW_KERNEL_HEADERS\s"; then
            echo "Installing kernel headers..."
            apt install -y "$NEW_KERNEL_HEADERS"
        fi
        
        echo ""
        echo "Updating target kernel version to: $NEW_KERNEL_VERSION"
        TARGET_KERNEL_VERSION="$NEW_KERNEL_VERSION"
        
        if [ ! -e "/usr/src/linux-headers-${TARGET_KERNEL_VERSION}" ] || [ ! -e "/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo "ERROR: Headers not found after installation!"
            echo "Expected: /usr/src/linux-headers-${TARGET_KERNEL_VERSION}"
            ls -la /usr/src/ | grep linux-headers
            exit 1
        fi
        
        echo "✓ Kernel headers verified for ${TARGET_KERNEL_VERSION}"
        
    elif [[ "$LINUX_DISTRO" == *"fedora"* ]]; then
        echo "Installing latest kernel..."
        yum -y install kernel kernel-devel
        
        NEW_KERNEL_VERSION=$(rpm -q kernel --last | head -1 | awk '{print $1}' | sed 's/kernel-//')
        echo "New kernel version: $NEW_KERNEL_VERSION"
        
        echo ""
        echo "========================================"
        echo "  ACTION REQUIRED: REBOOT"
        echo "========================================"
        echo "A new kernel (${NEW_KERNEL_VERSION}) has been installed."
        echo "STATUS: REBOOT_REQUIRED"
        exit 0
        
    elif [[ "$LINUX_DISTRO" == *"arch"* ]]; then
        echo "Installing kernel headers for Arch Linux..."
        
        # Detect which headers package to install based on kernel version
        HEADERS_PKG=""
        IS_AUR_PKG=false
        if [[ "$TARGET_KERNEL_VERSION" == *"-lts66"* ]]; then
            HEADERS_PKG="linux-lts66-headers"
            IS_AUR_PKG=true
            echo "Detected LTS 6.6 kernel, installing linux-lts66-headers..."
        elif [[ "$TARGET_KERNEL_VERSION" == *"-lts"* ]]; then
            # Extract LTS version (e.g., 6.12.63-1-lts -> linux-lts-headers)
            HEADERS_PKG="linux-lts-headers"
            echo "Detected LTS kernel, installing linux-lts-headers..."
        elif [[ "$TARGET_KERNEL_VERSION" == *"-zen"* ]]; then
            HEADERS_PKG="linux-zen-headers"
            echo "Detected Zen kernel, installing linux-zen-headers..."
        elif [[ "$TARGET_KERNEL_VERSION" == *"-hardened"* ]]; then
            HEADERS_PKG="linux-hardened-headers"
            echo "Detected Hardened kernel, installing linux-hardened-headers..."
        else
            HEADERS_PKG="linux-headers"
            echo "Detected standard kernel, installing linux-headers..."
        fi
        
        # Try to install the headers package
        INSTALLED=false
        
        # First, try pacman (for official packages)
        if ! $IS_AUR_PKG; then
            if pacman -Sy --noconfirm "$HEADERS_PKG" 2>/dev/null; then
                echo "✓ Installed $HEADERS_PKG via pacman"
                INSTALLED=true
            fi
        fi
        
        # If it's an AUR package or pacman failed, try AUR helpers
        if ! $INSTALLED && $IS_AUR_PKG; then
            # Check for paru
            if command -v paru >/dev/null 2>&1; then
                echo "Attempting to install $HEADERS_PKG via paru (AUR)..."
                if paru -S --noconfirm --needed "$HEADERS_PKG" 2>/dev/null; then
                    echo "✓ Installed $HEADERS_PKG via paru"
                    INSTALLED=true
                fi
            # Check for yay
            elif command -v yay >/dev/null 2>&1; then
                echo "Attempting to install $HEADERS_PKG via yay (AUR)..."
                if yay -S --noconfirm --needed "$HEADERS_PKG" 2>/dev/null; then
                    echo "✓ Installed $HEADERS_PKG via yay"
                    INSTALLED=true
                fi
            fi
        fi
        
        # If still not installed, show helpful message
        if ! $INSTALLED; then
            echo ""
            echo "WARNING: Failed to install $HEADERS_PKG automatically."
            if $IS_AUR_PKG; then
                echo "This package is from AUR and requires an AUR helper."
                echo ""
                echo "Please install it manually using one of these methods:"
                if command -v paru >/dev/null 2>&1; then
                    echo "  paru -S $HEADERS_PKG"
                elif command -v yay >/dev/null 2>&1; then
                    echo "  yay -S $HEADERS_PKG"
                else
                    echo "  paru -S $HEADERS_PKG  # if you have paru installed"
                    echo "  yay -S $HEADERS_PKG    # if you have yay installed"
                    echo ""
                    echo "If you don't have an AUR helper, install one first:"
                    echo "  # For paru:"
                    echo "  git clone https://aur.archlinux.org/paru.git"
                    echo "  cd paru && makepkg -si"
                fi
            else
                echo "You may need to install it manually using:"
                echo "  sudo pacman -S $HEADERS_PKG"
            fi
        fi
        
        # Arch Linux kernel headers are installed to /usr/lib/modules/$(uname -r)/build
        # Check if headers exist for current kernel
        if [ ! -e "/usr/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo ""
            echo "WARNING: Headers not found for ${TARGET_KERNEL_VERSION}"
            echo "This may happen if you're using a custom kernel."
            echo ""
            echo "Available kernel versions:"
            ls -d /usr/lib/modules/*/build 2>/dev/null | sed 's|/usr/lib/modules/||;s|/build||' || echo "None found"
            echo ""
            echo "Please install the matching headers package manually:"
            if [[ "$TARGET_KERNEL_VERSION" == *"-lts66"* ]]; then
                echo "  sudo pacman -S linux-lts66-headers"
                echo "  or"
                echo "  paru -S linux-lts66-headers  # if using AUR"
            else
                echo "  sudo pacman -S $HEADERS_PKG"
                echo "  or"
                echo "  paru -S $HEADERS_PKG  # if using AUR"
            fi
            echo ""
            echo "After installing headers, run this script again."
            exit 1
        fi
        
        echo "✓ Kernel headers verified for ${TARGET_KERNEL_VERSION}"
        
    else
        echo "Fatal: The system distro is unsupported";
        exit 1;
    fi
}

update_git() {
    KERNEL_MAJOR=$(echo ${TARGET_KERNEL_VERSION} | grep -oP '^\d+')
    KERNEL_MINOR=$(echo ${TARGET_KERNEL_VERSION} | grep -oP '^\d+\.(\d+)' | grep -oP '\d+$')
    
    echo ""
    echo "========================================"
    echo "  Preparing Source Code"
    echo "========================================"
    echo "Detected kernel version: ${KERNEL_MAJOR}.${KERNEL_MINOR}"
    
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
            PATCHES="0001-Add-a-gpu-pv-support.patch 0002-Add-a-multiple-kernel-version-support.patch";
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
                curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-6.6.y/$PATCH" | git apply -v --ignore-whitespace --ignore-space-change;
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

    # Fix eventfd_signal for kernels that require 2 parameters
    # The patch (0002-Fix-eventfd_signal.patch) removes the second parameter,
    # but some kernel versions still require it. We detect this by checking the kernel headers.
    echo "Checking eventfd_signal API compatibility..."
    DXGMODULE_FILE="/usr/src/dxgkrnl-$VERSION/dxgmodule.c"
    
    if [ -f "$DXGMODULE_FILE" ] && grep -q "eventfd_signal(event->cpu_event);" "$DXGMODULE_FILE"; then
        # Check if kernel headers define eventfd_signal with 2 parameters (__u64 n)
        NEEDS_TWO_PARAMS=false
        if grep -q "eventfd_signal.*struct eventfd_ctx.*__u64" /usr/lib/modules/${TARGET_KERNEL_VERSION}/build/include/linux/eventfd.h 2>/dev/null; then
            NEEDS_TWO_PARAMS=true
        elif grep -q "eventfd_signal.*struct eventfd_ctx.*__u64" /lib/modules/${TARGET_KERNEL_VERSION}/build/include/linux/eventfd.h 2>/dev/null; then
            NEEDS_TWO_PARAMS=true
        fi
        
        if [ "$NEEDS_TWO_PARAMS" = true ]; then
            echo "Detected 2-parameter eventfd_signal API in kernel headers, fixing..."
            sed -i 's/eventfd_signal(event->cpu_event);/eventfd_signal(event->cpu_event, 1);/g' "$DXGMODULE_FILE"
            echo "✓ Fixed eventfd_signal call to use 2 parameters"
        else
            echo "✓ Kernel uses 1-parameter eventfd_signal API, no fix needed"
        fi
    else
        echo "✓ eventfd_signal already uses correct format"
    fi

    echo "Configuring Makefile..."
    sed -i 's/\$(CONFIG_DXGKRNL)/m/' /usr/src/dxgkrnl-$VERSION/Makefile
    echo "EXTRA_CFLAGS=-I\$(PWD)/include -D_MAIN_KERNEL_" >> /usr/src/dxgkrnl-$VERSION/Makefile

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
    
    echo "Verifying kernel headers..."
    # Arch Linux uses /usr/lib/modules, others use /lib/modules (which may be a symlink)
    if [[ "$LINUX_DISTRO" == *"arch"* ]]; then
        HEADERS_PATH="/usr/lib/modules/${TARGET_KERNEL_VERSION}/build"
    else
        HEADERS_PATH="/lib/modules/${TARGET_KERNEL_VERSION}/build"
    fi
    
    if [ ! -e "$HEADERS_PATH" ]; then
        echo "ERROR: $HEADERS_PATH not found!"
        exit 1
    fi
    echo "✓ Headers path: $(readlink -f $HEADERS_PATH)"
    
    if dkms status | grep -q "dxgkrnl/$VERSION"; then
        echo "Removing existing dxgkrnl/$VERSION..."
        dkms remove dxgkrnl/$VERSION --all
    fi
    
    echo ""
    echo "Adding module to DKMS..."
    dkms -k ${TARGET_KERNEL_VERSION} add dxgkrnl/$VERSION
    
    echo ""
    echo "Building module (this may take a few minutes)..."
    if ! dkms -k ${TARGET_KERNEL_VERSION} build dxgkrnl/$VERSION; then
        echo ""
        echo "========================================"
        echo "  Build Failed"
        echo "========================================"
        echo "DKMS build failed. Please check the build log for details:"
        echo "  /var/lib/dkms/dxgkrnl/$VERSION/build/make.log"
        echo ""
        echo "Common issues:"
        echo "  1. Kernel version too new - patches may not be compatible"
        echo "  2. Missing dependencies - ensure all build tools are installed"
        echo "  3. Kernel headers mismatch - verify headers match kernel version"
        echo ""
        echo "Showing last 50 lines of build log:"
        echo "----------------------------------------"
        tail -50 /var/lib/dkms/dxgkrnl/$VERSION/build/make.log 2>/dev/null || echo "Log file not found"
        echo "----------------------------------------"
        exit 1
    fi
    
    echo ""
    echo "Installing module..."
    dkms -k ${TARGET_KERNEL_VERSION} install dxgkrnl/$VERSION || true

    # Check for module in both possible locations (Arch uses /usr/lib/modules, others use /lib/modules)
    MODULE_FOUND=false
    if [[ "$LINUX_DISTRO" == *"arch"* ]]; then
        if [ -f "/usr/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/dxgkrnl.ko" ] || [ -f "/usr/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/dxgkrnl.ko.zst" ]; then
            MODULE_FOUND=true
        fi
        # Also check /lib/modules in case it's a symlink
        if [ -f "/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/dxgkrnl.ko" ] || [ -f "/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/dxgkrnl.ko.zst" ]; then
            MODULE_FOUND=true
        fi
    else
        if [ -f "/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/dxgkrnl.ko" ] || [ -f "/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/dxgkrnl.ko.zst" ]; then
            MODULE_FOUND=true
        fi
    fi
    
    if [ "$MODULE_FOUND" = false ]; then
        echo ""
        echo "Error: DKMS installation failed (Module file not found)."
        echo "Checked locations:"
        if [[ "$LINUX_DISTRO" == *"arch"* ]]; then
            echo "  /usr/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/"
            echo "  /lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/"
        else
            echo "  /lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/"
        fi
        echo ""
        echo "Please check the build log:"
        echo "  /var/lib/dkms/dxgkrnl/$VERSION/build/make.log"
        exit 1
    fi

    echo ""
    echo "✓ DKMS module installed successfully"
    return 0
}

all() {
    TARGET_KERNEL_VERSION="$1";
    if [ -z "$TARGET_KERNEL_VERSION" ]; then
        TARGET_KERNEL_VERSION=`uname -r`
    fi

    echo "========================================"
    echo "  dxgkrnl Installation Script"
    echo "========================================"
    echo "Initial target kernel: ${TARGET_KERNEL_VERSION}"
    echo "Current running kernel: $(uname -r)"
    echo ""
    
    install_dependencies
    check_and_install_kernel
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

if [ -z "$1" ]; then
    all `uname -r`
elif [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+.+$ ]]; then
    all $1
else
    echo "Usage: $0 [kernel_version]"
    echo ""
    echo "Examples:"
    echo "  $0"
    echo "  $0 6.1.0-41-amd64"
    exit 1
fi

echo ""
echo "========================================"
echo "  Installation Completed Successfully!"
echo "========================================"
echo "Kernel version: ${TARGET_KERNEL_VERSION}"
echo "Module installed: dxgkrnl/${VERSION}"
echo ""
echo "STATUS: SUCCESS"
