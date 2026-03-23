<h1>
  <img src="https://github.com/Justsenger/ExHyperV/blob/main/img/logo.png?raw=true" width="32" alt="ExHyperV logo"> 
  ExHyperV
</h1>

<div align="center">

**A graphical Hyper-V management tool that allows mere mortals to easily master advanced Hyper-V features.**

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

**English** | [中文](https://github.com/Justsenger/ExHyperV/blob/main/README_zh.md)

---

ExHyperV aims to provide a graphical, easy-to-use configuration tool for advanced Hyper-V features by delving into technical details such as Hyper-V documentation, [WMI](https://github.com/Justsenger/HyperV-WMI-Documentation), and [HCS](https://learn.microsoft.com/en-us/virtualization/api/hcs/overview).

Due to limited personal time and energy, the project may contain untested scenarios or bugs. If you encounter any hardware/software issues during use, please feel free to report them via [Issues](https://github.com/Justsenger/ExHyperV/issues)!

Features will be gradually improved over time. If there is a specific feature you would like to see prioritized, or if you love this project, you can support it via the donation button at the bottom of the document and leave a message!

## 🎨 Interface Overview

ExHyperV uses the [WPF-UI](https://github.com/lepoco/wpfui) framework to provide a smooth, modern user interface experience with sci-fi visual effects. It supports both dark and light themes and automatically switches based on the system theme.

Supported languages: Simplified Chinese & English.

![Main Interface](https://github.com/Justsenger/ExHyperV/blob/main/img/01.png)

<details>
<summary>Click to see more screenshots</summary>
  
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/02.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/03.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/04.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/05.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/06.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/07.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/08.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/09.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/10.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/11.png)
![Feature](https://github.com/Justsenger/ExHyperV/blob/main/img/12.png)

</details>

## 🚀 Quick Start
---
### 1. Download and Run
- **Download**: Go to the [Releases page](https://github.com/Justsenger/ExHyperV/releases/latest) to download the latest version.
- **Run**: Extract the archive and run `ExHyperV.exe` directly.
---
### 2. Build (Optional)
1. Install [Visual Studio](https://visualstudio.microsoft.com/vs/) and ensure the .NET desktop development workload is selected.
2. Clone this repository using GitHub Desktop or Git.
3. Open the `/src/ExHyperV.sln` file with Visual Studio to compile.

Alternatively, you can download the [.NET SDK](https://dotnet.microsoft.com/zh-cn/download), open the project directory, and run:
```pwsh
cd src
dotnet build
```

## 📖 Technical Documentation

This section will be maintained long-term. It is written based on Hyper-V related documentation and development practices, and may contain inaccuracies.

---
### Introduction to Hyper-V
> [!NOTE]
> Hyper-V is a high-performance virtual machine manager (Hypervisor) based on Type-1 architecture.

When you enable the Hyper-V feature, the host system becomes a privileged virtual machine belonging to the root partition. The created virtual machines belong to child partitions; they are isolated from each other and cannot perceive each other's existence.

Virtualization technologies belonging to the Type-1 architecture include Hyper-V, Proxmox (KVM), VMware ESXi (VMkernel), Xen, etc., with performance utilization rates generally above 98%.

Virtualization technologies belonging to the Type-2 architecture include VMware Workstation, Oracle VirtualBox, Parallels Desktop, etc., with performance utilization rates around 90%~95%.

Based on these facts, you can view virtual machines as isolated small rooms where you can run potentially threatening programs, test system functions, multi-box games, or other uses without worrying about messing up the host system (Except in cases where FLR is not supported in PCIe passthrough, where a VM restart might restart the host, or viruses with lateral movement capabilities—please pay attention to network security).

You can enable/disable Hyper-V via the Control Panel or a simple Powershell command (requires Pro or Server edition). After confirming with Y and rebooting, processes like vmms.exe, vmcompute.exe, and vmmem will run continuously in the background, and the Hyper-V Manager icon will appear in the Start menu.
```
Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
```
---
### Scheduler
> [!NOTE]
> The scheduler coordinates how physical processor CPU time is allocated to virtual machine processors.

Hyper-V has three types of schedulers: Classic, Core, and Root. They can be categorized into two types: Manual (Classic, Core) and Automatic (Root). Core can be seen as a variant of Classic that improves security but may reduce performance in some scenarios.

The **Classic** scheduler dates back to Windows Server 2008. It is based on the principle of fair allocation using traditional time slicing. It randomly allocates VM processor time to any available logical processor on the host. If host resources are idle, it is more likely to allocate logical processors from different physical cores rather than hyper-threads to achieve better performance.

The **Core** scheduler appeared later, introduced in Windows Server 2016 and Windows 10 Build 14393. Its purpose is to mitigate side-channel attacks. Even if host resources are idle, it tends to allocate two threads of the same physical core rather than more physical cores. This strategy helps improve security and VM isolation but significantly reduces the CPU performance allocated to VMs when host resources are idle. Starting from Windows Server 2019, Windows Server defaults to using the Core scheduler.

The **Root** scheduler was released in Windows 10 Build 17134. It collects metrics on workload CPU usage and makes automatic scheduling decisions. It is very suitable for hybrid CPU architectures (Big/Little cores). Starting from Build 17134, Windows Hyper-V (Pro) defaults to using the Root scheduler.

The system type is independent of the scheduler type; you can switch arbitrarily, effective after rebooting the host.

---
### Processor (vCPU)
> [!NOTE]
> The ability of the virtual machine to request execution time on logical processors from the host.

#### Compute Resources

##### Core Count

Usually set to even numbers like 2, 4, 8, 16.

Increasing vCPUs significantly improves the processing speed of parallel tasks, but too many unnecessary vCPUs may bring scheduling pressure to the Hypervisor.

If the total number of VM cores exceeds the host's physical logical core count (overselling), applications requiring immediate response will be greatly affected.

##### Reserve

The lower limit percentage of execution time provided for this virtual machine. Reserve Value = Reserve * Core Count.

##### Limit

The upper limit percentage of execution time provided for this virtual machine. Limit Value = Limit * Core Count.

##### Weight

The priority of this virtual machine in competing for CPU execution time, ranging from 0 to 10000.

#### Advanced Features

##### Host Resource Protection

When enabled, it monitors I/O requests communicated through VMBus. If abnormal behavior such as interrupt storms occurs, it reduces CPU execution time allocation to prevent affecting the host system in the root partition. Ordinary users do not need to enable this.

##### Nested Virtualization

When enabled, it passes through the CPU's VT-x/AMD-V instruction set extensions, allowing you to run a virtual machine inside a Hyper-V virtual machine. This slightly increases CPU virtualization overhead.

After enabling nested virtualization and Hyper-V features in the VM, the VM's Task Manager will show L1/L2/L3 cache topology and will no longer be marked as "Virtual Machine: Yes," which helps with avoiding virtualization detection.

<div align="center">
<img width="616" height="532" alt="image" src="https://github.com/user-attachments/assets/00c838f1-91ef-42db-bf21-34c5b49b08b9" />
</div>

##### Migration Compatibility

When enabled, it masks advanced CPU instruction sets (such as AVX-512, etc.) to facilitate live migration on hosts with different hardware. Ordinary users do not need to enable this.

##### Legacy Compatibility

When enabled, it significantly strips down the CPU instruction set. This is beneficial for running Windows 7 or earlier operating systems but detrimental to running modern operating systems.

##### Virtual Machine SMT

When enabled, the virtual machine can perceive that its vCPUs appear as paired logical cores, helping the OS kernel inside the VM to better perform L1/L2 cache optimization and process scheduling.

##### Hide Hypervisor Presence

This is an early switch, and its function is still unclear. Located at [Msvm_ProcessorSettingData/HideHypervisorPresent](https://github.com/Justsenger/HyperV-WMI-Documentation/blob/main/docs/Msvm_ProcessorSettingData.md).

Indicates whether Hyper-V should report the presence of a hypervisor to nested guests.

##### Expose Architecture Performance Monitoring Unit

When enabled, it passes through the CPU's hardware counters, allowing development tools inside the virtual machine to directly access physical CPU performance monitoring hardware.

##### Expose Frequency Monitoring Registers

When enabled, allows the virtual machine operating system to read the actual frequency of the physical processor. Default is enabled.

##### Disable Side-Channel Attack Mitigations

When enabled, turns off software patches for vulnerabilities like Spectre and Meltdown. This slightly improves performance but reduces virtualization security.

##### Enable Slot Topology

When enabled, simulates multiple physical sockets for the virtual machine. May be useful for systems with multiple physical processors.

##### CPU Binding

CPU binding is implemented based on CPU Groups (Classic + Core schedulers) + Process Affinity (Root scheduler), allowing you to forcibly lock vCPUs to specified cores.

The best practice is binding 4 vCPUs to 4 cores. Binding 2 vCPUs to 4 cores will cause random drift, and binding 4 vCPUs to 2 cores will cause queuing, and so on.

If you find the scheduler performance poor, or have concerns about Intel's hybrid architecture or AMD's multi-chip architecture, please try using this feature.

---
### Memory
> [!NOTE]
> The capacity of RAM that the virtual machine can control.

#### Compute Resources

##### Startup Memory

The amount of physical memory the virtual machine must occupy when powered on. If the host lacks sufficient free memory, startup may fail.

If the operating system supports hot adjustment, this value can be modified during runtime to increase or decrease the memory allocation limit.

##### Memory Weight

The priority of multiple virtual machines competing for memory when host physical memory is insufficient.

##### Dynamic Memory

Allows the Hypervisor to scale the allocated memory amount in real-time based on the actual needs inside the virtual machine.

Minimum memory cannot be greater than startup memory. The virtual machine will naturally not exceed the Maximum Memory or the host's physical memory limit.

When using GPU-PV or PCIe passthrough, startup memory must be the same as minimum memory to determine memory address mapping relationships.

#### Advanced Features

##### Memory Page Size

Determines the "granularity" when mapping virtual memory to physical memory. Options: 4K, 2M, 1G. This option requires the host system version to be greater than 26100 and the VM configuration version to be greater than 12.0. It is beneficial for large databases or high-performance computing tasks.

##### Memory Encryption

When enabled, uses hardware features (AMD SEV or Intel TDX) to encrypt memory data in real-time. Even the host cannot read the memory data. Enabling this adds slight memory latency and increased CPU load.

---
### Storage
> [!NOTE]
> Local storage media accessible by the virtual machine.

Divided into virtual files and physical devices. Virtual files can be vhdx, vhd, and iso formats. Physical devices can select offline hard drives or optical drives on the host for passthrough (some USB storage media may not be supported).

The monitoring interface shows real-time read/write rates and capacity changes. The number on the left is the file size, and the number on the right is the capacity limit.

#### Slot Configuration

Hyper-V requires you to mount virtual files or physical devices to an IDE controller or SCSI controller for VM access. ExHyperV has simplified this operation to automatic allocation, allowing you to care only about the media source without worrying about slot allocation.

If you try to understand the complex slot logic, please refer to the following rules:

· For running Generation 1 VMs, IDE controllers cannot be uninstalled, but ISOs can be ejected and inserted.

· For Generation 1 VMs, ISOs can only be inserted into IDE controllers. Gen 1 VMs can only boot from media on IDE controllers.

· For Generation 2 VMs, SCSI controllers and the media within them can be ejected and removed at any time, so please operate with caution.

· For Generation 1 VMs, there are a total of 2 IDE controllers x 2 + 4 SCSI controllers x 64 = 260 slots available. For Generation 2 VMs, there are a total of 4 SCSI controllers x 64 = 256 slots available.

#### Media Settings

##### Source

Select a virtual file or an available physical device. Some USB storage media will not appear in the available list because they cannot be taken offline.

##### Virtual Files

###### Type: Hard Disk

When the disk type is **Dynamic**, the initial file size is small (generated based on block size and capacity for the block allocation table) rather than the full capacity, and it grows gradually.

When the disk type is **Fixed**, the initial value is the capacity size and does not change; read/write efficiency is higher.

When the disk type is **Differencing**, you need to specify a dynamic/fixed virtual hard disk to inherit all its parameters.

Sector Format: 512n, 512e (default), 4kn. Corresponding physical sector size and logical sector size are: 512/512, 4096/512, 4096/4096. Ordinary users can keep the default.

Block Size: Minimum storage unit. Larger blocks mean higher read/write efficiency but lower space utilization; smaller blocks mean lower read/write efficiency but higher space utilization.

###### Type: Optical Drive

Created using DiscUtils, enabling quick packaging of a specified folder and mounting it to the VM. Uses ISO 9660 standard with Joliet extension enabled.

Single file cannot exceed 4GB, total size cannot exceed 8TB, ISO volume label cannot exceed 32 characters, path depth cannot exceed 8 levels, single file or folder name cannot exceed 103 characters, and the total length of the absolute path of a file in the image cannot exceed 240 characters.

Due to many restrictions, this part may consider using the UDF standard as a replacement in the future. This function is used to compensate for Hyper-V's disadvantage in quickly creating ISOs.

---
### Graphics Card
> [!NOTE]
> The ability of the virtual machine to access the host physical graphics card via GPU-PV technology.

GPU-PV is a paravirtualization technology that allows multiple virtual machines to share the computing power of a physical GPU without PCIe passthrough. GPU-PV is still evolving; the newer the WDDM version, the more complete the functions. The host and VM should use the latest system versions as much as possible.

· The monitoring interface allows viewing the graphics engine utilization of all GPU-PV partitions on that VM, including four common engines: 3D Rendering, Copy, Video Encode, and Video Decode.

· Currently, Hyper-V cannot effectively limit the GPU resources used by each virtual machine. Parameters in `Set-VMGpuPartitionAdapter` do not take effect ([Relevant discussion](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298)). Therefore, this tool does not currently provide resource allocation functions.

· although virtual devices created by GPU-PV can call the physical GPU, they do not fully inherit its hardware characteristics and driver details. Certain software/games relying on specific hardware IDs or driver signatures may not run.

#### System Requirements

Host and VM must be the following versions to enable this capability.

- Windows 10 (Build 17134+)
- Windows 11
- Windows Server 2019
- Windows Server 2022
- Windows Server 2025

· The virtual machine must be a configuration version greater than 9.0 to be assigned a GPU-PV graphics card. There is no restriction on VM generation.

· Virtual machines with GPU-PV enabled do not support the checkpoint function.

· Graphics cards with GPU-PV enabled must exist on the host and cannot be used for PCIe passthrough simultaneously.

· Multiple GPU-PV graphics partitions obtained from the same graphics card cannot provide computing power exceeding the physical limit.

· A virtual machine can obtain GPU-PV graphics partitions from different graphics cards.

· There may be [memory leak issues](https://github.com/jamesstringer90/Easy-GPU-PV/issues/446). It is recommended to update the host system version to `26100.4946` or above.

#### WDDM Version and GPU-PV Features
> The higher the WDDM (Windows Display Driver Model) version, the more complete the GPU-PV features. It is recommended that both host and VM use the latest Windows versions.

| Windows Version (Build) | WDDM Version | Virtualization Related Updates |
| :--- | :--- | :--- |
| 17134 | 2.4 | First introduction of GPU paravirtualization technology. |
| 17763 | 2.5 | Optimized resource management and communication between host and VM. |
| 18362 | 2.6 | Improved video memory management efficiency, prioritizing continuous physical video memory allocation. |
| 19041 | 2.7 | VM Device Manager can correctly identify physical graphics card models. |
| 20348 | 2.9 | Started supporting Linux VMs and WSL2. |
| 22000 | 3.0 | Support for DMA remapping, breaking through GPU memory address limits. |
| 22621 | 3.1 | UMD/KMD memory sharing, reducing data copying, improving efficiency. |
| 26100 | 3.2 | VM Task Manager can view GPU performance counters. Introduced new features like GPU live migration, WDDM capability queries. |

#### GPU-PV Partial Compatibility List (Tested using Gpu Caps Viewer+DXVA Checker)

| Brand | Model | Architecture | Recognition | DirectX 12 | OpenGL | Vulkan | Codec | CUDA/OpenCL | Remarks |
| :--- | :--- | :--- | :--- |:--- | :--- | :--- | :--- | :--- | :--- |
| **Nvidia** | RTX 4090 | Ada Lovelace | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | RTX 4080 Super | Ada Lovelace | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | RTX 2080 Super | Turing | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | GTX 1050 | Pascal | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | GT 210 | Tesla | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not supported |
| **Intel**| Iris Xe Graphics| Xe-LP | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Incomplete HW ID| 
| **Intel**| A380 | Xe-HPG | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Incomplete HW ID|
| **Intel**| UHD Graphics 730 | Xe-LP | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Incomplete HW ID|
| **Intel**| UHD Graphics 620 Mobile | Generation 9.5 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Incomplete HW ID|
| **Intel**| HD Graphics 530 | Generation 9.0 | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not supported |
| **AMD** | Radeon Vega 3 | GCN 5.0 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | Incomplete HW ID|
| **AMD** | Radeon 8060S | RDNA 3.5 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | Incomplete HW ID |
| **AMD** | Radeon 890M | RDNA 3.5 | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Boot crashes host |
| **Qualcomm** | Qualcomm(R) Adreno(TM) X1-85 GPU | Adreno X1 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | Incomplete HW ID|
| **Moore Threads** | MTT S80 | MUSA | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not supported |


#### How to output video from the VM?

In the GPU-PV model, the VM's GPU-PV card acts as a "Render Device" and needs a "Display Device" to output the image. There are three solutions:

1.  **Microsoft Hyper-V Video (Default)**
    - **Pros**: Works out of the box, good compatibility.
    - **Cons**: Max resolution 1080p, refresh rate approx 60Hz.

2.  **Indirect Display Driver + Streaming (Recommended)**
    - Install [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) to create a high-performance virtual monitor.
    - Use streaming software like Parsec, Sunshine, or Moonlight. Pair them, set to auto-start, and connect while RDP and other remote desktops are closed to achieve a high-resolution, high-refresh-rate smooth experience.
    - ![Sunshine+PV Example](https://github.com/user-attachments/assets/e25fce26-6158-4052-9759-6d5d1ebf1c5d)

> [!NOTE]
> Here is a simple Sunshine + GPU-PV guide.

· Add GPU-PV to the virtual machine and ensure it works normally.

· Install [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) in the virtual machine, and ensure "Generic Monitor (VDD by MTT)" appears in monitors. Set the resolution and refresh rate via display settings.

· Install Sunshine in the virtual machine, and pair Moonlight with Sunshine.

· Set Sunshine to auto-start with administrator privileges.

· Reboot the virtual machine. Do not open the console or any remote desktop.

· Connect to the virtual machine via Moonlight. If all goes well, video and sound will be transmitted to the Moonlight client.

3.  **USB Graphics Card + GPU-PV**
    - **Idea**: Use PCIe passthrough to assign a USB controller to the VM, then connect a USB graphics card (e.g., based on [DisplayLink DL-6950](https://www.synaptics.com/cn/products/displaylink-graphics/integrated-chipsets/dl-6000) or [Silicon Motion SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html) chips) as the display device.
    - **Status**: This solution may conflict with large memory graphics cards and requires more testing.

#### Configuration Process

##### Environment Preparation

Add registry entries to the host system environment, disable security policies, etc., to avoid VM startup failures after assigning GPU-PV.

##### Power Check

The system optimization in the next step requires the virtual machine to be powered off to proceed.

##### System Optimization

Configure high MMIO space to 64GB, low space to 1GB, and enable Write Combining.

##### Assign Graphics Card

Create a GPU-PV partition for the selected graphics card and assign it to the virtual machine.

##### Driver Installation

This is optional. When adding multiple graphics cards, you can uncheck this to avoid importing drivers every time.

For Windows VMs, the host driver folder will be fully injected into the VM's specified partition. If it's an Nvidia card, registry fixes will also be added.

For Linux VMs, a different SSH-related flow will be executed for module compilation and driver installation. Systems or kernels outside the compatibility list need more testing.

![Linux&Blender](https://github.com/Justsenger/ExHyperV/blob/main/img/Linux.png)

Known Compatibility:
| System | Kernel Version | Dxgkrnl | CUDA | Vulkan | OpenGL | Codec |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | 
| Ubuntu 24.04  | 6.14.0-36-generic | ❌ | \ | \ | \ | \ |
| Ubuntu 22.04  | 6.8.0-87-generic | ✅ | ✅ | ✅ | ✅ | ✅ |
| Arch Linux | 6.6.119-1-lts66 | ✅ | ✅ | Untested | Untested | Untested |
| fnOS 0.9.2 | 6.12.18-trim | ❌ | \ | \ | \ | \ |

---
### Network
> [!NOTE]
> The ability of the virtual machine to access the network via a switch at the data link layer.

#### VLAN and Isolation
> [!NOTE]
> VLAN (Virtual Local Area Network) is a communication technology that logically divides a physical LAN into multiple independent broadcast domains.

**Access Mode**: Assigns the virtual machine's network adapter to a single VLAN. When VLAN ID equals 0, VLAN function is disabled.

The VLAN ID range is 1-4094. After setting a specific VLAN ID, the virtual machine can only communicate at Layer 2 with devices in the same virtual switch that are in the same VLAN ID.

**Trunk Mode**: Allows the virtual machine's network adapter to transmit traffic for multiple VLANs simultaneously.

· Native VLAN ID: Used to handle untagged default traffic.

· Allow List: Specifies the range of VLAN IDs allowed through this network card (e.g., 10, 20-30).

**Private Mode**: Implements further Layer 2 isolation within the same VLAN, often used in multi-tenant or hosting environments.

· Primary ID: Public VLAN identifier the VM belongs to.
· Secondary ID: Internal identifier used to implement subdivision isolation.

Three types:

· Isolated: The VM can only communicate with the gateway; other VMs within the same VLAN cannot access each other.

· Community: VMs within the same community can access each other but cannot communicate with other communities.

· Promiscuous: Can communicate with all VMs under the primary VLAN (usually assigned to gateways or firewalls).

#### Traffic Control (QoS)
> [!NOTE]
> Used to manage VM bandwidth allocation and prevent a single VM from saturating the network channel.

Maximum: The peak speed limit. The bandwidth cap for the VM when the network is idle.

Minimum: Guaranteed floor. When the network is busy, the system prioritizes reserving this bandwidth for the VM.

#### Hardware Acceleration
> [!NOTE]
> Offloads work that originally required host CPU processing to the physical network card hardware to improve performance.

**Single Root I/O Virtualization (SR-IOV)**: Allows the virtual machine to skip the virtual switch and directly "connect" to the hardware resources of the physical network card. This feature requires physical network card support, and SR-IOV must be checked when creating the virtual switch. Enable for server-grade NICs with high performance needs.

**Virtual Machine Queue (VMQ)**: Uses the physical NIC's hardware filtering to pre-classify packets sent to different VMs and deliver them directly to the VM's memory. Enable for 10GbE NICs; recommended to disable for 1GbE NICs or Broadcom NICs.

**IPsec Task Offload**: Transfers encryption/decryption calculations (IPsec protocol) in network transmission from the CPU to the NIC hardware. Default off is fine; almost useless for most.

#### Security and Monitoring
> [!NOTE]
> Enhances virtual network security isolation and traffic monitoring.

**Allow MAC Address Spoofing**: Allows the virtual machine to change its network card's MAC address. Usually used when enabling nested virtualization or for certain soft router systems that need to spoof MACs. If not enabled, the VM can only use the unique fixed MAC assigned to it.

**DHCP Guard**: Prevents the virtual machine from acting as a DHCP server, avoiding network disconnection caused by other devices in the network obtaining incorrect IP addresses from this VM.

**Router Guard**: Prevents the virtual machine from masquerading as a gateway, maliciously hijacking, or spoofing intranet traffic.

**Port Mirroring**: Copies traffic from VMs in the Source group to VMs in the Destination group.

**Join Source Group**: Traffic from this network adapter will be sent to the Destination group network adapters.

**Join Destination Group**: This network adapter will receive traffic from Source group network adapters.

**Storm Threshold**: Limits the number of broadcast/multicast packets the VM is allowed to send per second. Setting to 0 means no limit; recommended setting is 500-1000.

---
### PCIe Passthrough
> [!NOTE]
> PCIe Passthrough is actually an implementation of DDA (Discrete Device Assignment). For ease of understanding, the name is changed to PCIe Passthrough.

PCIe Passthrough allows removing a complete PCIe device (Graphics Card, Network Card, Sound Card, USB Controller, etc.) from the host and assigning it directly to a virtual machine.

Note: This feature requires the IOMMU switch in the BIOS to be enabled and requires a Server system environment.

#### Assignable Devices

PCIe Passthrough searches for assignable devices by PCIe device. If a device does not appear in the list, it means it does not belong to an independent PCIe device, and you need to try assigning its parent PCIe controller.

#### VM System

Usually use Windows 10/11 or higher. Linux requires further testing.

#### Host System Requirements

- Windows Server 2019
- Windows Server 2022
- Windows Server 2025

**Black Magic**: If you want to use PCIe passthrough on a non-Server system, you can try toggling the system version switch to change the identifier from WinNT to ServerNT, thereby tricking the Hypervisor. This switch currently only works for Build 26100 and below.

#### Three States of PCIe Devices

1.  **Host State**: The device is normally mounted to the host system and can only be used by the host.
2.  **Dismounted State**: The device has been uninstalled from the host (`Dismount-VMHostAssignableDevice`) but not assigned to a VM. At this point, the device is unavailable in the host Device Manager and needs to be remounted to the host or assigned to a VM.
3.  **Virtual State**: The device has been successfully assigned to a virtual machine.

#### PCIe Partial Graphics Card Compatibility List
> Compatibility performance can only be confirmed after installing drivers in the virtual machine. Welcome to share your test results via [Issues](https://github.com/Justsenger/ExHyperV/issues)!

| Brand | Model | Architecture | Boot | Function Level Reset (FLR) | Physical Display Output |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Nvidia** | RTX 5090 | Blackwell 2.0 | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4090 | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4080 Super | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4070 | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 2080 Super | Turing | ✅ | ✅ | ✅ |
| **Nvidia** | GTX 1660 Super | Turing | ✅ | ✅ | ✅ |
| **Nvidia** | GTX 1050 | Pascal | ✅ | ✅ | ✅ |
| **Nvidia** | GT 1030 | Pascal | ✅ | ✅ | ✅ |
| **Nvidia** | GT 210 | Tesla | ✅ | ✅ | ❌ |
| **Intel** | DG1 | Xe-LP | ✅ | ❌ | [Specific Driver](https://www.shengqipc.cn/d21.html) ✅ |
| **Intel** | A380 | Xe-HPG | Code 43 ❌ | ✅ | ❌ |
| **Intel**| UHD Graphics 620 Mobile | Generation 9.5 | Fails ❌ | ❌ | ❌ | 
| **Intel**| HD Graphics 610 | Generation 9.5 | Fails ❌ | ❌ | ❌ | 
| **Intel**| HD Graphics 530 | Generation 9.0 | Fails ❌ | ❌ | ❌ | ❌ |
| **AMD** | RX 580 | GCN 4.0 | Code 43 ❌ | ✅ | ❌ |
| **AMD** | Radeon Vega 3 | GCN 5.0 | Code 43 ❌ | ❌ | ❌ |
| **Qualcomm** | Qualcomm(R) Adreno(TM) X1-85 GPU | Adreno X1 | Not supported❌ | ❌ | ❌ |


- **Boot**: Whether the driver can be successfully installed and recognized after assignment to the VM. Code 43 indicates the driver level does not allow the card to work inside a VM.
- **Function Level Reset (FLR)**: If not supported, restarting the VM will also restart the host.
- **Physical Display Output**: Whether the VM can output video through the graphics card's physical interface (HDMI/DP).
---
### Virtual Switch
> [!NOTE]
> Displays the topology and connection status of Hyper-V switches on the host.

ExHyperV redefines Hyper-V's three switch types (External, Internal, Private) into three network modes (Bridged, NAT, No Upstream), where NAT mode integrates ICS functionality.

### Network Modes

**Bridged Mode**: Host and VM connect under the same external virtual switch, with a specified physical network card providing the outlet network.

**NAT Mode**: Host and VM connect under the same internal virtual switch. The host shares the physical network card's network to the VM via ICS and is responsible for the upstream outlet, NAT translation, and DHCP. Only one NAT mode network can exist.

**No Upstream**: Host and VM connect under the same internal virtual switch with no upstream network. The host can choose not to connect to this switch, in which case the virtual switch automatically switches to a Private switch.

· **Default Switch** belongs to a unique switch type working similarly to NAT mode and automatically switches the upstream network based on metrics.

## 🤝 Contribution
Any form of contribution is welcome!
- **Testing & Feedback**: Help us improve the compatibility list or test potential bugs.
- **Report Bugs**: Submit issues you encounter via [Issues](https://github.com/Justsenger/ExHyperV/issues).
- **Code Contribution**: Fork the project and submit a Pull Request.

## ❤️ Support the Project

If you find this project helpful, please consider sponsoring me!

[![Ko-fi](https://img.shields.io/badge/Sponsor-Ko--fi-F16061?style=for-the-badge&logo=ko-fi&logoColor=white)](https://ko-fi.com/saniye) &nbsp;&nbsp; [![Afdian](https://img.shields.io/badge/Sponsor-爱发电-633991?style=for-the-badge&logo=afdian&logoColor=white)](https://afdian.com/a/saniye)

## 🎖️ Wall of Honor

A huge thank you to our sponsors! Your generous support is the driving force behind the evolution of ExHyperV.

### 👑 God Tier
<a href="https://afdian.com/a/saniye"><img src="https://img.shields.io/badge/GOD-User--1A4FE-black?style=for-the-badge&logo=kingstontechnology&logoColor=FFD700&labelColor=black&color=FFD700" width="300px" /></a> <a href="https://afdian.com/a/saniye"><img src="https://img.shields.io/badge/GOD-ANONYMOUS-333333?style=for-the-badge&logo=cyberdefenders&logoColor=C0C0C0&labelColor=black&color=C0C0C0" width="300px" /></a>

---

### 🌌 Legend Tier
![](https://img.shields.io/badge/LEGEND-USER--09837-24292e?style=for-the-badge&logo=starship&logoColor=BE64FF&labelColor=24292e&color=BE64FF)
<a href="https://github.com/PIKACHUIM"><img src="https://img.shields.io/badge/LEGEND-PIKACHUIM-24292e?style=for-the-badge&logo=starship&logoColor=BE64FF&labelColor=24292e&color=BE64FF" /></a> 

---

### 🏅 Expert Tier
![](https://img.shields.io/badge/EXPERT-User--6b017-24292e?style=for-the-badge&logo=expertsexchange&logoColor=FFBF00&labelColor=24292e&color=FFBF00)
![](https://img.shields.io/badge/EXPERT-死都不怕还怕活着？-24292e?style=for-the-badge&logo=expertsexchange&logoColor=FFBF00&labelColor=24292e&color=FFBF00)
---




### 🔹 Beginner Tier
![](https://img.shields.io/badge/BEGINNER-激进娘-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/BEGINNER-User--FaTM-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
<a href="mailto:miooiio@outlook.jp"><img src="https://img.shields.io/badge/BEGINNER-User--53EDF-0078D4?style=flat-square&logo=hyperledger&logoColor=white" /></a> 
![](https://img.shields.io/badge/BEGINNER-User--56652-0078D4?style=flat-square&logo=hyperledger&logoColor=white)
![](https://img.shields.io/badge/BEGINNER-User--2b965-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/BEGINNER-苏芸晴或i-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/BEGINNER-tiwatezhanshen-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/BEGINNER-User--_tpuJ-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
