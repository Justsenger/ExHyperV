# ExHyperV
一款提供DDA和GPU半虚拟化（GPU分区）等HyperV虚拟机实验功能的软件，基于WPF-UI开发，让凡人也能轻松玩转Hyper-V高级功能。

通过对微软Hyper-V文档有效解读，以及对James的Easy-GPU-PV项目深入研究，完善修复了很多问题。但由于时间和精力有限，还有更多的测试未进行，所以如果你遇到了任何软件/游戏无法使用，请提出问题！


# 下载&构建
* 下载：等待添加发布链接。


* 构建：安装 Visual Studio 2022，添加C#和WPF，然后点击/src/ExHyperV.sln即可开始构建。


# 界面

![主界面](https://github.com/Justsenger/ExHyperV/blob/main/img/01.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/02.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/03.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/04.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/05.png)

# DDA
### DDA显卡兼容性（更新中）
以下兼容性通常需要安装到虚拟机才可以体现。如果您有更多的测试案例，请在问题中告诉我！完善下表可以给各位选择显卡提供更好的指导。

1.识别：显卡分配到虚拟机后可被GPU-Z正常识别。部分魔改卡此项可能存在问题。

2.功能层复位（FLR）：若不具备此功能，分配此显卡的虚拟机重启将导致宿主机重启。N卡通常完备，AMD和Intel未经广泛测试，可能存在硬件缺陷。

3.物理显示输出：虚拟机是否能通过显卡输出物理信号。

| 品牌 | 型号 | 识别 | 功能层复位| 物理显示输出 |
| -------- | -------- | -------- | -------- | -------- |
| Nvidia   | RTX 4070 |✔️ |✔️ | ✔️|
| Nvidia   | GT 1050 |✔️ |✔️ | ✔️|
| Nvidia   | GT 1030 |✔️ |✔️ | ✔️|
| Nvidia   | GT 210 |✔️ | ✔️ | ✖️|
| Intel   |  Intel DG1 |✔️ | ✖️ | 特定驱动✔️|

### DDA设备状态
设备共有三种状态：主机态、卸除态、虚拟态。尽管微软文档没有提及此事，但实际上掌控三种状态非常重要，否则会陷入混乱。

1.处于主机态时，设备挂载到宿主系统；

2.处于卸除态（#已卸除）时，通常是由于执行了"Dismount-VMHostAssignableDevice"，然而由于各种原因没有成功分配到虚拟机，导致设备处于一个中间态，无法被宿主的设备管理器正常识别，可以在软件中选择挂载到宿主或者再次尝试挂载到虚拟机。

3.处于虚拟态时，设备挂载到虚拟机系统。

# GPU-PV

### 简介

* GPU-PV全称为GPU paravirtualization，中文名叫做GPU半虚拟化。此功能自从WDDM 2.4开始提供，这同时意味着，虚拟机和宿主的系统版本一定不能低于17134，否则没有任何实现的可能。


* 本软件中，要求宿主机系统版本不得低于22000（可以使用Server 2022、Server 2025以及任何版本Win11），这是因为低于22000版本的Hyper-V命令组件中，Add-VMGpuPartitionAdapter缺少参数：InstancePath，这会导致无法指定需要虚拟化的特定显卡，会引入更多的混乱。因此，为了更加方便，请升级您的宿主系统。


* 目前没有找到任何方法可以限制虚拟机GPU的资源使用，Set-VMGpuPartitionAdapter的设定参数并不会起到任何[实质性作用](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298)。因此，在找到有效的方法前，不会开放资源分配功能。Nvidia的Grid驱动可以分割资源，但是它需要不菲的授权费。


* 下图描述了 WDDM 半虚拟化设计中涉及的各种组件。


![WDDM](https://github.com/Justsenger/ExHyperV/blob/main/img/WDDM.png)

### Windows版本

宿主：Server 2022、Server 2025以及任何版本Win11。需要高于虚拟机系统版本。

虚拟机：

(0,17134) 不支持

[17134,19041)  可以调用GPU，但不会正确显示显卡型号。

(19041,∞）推荐使用，包含win10和win11。

### 措施

* 对于Nvidia，会自动导入宿主系统的nvlddmkm.reg，并修改其中的DriverStore为HostDriverStore。


* 导入宿主驱动时，虚拟机中的HostDriverStore下所有文件将设定为只读属性，以防止任何驱动包括nvlddmkm.sys文件丢失。




