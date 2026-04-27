#!/bin/bash
# @Name: fnOS-1.1.23(6.12.18-trim)-HyperV-GPU-PV
# @Description: FNOS dxgkrnl + WSL libs + reboot auto-load via root crontab
# @Version: 3.7.0

# 發生錯誤時立即終止腳本，避免後續步驟在異常狀態下繼續執行
set -e

# 指定要抓取的 WSL2-Linux-Kernel 分支
BRANCH="linux-msft-wsl-6.6.y"
# 指定 libdxg 的來源分支
LIBDXG_BRANCH="main"
# DKMS 套件名稱
PKG_NAME="dxgkrnl"

# 簡易日誌函式：輸出帶時間戳的訊息
log() {
  echo "[$(date '+%H:%M:%S')] $*"
}

# 可重試執行命令的函式：
# 若命令失敗，最多重試 5 次，每次間隔 5 秒
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

# 尋找可執行命令的實際路徑：
# 先檢查傳入的候選路徑，再回退到 command -v
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

# 若不是 root，則自動用 sudo 重新以 root 權限執行本腳本
if [ "$(id -u)" -ne 0 ]; then
  exec sudo -E bash "$0" "$@"
fi

# 尋找系統工具實際位置，兼容不同發行版路徑差異
MODPROBE_BIN="$(find_cmd modprobe /usr/sbin/modprobe /sbin/modprobe)"
DEPMOD_BIN="$(find_cmd depmod /usr/sbin/depmod /sbin/depmod)"
LDCONFIG_BIN="$(find_cmd ldconfig /usr/sbin/ldconfig /sbin/ldconfig)"
LSMOD_BIN="$(find_cmd lsmod /usr/sbin/lsmod /sbin/lsmod /bin/lsmod)"

# 取得目前執行中的核心版本與架構
KERNEL="$(uname -r)"
ARCH="$(uname -m)"
# 核心標頭目錄
HEADERS_DIR="/usr/src/linux-headers-${KERNEL}"
# 核心編譯目錄
BUILD_DIR="/lib/modules/${KERNEL}/build"
# 腳本所在目錄，作為部署目錄
DEPLOY_DIR="$(dirname "$(realpath "$0")")"
# 預期外掛的 WSL / GPU 使用者態函式庫位置
LIB_DIR="${DEPLOY_DIR}/lib"

log "[+] Target kernel: ${KERNEL} (${ARCH})"
log "[+] Script dir: ${DEPLOY_DIR}"

# 檢查核心 build 目錄是否存在
if [ ! -d "${BUILD_DIR}" ]; then
  log "[ERROR] ${BUILD_DIR} 不存在"
  exit 1
fi

# 檢查核心 headers 目錄是否存在
if [ ! -d "${HEADERS_DIR}" ]; then
  log "[ERROR] ${HEADERS_DIR} 不存在"
  exit 1
fi

# 安裝編譯、DKMS、initramfs、cron 與相關工具依賴
log "[STEP: Installing basic dependencies...]"
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
  git dkms curl wget build-essential unzip aria2 \
  bc bison flex dwarves pahole \
  libelf-dev libssl-dev zlib1g-dev \
  ca-certificates linux-source-6.12 linux-config-6.12 \
  initramfs-tools lz4 cron kmod

# 準備 linux-source-6.12 原始碼工具鏈
log "[STEP: Preparing linux-source-6.12 toolchain...]"
cd /usr/src

# 若原始碼尚未解開，則從 tar.xz 解壓
if [ ! -d /usr/src/linux-source-6.12 ]; then
  if [ -f /usr/src/linux-source-6.12.tar.xz ]; then
    tar xf /usr/src/linux-source-6.12.tar.xz
  else
    log "[ERROR] /usr/src/linux-source-6.12.tar.xz 不存在"
    exit 1
  fi
fi

# 若目前核心 build 目錄沒有 .config，嘗試從 /boot 或 linux-config 還原
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

# 編譯 resolve_btfids，某些核心模組建置流程會用到
log "[STEP: Building resolve_btfids...]"
cd /usr/src/linux-source-6.12/tools/bpf/resolve_btfids
make
mkdir -p "${HEADERS_DIR}/tools/bpf/resolve_btfids"
ln -sf /usr/src/linux-source-6.12/tools/bpf/resolve_btfids/resolve_btfids \
  "${HEADERS_DIR}/tools/bpf/resolve_btfids/resolve_btfids"

# 編譯 objtool，核心模組建置常需此工具
log "[STEP: Building objtool...]"
cd /usr/src/linux-source-6.12/tools/objtool
make
mkdir -p "${HEADERS_DIR}/tools/objtool"
ln -sf /usr/src/linux-source-6.12/tools/objtool/objtool \
  "${HEADERS_DIR}/tools/objtool/objtool"

# 若系統可取得 BTF vmlinux，則複製到 build 目錄，提升相容性
if [ -f /sys/kernel/btf/vmlinux ]; then
  log "[STEP: Copying BTF vmlinux...]"
  cp /sys/kernel/btf/vmlinux "${BUILD_DIR}/vmlinux" 2>/dev/null || true
fi

