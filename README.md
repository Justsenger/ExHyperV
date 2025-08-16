# ExHyperV

<div align="center">

**A graphical management tool for advanced Hyper-V features, making it easy for anyone to master high-end functions like DDA and GPU-P.**

</div>

<p align="center">
  <a href="https://github.com/Justsenger/ExHyperV/releases/latest"><img src="https://img.shields.io/github/v/release/Justsenger/ExHyperV.svg?style=flat-square" alt="Latest release"></a>
  <a href="https://github.com/Justsenger/ExHyperV/releases"><img src="https://img.shields.io/github/downloads/Justsenger/ExHyperV/total.svg?style=flat-square" alt="Downloads"></a>
  <a href="https://t.me/ExHyperV"><img src="https://img.shields.io/badge/discussion-Telegram-blue.svg?style=flat-square" alt="Telegram"></a>
  <a href="https://github.com/Justsenger/ExHyperV/blob/main/LICENSE"><img src="https://img.shields.io/github/license/Justsenger/ExHyperV.svg?style=flat-square" alt="License"></a>
</p>

**English** | [中文](https://github.com/Justsenger/ExHyperV/blob/main/README_cn.md)

---

ExHyperV is built upon in-depth research of official Microsoft documentation and the [Easy-GPU-PV](https://github.com/jamesstringerparsec/Easy-GPU-PV) project. It aims to fix and enhance existing solutions by providing a graphical, user-friendly configuration tool for advanced Hyper-V features.

Due to limited personal time and resources, there may be untested scenarios. If you encounter any software or game compatibility issues, please feel free to report them via [Issues](https://github.com/Justsenger/ExHyperV/issues)!

## ✨ Interface Overview

![Main Interface](https://github.com/Justsenger/ExHyperV/blob/main/img/1.png)
<details>
<summary>Click to see more screenshots</summary>

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/2.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/3.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/4.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/5.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/6.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/various.png)
*<p align="center">The tool has expanded GPU recognition capabilities, but whether a function can be enabled depends on the hardware itself.</p>*

</details>

## 🚀 Quick Start

### 1. System Requirements

#### DDA (Discrete Device Assignment)
- Windows Server 2019 / 2022 / 2025

#### GPU-P (GPU Partitioning / Paravirtualization)
- Windows 11
- Windows Server 2022
- Windows Server 2025

> **Note**: This tool requires the host system to be **Build 22000 or newer**. This is because the `Add-VMGpuPartitionAdapter` cmdlet in older systems lacks the `InstancePath` parameter, making it impossible to precisely specify a GPU and potentially causing confusion. To simplify operations, please ensure your host system is updated.

### 2. Download & Run
- **Download**: Go to the [Releases page](https://github.com/Justsenger/ExHyperV/releases/latest) to download the latest version.
- **Run**: Unzip the package and run `ExHyperV.exe`.

### 3. Build (Optional)
1. Install [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) and ensure the .NET desktop development (C# and WPF) workload is installed.
2. Clone this repository.
3. Open `/src/ExHyperV.sln` with Visual Studio to compile the project.

## 📌 Important Notes & Limitations

- It is recommended to assign a **fixed amount** of RAM to your virtual machines.
- This tool supports both **Generation 1** and **Generation 2** virtual machines.
- GPU-PV does **not** support the **checkpoint** feature.
- A single physical GPU can only be used for either **DDA or GPU-P at a time**, not both simultaneously.
- A virtual machine can use **both DDA and GPU-P at the same time** (e.g., passthrough one device with DDA while using GPU-P from another card).
- A single physical GPU can create multiple GPU-P partitions for a **single virtual machine**, but the total performance remains unchanged.
- A virtual machine can use GPU-P partitions from **multiple different physical GPUs** simultaneously.

---

## Core Features Explained

### Ⅰ. DDA (Discrete Device Assignment)

DDA (Discrete Device Assignment) allows you to assign a complete PCIe device (like a GPU, network card, or USB controller) directly to a virtual machine.

- **Device Compatibility**: If a device is not shown in the list, it means it cannot be assigned independently. You may need to assign its parent PCIe controller instead.
- **GPU Support**:
    - **Nvidia**: Generally works well.
    - **AMD/Intel**: Not extensively tested. AMD consumer-grade GPUs may have issues due to a lack of support for [Function-Level Reset (FLR)](https://www.reddit.com/r/Amd/comments/jehkey/will_big_navi_support_function_level_reset_flr/). Feedback is welcome!
- **VM Guest OS**: Windows is recommended, with no specific version restrictions. Linux is untested.

#### DDA Device States Explained
> Understanding the three device states is crucial for troubleshooting.

1.  **Host State**: The device is normally attached to the host system and can be used by the host.
2.  **Dismounted State**: The device has been dismounted from the host (`Dismount-VMHostAssignableDevice`) but has not been successfully assigned to a VM. In this state, the device is unavailable in the host's Device Manager. You can use this tool to remount it to the host or assign it to a VM.
3.  **Guest State**: The device has been successfully assigned to and is mounted in the virtual machine.

#### DDA Graphics Card Compatibility (Continuously updated)
> True compatibility can only be confirmed after installing drivers inside the virtual machine. Please share your test results via [Issues](https://github.com/Justsenger/ExHyperV/issues)!

| Brand | Model | Architecture | Recognition | Function-Level Reset (FLR) | Physical Display Output |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Nvidia** | RTX 5090 | Blackwell 2.0 | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4090 | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4080 Super | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4070 | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | GTX 1660 Super | Turing | ✅ | ✅ | ✅ |
| **Nvidia** | GTX 1050 | Pascal | ✅ | ✅ | ✅ |
| **Nvidia** | GT 1030 | Pascal | ✅ | ✅ | ✅ |
| **Nvidia** | GT 210 | Tesla | ✅ | ✅ | ❌ |
| **Intel** | DG1 | Xe-LP | ✅ | ❌ | [Specific driver](https://www.shengqipc.cn/d21.html) ✅ |
| **Intel** | A380 | Xe-HPG | Code 43 ❌ | ✅ | ❌ |
| **Intel**| UHD Graphics 620 Mobile | Generation 9.5 | Passthrough failed ❌ | ❌ | ❌ | 
| **Intel**| HD Graphics 530 | Generation 9.0 | Passthrough failed ❌ | ❌ | ❌ |
| **AMD** | Radeon Vega 3 | GCN 5.0 | Code 43 ❌ | ❌ | ❌ |

- **Recognition**: Whether the driver can be successfully installed and recognized after being assigned to the VM.
- **Function-Level Reset (FLR)**: If not supported, restarting the VM will cause the host to reboot as well.
- **Physical Display Output**: Whether the VM can output a video signal through the GPU's physical ports (HDMI/DP).

---

### Ⅱ. GPU-P (GPU Paravirtualization / GPU Partitioning)

GPU-P (or GPU-PV) is a paravirtualization technology that allows multiple virtual machines to share the computing power of a physical GPU without full passthrough.

- **Resource Limits**: Currently, Hyper-V does not natively support limiting the GPU resources used by each VM. The parameters in `Set-VMGpuPartitionAdapter` are not effective ([related discussion](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298)). Therefore, this tool does not offer resource allocation features at this time.
- **Drivers & Compatibility**: The virtual device created by GPU-P can call the physical GPU, but it does not fully inherit its hardware features or driver details. Software or games that rely on specific hardware IDs or driver signatures may not run.

#### WDDM Versions & The Evolution of GPU-P
> The higher the WDDM (Windows Display Driver Model) version, the more mature the GPU-P functionality. It is recommended to use the latest Windows versions for both the host and the guest VM.

| Windows Version (Build) | WDDM Version | Key Virtualization Updates |
| :--- | :--- | :--- |
| 17134 | 2.4 | First introduction of IOMMU-based GPU isolation. |
| 17763 | 2.5 | Optimized resource management and communication between host and guest. |
| 18362 | 2.6 | Improved video memory management, prioritizing contiguous physical memory. |
| 19041 | 2.7 | VM's Device Manager can correctly identify the physical GPU model. |
| 20348 | 2.9 | Support for Cross-Adapter Resource Scan-Out (CASO), reducing latency. |
| 22000 | 3.0 | Support for DMA remapping, overcoming GPU memory address limitations. |
| 22621 | 3.1 | Shared memory between UMD/KMD, reducing data copies and improving efficiency. |
| 26100 | 3.2 | Introduction of GPU live migration, WDDM feature queries, and more. |

![WDDM Architecture](https://github.com/Justsenger/ExHyperV/blob/main/img/WDDM.png)

#### GPU-P Graphics Card Compatibility (Tested with Gpu Caps Viewer + DXVA Checker, continuously updated)

| Brand | Model | Architecture | Recognition | DirectX 12 | OpenGL | Vulkan | Codec | CUDA/OpenCL | Notes |
| :--- | :--- | :--- | :--- |:--- | :--- | :--- | :--- | :--- | :--- |
| **Nvidia** | RTX 4090 | Ada Lovelace | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | RTX 4080 Super | Ada Lovelace | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | GTX 1050 | Pascal | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | GT 210 | Tesla | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not supported |
| **Intel**| Iris Xe Graphics| Xe-LP | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Partial hardware recognition| 
| **Intel**| A380 | Xe-HPG | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Partial hardware recognition|
| **Intel**| UHD Graphics 730 | Xe-LP | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Partial hardware recognition|
| **Intel**| UHD Graphics 620 Mobile | Generation 9.5 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Partial hardware recognition|
| **Intel**| HD Graphics 530 | Generation 9.0 | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not supported |
| **AMD** | Radeon Vega 3 | GCN 5.0 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | Partial hardware recognition|
| **AMD** | Radeon 890M | RDNA 3.5 | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Host crashes on startup |
| **Moore Threads** | MTT S80 | MUSA | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not supported |

#### How to Get Display Output from the VM?

In GPU-P mode, the physical GPU acts as a "render adapter" and needs to be paired with a "display adapter" to output a screen. Here are three options:

1.  **Microsoft Hyper-V Video (Default)**
    - **Pros**: Good compatibility, works out of the box.
    - **Cons**: Maximum resolution of 1080p, low refresh rate (around 62Hz).

2.  **Indirect Display Driver + Streaming (Recommended)**
    - Install a [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) to create a high-performance virtual monitor.
    - Use streaming software like Parsec, Sunshine, or Moonlight to get a smooth, high-resolution, high-refresh-rate experience.
    - ![Sunshine+PV Example](https://github.com/user-attachments/assets/e25fce26-6158-4052-9759-6d5d1ebf1c5d)

3.  **USB Graphics Card + DDA (Experimental)**
    - **Concept**: Passthrough a USB controller to the VM via DDA, then connect a USB graphics card (e.g., based on [DisplayLink DL-6950](https://www.synaptics.com/products/displaylink-graphics/integrated-chipsets/dl-6000) or [Silicon Motion SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html) chips) as the display adapter.
    - **Status**: The author is currently investigating conflict issues when using this solution with large-VRAM GPUs. Not recommended for general users at this time.

## ⚙️ How It Works

To simplify configuration, this tool automatically performs the following actions:
- **Driver Injection**: Automatically imports the GPU drivers from the host's `HostDriverStore` into the virtual machine.
- **Driver Protection**: Sets the imported driver files to "read-only" to prevent accidental modification or deletion.
- **Nvidia Registry Fix**: Automatically modifies Nvidia-related registry keys in the VM to point the driver path to `HostDriverStore`, ensuring the drivers are loaded correctly.

## 🤝 Contributing
Contributions of any kind are welcome!
- **Testing & Feedback**: Help us improve the compatibility lists.
- **Reporting Bugs**: Submit issues you encounter via [Issues](https://github.com/Justsenger/ExHyperV/issues).
- **Code Contributions**: Fork the project and submit a Pull Request.

## ❤️ Support the Project
If you find this project helpful, please consider sponsoring me. It will motivate me to continue maintenance and development!

[![Sponsor](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://afdian.com/a/saniye)
