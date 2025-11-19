#!/bin/bash -e

# --------------------------------------------------------
# install_dxgkrnl.sh
# --------------------------------------------------------

WORKDIR="$(dirname $(realpath $0))"
LINUX_DISTRO="$(cat /etc/*-release)"
LINUX_DISTRO=${LINUX_DISTRO,,}

# 指向你的 patches 根目录
PATCH_BASE_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/patches"

KERNEL_6_6_NEWER_REGEX="^(6\.[6-9]\.|6\.[0-9]{2,}\.)"
KERNEL_5_15_NEWER_REGEX="^(5\.1[5-9]+\.)"

install_dependencies() {
    NEED_TO_INSTALL=""
    if [ ! -e "/bin/git" ] && [ ! -e "/usr/bin/git" ]; then
        NEED_TO_INSTALL="git"; 
    fi
    # 确保 curl 被安装
    if [ ! -e "/usr/bin/curl" ] && [ ! -e "/bin/curl" ]; then
        NEED_TO_INSTALL="$NEED_TO_INSTALL curl"
    fi
    if [ ! -e "/sbin/dkms" ] && [ ! -e "/bin/dkms" ] && [ ! -e "/usr/bin/dkms" ]; then
        NEED_TO_INSTALL="$NEED_TO_INSTALL dkms"
    fi
    if [ ! -e "/usr/src/linux-headers-${TARGET_KERNEL_VERSION}" ]; then
        NEED_TO_INSTALL="$NEED_TO_INSTALL linux-headers-${TARGET_KERNEL_VERSION}";
    fi

    if [[ -z "$NEED_TO_INSTALL" ]]; then
        echo "All dependencies are already installed."
        return 0;
    fi

    if [[ "$LINUX_DISTRO" == *"debian"* ]]; then
        apt update;
        apt install -y $NEED_TO_INSTALL;
    elif [[ "$LINUX_DISTRO" == *"fedora"* ]]; then
        yum -y install $NEED_TO_INSTALL;
    else
        echo "Fatal: The system distro is unsupported";
        exit 1;
    fi
}

update_git() {
    if [[ "${TARGET_KERNEL_VERSION}" =~ $KERNEL_6_6_NEWER_REGEX ]]; then
        TARGET_BRANCH="linux-msft-wsl-6.6.y";
    elif [[ "${TARGET_KERNEL_VERSION}" =~ $KERNEL_5_15_NEWER_REGEX ]]; then
        TARGET_BRANCH="linux-msft-wsl-5.15.y";
    else
        echo "Fatal: Unsupported kernel version (5.15.0 <=)";
        exit 1;
    fi

    if [ ! -e "/tmp/WSL2-Linux-Kernel" ]; then
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
    cd /tmp/WSL2-Linux-Kernel

    case $CURRENT_BRANCH in
        "linux-msft-wsl-5.15.y")
            PATCHES="0001-Add-a-gpu-pv-support.patch \
                     0002-Add-a-multiple-kernel-version-support.patch";
            if [[ "$TARGET_KERNEL_VERSION" != *"azure"* ]]; then
                    PATCHES="$PATCHES 0003-Fix-gpadl-has-incomplete-type-error.patch";
            fi
            
            for PATCH in $PATCHES; do
                echo "Downloading patch: $PATCH"
                # 修正：使用完整的文件夹名称 linux-msft-wsl-5.15.y
                curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/$PATCH" | git apply -v;
                echo;
            done
            ;;
        "linux-msft-wsl-6.6.y")
            PATCHES="0001-Add-a-gpu-pv-support.patch";
            if [[ "$TARGET_KERNEL_VERSION" != *"truenas"* ]]; then
                PATCHES="$PATCHES 0002-Fix-eventfd_signal.patch";
            fi

            for PATCH in $PATCHES; do
                echo "Downloading patch: $PATCH"
                # 修正：使用完整的文件夹名称 linux-msft-wsl-6.6.y
                curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-6.6.y/$PATCH" | git apply -v;
                echo;
            done
            ;;
        *)
            echo "Fatal: \"$CURRENT_BRANCH\" is not available";
            exit 1;;
    esac

    echo -e "Copy: \n  \"/tmp/WSL2-Linux-Kernel/drivers/hv/dxgkrnl\" -> \"/usr/src/dxgkrnl-$VERSION\""
    cp -r ./drivers/hv/dxgkrnl /usr/src/dxgkrnl-$VERSION

    echo -e "Copy: \n  \"/tmp/WSL2-Linux-Kernel/include\" -> \"/usr/src/dxgkrnl-$VERSION/include\""
    cp -r ./include /usr/src/dxgkrnl-$VERSION/include

    sed -i 's/\$(CONFIG_DXGKRNL)/m/' /usr/src/dxgkrnl-$VERSION/Makefile
    echo "EXTRA_CFLAGS=-I\$(PWD)/include -D_MAIN_KERNEL_ \
                       -I/usr/src/linux-headers-\${kernelver}/include/linux \
                       -include /usr/src/linux-headers-\${kernelver}/include/linux/vmalloc.h" >> /usr/src/dxgkrnl-$VERSION/Makefile

    if [[ "${TARGET_KERNEL_VERSION}" =~ $KERNEL_6_6_NEWER_REGEX ]]; then
        BUILD_EXCLUSIVE_KERNEL=$KERNEL_6_6_NEWER_REGEX
    else
        BUILD_EXCLUSIVE_KERNEL=$KERNEL_5_15_NEWER_REGEX
    fi

    cat > /usr/src/dxgkrnl-$VERSION/dkms.conf << EOF
PACKAGE_NAME="dxgkrnl"
PACKAGE_VERSION="$VERSION"
BUILT_MODULE_NAME="dxgkrnl"
DEST_MODULE_LOCATION="/kernel/drivers/hv/dxgkrnl/"
AUTOINSTALL="yes"
BUILD_EXCLUSIVE_KERNEL="$BUILD_EXCLUSIVE_KERNEL"
EOF
}

install_dkms() {
    if dkms status | grep -q "dxgkrnl/$VERSION"; then
        echo "Module dxgkrnl/$VERSION already exists, removing first..."
        dkms remove dxgkrnl/$VERSION --all
    fi
    dkms -k ${TARGET_KERNEL_VERSION} add dxgkrnl/$VERSION
    dkms -k ${TARGET_KERNEL_VERSION} build dxgkrnl/$VERSION
    dkms -k ${TARGET_KERNEL_VERSION} install dxgkrnl/$VERSION
}

all() {
    TARGET_KERNEL_VERSION="$1";
    if [ "$TARGET_KERNEL_VERSION" == "" ]; then
        TARGET_KERNEL_VERSION=`uname -r`
    fi

    echo -e "\nTarget Kernel Version: ${TARGET_KERNEL_VERSION}\n"
    install_dependencies
    update_git
    get_version
    echo -e "\nModule Version: ${CURRENT_BRANCH} @ ${VERSION}\n"
    install
    install_dkms
}

if [ -z $1 ]; then
    all `uname -r`
elif [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+.+$ ]]; then
    all $1
else
    echo "Usage: $0 [kernel_version]"
fi

echo "Done."