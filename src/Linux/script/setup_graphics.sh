#!/bin/bash
set -e

echo "[+] (Graphics) Cleaning up old PPA configurations..."
sudo apt-get install -y -qq ppa-purge
sudo ppa-purge -y ppa:kisak/turtle || true
sudo ppa-purge -y ppa:kisak/kisak-mesa || true
sudo rm -f /etc/apt/preferences.d/99-mesa-pinning

echo "[+] (Graphics) Installing base dependencies & Official Mesa (v23)..."
sudo apt-get update -qq
# 关键：显式安装 libgl1-mesa-dri (OpenGL) 让它占住官方源的版本
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
    linux-headers-$(uname -r) build-essential git dkms curl \
    software-properties-common mesa-utils vulkan-tools mesa-va-drivers vainfo libgl1-mesa-dri

echo "[+] (Graphics) Adding Kisak PPA for Vulkan (v25)..."
sudo add-apt-repository ppa:kisak/turtle -y
sudo apt-get update -qq

echo "[+] (Graphics) Configuring APT Pinning to lock Vulkan to Kisak PPA..."
# 核心逻辑：只让 mesa-vulkan-drivers 使用 kisak 源
sudo bash -c 'cat > /etc/apt/preferences.d/99-mesa-pinning <<EOF
Package: mesa-vulkan-drivers
Pin: version *kisak*
Pin-Priority: 900
EOF'

echo "[+] (Graphics) Installing latest Vulkan drivers..."
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mesa-vulkan-drivers