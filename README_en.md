# ExHyperV
A software that provides DDA and GPU paravirtualization (GPU partitioning) features, making it easy for ordinary people to master Hyper-V's advanced features.

By effectively interpreting Microsoft's Hyper-V documentation and conducting in-depth research on James' Easy-GPU-PV project, many issues have been improved and fixed. However, due to time and energy constraints, more testing remains to be done. If you encounter any software or game compatibility issues, please report them!

# Download & Build
* Download: [Latest Version](https://github.com/Justsenger/ExHyperV/releases/latest)

* Build: Install Visual Studio 2022, add C# and WPF, and open the `/src/ExHyperV.sln` file to start building your version.

# Interface Overview

![Main Interface](https://github.com/Justsenger/ExHyperV/blob/main/img/1.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/2.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/3.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/4.png)

![Features](https://github.com/Justsenger/ExHyperV/blob/main/img/5.png)

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

For GPU-PV, this tool requires the host system version to be at least 22000 (all Win10 versions). This is because versions lower than 22000 lack the `InstancePath` parameter in the `Add-VMGpuPartitionAdapter` command, which causes an inability to specify a particular GPU for virtualization, resulting in further confusion. Therefore, it is recommended to upgrade your host system for simplicity.

# DDA

### Introduction

DDA stands for Discrete Device Assignment, which allows the assignment of a discrete device to a virtual machine. It is assigned at the PCIE bus level, such as GPUs, network cards, and USB controllers (CPU-connected, motherboard chipset, or standalone USB chips). If your device is not listed in the tool, it means it cannot be directly passed through and requires passing through a higher-level controller.

### DDA GPU Compatibility (Requires More Feedback)
The compatibility of GPUs is detected once assigned to the virtual machine. If you have more testing cases, please let me know! Improving the table can provide better guidance for selecting GPUs.

1. Recognition: Some cards, such as modified laptop cards or mining cards, may have issues.
2. Function Level Reset (FLR): If a GPU does not support FLR, rebooting the VM with this GPU may cause the host to reboot. Nvidia cards typically support it, while AMD and Intel have not been widely tested and may have [hardware defects](https://www.reddit.com/r/Amd/comments/jehkey/will_big_navi_support_function_level_reset_flr/).
3. Physical Display Output: Whether the virtual machine can output a physical signal through the GPU.

| Brand  | Model       | Recognition | FLR Support | Physical Display Output |
|--------|-------------|-------------|-------------|-------------------------|
| Nvidia | RTX 4070    | ✔️          | ✔️          | ✔️                       |
| Nvidia | GT 1050     | ✔️          | ✔️          | ✔️                       |
| Nvidia | GT 1030     | ✔️          | ✔️          | ✔️                       |
| Nvidia | GT 210      | ✔️          | ✔️          | ✖️                       |
| Intel  | Intel DG1   | ✔️          | ✖️          | Specific Driver ✔️       |

### DDA Device States
Devices can be in one of three states: host, dismounted, or virtualized. Although Microsoft documentation does not mention this, managing these states properly is crucial to avoid confusion.

1. In the host state, the device is mounted to the host system.
2. Executing "Dismount-VMHostAssignableDevice" will put the device into the dismounted state (#Dismounted). If the device cannot be assigned to the virtual machine, it enters an intermediate state and may not be recognized by the host device manager. You can choose to remount it to the host or retry mounting it to the virtual machine.
3. In the virtual state, the device is mounted to the virtual machine.

# GPU-PV

### Introduction

* GPU-PV stands for GPU Paravirtualization. This feature has been available since WDDM 2.4, which means that both the host and guest system versions must be at least 17134 for it to work.

* Currently, there is no way to limit GPU resource usage within the virtual machine. The `Set-VMGpuPartitionAdapter` setting does not have any [substantive effect](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298). Therefore, resource allocation will not be provided until a method is found. Nvidia's Grid drivers can partition resources but require expensive licensing fees.

* The logical adapter created through GPU-PV only simulates a physical adapter at the system level, but it does not fully inherit the unique registry parameters, hardware features, or driver characteristics of the physical adapter. If the software or game you attempt to run depends on these special flags, errors may occur and require specific fixes.

### WDDM Paravirtualization Design Overview

![WDDM](https://github.com/Justsenger/ExHyperV/blob/main/img/WDDM.png)

The following is the correspondence between Windows versions and WDDM versions. The higher the WDDM version, the more complete the GPU-PV functionality.

| Windows Version | WDDM Version | Virtualization Related Updates         |
|-----------------|--------------|----------------------------------------|
| 17134           | 2.4          | Introduced GPU isolation based on IOMMU. |
| 17763           | 2.5          | Enhanced virtualization support for resource handles and event signaling. |
| 18362           | 2.6          | Improved memory management for GPU.   |
| 19041           | 2.7          | Virtual machine device manager can correctly recognize the physical GPU model corresponding to the Microsoft Virtual Renderer Driver. |
| 20348           | 2.9          | Added support for cross-adapter resource scanning output (CASO). |
| 22000           | 3.0          | DMA remapping breaks GPU address limits, allowing GPU to access more memory. |
| 22621           | 3.1          | UMD and KMD share the same memory region, optimizing memory utilization and improving data access speed. |
| 26100           | 3.2          | Added GPU live migration functionality. |

### Windows Versions

Host: Win11, Server 2022, Server 2025.

Guest:

* Versions below 17134 do not support GPU paravirtualization.
* Versions between 17134 and 19040 (WDDM 2.4-2.6) can call GPU but will not display the correct GPU model.
* From version 19041 (WDDM 2.7) onwards, GPU functionality is fully supported.

### Display Signal Output from Virtual Machine

In GPU paravirtualization, the GPU received by the virtual machine from the host is treated as a "render adapter," typically paired with Microsoft's Hyper-V video as a "display adapter." However, Microsoft Hyper-V video supports up to 1080p, and the refresh rate is significantly limited. Therefore, we need a better "display adapter."

The following options can be used for display signal output:

> Microsoft Hyper-V Video

This is the default option with no compatibility issues. More information is being gathered.

> Indirect Display Driver

This involves various streaming solutions. More information is being gathered.

> USB Graphics Card (Requires DDA passthrough USB controller x1)

Common chips include [DL-6950](https://www.synaptics.com/cn/products/displaylink-graphics/integrated-chipsets/dl-6000) and [SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html). The following images describe how the render adapter (GTX 1050) and display adapter (DL 6950) work together, with minimal delay in Fortnite at 144Hz.

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d1.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d2.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d3.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d4.png)

# Notes

### Facts

* All features can be used with both first-generation and second-generation virtual machines.
* No need to disable checkpoint functionality for any feature.
* A single GPU can be used either for DDA or GPU-PV, not both simultaneously.
* A virtual machine can use both DDA and GPU-PV simultaneously.
* A virtual machine can receive multiple logical adapter partitions from the same GPU, but total performance remains unchanged.
* A virtual machine can receive multiple logical adapter partitions from multiple GPUs.

### Magic

* The tool imports host drivers into the virtual machine. Additionally, all files in the HostDriverStore are set to read-only attributes to prevent any driver files from being lost.
* For Nvidia, it automatically imports the `nvlddmkm.reg` from the host system and modifies the DriverStore path to HostDriverStore.
