#!/bin/bash
# @Name: fnOS-1.1.23-6.12.18-trim-HyperV-GPU-PV
# @Description: 在Win10 22h2 Build.19045測試可用, 已測試Win11 24h2不可用, 其他版本未知
# @Version: 3.8.0

set -e

BRANCH="linux-msft-wsl-6.6.y"
LIBDXG_BRANCH="main"
PKG_NAME="dxgkrnl"

log() {
  echo "[$(date '+%H:%M:%S')] $*"
}

retry_cmd() {
  local n=1
  local max=5
  local delay=5
  while true; do
    if "$@"; then
      break
    else
      if [ $n -lt $max ]; then
        log " -> [WARNING] 命令失敗，${delay} 秒後重試 (${n}/${max}): $*"
        sleep $delay
        n=$((n + 1))
      else
        log " -> [ERROR] 已達最大重試次數 (${max}): $*"
        return 1
      fi
    fi
  done
}

find_cmd() {
  local cmd="$1"
  shift || true
  for p in "$@"; do
    if [ -x "$p" ]; then
      echo "$p"
      return 0
    fi
  done
  command -v "$cmd" 2>/dev/null || true
}

if [ "$(id -u)" -ne 0 ]; then
  exec sudo -E bash "$0" "$@"
fi

MODPROBE_BIN="$(find_cmd modprobe /usr/sbin/modprobe /sbin/modprobe)"
DEPMOD_BIN="$(find_cmd depmod /usr/sbin/depmod /sbin/depmod)"
LDCONFIG_BIN="$(find_cmd ldconfig /usr/sbin/ldconfig /sbin/ldconfig)"
LSMOD_BIN="$(find_cmd lsmod /usr/sbin/lsmod /sbin/lsmod /bin/lsmod)"

KERNEL="$(uname -r)"
ARCH="$(uname -m)"
HEADERS_DIR="/usr/src/linux-headers-${KERNEL}"
BUILD_DIR="/lib/modules/${KERNEL}/build"
DEPLOY_DIR="$(dirname "$(realpath "$0")")"
LIB_DIR="${DEPLOY_DIR}/lib"

log "[+] Target kernel: ${KERNEL} (${ARCH})"
log "[+] Script dir: ${DEPLOY_DIR}"

if [ ! -d "${BUILD_DIR}" ]; then
  log "[ERROR] ${BUILD_DIR} 不存在"
  exit 1
fi

if [ ! -d "${HEADERS_DIR}" ]; then
  log "[ERROR] ${HEADERS_DIR} 不存在"
  exit 1
fi

log "[STEP: Installing basic dependencies...]"
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
  git dkms curl wget build-essential unzip aria2 \
  bc bison flex dwarves pahole \
  libelf-dev libssl-dev zlib1g-dev \
  ca-certificates linux-source-6.12 linux-config-6.12 \
  initramfs-tools lz4 cron kmod

log "[STEP: Preparing linux-source-6.12 toolchain...]"
cd /usr/src

if [ ! -d /usr/src/linux-source-6.12 ]; then
  if [ -f /usr/src/linux-source-6.12.tar.xz ]; then
    tar xf /usr/src/linux-source-6.12.tar.xz
  else
    log "[ERROR] /usr/src/linux-source-6.12.tar.xz 不存在"
    exit 1
  fi
fi

if [ ! -f "${BUILD_DIR}/.config" ]; then
  log " -> Missing ${BUILD_DIR}/.config, trying to restore..."
  if [ -f "/boot/config-${KERNEL}" ]; then
    cp "/boot/config-${KERNEL}" "${BUILD_DIR}/.config" || true
  elif [ -f "/usr/src/linux-config-6.12/config.amd64_none_amd64.xz" ]; then
    xz -dc /usr/src/linux-config-6.12/config.amd64_none_amd64.xz > "${BUILD_DIR}/.config" || true
  else
    log " -> No matching .config source found, continuing anyway..."
  fi
fi

log "[STEP: Building resolve_btfids...]"
cd /usr/src/linux-source-6.12/tools/bpf/resolve_btfids
make
mkdir -p "${HEADERS_DIR}/tools/bpf/resolve_btfids"
ln -sf /usr/src/linux-source-6.12/tools/bpf/resolve_btfids/resolve_btfids \
  "${HEADERS_DIR}/tools/bpf/resolve_btfids/resolve_btfids"

log "[STEP: Building objtool...]"
cd /usr/src/linux-source-6.12/tools/objtool
make
mkdir -p "${HEADERS_DIR}/tools/objtool"
ln -sf /usr/src/linux-source-6.12/tools/objtool/objtool \
  "${HEADERS_DIR}/tools/objtool/objtool"

if [ -f /sys/kernel/btf/vmlinux ]; then
  log "[STEP: Copying BTF vmlinux...]"
  cp /sys/kernel/btf/vmlinux "${BUILD_DIR}/vmlinux" 2>/dev/null || true
fi

log "[STEP: Cleaning old workspace...]"
rm -rf /tmp/libdxg /tmp/WSL2-Linux-Kernel /tmp/extra-defines.h
dkms remove -m "${PKG_NAME}" --all >/dev/null 2>&1 || true
rm -rf /var/lib/dkms/${PKG_NAME} || true
rm -rf /usr/src/${PKG_NAME}-* || true

