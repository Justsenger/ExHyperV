#!/bin/bash
set -e

echo "[+] (Graphics) Cleaning up old PPA configurations..."
sudo apt-get install -y -qq ppa-purge
sudo ppa-purge -y ppa:kisak/turtle || true
sudo ppa-purge -y ppa:kisak/kisak-mesa || true
sudo rm -f /etc/apt/preferences.d/99-mesa-pinning
sudo rm -f /etc/apt/preferences.d/00-mesa-hold-gl

echo "[+] (Graphics) Installing base dependencies & Official Mesa (v23)..."
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
    linux-headers-$(uname -r) build-essential git dkms curl \
    software-properties-common mesa-utils vulkan-tools mesa-va-drivers vainfo libgl1-mesa-dri

echo "[+] (Graphics) Adding Kisak PPA for Vulkan (v25)..."
sudo add-apt-repository ppa:kisak/turtle -y
sudo apt-get update -qq

echo "[+] (Graphics) Applying Safety Lock for OpenGL (Force v23)..."
sudo bash -c 'cat > /etc/apt/preferences.d/00-mesa-hold-gl <<EOF
Package: libgl1-mesa-dri libglapi-mesa libglx-mesa0 libgbm1
Pin: release o=Ubuntu
Pin-Priority: 1001
EOF'

echo "[+] (Graphics) Ensuring OpenGL is compliant (Repairing v25 users)..."
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --allow-downgrades \
    libgl1-mesa-dri libglapi-mesa libglx-mesa0 libgbm1

echo "[+] (Graphics) Configuring APT Pinning to lock Vulkan to Kisak PPA..."
sudo bash -c 'cat > /etc/apt/preferences.d/99-mesa-pinning <<EOF
Package: mesa-vulkan-drivers
Pin: version *kisak*
Pin-Priority: 900
EOF'

echo "[+] (Graphics) Installing latest Vulkan drivers..."
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mesa-vulkan-drivers