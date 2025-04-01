# Document/文档

[English](https://github.com/Justsenger/ExHyperV/blob/main/README_en.md) | [中文](https://github.com/Justsenger/ExHyperV)


# ExHyperV
一款提供DDA和GPU半虚拟化（GPU分区）等功能的软件，让凡人也能轻松玩转Hyper-V高级功能。

通过对微软Hyper-V文档有效解读，以及对James的[Easy-GPU-PV](https://github.com/jamesstringerparsec/Easy-GPU-PV)项目深入研究，完善修复了很多问题。但由于时间和精力有限，还有更多的测试未进行，所以如果你遇到了任何软件/游戏无法使用，请提出问题！


# 下载&构建
* 下载：[最新版](https://github.com/Justsenger/ExHyperV/releases/latest)

* 构建：安装 Visual Studio 2022，添加C#和WPF，点击/src/ExHyperV.sln即可开始构建您的版本。


# 界面一览

![主界面](https://github.com/Justsenger/ExHyperV/blob/main/img/01.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/02.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/03.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/04.png)

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/05.png)

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

对于GPU-PV，本工具要求宿主机系统版本号不得低于22000，这是因为低于22000版本的Hyper-V组件中，Add-VMGpuPartitionAdapter缺少参数InstancePath，这会导致无法指定需要虚拟化的特定显卡，会引发不必要的混乱。因此，为了更加简易，请升级您的宿主系统。

# DDA
Wiki: [English]() |  [中文](https://github.com/Justsenger/ExHyperV/wiki/DDA)

# GPU-PV
Wiki: [English]() |  [中文](https://github.com/Justsenger/ExHyperV/wiki/GPUPV)


# 笔记

### 事实

* 可以任意选择一代虚拟机或者二代虚拟机。


* 不需要禁用检查点功能。


* 一张显卡同时只能用于DDA或者GPU-PV。


* 一个虚拟机可以同时使用DDA和GPU-PV。(对于显存大于4G的显卡，目前MIMO存在问题)


* 一个虚拟机可以从同一张显卡获得多个逻辑适配器分区，但是总性能不变。


* 一个虚拟机可以从多张显卡获得多个逻辑适配器分区。


### 魔法

* 工具会将宿主驱动导入到虚拟机。同时，HostDriverStore下所有文件将设定为只读属性，以防止任何驱动文件丢失。


* 对于Nvidia，会自动导入宿主系统的nvlddmkm.reg，并修改其中的DriverStore为HostDriverStore。







