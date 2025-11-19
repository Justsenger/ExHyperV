#!/bin/bash
# configure_system.sh
# 参数 $1: "enable_graphics" 或其他

ENABLE_GRAPHICS=$1
DEPLOY_DIR="$HOME/exhyperv_deploy"
LIB_DIR="$DEPLOY_DIR/lib"
GITHUB_LIB_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/lib"

echo "[+] Checking and downloading missing core libraries..."
LIBS=("libd3d12.so" "libd3d12core.so" "libdxcore.so")

for lib in "${LIBS[@]}"; do
    if [ ! -f "$LIB_DIR/$lib" ]; then
        echo " -> $lib not found locally, downloading from GitHub..."
        wget -q -c "$GITHUB_LIB_URL/$lib" -O "$LIB_DIR/$lib"
    fi
done

echo "[+] Deploying driver files..."
sudo mkdir -p /usr/lib/wsl/drivers /usr/lib/wsl/lib
sudo rm -rf /usr/lib/wsl/drivers/* /usr/lib/wsl/lib/*
sudo cp -r $DEPLOY_DIR/drivers/* /usr/lib/wsl/drivers/
sudo cp -a $LIB_DIR/*.so* /usr/lib/wsl/lib/

if [ -f "$LIB_DIR/nvidia-smi" ]; then
    sudo cp $LIB_DIR/nvidia-smi /usr/bin/nvidia-smi
    sudo chmod 755 /usr/bin/nvidia-smi
fi

sudo ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so
sudo chmod -R 0555 /usr/lib/wsl
sudo chown -R root:root /usr/lib/wsl

# ldconfig
echo "/usr/lib/wsl/lib" | sudo tee /etc/ld.so.conf.d/ld.wsl.conf > /dev/null
sudo ldconfig

# ==========================================================
# ### 关键变更点：内核模块加载策略修改 (延迟加载 dxgkrnl) ###
# ==========================================================

echo "[+] Configuring Kernel Modules (vgem & dxgkrnl)..."

# 1. vgem 依然使用标准方式自动加载
echo "vgem" | sudo tee /etc/modules-load.d/vgem.conf > /dev/null
sudo modprobe vgem

# 2. dxgkrnl 加入黑名单，防止系统启动时自动加载
echo "blacklist dxgkrnl" | sudo tee /etc/modprobe.d/blacklist-dxgkrnl.conf > /dev/null

# 3. 更新 initramfs 以应用黑名单
echo " -> Updating initramfs (this may take a while)..."
sudo update-initramfs -u

# 4. 创建延迟加载脚本 (完全匹配你的输入)
echo " -> Creating late-load script..."
sudo tee /usr/local/bin/load_dxg_driver.sh > /dev/null << 'EOF'
#!/bin/bash
modprobe dxgkrnl
if [ -e /dev/dxg ]; then
    chmod 666 /dev/dxg
fi
EOF
sudo chmod +x /usr/local/bin/load_dxg_driver.sh

# 5. 创建 systemd 服务 (完全匹配你的输入)
echo " -> Creating systemd service for late loading..."
sudo tee /etc/systemd/system/load-dxg-late.service > /dev/null << 'EOF'
[Unit]
Description=Late load dxgkrnl
After=graphical.target

[Service]
Type=simple
User=root
ExecStart=/usr/local/bin/load_dxg_driver.sh

[Install]
WantedBy=graphical.target
EOF

# 6. 启用服务
sudo systemctl daemon-reload
sudo systemctl enable load-dxg-late.service

# ==========================================================
# ### 变更结束 ###
# ==========================================================

if [ "$ENABLE_GRAPHICS" == "enable_graphics" ]; then
    echo "[+] Configuring environment variables for Graphics..."
    
    # Clean old
    sudo sed -i '/GALLIUM_DRIVERS/d' /etc/environment
    sudo sed -i '/LIBVA_DRIVER_NAME/d' ~/.bashrc
    
    # Add new
    cat >> ~/.bashrc <<EOF

# GPU-PV Configuration
export GALLIUM_DRIVERS=d3d12
export DRI_PRIME=1
export LIBVA_DRIVER_NAME=d3d12
EOF
    
    sudo usermod -a -G video,render $USER
    sudo chmod 666 /dev/dri/* || true
    sudo ln -sf /dev/dri/card1 /dev/dri/card0
fi

echo "[+] Cleaning up..."
rm -rf $DEPLOY_DIR