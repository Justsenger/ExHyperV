#!/bin/bash
set -euo pipefail

LINUX_DISTRO="$(cat /etc/*-release 2>/dev/null || echo "")"
LINUX_DISTRO=${LINUX_DISTRO,,}

install_graphics_apt() {
    echo "[+] (Graphics) Cleaning up old PPA configurations..."
    sudo apt-get install -y -qq ppa-purge
    sudo ppa-purge -y ppa:kisak/turtle || true
    sudo ppa-purge -y ppa:kisak/kisak-mesa || true
    sudo rm -f /etc/apt/preferences.d/99-mesa-pinning
    sudo rm -f /etc/apt/preferences.d/00-mesa-hold-gl

    echo "[+] (Graphics) Installing base dependencies & Official Mesa..."
    sudo apt-get update -qq
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
        linux-headers-$(uname -r) build-essential git dkms curl \
        software-properties-common mesa-utils vulkan-tools mesa-va-drivers vainfo libgl1-mesa-dri

    echo "[+] (Graphics) Adding Kisak PPA for Vulkan..."
    sudo add-apt-repository ppa:kisak/turtle -y
    sudo apt-get update -qq

    echo "[+] (Graphics) Applying OpenGL package pinning..."
    sudo bash -c 'cat > /etc/apt/preferences.d/00-mesa-hold-gl <<EOF
Package: libgl1-mesa-dri libglapi-mesa libglx-mesa0 libgbm1
Pin: release o=Ubuntu
Pin-Priority: 1001
EOF'

    echo "[+] (Graphics) Ensuring OpenGL packages stay on compatible versions..."
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --allow-downgrades \
        libgl1-mesa-dri libglapi-mesa libglx-mesa0 libgbm1

    echo "[+] (Graphics) Pinning Vulkan package source..."
    sudo bash -c 'cat > /etc/apt/preferences.d/99-mesa-pinning <<EOF
Package: mesa-vulkan-drivers
Pin: version *kisak*
Pin-Priority: 900
EOF'

    echo "[+] (Graphics) Installing latest Vulkan drivers..."
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mesa-vulkan-drivers
}

install_graphics_arch() {
    echo "[+] (Graphics) Installing Arch Linux graphics stack..."
    sudo pacman -Sy --noconfirm --needed \
        mesa mesa-utils vulkan-tools vulkan-icd-loader libva-utils libva-mesa-driver

    # Install a Mesa Vulkan ICD explicitly. On some Arch setups, vulkan-tools +
    # loader are present but no usable ICD JSON is installed by default.
    if pacman -Si vulkan-dzn >/dev/null 2>&1; then
        echo "[+] (Graphics) Installing vulkan-dzn..."
        sudo pacman -Sy --noconfirm --needed vulkan-dzn
    elif pacman -Si vulkan-swrast >/dev/null 2>&1; then
        echo "[+] (Graphics) Installing vulkan-swrast (fallback)..."
        sudo pacman -Sy --noconfirm --needed vulkan-swrast
    else
        echo "[Warning] No known Mesa Vulkan ICD package found (vulkan-dzn/vulkan-swrast)."
    fi

    # Prefer D3D12-based Mesa Vulkan ICD when available in GPU-P guests.
    DZN_JSON=""
    if [ -f /usr/share/vulkan/icd.d/dzn_icd.json ]; then
        DZN_JSON="/usr/share/vulkan/icd.d/dzn_icd.json"
    elif [ -f /usr/share/vulkan/icd.d/dzn_icd.x86_64.json ]; then
        DZN_JSON="/usr/share/vulkan/icd.d/dzn_icd.x86_64.json"
    elif [ -f /usr/share/vulkan/icd.d/dzn_icd.i686.json ]; then
        DZN_JSON="/usr/share/vulkan/icd.d/dzn_icd.i686.json"
    fi

    if [ -n "$DZN_JSON" ]; then
        echo "[+] (Graphics) Pinning Vulkan ICD to DZN: $DZN_JSON"
        sudo tee /etc/profile.d/exhyperv-vulkan.sh > /dev/null <<EOF
export VK_ICD_FILENAMES="$DZN_JSON"
EOF
        sudo chmod 644 /etc/profile.d/exhyperv-vulkan.sh
        sudo touch /etc/environment
        sudo sed -i '/^VK_ICD_FILENAMES=/d' /etc/environment
        echo "VK_ICD_FILENAMES=$DZN_JSON" | sudo tee -a /etc/environment > /dev/null
    else
        echo "[Warning] DZN ICD JSON not found. Vulkan may fall back to non-working ICDs."
        sudo rm -f /etc/profile.d/exhyperv-vulkan.sh
        sudo touch /etc/environment
        sudo sed -i '/^VK_ICD_FILENAMES=/d' /etc/environment
    fi

    echo "[+] (Graphics) Arch Linux graphics packages are ready."
}

if [[ "$LINUX_DISTRO" == *"debian"* || "$LINUX_DISTRO" == *"ubuntu"* ]]; then
    install_graphics_apt
elif [[ "$LINUX_DISTRO" == *"arch"* ]]; then
    install_graphics_arch
else
    echo "[Warning] setup_graphics.sh: unsupported distro. Skipping graphics package setup."
fi
