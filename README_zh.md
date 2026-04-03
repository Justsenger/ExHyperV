<h1>
  <img src="https://github.com/Justsenger/ExHyperV/blob/main/img/logo.png?raw=true" width="32" alt="ExHyperV logo"> 
  ExHyperV
</h1>

<div align="center">

**一款图形化的 Hyper-V 管理工具，能让凡人也能轻松玩转Hyper-V的高级功能。**

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

[English](https://github.com/Justsenger/ExHyperV) | **中文**

---

ExHyperV 通过深入研究 Hyper-V 文档、 [WMI](https://github.com/Justsenger/HyperV-WMI-Documentation) 以及 [HCS](https://learn.microsoft.com/en-us/virtualization/api/hcs/overview) 等技术细节，旨在为用户提供一个图形化的、易于使用的 Hyper-V 高级功能配置工具。

由于个人时间和精力有限，项目可能存在未经测试的场景或错误。如果您在使用中遇到任何关于硬件/软件的问题，欢迎通过 [Issues](https://github.com/Justsenger/ExHyperV/issues) 提出！

各项功能将随着时间的推进逐步完善。如果您有特别希望优先添加的功能，或非常喜爱此项目，可以通过文档底部的赞赏按钮提供赞助并留言！

## 🎨 界面一览

ExHyperV 使用 [WPF-UI](https://github.com/lepoco/wpfui) 框架，提供流畅现代的用户界面体验和科幻的视觉效果。支持黑色主题和白色主题，并且会根据系统主题自动切换。

目前支持的语言：简体中文、英文。

![主界面](https://github.com/Justsenger/ExHyperV/blob/main/img/01.png)

<details>
<summary>点击查看更多界面截图</summary>
  
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/02.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/03.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/04.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/05.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/06.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/07.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/08.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/09.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/10.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/11.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/12.png)

</details>

## 🚀 快速开始
---
### 1. 下载与运行
- **下载**: 前往 [Releases 页面](https://github.com/Justsenger/ExHyperV/releases/latest)下载最新版本。
- **运行**: 解压后直接运行 `ExHyperV.exe` 即可。
---
### 2. 构建 (可选)
1. 安装 [Visual Studio](https://visualstudio.microsoft.com/vs/)，并确保勾选 .NET 桌面开发。
2. 使用 GitHub Desktop 或 Git 克隆本仓库。
3. 使用 Visual Studio 打开 `/src/ExHyperV.sln` 文件，即可编译。

除此之外，您也可以直接下载 [.NET SDK](https://dotnet.microsoft.com/zh-cn/download) ,打开项目目录：
```pwsh
cd src
dotnet build
```

## 📖 技术文档

这部分内容将长期维护，根据 HyperV 相关文档以及开发实践编写而成，可能会存在问题。

---
### Hyper-V 简介
> [!NOTE]
> Hyper-V 是基于 Type-1 架构的高性能虚拟机管理软件（Hypervisor）。

当您开启Hyper-V功能后，宿主系统将变成属于根分区的一个具有特权的虚拟机。创建的虚拟机属于子分区，它们相互隔离，无法感知彼此的存在。

属于 Type-1 架构的虚拟化技术包括：Hyper-V、Proxmox (KVM)、VMware ESXi (VMkernel)、Xen 等，性能利用率大约在98%以上。

属于 Type-2 架构的虚拟化技术包括：VMware Workstation、Oracle VirtualBox、Parallels Desktop 等，性能利用率大约在90%~95%。

基于这样的事实，您可以将虚拟机看作一个个隔离的小房间，在里面运行具有潜在威胁的程序、测试系统功能、多开游戏或者其他用途，而不用担心弄糟宿主系统（1.PCIe 直通中不支持 FLR 的情况除外，虚拟机重启会连带宿主重启。2.具有横向渗透能力的病毒也不在此列，请注意网络安全）。

您可以通过控制面板开启/关闭 HyperV 功能，或一行简单的命令 Powershell 并按 Y 确认（需要专业版或服务器版），重启后vmms.exe、vmcompute.exe以及vmmem等进程将在后台持续运行，同时开始菜单将出现 HyperV 管理器图标。
```
Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
```
---
### 调度器
> [!NOTE]
> 调度器用于协调如何将物理处理器的 CPU 时间分配给虚拟机处理器。

Hyper-V 具有三种调度器：Classic / Core / Root ，分别叫经典调度器、核心调度器和根调度器。可以分为两类：手动挡（Classic、Core）和自动挡（Root）。Core可以视作Classic的变种，提高了安全性，但是会降低部分场景下的性能。

Classic 调度器的出现时间可以追溯到 Windows Server 2008，是基于传统时间片轮换的公平分配原则，将虚拟机的处理器时间随机分给宿主所有可用的逻辑处理器；在宿主资源空闲的情况下，更可能分配到不同物理核心的逻辑处理器，而不是超线程，从而获取更好的性能。

Core 调度器出现时间稍晚，自 Windows Server 2016 以及 Windows 10 Build 14393推出，目的在于缓解侧信道攻击，即使是宿主资源空闲的情况下，也更倾向于分配同一个物理核心的两个线程，而不是更多物理核心。这样的策略有助于提高安全性和虚拟机隔离，但是会显著降低宿主资源空闲时虚拟机能分配到的CPU性能。从 Windows Server 2019 起，Windows Server 将默认使用核心调度器。 

Root 调度器发布于Windows 10 Build 17134，它会收集工作负荷 CPU 使用情况的指标，自动作出调度决策，对于大小核的CPU架构来说非常适合。从 Build 17134 起，专业版Windows Hyper-V 将默认使用 Root 调度器。

系统类型与调度器的类型无关，可以任意切换，重启宿主后生效。

---
### 处理器（vCPU）
> [!NOTE]
> 虚拟机向宿主申请逻辑处理器执行时间的调度能力。

#### 计算资源

##### 核心数

通常设定为2、4、8、16等偶数。

增加 vCPU 会显著提升并行任务的处理速度，但过多不必要的 vCPU 可能为 Hypervisor 带来调度压力。

若所有虚拟机的核心总数超过宿主物理逻辑核心数（超售），需要即时响应的应用会受到极大影响。

##### 预留

为该虚拟机提供的执行时间下限百分比。预留值=预留*核心数。

##### 限制

为该虚拟机提供的执行时间上限百分比。限制值=限制*核心数。

##### 权重

该虚拟机在 CPU 执行时间竞争中的优先级，范围是0~10000。

#### 高级功能

##### 主机资源保护

开启后对虚拟机通过 VMBus 通信的 I/O 请求进行监测，出现中断风暴等异常行为时降低 CPU 执行时间分配，以防止影响根分区的宿主系统。普通用户无需开启。

##### 嵌套虚拟化

开启后透传 CPU 的 VT-x/AMD-V 指令集扩展，允许在 Hyper-V 虚拟机里再运行虚拟机，将略微增加 CPU 虚拟化开销。

虚拟机开启嵌套虚拟化以及 HyperV 功能后，虚拟机的任务管理器将显示L1/L2/L3缓存拓扑，并不再标记为“虚拟机：是”，对于过虚拟化有一定帮助。

<div align="center">
<img width="616" height="532" alt="image" src="https://github.com/user-attachments/assets/00c838f1-91ef-42db-bf21-34c5b49b08b9" />
</div>


##### 迁移兼容性

开启后将屏蔽 CPU 的高级指令集（如 AES, AVX, AVX2, FMA3, SHA, SSE4A 等），便于在不同硬件的宿主上进行实时迁移。普通用户无需开启。

##### 旧系统兼容性

开启后将大幅精简 CPU 指令集，有利于运行 Windows 7 甚至更早的操作系统，不利于运行现代操作系统。

##### 虚拟机 SMT

开启后虚拟机能感知到它的 vCPU 是成对出现的逻辑核心，有助于虚拟机内部的操作系统内核更好地进行 L1/L2 缓存优化和进程调度。

##### 隐藏 Hypervisor 标识 # 即将删除

这是一个早期开关，功能尚不明朗。位于[Msvm_ProcessorSettingData/HideHypervisorPresent](https://github.com/Justsenger/HyperV-WMI-Documentation/blob/main/docs/Msvm_ProcessorSettingData.md)。

指示 Hyper-V 是否应向嵌套客体报告存在虚拟机监控程序。

##### 暴露架构性能监控单元

开启后透传 CPU 的硬件计数器，允许虚拟机里的开发工具直接访问物理 CPU 的性能监测硬件。

##### 暴露频率监视寄存器

开启后允许虚拟机操作系统读取物理处理器的真实频率，默认是开启的。

##### 禁用侧信道攻击缓解 

开启后关闭对 Spectre、Meltdown等漏洞的软件补丁，小幅提升性能，虚拟化安全性下降。

##### 启用插槽拓扑 

开启后将为虚拟机模拟多个物理插槽，对于多个物理处理器的系统或许有用。

##### CPU 绑定

CPU 绑定实现基于 CPU 组（经典调度器+核心调度器）+ 进程亲和性（根调度器），允许您将vCPU强制锁定到指定的核心上。

最好的实践是4个vCPU绑定到4个核心。2个vCPU绑定到4个核心将发生随机漂移，4个vCPU绑定到2个核心将发生排队，以此类推。

如果您发现调度器的表现很糟糕，或者对于 Intel 的混合架构和 AMD 的多芯片架构有任何顾虑，请尝试使用此功能。

---
### 内存
> [!NOTE]
> 虚拟机能支配的运行内存容量的能力。

#### 计算资源

##### 启动内存

虚拟机在开机时必须占用的物理内存量。若宿主空闲内存不够，可能会启动失败。

若操作系统支持热调整，可在运行时修改此数值从而增大或缩减内存分配上限。

##### 内存权重

当宿主机物理内存不足时，多个虚拟机之间争抢内存的优先级。

##### 动态内存

允许 Hypervisor 根据虚拟机内部的实际需求，实时伸缩分配的内存量。

最小内存不能大于启动内存。虚拟机最多能获得的物理内存不会最大内存或宿主机物理内存的上限。

当使用 GPU-PV 或 Pcie 直通 时，启动内存必须和最小内存相同以确定内存地址映射关系。

#### 高级功能

##### 内存页大小

决定虚拟内存与物理内存映射时的“颗粒度”，可选4K、2M、1G。该选项需要宿主系统版本大于26100，并且虚拟机配置版本大于12.0，有利于大型数据库或高性能计算任务。

##### 内存加密

开启后将利用硬件特性（AMD SEV 或 Intel TDX）对内存数据进行实时加密，即使是宿主机也无法读取内存数据，开启后会带来轻微的内存延迟和 CPU 负载增加。


---
### 存储
> [!NOTE]
> 虚拟机能访问的本地存储介质。

分为虚拟文件和物理设备。虚拟文件可选择vhdx、vhd和iso等格式。物理设备可选择宿主机上可脱机的硬盘或光驱进行直通，部分 USB 存储介质可能不支持。

监控界面可查看实时读写速率以及容量变化。左侧的数字是文件大小，右侧的数字是容量上限。

#### 插槽配置

Hyper-V 要求您将虚拟文件或物理设备挂载到 IDE 控制器或 SCSI 控制器上来供虚拟机访问。ExhyperV 已经将这个操作简化为自动分配，允许您只关心媒体来源而无需关心插槽分配。

如果您尝试理解复杂的插槽逻辑，请参考以下规则：

· 对于运行中的1代虚拟机，IDE 控制器无法被卸载，但是 ISO 可以弹出和插入。

· 对于1代虚拟机，ISO 只能插入 IDE 控制器。1代虚拟机只能从 IDE 控制器的介质上启动。

· 对于2代虚拟机，SCSI 控制器及里面的存储介质随时都可以弹出和移除，因此请小心操作。

· 对于1代虚拟机，总共有2个 IDE 控制器x2+4个 SCSI 控制器x64=260个位置可以使用。对于2代虚拟机，总共有 4 个 SCSI 控制器x64=256个位置可以使用。

#### 媒体设置

##### 来源

选择虚拟文件或者可用的物理设备。部分 USB 存储介质由于无法脱机，不会出现在可用列表。

##### 虚拟文件

###### 类型为硬盘

当磁盘类型为动态磁盘，初始值为一个很小的值（取决于块大小和容量而生成的块分配表）而不是容量大小，会逐渐增大。

当磁盘类型为固定磁盘，初始值即为容量大小且不会变化，读写效率更高。

当磁盘类型为差异磁盘，需要指定一个动态磁盘/固定磁盘的虚拟硬盘并继承它的一切参数。

扇区格式：512n、512e（默认）、4kn。对应的物理扇区大小和逻辑扇区大小分别为：512/512、4096/512、4096/4096，普通用户保持默认即可。

块大小：最小存储单位。块越大，读写效率越高，空间利用率越低；块越小，读写效率越低，空间利用率越高。

###### 类型为光驱

利用 DiscUtils 创建，可实现将指定文件夹快速打包并挂载到虚拟机，采用ISO 9660 标准并启用了Joliet 扩展。

单文件不能超过 4GB，总大小不能超过8TB，ISO卷标不能超过32个字符，路径深度不能超过8层，单个文件或文件夹的名称不能超过 103 个字符，文件在镜像内的绝对路径总长度不能超过 240 个字符。

由于限制条件很多，这部分后续可能会考虑使用 UDF 标准替代。此功能用于弥补 Hyper-V 对于快速创建 ISO 的劣势。

---
### 显卡
> [!NOTE]
> 虚拟机通过 GPU-PV 技术访问宿主机物理显卡的能力。

GPU-PV 是一种半虚拟化技术，它允许多个虚拟机共享使用物理 GPU 的计算能力，而无需 PCIe 直通。GPU-PV 仍然在不断进化，WDDM 版本越新，功能越全。宿主和虚拟机尽量使用最新的系统版本。

· 监控界面可查看该虚拟机上所有 GPU-PV 分区的图形引擎利用率，包含四个常用引擎：3D 渲染、数据复制、视频编码和视频解码。

· 目前，Hyper-V 无法有效限制每个虚拟机使用的 GPU 资源。`Set-VMGpuPartitionAdapter` 中的参数并不生效 ([相关讨论](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298))。因此，本工具暂不提供资源分配功能。

· GPU-PV 创建的虚拟设备虽然能调用物理 GPU，但并未完整继承其硬件特征和驱动细节。某些依赖特定硬件ID或驱动签名的软件/游戏可能无法运行。


#### 系统要求

宿主和虚拟机必须是如下版本才能启用此能力。

- Windows 10 （Build 17134+）
- Windows 11
- Windows Server 2019 
- Windows Server 2022
- Windows Server 2025

· 虚拟机必须是大于 9.0 的配置版本才能分配 GPU-PV 显卡。不限制虚拟机代数。

· 启用了 GPU-PV 的虚拟机不支持检查点功能。

· 启用了 GPU-PV 的显卡必须存在于宿主机，不能同时用于 PCIe 直通。

· 从同一张显卡获取的多个 GPU-PV 显卡分区不能提供超过物理上限的算力。

· 虚拟机可以获取来自不同显卡的 GPU-PV 显卡分区。

· 可能存在[内存泄露问题](https://github.com/jamesstringer90/Easy-GPU-PV/issues/446)，建议将宿主机系统版本更新到 `26100.4946`以上。

#### WDDM 版本与 GPU-PV 功能
> WDDM (Windows Display Driver Model) 版本越高，GPU-PV 功能越完善。建议宿主和虚拟机都使用最新的 Windows 版本。

| Windows 版本 (Build) | WDDM 版本 | 虚拟化相关功能更新 |
| :--- | :--- | :--- |
| 17134 | 2.4 | 首次引入GPU 半虚拟化技术。 |
| 17763 | 2.5 | 优化宿主与虚拟机间的资源管理与通信。 |
| 18362 | 2.6 | 提升显存管理效率，优先分配连续物理显存。 |
| 19041 | 2.7 | 虚拟机设备管理器可正确识别物理显卡型号。 |
| 20348 | 2.9 | 开始支持 Linux 虚拟机及 WSL2。|
| 22000 | 3.0 | 支持 DMA 重映射，突破 GPU 内存地址限制。 |
| 22621 | 3.1 | UMD/KMD 内存共享，减少数据复制，提升效率。 |
| 26100 | 3.2 | 虚拟机任务管理器可查看 GPU 性能计数。引入 GPU 实时迁移、WDDM 功能查询等新特性。 |

#### GPU-PV 部分显卡兼容性列表 (使用Gpu Caps Viewer+DXVA Checker测试)

| 品牌 | 型号 | 架构 | 识别 | DirectX 12 | OpenGL | Vulkan | Codec | CUDA/OpenCL | 备注 |
| :--- | :--- | :--- | :--- |:--- | :--- | :--- | :--- | :--- | :--- |
| **Nvidia** | RTX 4090 | Ada Lovelace | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | RTX 4080 Super | Ada Lovelace | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | RTX 2080 Super | Turing | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | GTX 1050 | Pascal | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Nvidia** | GT 210 | Tesla | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 不支持 |
| **Nvidia** | Tesla V100-SXM2-16GB | Volta | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 启动会导致宿主崩溃 #95 |
| **Intel**| Iris Xe Graphics| Xe-LP | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | 硬件识别残缺| 
| **Intel**| A380 | Xe-HPG | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | 硬件识别残缺|
| **Intel**| UHD Graphics 730 | Xe-LP | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | 硬件识别残缺|
| **Intel**| UHD Graphics 620 Mobile | Generation 9.5 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | 硬件识别残缺|
| **Intel**| HD Graphics 530 | Generation 9.0 | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 不支持 |
| **AMD** | Radeon Vega 3 | GCN 5.0 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | 硬件识别残缺|
| **AMD** | Radeon 8060S | RDNA 3.5 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | 硬件识别残缺 |
| **AMD** | Radeon 890M | RDNA 3.5 | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 启动会导致宿主崩溃 |
| **Moore Threads** | MTT S80 | MUSA | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 不支持 |
| **Qualcomm** | Qualcomm(R) Adreno(TM) X1-85 GPU | Adreno X1 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | 硬件识别残缺|
| **Qualcomm** | Qualcomm(R) Adreno(TM) 8cx Gen 3 | Adreno | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | 硬件识别残缺|

#### 如何从虚拟机输出画面？

GPU-PV 模型中，虚拟机的 GPU-PV 显卡作为“渲染设备”，还需要搭配一个“显示设备”来输出画面。有以下三种方案：

1.  **Microsoft Hyper-V 视频 (默认)**
    - **优点**: 开箱即用，兼容性良好。
    - **缺点**: 分辨率最高 1080p，刷新率约 60Hz。

2.  **间接显示驱动 + 串流 (推荐)**
    - 安装 [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) 创建一个高性能的虚拟显示器。
    - 使用 Parsec, Sunshine, 或 Moonlight 等串流软件，配对并设置好开机启动，在关闭RDP以及其他远程桌面的情况下连接，从而获得高分辨率、高刷新率的流畅体验。
    - ![Sunshine+PV 示例](https://github.com/user-attachments/assets/e25fce26-6158-4052-9759-6d5d1ebf1c5d)

> [!NOTE]
> 这里是一份简单的 Sunshine + GPU-PV 操作指南。

· 将 GPU-PV 添加到虚拟机并正常工作。

· 将 [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)  安装到虚拟机，并确保监视器出现了“Generic Monitor (VDD by MTT)”，通过显示设置设定好分辨率和刷新率。

· 将 Sunshine 安装到虚拟机，并将 Moonlight 与 Sunshine 配对。

· 将 Sunshine 设定为以管理员权限开机自动启动。

· 重启虚拟机，不要打开控制台或者任何远程桌面。

· Moonlight 连接虚拟机，如果一切顺利，此时画面和声音都将传输到 Moonlight 客户端。

3.  **USB 显卡 + GPU-PV**
    - **思路**: 通过 PCIe 直通分配一个 USB 控制器给虚拟机，再连接一个 USB 显卡（如基于 [DisplayLink DL-6950](https://www.synaptics.com/cn/products/displaylink-graphics/integrated-chipsets/dl-6000) 或 [Silicon Motion SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html) 芯片的产品）作为显示设备。
    - **状态**: 此方案与大显存显卡可能存在内存资源冲突问题，还需要更多测试。

#### 配置流程

##### 环境准备

向宿主系统环境添加注册表，禁用安全策略等避免分配 GPU-PV 后无法启动虚拟机。

##### 电源检查

下一步的系统优化需要关闭虚拟机电源才能继续。

##### 系统优化

探测宿主物理寻址能力，自动配置 MMIO 空间，开启写合并缓存。

查询物理寻址能力：

``` $n="CheckMMIO_$(Get-Random)";New-VM $n -Gen 2 -NoVHD|Out-Null;Set-VM $n -AutomaticCheckpointsEnabled $false|Out-Null;$m=[WMI](gwmi -n root\virtualization\v2 Msvm_VirtualSystemManagementService).Path;@(1073741824,268435456,134217728,67108864,16777216,4194304,1048576,524288,262144,131072,65536,34816)|%{$v=$_;$p=(gwmi -n root\virtualization\v2 Msvm_VirtualSystemSettingData|? ElementName -eq $n).Path;if($p){$s=[WMI]$p;$s.HighMmioGapBase=$v-1024;$s.HighMmioGapSize=1024;$m.ModifySystemSettings($s.GetText(2))|Out-Null;try{Start-VM $n -EA Stop;$g=[math]::Ceiling($v/1024);$b=[math]::Log($v,2)+20;$o=if($g-ge1024){"$([math]::round($g/1024,1))TB"}else{"$g GB"};"$b bit / $o";Stop-VM $n -TurnOff -F;while((Get-VM $n).State -ne 'Off'){sleep 1};Remove-VM $n -F;break}catch{}}};Get-VM "T_*" -EA 0|Remove-VM -F -EA 0
```

##### 分配显卡

为选择的显卡创建 GPU-PV 分区并分配到虚拟机。

##### 驱动安装

这是一个可选项，需要添加多张显卡时，可以取消勾选以免每次都导入驱动。

对于Windows虚拟机，将全量注入宿主驱动文件夹到虚拟机指定分区，如果是 Nvidia 显卡还会添加额外修复，例如注册表。同时，会为虚拟机的 System32 目录下创建某些驱动文件的链接文件，具体映射关系参考[drivermapping.md](https://github.com/Justsenger/ExHyperV/blob/main/doc/drivermapping.md).

对于Linux虚拟机，会执行 SSH 自动化流程进行模块编译和驱动安装，兼容列表之外的系统或内核还需更多测试。

![Linux&Blender](https://github.com/Justsenger/ExHyperV/blob/main/img/Linux.png)

已知兼容性：
| 系统 | 内核版本 | Dxgkrnl | CUDA | Vulkan | OpenGL | Codec |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | 
| Ubuntu 24.04  | 6.14.0-36-generic | ❌ | \ | \ | \ | \ |
| Ubuntu 22.04  | 6.8.0-87-generic | ✅ | ✅ | ✅ | ✅ | ✅ |
| Arch Linux | 6.6.119-1-lts66 | ✅ | ✅ | 未测试 | 未测试 | 未测试 |
| fnOS 0.9.2 | 6.12.18-trim | ❌ | \ | \ | \ | \ |

---
### 网络
> [!NOTE]
> 虚拟机在数据链路层通过交换机访问网络的能力。

#### VLAN 与隔离
> [!NOTE]
> VLAN（Virtual Local Area Network，虚拟局域网）是一种将物理 LAN 在逻辑上划分为多个独立广播域的通信技术。

接入模式：将虚拟机的网络适配器分配给单个 VLAN。当VLAN ID 等于 0 时，代表关闭 VLAN 功能。

VLAN ID 可以设定的范围是 1-4094 ，设定特定 VLAN ID 后，该虚拟机只能与该虚拟交换机中中处于相同 VLAN ID 的设备进行二层通信。

中继模式：允许虚拟机的网络适配器同时传输多个 VLAN 的流量。

· 本征 VLAN ID：用于处理不带标签的默认流量。

· 允许列表：指定允许通过该网卡的 VLAN ID 范围（如：10, 20-30）。

专用模式：在同一个 VLAN 内部实现进一步的二层隔离，常用于多租户或主机托管环境。

· 主 ID (Primary ID)：虚拟机所属的公共 VLAN 标识。
· 辅助 ID (Secondary ID)：用于实现细分隔离的内部标识。

三种类型：

· 隔离 (Isolated)：虚拟机只能与网关通信，同一 VLAN 内的其他虚拟机也无法互访。

· 社区 (Community)：同一社区内的虚拟机可以互访，但无法与其他社区通信。

· 混杂 (Promiscuous)：可以与该主 VLAN 下的所有虚拟机通信（通常分配给网关或防火墙）。

#### 流量控制 (QoS) 
> [!NOTE]
> 用于管理虚拟机的带宽分配，防止单个虚拟机占满网络通道。

上限：最高限速。在网络空闲时，该虚拟机的带宽上限。

下限：最低保障。在网络繁忙时，系统会优先预留给该虚拟机的带宽。

#### 硬件加速
> [!NOTE]
> 将原本需要宿主机 CPU 处理的工作交给物理网卡硬件完成，从而提升性能。

单根 I/O 虚拟化：让虚拟机跳过虚拟交换机，直接“连接”在物理网卡的硬件资源上。此功能需要物理网卡支持，同时创建虚拟交换机时需勾选启用SR-IOV。服务器级网卡且有高性能需求就开。

虚拟机队列：利用物理网卡的硬件过滤功能，把发给不同虚拟机的数据包提前分类，直接投递到虚拟机的内存中。万兆网卡开，千兆网卡或博通网卡建议关。

IPsec任务卸载：将网络传输中的加密、解密计算（IPsec 协议）从 CPU 转移到网卡硬件中处理。默认关掉即可，几乎没用。

#### 安全与监控
> [!NOTE]
> 增强虚拟网络的安全隔离以及流量监控。

允许 MAC 地址欺骗：允许虚拟机更改其网卡的 MAC 地址。通常用于嵌套虚拟化同时启用，或某些需要伪造 MAC 的软路由系统的场景。如果不开启，虚拟机只能使用分配给它的唯一固定 MAC。

DHCP 防护：防止该虚拟机私自充当 DHCP 服务器，避免网络中其他设备从这台虚拟机获取到错误的 IP 地址而导致断网。

路由器防护：防止该虚拟机伪装成网关，恶意劫持或欺骗内网流量。

端口镜像：将发送组的虚拟机的流量复制到接收组的虚拟机。

加入发送组：该网络适配器的流量将被发送到接收组的网络适配器。

加入接收组：该网络适配器将接收来自发送组的网络适配器的流量。

风暴阈值：限制该虚拟机每秒允许发送的广播/多播数据包数量。设定为 0 表示不限制，建议设定为500-1000。

---
### PCIe 直通
> [!NOTE]
> PCIe 直通实际上是 DDA（离散设备分配）的实现，为了便于理解，将名称修正为 PCIe 直通。

PCIe 直通允许将一个完整的 PCIe 设备（显卡、网卡、声卡、USB 控制器等）从宿主机移除并直接分配给虚拟机。

注意，此功能必须开启 BIOS 里面的 IOMMU 开关，并且需要服务器系统环境。

#### 可分配设备

PCIe 直通以 PCIe 设备为单位查找可分配设备。如果设备未显示在列表中，意味着它不属于独立的 PCIe 设备，您需要尝试分配其更上一级的 PCIe 控制器。

#### 虚拟机系统

通常使用 Windows 10/11以上，Linux 还需进一步测试。

#### 宿主系统要求

- Windows Server 2019 
- Windows Server 2022
- Windows Server 2025

**黑魔法**：如果您想在非 Server 系统上使用 PCIe 直通，可以尝试切换系统版本的开关，将标识位从 WinNT 变为 ServerNT，从而欺骗 Hypervisor。此开关目前仅对以下版本生效。

生效的版本：专业教育版、专业工作站、专业单语言、专业中文、教育版、企业 LTSC、企业版 G、企业版多会话、IOT 企业版

无效的版本：专业版 家庭版 企业版 、家庭单语言版


#### PCIe 设备的三种状态

1.  **主机态**: 设备正常挂载到宿主系统，只能被宿主使用。
2.  **卸除态**: 设备已从宿主卸载 (`Dismount-VMHostAssignableDevice`)，但未分配给虚拟机。此时设备在宿主设备管理器中不可用，需要重新挂载到宿主或分配给虚拟机。
3.  **虚拟态**: 设备已成功分配给虚拟机。

#### PCIe 部分显卡兼容性列表
> 兼容性表现需要在虚拟机中安装驱动后才能确认。欢迎通过 [Issues](https://github.com/Justsenger/ExHyperV/issues) 分享您的测试结果！

| 品牌 | 型号 | 架构 | 启动 | 功能层复位 (FLR) | 物理显示输出 |
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
| **Nvidia** | Tesla V100-SXM2-16GB | Volta | ✅ | ✅ | ❌ |
| **Intel** | DG1 | Xe-LP | ✅ | ❌ | [特定驱动](https://www.shengqipc.cn/d21.html) ✅ |
| **Intel** | A380 | Xe-HPG | Code 43 ❌ | ✅ | ❌ |
| **Intel**| UHD Graphics 620 Mobile | Generation 9.5 | 无法直通❌ | ❌ | ❌ | 
| **Intel**| HD Graphics 610 | Generation 9.5 | 无法直通❌ | ❌ | ❌ | 
| **Intel**| HD Graphics 530 | Generation 9.0 | 无法直通❌ | ❌ | ❌ | ❌ |
| **AMD** | RX 580 | GCN 4.0 | Code 43 ❌ | ✅ | ❌ |
| **AMD** | Radeon Vega 3 | GCN 5.0 | Code 43 ❌ | ❌ | ❌ |
| **Qualcomm** | Qualcomm(R) Adreno(TM) X1-85 GPU | Adreno X1 | 不支持❌ | ❌ | ❌ |
| **Qualcomm** | Qualcomm(R) Adreno(TM) 8cx Gen 3  | Adreno | 不支持❌ | ❌ | ❌ |
 

- **启动**: 分配到虚拟机后能否成功安装驱动并被识别。代码 43 说明驱动层面不允许显卡在虚拟机内工作。
- **功能层复位 (FLR)**: 若不支持，重启虚拟机会连带宿主机重启。
- **物理显示输出**: 虚拟机能否通过显卡的物理接口（HDMI/DP）输出画面。
---
### 虚拟交换机
> [!NOTE]
> 显示宿主上 Hyper-V 交换机的拓扑结构以及连接状况。

ExHyperV 将 HyperV 的三种交换机类型（外部、内部、私有）重新定义为三种网络模式（桥接、NAT、无上游），其中 NAT 模式集成了 ICS 功能。

### 网络模式

桥接模式：宿主和虚拟机将连接在同一个外部虚拟交换机下面，由指定物理网卡提供出口网络。

NAT 模式：宿主和虚拟机将连接在同一个内部虚拟交换机下面，宿主通过 ICS 将物理网卡的网络共享给虚拟机，并负责上游出口、NAT 转换以及 DHCP 。只能存在一个NAT模式的网络。

无上游：宿主和虚拟机将连接在同一个内部虚拟交换机下面，没有上游网络。宿主可以选择不连接到该交换机，此时虚拟交换机自动切换为私有交换机。

· Default Switch 属于独特的交换机类型，工作方式类似 NAT 模式，会根据跃点自动切换上游网络。



## 🤝 贡献
欢迎任何形式的贡献！
- **测试与反馈**: 帮助我们完善兼容性列表或测试潜在的Bug。
- **报告 Bug**: 通过 [Issues](https://github.com/Justsenger/ExHyperV/issues) 提交您遇到的问题。
- **代码贡献**: Fork 项目并提交 Pull Request。

## ❤️ 支持项目

如果你觉得这个项目对你有帮助，欢迎考虑赞助我！

[![Ko-fi](https://img.shields.io/badge/Sponsor-Ko--fi-F16061?style=for-the-badge&logo=ko-fi&logoColor=white)](https://ko-fi.com/saniye) &nbsp;&nbsp; [![爱发电](https://img.shields.io/badge/Sponsor-爱发电-633991?style=for-the-badge&logo=afdian&logoColor=white)](https://afdian.com/a/saniye)

## 🎖️ 致谢名单

非常感谢所有赞助者的支持！你们的慷慨资助是 ExHyperV 持续进化的核心动力。

### 👑 神
<a href="https://afdian.com/a/saniye"><img src="https://img.shields.io/badge/神-User--1A4FE-black?style=for-the-badge&logo=kingstontechnology&logoColor=FFD700&labelColor=black&color=FFD700" width="300px" /></a> <a href="https://afdian.com/a/saniye"><img src="https://img.shields.io/badge/神-ANONYMOUS-333333?style=for-the-badge&logo=cyberdefenders&logoColor=C0C0C0&labelColor=black&color=C0C0C0" width="300px" /></a>

---

### 🌌 传说
![](https://img.shields.io/badge/传说-USER--09837-24292e?style=for-the-badge&logo=starship&logoColor=BE64FF&labelColor=24292e&color=BE64FF)
<a href="https://github.com/PIKACHUIM"><img src="https://img.shields.io/badge/传说-PIKACHUIM-24292e?style=for-the-badge&logo=starship&logoColor=BE64FF&labelColor=24292e&color=BE64FF" /></a> 

---

### 🏅 达人
![](https://img.shields.io/badge/达人-User--6b017-24292e?style=for-the-badge&logo=expertsexchange&logoColor=FFBF00&labelColor=24292e&color=FFBF00)
![](https://img.shields.io/badge/达人-死都不怕还怕活着？-24292e?style=for-the-badge&logo=expertsexchange&logoColor=FFBF00&labelColor=24292e&color=FFBF00)
---

### 🔹 初心
![](https://img.shields.io/badge/初心-激进娘-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/初心-User--FaTM-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
<a href="mailto:miooiio@outlook.jp"><img src="https://img.shields.io/badge/初心-User--53EDF-0078D4?style=flat-square&logo=hyperledger&logoColor=white" /></a> 
![](https://img.shields.io/badge/初心-User--56652-0078D4?style=flat-square&logo=hyperledger&logoColor=white)
![](https://img.shields.io/badge/初心-User--2b965-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/初心-苏芸晴或i-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/初心-tiwatezhanshen-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
![](https://img.shields.io/badge/初心-User--_tpuJ-0078D4?style=flat-square&logo=hyperledger&logoColor=white) 
