<h1>
  <img src="https://github.com/Justsenger/ExHyperV/blob/main/img/logo.png?raw=true" width="32" alt="ExHyperV logo">
  ExHyperV
</h1>

<div align="center">

**A graphical Hyper-V advanced tool that makes it easy for everyone to master advanced features of Hyper-V.**

</div>

<p align="center">
  <a href="https://github.com/Justsenger/ExHyperV/releases/latest"><img src="https://img.shields.io/github/v/release/Justsenger/ExHyperV.svg?style=flat-square" alt="Latest release"></a>
  <img width="3" src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7">
  <a href="https://github.com/Justsenger/ExHyperV/releases"><img src="https://aged-moon-0505.shalingye.workers.dev/" alt="Downloads"></a>
  <img width="3" src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7">
  <a href="https://t.me/ExHyperV"><img src="https://img.shields.io/badge/discussion-Telegram-blue.svg?style=flat-square" alt="Telegram"></a>
  <img width="3" src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7">
  <a href="https://github.com/Justsenger/ExHyperV/blob/main/LICENSE"><img src="https://img.shields.io/github/license/Justsenger/ExHyperV.svg?style=flat-square" alt="License"></a>
</p>

**English** | [中文](https://github.com/Justsenger/ExHyperV/blob/main/README_cn.md)

---

ExHyperV is built through in-depth research of Microsoft official documentation, [WMI](https://github.com/Justsenger/HyperV-WMI-Documentation), and [HCS](https://learn.microsoft.com/en-us/virtualization/api/hcs/overview) documentation. It aims to provide users with a graphical and easy-to-use configuration tool for advanced Hyper-V features.

Due to limited personal time and resources, there may be untested scenarios or errors in the project. If you encounter any hardware or software-related issues during use, please feel free to report them via [Issues](https://github.com/Justsenger/ExHyperV/issues)!

Features will be gradually improved over time. If you have specific features you'd like to see prioritized or really love this project, you can provide sponsorship through the [Sponsor](https://afdian.com/a/saniye) button and leave a comment!

## 🎨 Interface Overview

The interface uses the [WPF-UI](https://github.com/lepoco/wpfui) framework, providing a smooth, modern user interface with a futuristic visual experience. It supports both dark and light themes and automatically switches based on your system theme.

![Main Interface](https://github.com/Justsenger/ExHyperV/blob/main/img/01.png)

<details>
<summary>Click to see more interface screenshots</summary>

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/02.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/03.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/04.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/05.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/06.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/07.png)
![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/08.png)

</details>

## 🚀 Quick Start

### 1. Download & Run
- **Download**: Go to the [Releases page](https://github.com/Justsenger/ExHyperV/releases/latest) to download the latest version.
- **Run**: Simply extract the archive and run `ExHyperV.exe`.

### 2. Build (Optional)
1. Install [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) and ensure you have selected the .NET desktop development workload.
2. Clone this repository using GitHub Desktop or Git.
3. Open `/src/ExHyperV.sln` with Visual Studio to compile.

Alternatively, you can download the [.NET SDK](https://dotnet.microsoft.com/download) directly and open the project directory:
```pwsh
cd src
dotnet build
```

## 📖 Technical Documentation

This documentation is maintained long-term and is based on the author's Hyper-V development practices and related resources. It may contain issues or missing content.

---

### Introduction to Hyper-V

Hyper-V is a high-performance virtualization management software (Hypervisor) based on Type-1 architecture. When you enable the Hyper-V feature, your host system becomes a privileged virtual machine belonging to the root partition. Virtual machines you create belong to child partitions and are isolated from each other, unable to sense each other's existence.

Type-1 virtualization technologies include: Hyper-V, Proxmox (KVM), VMware ESXi (VMkernel), Xen, etc., with performance utilization rates above 98%.

Type-2 virtualization technologies include: VMware Workstation, Oracle VirtualBox, Parallels Desktop, etc., with performance utilization rates around 90-95%.

Based on this, you can think of virtual machines as isolated rooms where you can run potentially threatening programs, test system functionality, or for other purposes, without worrying about damaging your host system (except in advanced features like DDA where unsupported devices may cause host reboot, or viruses with lateral movement capabilities).

You can enable Hyper-V through Control Panel or with a simple PowerShell command (requires Professional or Server edition):
```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
```

---

### Scheduler

The scheduler is responsible for coordinating how physical processor CPU time is allocated to virtual processors.

Hyper-V has three schedulers: Classic, Core, and Root, which can be divided into two categories: manual (Classic, Core) and automatic (Root).

**Classic Scheduler**: Emerged early, dating back to Windows Server 2008, based on traditional time-slice round-robin fairness principles. It randomly allocates virtual machine processor time to all available logical processors on the host. When host resources are idle, it's more likely to allocate to different physical cores rather than hyperthreads, achieving better performance.

**Core Scheduler**: Introduced later since Windows Server 2016 and Windows 10 Build 14393, designed to mitigate side-channel attacks. Even when host resources are idle, it prefers allocating two threads of the same physical core rather than more physical cores. This improves security and VM isolation but significantly reduces CPU performance for VMs when host resources are idle. Since Windows Server 2019, server editions of Windows Hyper-V use the Core scheduler by default.

**Root Scheduler**: Released in Windows 10 Build 17134. This scheduler collects metrics on workload CPU usage and makes automatic scheduling decisions, ideal for big.LITTLE CPU architectures. Since Build 17134, Professional edition Windows Hyper-V uses the Root scheduler by default.

The system type does not restrict scheduler types - you can switch freely in ExHyperV. However, Root scheduler CPU affinity cannot be saved after VM shutdown because the backend implementation relies on process affinity rather than CPU groups.

---

### Processor (vCPU)

The ability of a virtual machine to request logical processor execution time from the host.

#### Computational Resources

##### Number of Cores

Typically set to even numbers like 2, 4, 8, 16, etc. Odd numbers can start but are not recommended.

If your VM's task is highly parallelizable, increasing vCPUs significantly improves speed. Unnecessary excess vCPUs may create scheduling pressure on the Hypervisor. Having total VM cores exceed the host's physical logical cores (oversubscription) is unfavorable for applications requiring real-time response.

##### Reservation

The minimum percentage of compute power provided to this VM. Value = Reservation × Core Count.

##### Limit

The maximum percentage of compute power provided to this VM. Value = Limit × Core Count.

##### Weight

This VM's priority level in CPU resource competition.

#### Advanced Features

##### Host Resource Protection

When enabled, monitors I/O requests from VMs through VMBus communication. When abnormal behavior like interrupt storms occurs, CPU time allocation is reduced to prevent affecting the host OS in the root partition. Regular users don't need to enable this.

##### Nested Virtualization

When enabled, passes through CPU VT-x/AMD-V instruction set extensions, allowing virtual machines within Hyper-V VMs to run VMs (VM nesting). This slightly increases CPU virtualization overhead.

##### Migration Compatibility

When enabled, masks advanced CPU instruction sets (like AVX-512), facilitating live migration across different physical devices. Regular users don't need to enable this.

##### Legacy OS Compatibility

When enabled, significantly simplifies CPU instruction sets, beneficial for running Windows 7 or earlier operating systems, but detrimental to running modern operating systems.

##### Virtual Machine SMT

When enabled, the VM can sense that its vCPUs appear in pairs as logical cores, helping the guest OS kernel optimize L1/L2 cache and process scheduling better.

##### Hide Hypervisor Identity

An early-stage switch with unclear functionality. Located at [Msvm_ProcessorSettingData/HideHypervisorPresent](https://github.com/Justsenger/HyperV-WMI-Documentation/blob/main/docs/Msvm_ProcessorSettingData.md).

Indicates whether Hyper-V should report the presence of a hypervisor to nested guests.

##### Expose Architectural Performance Monitoring Unit (PMU)

When enabled, passes through CPU hardware counters, allowing development tools (like Intel VTune, perf) inside VMs to directly access physical CPU performance monitoring hardware.

##### Expose Frequency Monitoring Register

When enabled, allows the VM OS to read the real frequency of the physical processor. Enabled by default.

##### Disable Side-Channel Attack Mitigation

When enabled, disables software patches for Spectre, Meltdown, and similar vulnerabilities. Provides slight performance improvement but reduces virtualization security.

##### Enable Socket Topology

When enabled, simulates multiple physical sockets for the VM. Untested.

##### CPU Affinity

Implementation based on CPU groups (Classic + Core schedulers) + process affinity (Root scheduler), allowing you to force-lock vCPUs to specified cores.

Best practice: bind 4 vCPUs to 4 cores. Binding 2 vCPUs to 4 cores will cause random drift; binding 4 vCPUs to 2 cores will cause queuing, and so on.

If you don't trust the scheduler's allocation policy or have concerns about big.LITTLE cores, you can try using this feature.

---

### Memory

The ability of a virtual machine to control runtime memory. Hyper-V uses second-level address translation technology to directly map VM physical memory requests to real physical memory.

#### Computational Resources

##### Startup Memory

The amount of physical memory a VM must occupy when booting. If the host's free memory is insufficient, startup may fail. If the OS supports it, you can modify this value at runtime to increase or decrease memory allocation (TODO: not implemented yet).

##### Memory Weight

When the host's physical memory is insufficient, the priority level for multiple VMs competing for memory.

##### Dynamic Memory

Allows the Hypervisor to dynamically scale allocated memory based on actual VM requirements in real-time.

Minimum memory cannot exceed startup memory. The maximum memory implementation will not exceed the host's physical memory limit.

When using GPU-PV or DDA, startup memory must equal minimum memory to ensure memory address mapping.

#### Advanced Features

##### Memory Page Size

Determines the "granularity" when mapping virtual to physical memory: 4K, 2M, or 1G. This option is available on VM configuration versions 12.0 and later, beneficial for large-scale databases or high-performance computing tasks.

##### Memory Encryption

When enabled, uses hardware features (AMD SEV or Intel TDX) to encrypt memory data in real-time. Even the host cannot read memory data. Enabling causes slight memory latency and CPU load increase.

---

### Storage

Storage media accessible to virtual machines. Divided into virtual files and physical devices. Virtual files include vhdx, vhd, and iso formats, simulating complete physical hard disks or optical drives. Physical devices can be direct pass-through of available host hard drives or optical drives.

The dropdown menu allows real-time viewing of read/write speeds and capacity changes. The number on the left is file size; the number on the right is capacity limit.

#### Slot Configuration

Hyper-V requires you to mount virtual files or physical devices to IDE or SCSI controllers for VM access. ExHyperV simplifies this operation through automatic allocation, allowing you to focus on the media source without worrying about slot assignment.

If you want to understand the complex slot logic, refer to these rules:

1. For running Gen 1 VMs, IDE controllers cannot be unloaded, but ISOs can be ejected and inserted.

2. For Gen 1 VMs, ISOs can only be inserted into IDE controllers. Gen 1 VMs can only boot from IDE controller media.

3. For Gen 1 VMs, there are a total of 2 IDE controllers × 2 + 4 SCSI controllers × 64 = 260 available positions.

4. For Gen 2 VMs, SCSI controllers and their storage media can be removed at any time, so be careful.

5. For Gen 2 VMs, there are a total of 4 SCSI controllers × 64 = 256 available positions.

(There may be additional rules. UI still needs refinement.)

#### Media Settings

##### Source

Select virtual files or available physical devices. Some USB storage cannot be offlined and won't appear in the available list.

##### Virtual Files

###### Type: Hard Drive

For dynamic expansion disks, the initial value is very small (depends on block size and capacity), gradually increasing.

For fixed-size disks, the initial value equals capacity size and doesn't change, with higher read/write efficiency.

For differencing disks, you need to specify a dynamic expansion or fixed-size virtual hard disk to inherit all its parameters (can be customized via binary, but not very meaningful).

Sector format: 512n, 512e (default), 4kn, representing physical/logical sector size combinations of (512+512, 4096+512, and 4096+4096 respectively). Regular users should keep the default.

Block size: Minimum storage unit. Larger blocks have higher read/write efficiency but lower space utilization; smaller blocks have lower read/write efficiency but higher space utilization.

###### Type: Optical Drive

ISO creation implemented using DiscUtils, allowing quick packaging and mounting of specified folders. Documentation pending.

---

### Graphics Card (GPU)

The ability of a virtual machine to access the host's physical GPU through GPU-PV technology. This feature heavily depends on WDDM version - use the latest system versions for both host and guest when possible.

GPU-PV (also called GPU-P) is a paravirtualization technology allowing multiple virtual machines to share physical GPU computing power without full passthrough.

**Resource Limits**: Currently, Hyper-V natively cannot effectively limit GPU resources used by each VM. Parameters in `Set-VMGpuPartitionAdapter` are not effective ([related discussion](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298)). Therefore, this tool does not provide resource allocation features.

**Drivers & Compatibility**: While GPU-P virtual devices can call physical GPUs, they don't fully inherit their hardware features or driver details. Software or games relying on specific hardware IDs or driver signatures may not run.

The dropdown menu shows all GPU-P partitions on this VM with their 3D graphics engine utilization, including four common engines: 3D, Copy, Encoding, and Decoding.

#### System Requirements

Both host and VM must be these versions to enable this capability:

- Windows 10 (Build 17134+)
- Windows 11
- Windows Server 2019
- Windows Server 2022
- Windows Server 2025

VMs must have configuration version greater than 9.0 to allocate GPU-PV graphics cards. Supports both Gen 1 and Gen 2 VMs.

VMs with GPU-PV enabled do not support checkpoint functionality.

GPU-P cards must exist on the host and cannot be used simultaneously with DDA.

Multiple GPU-P partitions from the same GPU cannot provide more than the physical limit of computing power.

VMs can obtain GPU-P partitions from different graphics cards.

There may be [memory leak issues](https://github.com/jamesstringer90/Easy-GPU-PV/issues/446). Consider updating your host system to version `26100.4946` or later.

#### WDDM Versions & GPU-P Evolution

> The higher the WDDM (Windows Display Driver Model) version, the more mature GPU-P functionality becomes. It's recommended to use the latest Windows versions for both host and guest.

| Windows Version (Build) | WDDM Version | Key Virtualization Updates |
| :--- | :--- | :--- |
| 17134 | 2.4 | First introduction of IOMMU-based GPU isolation. |
| 17763 | 2.5 | Optimized resource management and communication between host and guest. |
| 18362 | 2.6 | Improved video memory management, prioritizing contiguous physical memory. |
| 19041 | 2.7 | VM Device Manager can correctly identify physical GPU model. |
| 20348 | 2.9 | Support for Cross-Adapter Resource Scan-Out (CASO), reducing latency. |
| 22000 | 3.0 | Support for DMA remapping, overcoming GPU memory address limitations. |
| 22621 | 3.1 | Shared memory between UMD/KMD, reducing data copies and improving efficiency. |
| 26100 | 3.2 | Introduction of GPU live migration, WDDM feature queries, and more. Task Manager in VM can show GPU performance. |

#### GPU-P Graphics Card Compatibility List (Tested with Gpu Caps Viewer + DXVA Checker)

| Brand | Model | Architecture | Recognition | DirectX 12 | OpenGL | Vulkan | Codec | CUDA/OpenCL | Notes |
| :--- | :--- | :--- |:--- | :--- | :--- | :--- | :--- | :--- | :--- |
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
| **AMD** | Radeon 8060S | RDNA 3.5 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Partial hardware recognition |
| **AMD** | Radeon 890M | RDNA 3.5 | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Host crashes on startup |
| **Moore Threads** | MTT S80 | MUSA | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not supported |

#### How to Get Display Output from the VM?

In GPU-P mode, the physical GPU acts as a "render adapter" and needs to be paired with a "display adapter" for screen output. Three options:

1.  **Microsoft Hyper-V Video (Default)**
    - **Pros**: Good compatibility, works out of the box.
    - **Cons**: Maximum resolution 1080p, low refresh rate (around 60Hz).

2.  **Indirect Display Driver + Streaming (Recommended)**
    - Install a [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) to create a high-performance virtual monitor (may require recent version).
    - Use streaming software like Parsec, Sunshine, or Moonlight. With RDP and other remote desktop services disabled, achieve high-resolution, high-refresh-rate smooth experience.
    - ![Sunshine+PV Example](https://github.com/user-attachments/assets/e25fce26-6158-4052-9759-6d5d1ebf1c5d)

3.  **USB Graphics Card + GPU-P**
    - **Concept**: Direct pass-through a USB controller to the VM via DDA, then connect a USB graphics card (e.g., based on [DisplayLink DL-6950](https://www.synaptics.com/products/displaylink-graphics/integrated-chipsets/dl-6000) or [Silicon Motion SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html) chips) as the display adapter.
    - **Status**: This solution has memory resource conflicts with large-VRAM GPUs. Not recommended for general users at this time.

#### Configuration Process

##### Environment Preparation

Add registry entries to host system to disable security policies, preventing VM startup failure after GPU-P assignment.

##### Power Check

MMIO space configuration and write-combining cache require VM shutdown.

##### System Optimization

Configure MMIO space and enable write-combining cache.

##### GPU Assignment

Create GPU-P partitions and assign the selected GPU to the VM.

##### Driver Installation

Optional. For Windows VMs, automatically check GPU driver folders (if not found, full injection) and inject to VM specified location (may need manual partition selection), and add registry fixes for Nvidia.

For Linux VMs, execute an SSH-related process for kernel module compilation and driver installation. Systems or kernels outside the compatibility list need more testing.

![Linux&Blender](https://github.com/Justsenger/ExHyperV/blob/main/img/Linux.png)

Known Compatibility:
| OS | Kernel Version | Dxgkrnl | CUDA | Vulkan | OpenGL | Codec |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| Ubuntu 24.04  | 6.14.0-36-generic | ❌ | \ | \ | \ | \ |
| Ubuntu 22.04  | 6.8.0-87-generic | ✅ | ✅ | ✅ | ✅ | ✅ |
| Arch Linux | 6.6.119-1-lts66 | ✅ | ✅ | Untested | Untested | Untested |
| fnOS 0.9.2 | 6.12.18-trim | ❌ | \ | \ | \ | \ |

---

### Virtual Network Switch

To be enhanced.

---

### DDA (Discrete Device Assignment)

DDA allows complete PCIe devices (like graphics cards, network adapters, audio cards, USB controllers) to be unmounted from the host and directly assigned to virtual machines.

#### Assignable Devices

DDA divides assignable devices by PCIe units. If a device doesn't appear in the list, it cannot be independently assigned. You need to try assigning its parent PCIe controller instead.

#### Virtual Machine OS

Windows is recommended. Linux is untested.

#### Host System Requirements

- Windows Server 2019
- Windows Server 2022
- Windows Server 2025

**Black Magic**: If you want to use DDA on non-Server systems, try switching the system version flag from WinNT to ServerNT to deceive the Hypervisor.

#### Three States of DDA Devices

1.  **Host State**: Device is normally mounted to the host system and can be used by the host.
2.  **Dismounted State**: Device has been unmounted from the host (`Dismount-VMHostAssignableDevice`) but hasn't been successfully assigned to a VM. The device is unavailable in the host's Device Manager. You can use this tool to remount it to the host or assign it to a VM.
3.  **Guest State**: Device has been successfully mounted in the VM.

#### DDA Graphics Card Compatibility List (Continuously updated)
> True compatibility can only be confirmed after installing drivers inside the VM. Please share your test results via [Issues](https://github.com/Justsenger/ExHyperV/issues)!

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
| **Intel**| HD Graphics 610 | Generation 9.5 | Passthrough failed ❌ | ❌ | ❌ |
| **Intel**| HD Graphics 530 | Generation 9.0 | Passthrough failed ❌ | ❌ | ❌ |
| **AMD** | RX 580 | GCN 4.0 | Code 43 ❌ | ✅ | ❌ |
| **AMD** | Radeon Vega 3 | GCN 5.0 | Code 43 ❌ | ❌ | ❌ |

- **Recognition**: Whether the driver can be successfully installed and recognized after assignment to the VM. Code 43 means the driver doesn't allow the GPU to work in VMs.
- **Function-Level Reset (FLR)**: If not supported, restarting the VM will cause the host to restart as well.
- **Physical Display Output**: Whether the VM can output video through the GPU's physical ports (HDMI/DP).

---

## 🤝 Contributing

Contributions of any kind are welcome!
- **Testing & Feedback**: Help us improve compatibility lists or test potential bugs.
- **Report Bugs**: Submit issues you encounter via [Issues](https://github.com/Justsenger/ExHyperV/issues).
- **Code Contributions**: Fork the project and submit a Pull Request.

## ❤️ Support the Project

If you find this project helpful, please consider sponsoring me. Your support motivates me to continue maintenance and development!

[![Sponsor](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://afdian.com/a/saniye)
