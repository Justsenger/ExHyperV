[中文文档](https://github.com/Justsenger/ExHyperV) 

[English](https://github.com/Justsenger/ExHyperV/blob/main/README_en.md)

# ExHyperV
A software that provides features like DDA and GPU paravirtualization (GPU partitioning), making it easy for anyone to use advanced Hyper-V features.

By effectively interpreting Microsoft's Hyper-V documentation and thoroughly studying James's Easy-GPU-PV project, many issues have been improved and fixed. However, due to limited time and resources, there are still many tests that haven't been conducted. So if you encounter any issues with software or games not working, please report them!

# Download & Build
* Download: [Latest Version](https://github.com/Justsenger/ExHyperV/releases/latest)

* Build: Install Visual Studio 2022, add C# and WPF, then open /src/ExHyperV.sln to start building your version.

# Interface Overview

![Main Interface](https://github.com/Justsenger/ExHyperV/blob/main/img/1.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/2.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/3.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/4.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/5.png)

# DDA
### DDA GPU Compatibility (Updated)
The following compatibility generally requires installation on the virtual machine to be reflected. If you have more test cases, please let me know! Improving this table will provide better guidance for selecting GPUs.

1. Recognition: After the GPU is assigned to the VM, it may not work properly. Some modified cards may have issues here.

2. Functional Layer Reset (FLR): If this function is not available, restarting the VM with the assigned GPU will cause the host to restart. NVIDIA cards typically support this well, while AMD and Intel have not been widely tested and may have hardware defects.

3. Physical Display Output: Whether the VM can output physical signals through the GPU.

| Brand  | Model       | Recognition | FLR Support | Physical Display Output |
|--------|-------------|-------------|-------------|-------------------------|
| Nvidia | RTX 4070    | ✔️          | ✔️          | ✔️                      |
| Nvidia | GT 1050     | ✔️          | ✔️          | ✔️                      |
| Nvidia | GT 1030     | ✔️          | ✔️          | ✔️                      |
| Nvidia | GT 210      | ✔️          | ✔️          | ✖️                      |
| Intel  | Intel DG1   | ✔️          | ✖️          | Specific Driver ✔️      |

### DDA Device Status
Devices have three states: Host State, Removed State, and Virtual State. Although Microsoft’s documentation does not mention this, managing these three states is crucial to avoid confusion.

1. In Host State, the device is mounted to the host system.

2. Executing "Dismount-VMHostAssignableDevice" will change the device to Removed State (#Removed). However, if the device fails to be successfully assigned to the VM, it will be in an intermediate state and cannot be recognized by the host's device manager. You can either mount it back to the host or retry mounting it to the VM.

3. In Virtual State, the device is mounted to the virtual machine system.

# GPU-PV

### Introduction

* GPU-PV stands for GPU Paravirtualization. This feature has been available since WDDM 2.4, which also means that the virtual machine and host system versions must be at least 17134 for it to work.

* The host system version must be at least 22000 (you can use Server 2022, Server 2025, or non-Home editions of Windows 11). This is because Hyper-V components in versions below 22000 lack the InstancePath parameter in Add-VMGpuPartitionAdapter, making it impossible to specify the specific GPU to virtualize, which could cause more confusion. Therefore, for simplicity, please upgrade your host system.

* Currently, there is no method to limit GPU resource usage in virtual machines. The parameters for Set-VMGpuPartitionAdapter do not have any [substantive effect](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298). NVIDIA's Grid drivers can partition resources, but they require a costly license.

* The logical adapters created by GPU-PV only simulate physical adapters at the system level, but they do not inherit certain unique registry parameters, hardware features, and driver features of physical adapters well. Therefore, if the software or game you attempt to run depends on these special flags, errors may occur and need targeted fixes, which is the main purpose of this project.

* The following diagram describes the various components involved in WDDM paravirtualization.

![WDDM](https://github.com/Justsenger/ExHyperV/blob/main/img/WDDM.png)

| Windows Version | WDDM Version | Virtualization-related Function Updates |
|-----------------|--------------|----------------------------------------|
| 17134           | 2.4          | Introduced IOMMU-based GPU isolation.  |
| 17763           | 2.5          | Enhanced virtualization support, improving resource handle management and event signaling between host and guest. |
| 18362           | 2.6          | Improved VRAM management. VM VRAM is prioritized in continuous physical GPU memory, ensuring memory residency status. |
| 19041           | 2.7          | VMs can correctly identify physical GPU models and replace Microsoft Virtual Renderer Driver. |
| 20348           | 2.9          | Added Cross-Adapter Resource Scan Output (CASO), allowing direct rendering to the display adapter, reducing latency and bandwidth requirements. |
| 22000           | 3.0          | DMA remapping breaks GPU address limits, allowing GPUs to access more memory beyond hardware limits. Improved UMD and KMD event signaling for better debugging. |
| 22621           | 3.1          | UMD and KMD share the same memory area, reducing memory copying and transmission, optimizing memory utilization, and increasing data access speed. |
| 26100           | 3.2          | Added live migration functionality. Enhanced graphics driver timeout detection and recovery analysis. Introduced WDDM feature query mechanism. |

### Windows Versions

Host: Server 2022, Server 2025, and Windows 11 with Hyper-V enabled.

VM:

* Versions 17134 and below do not support GPU functionality.

* Versions 17134 to 19040 (WDDM 2.4-2.6) can access GPUs but cannot properly display the GPU model.

* From version 19041 (WDDM 2.7) onwards, all GPU features can be fully utilized.

### Extra Magic

* When importing host drivers, all files in the VM's HostDriverStore will be set to read-only attributes to prevent any drivers, including nvlddmkm.sys, from being lost.

* For NVIDIA, the host system’s nvlddmkm.reg will be automatically imported, and the DriverStore path will be modified to HostDriverStore.