# 清理舊的暫存區與舊版 DKMS/原始碼殘留
log "[STEP: Cleaning old workspace...]"
rm -rf /tmp/libdxg /tmp/WSL2-Linux-Kernel /tmp/extra-defines.h
dkms remove -m "${PKG_NAME}" --all >/dev/null 2>&1 || true
rm -rf /var/lib/dkms/${PKG_NAME} || true
rm -rf /usr/src/${PKG_NAME}-* || true

# 只淺層抓取 libdxg，並透過 sparse-checkout 只取 include
log "[STEP: Cloning libdxg...]"
cd /tmp
retry_cmd git clone -b "${LIBDXG_BRANCH}" --no-checkout --depth=1 https://github.com/microsoft/libdxg.git
cd /tmp/libdxg
git sparse-checkout init --cone
git sparse-checkout set include
git checkout

# 只淺層抓取 WSL2 核心樹，並只取 dxgkrnl 與必要標頭
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

# 以目前 commit 短 SHA 組成版本號，供 DKMS 使用
RUN="$(git rev-parse --short HEAD || echo custom)"
VERSION="${RUN}fnos"
DXGSRC="/usr/src/${PKG_NAME}-${VERSION}"

# 建立 dxgkrnl 的 DKMS 原始碼目錄
log "[STEP: Preparing DXG source tree...]"
rm -rf "${DXGSRC}"
mkdir -p "${DXGSRC}"
cp -r /tmp/WSL2-Linux-Kernel/drivers/hv/dxgkrnl/* "${DXGSRC}/"

# 建立額外 include 目錄結構
mkdir -p "${DXGSRC}/include/uapi/misc"
mkdir -p "${DXGSRC}/include/linux"
mkdir -p "${DXGSRC}/include/libdxg"
mkdir -p "${DXGSRC}/mm"

# 複製 libdxg 與必要標頭檔到 DXG 原始碼樹
cp -r /tmp/libdxg/include/* "${DXGSRC}/include/libdxg/"
cp /tmp/WSL2-Linux-Kernel/include/uapi/misc/d3dkmthk.h "${DXGSRC}/include/uapi/misc/d3dkmthk.h"
cp /tmp/WSL2-Linux-Kernel/include/linux/hyperv.h "${DXGSRC}/include/linux/hyperv_dxgkrnl.h"
cp /tmp/WSL2-Linux-Kernel/include/linux/eventfd.h "${DXGSRC}/include/linux/eventfd.h"

# 針對不同核心/來源差異做簡單修補
log "[STEP: Adjusting sources...]"
# 把 Makefile 中 CONFIG_DXGKRNL 改成模組模式 m
sed -i 's/\$(CONFIG_DXGKRNL)/m/' "${DXGSRC}/Makefile"
# 修正 eventfd include 路徑
sed -i 's#uapi/linux/eventfd.h#linux/eventfd.h#g' "${DXGSRC}/include/linux/eventfd.h" || true
# 避免與宿主系統 hyperv.h 名稱衝突，改引用重新命名後的標頭
sed -i 's#linux/hyperv.h#linux/hyperv_dxgkrnl.h#' "${DXGSRC}/dxgmodule.c"
# 修正部分核心版本 eventfd_signal 參數差異
sed -i 's/eventfd_signal(event->cpu_event, 1)/eventfd_signal(event->cpu_event)/g' "${DXGSRC}/dxgmodule.c" || true

# 下載額外相容性巨集定義
log "[STEP: Downloading extra-defines.h...]"
retry_cmd wget -q https://raw.githubusercontent.com/MBRjun/dxgkrnl-dkms-lts/master/extra-defines.h -O /tmp/extra-defines.h
cp /tmp/extra-defines.h "${DXGSRC}/include/extra-defines.h"

# 追加額外編譯旗標，補齊 include 與必要強制包含檔案
cat >> "${DXGSRC}/Makefile" <<'EOF'

EXTRA_CFLAGS += -I$(PWD)/include -DMAIN_KERNEL -DCONFIG_DXGKRNL=m \
-include $(PWD)/include/extra-defines.h \
-I$(PWD)/include/libdxg \
-I/usr/src/linux-source-6.12/include/linux \
-include /usr/src/linux-source-6.12/include/linux/vmalloc.h \
-include $(PWD)/include/uapi/misc/d3dkmthk.h \
-Wno-empty-body
EOF

# 產生 DKMS 設定檔，讓系統可對此模組進行 add/build/install
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

# 透過 DKMS 註冊、建置並安裝 dxgkrnl 模組
log "[STEP: Building and Installing DXG Module...]"
dkms add -m "${PKG_NAME}" -v "${VERSION}"
dkms build -m "${PKG_NAME}" -v "${VERSION}" -k "${KERNEL}"
dkms install -m "${PKG_NAME}" -v "${VERSION}" -k "${KERNEL}" --force

# 重新產生模組相依資訊，並嘗試立即載入 dxgkrnl
log "[STEP: Testing module load...]"
[ -n "${DEPMOD_BIN}" ] && "${DEPMOD_BIN}" -a "${KERNEL}" || true
[ -n "${MODPROBE_BIN}" ] && "${MODPROBE_BIN}" dxgkrnl || true

# 驗證模組檔案與 /dev/dxg 是否成功出現
log "[STEP: Verifying module...]"
find "/lib/modules/${KERNEL}" -name dxgkrnl.ko -o -name dxgkrnl.ko.xz 2>/dev/null || true
[ -n "${LSMOD_BIN}" ] && "${LSMOD_BIN}" | grep dxgkrnl || true
ls -l /dev/dxg || true

# 部署 WSL 使用者態核心函式庫
log "[STEP: Deploying WSL Core Libraries...]"
mkdir -p /usr/lib/wsl/lib
if [ -d "${LIB_DIR}" ]; then
  cp -a "${LIB_DIR}/." /usr/lib/wsl/lib/ 2>/dev/null || true
  # 若附帶 nvidia-smi，則一併安裝到 /usr/bin
  if [ -f "${LIB_DIR}/nvidia-smi" ]; then
    cp "${LIB_DIR}/nvidia-smi" /usr/bin/nvidia-smi
    chmod 755 /usr/bin/nvidia-smi
  fi
fi

# 建立大小寫相容的 libD3D12Core.so 符號連結
ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so 2>/dev/null || true

# 修正 libcuda 相關符號連結，讓一般程式較容易找到
log "[STEP: Fixing libcuda symlinks...]"
mkdir -p /usr/lib/x86_64-linux-gnu
if [ -f /usr/lib/wsl/lib/libcuda.so.1 ]; then
  ln -sf /usr/lib/wsl/lib/libcuda.so.1 /usr/lib/x86_64-linux-gnu/libcuda.so.1
  ln -sf /usr/lib/x86_64-linux-gnu/libcuda.so.1 /usr/lib/x86_64-linux-gnu/libcuda.so
fi

# 把 /usr/lib/wsl/lib 加入動態連結器搜尋路徑
cat > /etc/ld.so.conf.d/ld.wsl.conf <<'EOF'
/usr/lib/wsl/lib
EOF

# 重新整理 linker cache
[ -n "${LDCONFIG_BIN}" ] && "${LDCONFIG_BIN}" || true

# 設定開機模組載入
log "[STEP: Configuring module loading...]"
mkdir -p /etc/modules-load.d
# vgem 可能是相關圖形堆疊依賴之一，先設為開機載入
echo "vgem" > /etc/modules-load.d/vgem.conf
[ -n "${MODPROBE_BIN}" ] && "${MODPROBE_BIN}" vgem || true

# 移除可能阻止 dxgkrnl 載入的 blacklist
rm -f /etc/modprobe.d/blacklist-dxgkrnl.conf

# 建立開機後自動載入 dxg 驅動的小腳本
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

# 若 /dev/dxg 已建立，則放寬權限
if [ -e /dev/dxg ]; then
  chmod 666 /dev/dxg || true
fi

# 把模組與裝置節點狀態寫入日誌
if command -v lsmod >/dev/null 2>&1; then
  lsmod | grep dxgkrnl >> "\$LOG" 2>&1 || true
fi
ls -l /dev/dxg >> "\$LOG" 2>&1 || true
EOF
chmod +x /usr/local/bin/load_dxg_driver.sh

# 更新 initramfs，讓開機環境同步新設定
log "[STEP: Rebuilding initramfs (normal mode)...]"
update-initramfs -u || true

# 透過 root crontab 在每次開機後延遲 20 秒載入驅動
log "[STEP: Registering reboot auto-load via root crontab...]"
TMP_CRON="$(mktemp)"
crontab -l 2>/dev/null | grep -v 'load_dxg_driver.sh' > "${TMP_CRON}" || true
cat >> "${TMP_CRON}" <<'EOF'
@reboot /bin/bash -lc 'sleep 20; /usr/local/bin/load_dxg_driver.sh'
EOF
crontab "${TMP_CRON}"
rm -f "${TMP_CRON}"

# 確保 cron 服務正在運作
log "[STEP: Ensuring cron is running...]"
service cron start >/dev/null 2>&1 || true

# 目前這個 session 先直接手動執行一次載入腳本
log "[STEP: Loading dxg directly for current session...]"
/usr/local/bin/load_dxg_driver.sh || true

# 檢查 linker cache 中是否已有目標圖形函式庫
log "[STEP: Checking runtime libraries...]"
if [ -n "${LDCONFIG_BIN}" ]; then
  "${LDCONFIG_BIN}" -p | grep -E 'libcuda|libdxcore|libd3d12' || true
fi

# 用 Python ctypes 做簡單 smoke test，確認主要函式庫是否可載入
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

# 最後再做一次模組與裝置節點驗證
log "[STEP: Final verification...]"
[ -n "${LSMOD_BIN}" ] && "${LSMOD_BIN}" | grep -E 'dxgkrnl|vgem' || true
ls -l /dev/dxg || true

# 回到根目錄，保留部署目錄不刪除，以兼容外部清理流程
log "[STEP: Keeping deployment directory for external cleanup compatibility...]"
cd /

# 腳本完成
log "[STATUS: SUCCESS]"
exit 0