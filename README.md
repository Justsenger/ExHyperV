# Discussion Group

[Telegram](https://t.me/ExHyperV)

# 文档/Document

[中文](https://github.com/Justsenger/ExHyperV/blob/main/README_cn.md) | [English](https://github.com/Justsenger/ExHyperV)

# ExHyperV

A software that provides features such as DDA and GPU paravirtualization (GPU partitioning), allowing users to easily access advanced Hyper-V features.

By effectively interpreting Microsoft Hyper-V documentation and conducting in-depth research on James' Easy-GPU-PV project, many issues have been fixed and improved. However, due to limited time and resources, further testing is still needed. If you encounter any issues with software or games not working, please report them!

# Download & Build

* Download: [Latest Version](https://github.com/Justsenger/ExHyperV/releases/latest)

* Build: Install Visual Studio 2022, add C# and WPF, then open /src/ExHyperV.sln to start building your version.

# Interface Overview

![Main Interface](https://github.com/Justsenger/ExHyperV/blob/main/img/1.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/2.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/3.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/4.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/5.png)

Yes, it even supports up to 8 types of graphics card recognition! But it may not be possible to activate the function...

![Function](https://github.com/Justsenger/ExHyperV/blob/main/img/various.png)

# Supported Windows Versions

### DDA

* Windows Server 2016
* Windows Server 2019
* Windows Server 2022
* Windows Server 2025

### GPU-PV

* Windows 11
* Windows Server 2022
* Windows Server 2025

For GPU-PV, this tool requires the host system to be version 22000 or above because Hyper-V components in versions below 22000 lack the InstancePath parameter in Add-VMGpuPartitionAdapter, preventing the specification of a specific GPU to virtualize. Therefore, please upgrade your host system for easier operation.

# DDA

### Introduction

DDA stands for Discrete Device Assignment, which allows independent devices to be assigned to virtual machines. Devices are assigned based on the PCIe bus, such as GPUs, network cards, and USB controllers (CPU-direct, motherboard chipsets, or dedicated USB chips). If your device is not listed in the tool, it cannot be assigned directly, and you need to pass through the higher-level controller.

### DDA Device Status
> There are three device states: Host State, Disassociated State, and Virtual State. Although this is not mentioned in Microsoft's documentation, understanding these states is crucial to avoid confusion.

1. In Host State, the device is attached to the host system.
2. Executing `Dismount-VMHostAssignableDevice` transitions the device to the Disassociated State (#Disassociated). However, if the device fails to be assigned to a virtual machine, it enters an intermediate state and may not be properly recognized by the host's device manager. You can either remount it to the host or attempt to assign it to the VM again.
3. In Virtual State, the device is assigned to the virtual machine.

### DDA Graphics Card Compatibility (More Feedback Needed)
> Compatibility needs to be checked after installation in the virtual machine. If you have more test cases, please let me know in the issues! Enhancing this table will help provide better guidance for selecting graphics cards.

| Brand | Model | Recognition | FLR | Physical Display Output |
| ------- | ------- | -------- | ---- | ------------------------ |
| Nvidia  | RTX 5090 |✔️ |✔️ | ✔️ |
| Nvidia  | RTX 4090 |✔️ |✔️ | ✔️ |
| Nvidia  | RTX 4070 |✔️ |✔️ | ✔️ |
| Nvidia  | GT 1050 |✔️ |✔️ | ✔️ |
| Nvidia  | GT 1030 |✔️ |✔️ | ✔️ |
| Nvidia  | GT 210 |✔️ | ✔️ | ✖️ |
| Intel   | Intel DG1 |✔️ | ✖️ | Specific driver✔️ |

1. Recognition: After assigning the graphics card to the virtual machine, it may not function properly. Some modified laptop cards or mining cards may encounter issues.
2. Function-Level Reset (FLR): If the card does not support this function, restarting the virtual machine that has this GPU assigned will cause a reboot of the host. Nvidia cards work well, but AMD and Intel cards are not widely tested and may have [hardware issues](https://www.reddit.com/r/Amd/comments/jehkey/will_big_navi_support_function_level_reset_flr/).
3. Physical Display Output: Whether the virtual machine can output a physical signal through the graphics card.

# GPU-PV

### Introduction

* GPU-PV stands for GPU paravirtualization. This feature was introduced starting with WDDM 2.4, meaning that both the host and guest systems must be at least version 17134 to function.

* Currently, there is no way to limit the GPU resource usage for virtual machines, as the parameters in `Set-VMGpuPartitionAdapter` have no [substantive effect](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298). Therefore, resource allocation functionality will not be developed until an effective solution is found. Nvidia's Grid driver can partition resources, but it requires a significant licensing fee.

* The logical adapter created through GPU-PV only simulates the physical adapter at the system level. It does not inherit the physical adapter's unique registry parameters, hardware characteristics, or driver features very well. Therefore, if software or games that rely on these specific flags are attempted, errors may occur and will require targeted fixes.

* Below is an introduction to various components involved in the WDDM paravirtualization design.

![WDDM](https://github.com/Justsenger/ExHyperV/blob/main/img/WDDM.png)

* Below is the correspondence between Windows versions and WDDM versions. The higher the WDDM version, the better the GPU-PV functionality.

| Windows Version | WDDM Version | Virtualization Feature Updates |
| ---------------- | ------------ | ------------------------------ |
| 17134 | 2.4 | Introduced IOMMU-based GPU isolation. |
| 17763 | 2.5 | Enhanced virtualization support for resource handle management and event signaling between host and guest, improving coordination of user-mode and kernel-mode drivers. |
| 18362 | 2.6 | Improved video memory management. Virtual machine video memory is preferentially allocated in GPU-contiguous physical memory and can reflect memory residency state. |
| 19041 | 2.7 | Virtual machine device manager can correctly identify the physical GPU model corresponding to the Microsoft Virtual Renderer Driver. |
| 20348 | 2.9 | Added support for Cross-Adapter Resource Scan Output (CASO), allowing rendering adapter screens to be directly output to display adapters without needing two copies, reducing latency and bandwidth requirements. |
| 22000 | 3.0 | Broke through GPU address limits with DMA remapping, allowing the GPU to access more memory than the hardware limit. Improved event signaling mechanisms between UMD and KMD, enhancing debugging capabilities. |
| 22621 | 3.1 | Shared memory regions between UMD and KMD reduce memory copying and transmission, optimizing memory utilization and speeding up data access. |
| 26100 | 3.2 | Added GPU real-time migration functionality. Enhanced timeout detection and recovery analysis for graphics drivers. Introduced WDDM feature query mechanism. |

### Virtual Machine Windows Versions

* Versions below 17134 do not support GPU paravirtualization.

* Versions between 17134 and 19040 (WDDM 2.4–2.6) can call the GPU but will not display the correct graphics card model.

* Starting from version 19041 (WDDM 2.7), GPU functionality can be used properly.

### Display Signal Output from Virtual Machine

In the GPU paravirtualization model, the virtual machine accesses the GPU from the host as a "render adapter" and typically pairs with the Microsoft Hyper-V video as a "display adapter" to output the screen. However, Microsoft Hyper-V video supports only up to 1080p and has a severely limited refresh rate. Therefore, we need a better "display adapter."

Here are several options to achieve display signal output:

> Microsoft Hyper-V Video

This is the default option and is well compatible. However, it reportedly supports a maximum resolution of 1920x1080 and a refresh rate of 62Hz. Further information is being compiled.

> Indirect Display Drivers

You can try [this](https://github.com/VirtualDrivers/Virtual-Display-Driver) and use it with streaming software. Further information is being compiled.

> USB Graphics Card (Requires DDA passthrough of USB controller x1)

A USB controller can be passed through, and a USB graphics card can be used. Currently, there seem to be issues with using graphics cards with more than 4GB of memory together with DDA. Further information is being compiled.

Common chips are [DL-6950](https://www.synaptics.com/cn/products/displaylink-graphics/integrated-chipsets/dl-6000) and [SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html). The following images describe the scenario where a render adapter (GTX 1050) and display adapter (DL 6950) work together, achieving normal operation at 144Hz with almost no latency.

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d1.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d2.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d3.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d4.png)

# Notes

### Facts

* It is best for the virtual machine to have a fixed allocation of running memory.
* All features can be used with either generation 1 or generation 2 virtual machines.
* No need to disable checkpoint functionality for any feature.
* A single graphics card can only be used as either DDA or GPU-PV at a time.
* A virtual machine can use both DDA and GPU-PV simultaneously.
* A virtual machine can receive multiple logical adapter partitions from the same graphics card, but total performance remains unchanged.
* A virtual machine can receive multiple logical adapter partitions from multiple graphics cards.

### Magic

* The tool will import host drivers into the virtual machine. Additionally, all files in HostDriverStore will be set to read-only to prevent any driver files from being lost.
* For Nvidia, the tool will automatically import the host system’s `nvlddmkm.reg` and modify the `DriverStore` to `HostDriverStore`.
