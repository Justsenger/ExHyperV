# 讨论群
[Telegram](https://t.me/ExHyperV)

# 文档/Document

[中文](https://github.com/Justsenger/ExHyperV/blob/main/README_cn.md) | [English](https://github.com/Justsenger/ExHyperV)


# ExHyperV
一款提供DDA和GPU半虚拟化（GPU分区）等功能的软件，让凡人也能轻松玩转Hyper-V高级功能。

通过对微软Hyper-V文档有效解读，以及对James的Easy-GPU-PV项目深入研究，完善修复了很多问题。但由于时间和精力有限，还有更多的测试未进行，所以如果你遇到了任何软件/游戏无法使用，请提出问题！


# 下载&构建
* 下载：[最新版](https://github.com/Justsenger/ExHyperV/releases/latest)

* 构建：安装 Visual Studio 2022，添加C#和WPF，点击/src/ExHyperV.sln即可开始构建您的版本。


# 界面一览

![主界面](https://github.com/Justsenger/ExHyperV/blob/main/img/01.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/02.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/03.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/04.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/05.png)

是的，甚至支持高达8种类别的显卡识别！但是不一定可以激活功能...

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/various.png)

# 可用的Windows版本

### DDA

* Windows Server 2016

* Windows Server 2019

* Windows Server 2022

* Windows Server 2025

### GPU-PV

* Windows 11

* Windows Server 2022

* Windows Server 2025

对于GPU-PV，本工具要求宿主机系统版本不得低于22000，这是因为低于22000版本的Hyper-V组件中，Add-VMGpuPartitionAdapter缺少参数InstancePath，这会导致无法指定需要虚拟化的特定显卡，会引发不必要的混乱。因此，为了更加轻松，请升级您的宿主系统。

# DDA

### 简介

DDA全称Discrete Device Assignment，即离散设备分配，可以将独立的设备分配到虚拟机中。它是以PCIE总线为单位进行分配的，例如显卡、网卡、USB控制器（CPU直连、主板芯片组、独立USB芯片），如果您的设备不在工具显示的列表中，则说明不可以单独直通它，需要直通更上一级的控制器。


### DDA设备状态
> 设备共有3种状态：主机态、卸除态、虚拟态。尽管微软文档没有提及此事，但实际上了解这三种状态非常重要，否则会陷入混乱。

1.处于主机态时，设备挂载到宿主系统。

2.执行"Dismount-VMHostAssignableDevice"会让设备转变为卸除态（#已卸除）。然而，若由于各种原因没有成功分配到虚拟机，这个设备将处于一个中间态，无法被宿主的设备管理器正常识别。可以在本工具中选择挂载到宿主或者再次尝试挂载到虚拟机。

3.处于虚拟态时，设备挂载到虚拟机系统。

### DDA显卡兼容性（需要更多反馈）
> 以下兼容性需要安装到虚拟机才能发现。如果您有更多的测试案例，请在问题中告诉我！完善下表可为选择显卡提供更好的指导。

| 品牌 | 型号 | 识别 | 功能层复位| 物理显示输出 |
| -------- | -------- | -------- | -------- | -------- |
| Nvidia   | RTX 5090 |✔️ |✔️ | ✔️|
| Nvidia   | RTX 4090 |✔️ |✔️ | ✔️|
| Nvidia   | RTX 4070 |✔️ |✔️ | ✔️|
| Nvidia   | GT 1050 |✔️ |✔️ | ✔️|
| Nvidia   | GT 1030 |✔️ |✔️ | ✔️|
| Nvidia   | GT 210 |✔️ | ✔️ | ✖️|
| Intel   |  Intel DG1 |✔️ | ✖️ | 特定驱动✔️|

1.识别：显卡分配到虚拟机后可能无法正常使用。部分笔记本魔改卡、矿卡此项可能存在问题。

2.功能层复位（FLR）：若不具备此功能，分配此显卡的虚拟机重启将导致宿主机重启。Nvidia表现良好，而AMD和Intel未经广泛测试，可能存在[硬件缺陷](https://www.reddit.com/r/Amd/comments/jehkey/will_big_navi_support_function_level_reset_flr/)。

3.物理显示输出：虚拟机是否能通过显卡输出物理信号。

# GPU-PV

### 简介

* GPU-PV全称为GPU paravirtualization，即GPU半虚拟化。此功能自从WDDM 2.4开始提供，这同时意味着，虚拟机和宿主的系统版本一定不能低于17134，否则没有任何实现的可能。

* 目前没有办法可以限制虚拟机GPU的资源使用，Set-VMGpuPartitionAdapter的设定参数并不会起到任何[实质性作用](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298)。因此，在找到有效的方法前，不会开发资源分配功能。Nvidia的Grid驱动可以分割资源，但是它需要不菲的授权费。

* 通过GPU-PV创建的逻辑适配器，仅仅是从系统层面模拟物理适配器，但对于物理适配器独特的注册表参数、硬件特征、驱动特征并没有很好地继承。因此，如果您尝试打开的软件/游戏依赖于这些特殊的标志，很可能出现错误，需要针对性的修复。


* 接下来介绍 WDDM 半虚拟化设计中涉及的各种组件。


![WDDM](https://github.com/Justsenger/ExHyperV/blob/main/img/WDDM_cn.png)

* 以下是Windows版本和WDDM版本的对应关系，WDDM版本越高，GPU-PV的功能就越完善。

| Windows版本 | WDDM版本 | 虚拟化相关功能更新 |
| -------- | -------- | ------------------------ |
| 17134  | 2.4      | 引入了基于 IOMMU 的 GPU 隔离。 |
| 17763  | 2.5      | 增强了虚拟化的支持，使得宿主和来宾之间的资源句柄管理和事件信号化，用户模式驱动（UMD）和内核模式驱动（KMD）能够更好地协同工作。 |
| 18362  | 2.6      | 提升了显存管理。虚拟机显存优先分配在GPU连续的物理显存，能收到内存的驻留状态。 |
| 19041  | 2.7      | 虚拟机设备管理器可以正确识别Microsoft Virtual Renderer Driver所对应的物理显卡型号。 |
| 20348  | 2.9      | 增加了支持跨适配器资源扫描输出（CASO）功能，渲染适配器的画面可以直接输出到显示适配器，而无需进行两次复制，降低了延迟和带宽需求。 |
| 22000  | 3.0      | 通过DMA重映射突破GPU地址限制，允许GPU访问超过硬件限制的更多内存。提高了用户模式驱动（UMD）和内核模式驱动（KMD）的事件信号机制，提升调试能力。 |
| 22621  | 3.1      | 用户模式驱动（UMD）和内核模式驱动（KMD）共享相同的内存区域，减少内存的复制和传输，优化内存的利用效率，提高数据访问速度。 |
| 26100  | 3.2      | 增加了GPU实时迁移功能。增强了图形驱动的超时检测和恢复分析。引入了WDDM功能查询机制。 |


### 虚拟机Windows版本

* 17134以下版本不支持GPU半虚拟化。


* 17134到19040之间的版本（WDDM 2.4-2.6）可以调用GPU，但不会显示正确的显卡型号。


* 从19041版本（WDDM 2.7）开始，可以正常使用GPU功能。


### 从虚拟机引出显示信号

在GPU半虚拟化模型中，虚拟机从宿主机获取到的GPU是作为“渲染适配器”存在的，通常会与作为“显示适配器”的 Microsoft Hyper-V 视频[配对](https://learn.microsoft.com/zh-cn/windows-hardware/drivers/display/gpu-paravirtualization#gpuvirtualizationflags)进行画面输出。然而，Microsoft Hyper-V 视频仅支持到1080p，刷新率也受到严重限制，因此我们需要一个更好的“显示适配器”。

总共有以下方案可以实现显示信号输出:

>Microsoft Hyper-V 视频

这是默认选项，兼容性良好，但是据说分辨率最高1920*1080，刷新率62Hz，资料整理中。

>间接显示驱动程序

可以尝试[这个](https://github.com/VirtualDrivers/Virtual-Display-Driver)，然后搭配Sunshine等串流软件使用。

![Sunshine+PV](https://github.com/user-attachments/assets/e25fce26-6158-4052-9759-6d5d1ebf1c5d)

> USB 显卡（需要DDA直通USB控制器x1） 

可以直通一个USB控制器，然后搭配USB显卡使用。目前，对于显存大于4G的显卡似乎和DDA搭配使用存在问题，资料整理中。

比较常见的芯片是[DL-6950](https://www.synaptics.com/cn/products/displaylink-graphics/integrated-chipsets/dl-6000)和[SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html)，以下一组图片描述了渲染适配器（GTX 1050）和显示适配器（DL 6950）联合工作的场景，可以在144Hz下几乎无延迟地正常工作。

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d1.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d2.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d3.png)

![DL](https://github.com/Justsenger/ExHyperV/blob/main/img/d4.png)

# 笔记

### 事实

* 虚拟机最好将运行内存固定分配。


* 所有功能均可以使用一代虚拟机或者二代虚拟机，任意选择。


* 所有功能不需要禁用检查点功能。


* 一张显卡同一时间只能作为DDA或者GPU-PV。


* 一个虚拟机可以同时使用DDA和GPU-PV。


* 一个虚拟机可以从同一张显卡获得多个逻辑适配器分区，但是总性能不变。


* 一个虚拟机可以从多张显卡获得多个逻辑适配器分区。


### 魔法

* 工具会将宿主驱动导入到虚拟机。同时，HostDriverStore下所有文件将设定为只读属性，以防止任何驱动文件丢失。


* 对于Nvidia，会自动导入宿主系统的nvlddmkm.reg，并修改其中的DriverStore为HostDriverStore。