log "[STEP: Cloning libdxg...]"
cd /tmp
retry_cmd git clone -b "${LIBDXG_BRANCH}" --no-checkout --depth=1 https://github.com/microsoft/libdxg.git
cd /tmp/libdxg
git sparse-checkout init --cone
git sparse-checkout set include
git checkout

log "[STEP: Cloning WSL2-Linux-Kernel (${BRANCH})...]"
cd /tmp
retry_cmd git clone -b "${BRANCH}" --no-checkout --depth=1 https://github.com/microsoft/WSL2-Linux-Kernel.git
cd /tmp/WSL2-Linux-Kernel
git sparse-checkout init --no-cone
git sparse-checkout set \
  /drivers/hv/dxgkrnl \
  /include/uapi/misc/d3dkmthk.h \
  /include/linux/hyperv.h \
  /include/linux/eventfd.h
git checkout

RUN="$(git rev-parse --short HEAD || echo custom)"
VERSION="${RUN}fnos"
DXGSRC="/usr/src/${PKG_NAME}-${VERSION}"

log "[STEP: Preparing DXG source tree...]"
rm -rf "${DXGSRC}"
mkdir -p "${DXGSRC}"
cp -r /tmp/WSL2-Linux-Kernel/drivers/hv/dxgkrnl/* "${DXGSRC}/"

mkdir -p "${DXGSRC}/include/uapi/misc"
mkdir -p "${DXGSRC}/include/linux"
mkdir -p "${DXGSRC}/include/libdxg"
mkdir -p "${DXGSRC}/mm"

cp -r /tmp/libdxg/include/* "${DXGSRC}/include/libdxg/"
cp /tmp/WSL2-Linux-Kernel/include/uapi/misc/d3dkmthk.h "${DXGSRC}/include/uapi/misc/d3dkmthk.h"
cp /tmp/WSL2-Linux-Kernel/include/linux/hyperv.h "${DXGSRC}/include/linux/hyperv_dxgkrnl.h"
cp /tmp/WSL2-Linux-Kernel/include/linux/eventfd.h "${DXGSRC}/include/linux/eventfd.h"

log "[STEP: Adjusting sources...]"
sed -i 's/\$(CONFIG_DXGKRNL)/m/' "${DXGSRC}/Makefile"
sed -i 's#uapi/linux/eventfd.h#linux/eventfd.h#g' "${DXGSRC}/include/linux/eventfd.h" || true
# 重新命名 hyperv.h，避免与主机内核头文件冲突。
sed -i 's#linux/hyperv.h#linux/hyperv_dxgkrnl.h#' "${DXGSRC}/dxgmodule.c"
sed -i 's/eventfd_signal(event->cpu_event, 1)/eventfd_signal(event->cpu_event)/g' "${DXGSRC}/dxgmodule.c" || true

log "[STEP: Downloading extra-defines.h...]"
retry_cmd wget -q https://raw.githubusercontent.com/MBRjun/dxgkrnl-dkms-lts/master/extra-defines.h -O /tmp/extra-defines.h
cp /tmp/extra-defines.h "${DXGSRC}/include/extra-defines.h"

cat >> "${DXGSRC}/Makefile" <<'EOF'

EXTRA_CFLAGS += -I$(PWD)/include -DMAIN_KERNEL -DCONFIG_DXGKRNL=m \
-include $(PWD)/include/extra-defines.h \
-I$(PWD)/include/libdxg \
-I/usr/src/linux-source-6.12/include/linux \
-include /usr/src/linux-source-6.12/include/linux/vmalloc.h \
-include $(PWD)/include/uapi/misc/d3dkmthk.h \
-Wno-empty-body
EOF

log "[STEP: Writing DKMS config...]"
cat > "${DXGSRC}/dkms.conf" <<EOF
PACKAGE_NAME="${PKG_NAME}"
PACKAGE_VERSION="${VERSION}"
BUILT_MODULE_NAME[0]="dxgkrnl"
DEST_MODULE_LOCATION[0]="/kernel/drivers/hv/dxgkrnl"
AUTOINSTALL="yes"
MAKE[0]="make -j1 KERNELRELEASE=\${kernelver} -C /lib/modules/\${kernelver}/build M=\${dkms_tree}/${PKG_NAME}/${VERSION}/build"
CLEAN="make -C /lib/modules/\${kernelver}/build M=\${dkms_tree}/${PKG_NAME}/${VERSION}/build clean"
EOF

log "[STEP: Building and Installing DXG Module...]"
dkms add -m "${PKG_NAME}" -v "${VERSION}"
dkms build -m "${PKG_NAME}" -v "${VERSION}" -k "${KERNEL}"
dkms install -m "${PKG_NAME}" -v "${VERSION}" -k "${KERNEL}" --force

log "[STEP: Testing module load...]"
[ -n "${DEPMOD_BIN}" ] && "${DEPMOD_BIN}" -a "${KERNEL}" || true
[ -n "${MODPROBE_BIN}" ] && "${MODPROBE_BIN}" dxgkrnl || true

log "[STEP: Verifying module...]"
find "/lib/modules/${KERNEL}" -name dxgkrnl.ko -o -name dxgkrnl.ko.xz 2>/dev/null || true
[ -n "${LSMOD_BIN}" ] && "${LSMOD_BIN}" | grep dxgkrnl || true
ls -l /dev/dxg || true

log "[STEP: Deploying WSL Core Libraries...]"
mkdir -p /usr/lib/wsl/lib
if [ -d "${LIB_DIR}" ]; then
  cp -a "${LIB_DIR}/." /usr/lib/wsl/lib/ 2>/dev/null || true
  if [ -f "${LIB_DIR}/nvidia-smi" ]; then
    cp "${LIB_DIR}/nvidia-smi" /usr/bin/nvidia-smi
    chmod 755 /usr/bin/nvidia-smi
  fi
fi

ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so 2>/dev/null || true

log "[STEP: Fixing libcuda symlinks...]"
mkdir -p /usr/lib/x86_64-linux-gnu
if [ -f /usr/lib/wsl/lib/libcuda.so.1 ]; then
  ln -sf /usr/lib/wsl/lib/libcuda.so.1 /usr/lib/x86_64-linux-gnu/libcuda.so.1
  ln -sf /usr/lib/x86_64-linux-gnu/libcuda.so.1 /usr/lib/x86_64-linux-gnu/libcuda.so
fi

cat > /etc/ld.so.conf.d/ld.wsl.conf <<'EOF'
/usr/lib/wsl/lib
EOF

[ -n "${LDCONFIG_BIN}" ] && "${LDCONFIG_BIN}" || true

log "[STEP: Configuring module loading...]"
mkdir -p /etc/modules-load.d
echo "vgem" > /etc/modules-load.d/vgem.conf
[ -n "${MODPROBE_BIN}" ] && "${MODPROBE_BIN}" vgem || true

rm -f /etc/modprobe.d/blacklist-dxgkrnl.conf

cat > /usr/local/bin/load_dxg_driver.sh <<EOF
#!/bin/bash
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
LOG=/var/log/load-dxg.log
KERNEL="${KERNEL}"

echo "==== \$(date) ====" >> "\$LOG"
rm -f /etc/modprobe.d/blacklist-dxgkrnl.conf

if [ -x /usr/sbin/depmod ]; then
  /usr/sbin/depmod -a "\$KERNEL" >> "\$LOG" 2>&1 || true
elif [ -x /sbin/depmod ]; then
  /sbin/depmod -a "\$KERNEL" >> "\$LOG" 2>&1 || true
fi

if [ -x /usr/sbin/modprobe ]; then
  /usr/sbin/modprobe -v dxgkrnl >> "\$LOG" 2>&1 || true
elif [ -x /sbin/modprobe ]; then
  /sbin/modprobe -v dxgkrnl >> "\$LOG" 2>&1 || true
fi

if [ -e /dev/dxg ]; then
  chmod 666 /dev/dxg || true
fi

if command -v lsmod >/dev/null 2>&1; then
  lsmod | grep dxgkrnl >> "\$LOG" 2>&1 || true
fi
ls -l /dev/dxg >> "\$LOG" 2>&1 || true
EOF
chmod +x /usr/local/bin/load_dxg_driver.sh

log "[STEP: Rebuilding initramfs (normal mode)...]"
update-initramfs -u || true

log "[STEP: Registering reboot auto-load via root crontab...]"
TMP_CRON="$(mktemp)"
crontab -l 2>/dev/null | grep -v 'load_dxg_driver.sh' > "${TMP_CRON}" || true
cat >> "${TMP_CRON}" <<'EOF'
@reboot /bin/bash -lc 'sleep 20; /usr/local/bin/load_dxg_driver.sh'
EOF
crontab "${TMP_CRON}"
rm -f "${TMP_CRON}"

log "[STEP: Ensuring cron is running...]"
service cron start >/dev/null 2>&1 || true

log "[STEP: Loading dxg directly for current session...]"
/usr/local/bin/load_dxg_driver.sh || true

log "[STEP: Checking runtime libraries...]"
if [ -n "${LDCONFIG_BIN}" ]; then
  "${LDCONFIG_BIN}" -p | grep -E 'libcuda|libdxcore|libd3d12' || true
fi

log "[STEP: Optional runtime smoke test...]"
python3 - <<'PY' || true
import ctypes
for lib in ["libcuda.so", "libcuda.so.1", "libdxcore.so", "libd3d12.so"]:
    try:
        ctypes.CDLL(lib)
        print(lib, "OK")
    except Exception as e:
        print(lib, "FAIL", e)
PY

log "[STEP: Final verification...]"
[ -n "${LSMOD_BIN}" ] && "${LSMOD_BIN}" | grep -E 'dxgkrnl|vgem' || true
ls -l /dev/dxg || true

# 保留部署目录以兼容外部清理流程。
log "[STEP: Keeping deployment directory for external cleanup compatibility...]"
cd /

log "[STATUS: SUCCESS]"
exit 0
