# ExHyperV

<div align="center">

**一款图形化的 Hyper-V 高级功能管理工具，能让凡人也能轻松玩转 DDA、GPU-P 等高阶功能。**

</div>

<p align="center">
  <a href="https://github.com/Justsenger/ExHyperV/releases/latest"><img src="https://img.shields.io/github/v/release/Justsenger/ExHyperV.svg?style=flat-square" alt="最新版本"></a>
  <a href="https://github.com/Justsenger/ExHyperV/releases"><img src="https://img.shields.io/github/downloads/Justsenger/ExHyperV/total.svg?style=flat-square" alt="下载量"></a>
  <a href="https://t.me/ExHyperV"><img src="https://img.shields.io/badge/discussion-Telegram-blue.svg?style=flat-square" alt="Telegram"></a>
  <a href="https://github.com/Justsenger/ExHyperV/blob/main/LICENSE"><img src="https://img.shields.io/github/license/Justsenger/ExHyperV.svg?style=flat-square" alt="许可证"></a>
</p>

[English](https://github.com/Justsenger/ExHyperV) | **中文**

---

ExHyperV 深入研究了微软官方文档和 [Easy-GPU-PV](https://github.com/jamesstringerparsec/Easy-GPU-PV) 项目，旨在修复和完善现有方案，为用户提供一个图形化的、易于使用的 Hyper-V 高级功能配置工具。

由于个人时间和精力有限，项目可能存在未经测试的场景。如果您在使用中遇到任何软件/游戏兼容性问题，欢迎通过 [Issues](https://github.com/Justsenger/ExHyperV/issues) 提出！

## ✨ 功能概览

![主界面](https://github.com/Justsenger/ExHyperV/blob/main/img/01.png)
<details>
<summary>点击查看更多界面截图</summary>

![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/02.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/03.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/04.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/05.png)
![功能](https://github.com/Justsenger/ExHyperV/blob/main/img/various.png)
*<p align="center">工具扩展了显卡识别能力，但具体功能启用情况取决于硬件本身。</p>*

</details>

## 🚀 快速开始

### 1. 系统要求

#### DDA (离散设备分配)
- Windows Server 2019 / 2022 / 2025

#### GPU-P (GPU 分区/半虚拟化)
- Windows 11
- Windows Server 2022
- Windows Server 2025

> **注意**: 本工具要求宿主机系统版本不低于 **Build 22000**。因为旧版系统中的 `Add-VMGpuPartitionAdapter` 命令缺少 `InstancePath` 参数，无法精确指定显卡，可能导致混乱。为了简化操作，请确保您的宿主系统已更新。

### 2. 下载与运行
- **下载**: 前往 [Releases 页面](https://github.com/Justsenger/ExHyperV/releases/latest)下载最新版本。
- **运行**: 解压后直接运行 `ExHyperV.exe` 即可。

### 3. 构建 (可选)
1. 安装 [Visual Studio 2022](https://visualstudio.microsoft.com/vs/)，并确保已安装 .NET桌面开发（C# 和 WPF）工作负载。
2. 克隆本仓库。
3. 使用 Visual Studio 打开 `/src/ExHyperV.sln` 文件，即可编译。

## 📌 重要提示与限制

- 建议为虚拟机分配**固定大小**的运行内存。
- 本工具支持**第一代**和**第二代**虚拟机。
- GPU-PV不支持**检查点**功能。
- 一张物理显卡在同一时间**只能用于 DDA 或 GPU-P**，不能两者共用。
- 一个虚拟机可以**同时使用 DDA 和 GPU-P** (例如，DDA 直通一个设备，同时使用另一张卡的 GPU-P 功能)。
- 一张物理显卡可以为**单个虚拟机**划分出多个 GPU-P 分区，但总性能不变。
- 一个虚拟机可以同时使用来自**多张不同物理显卡**的 GPU-P 分区。

---

## 核心功能详解

### Ⅰ. DDA (离散设备分配)

DDA (Discrete Device Assignment) 允许你将一个完整的 PCIe 设备（如显卡、网卡、USB 控制器）直接分配给虚拟机。

- **设备兼容性**: 如果设备未显示在列表中，意味着它无法被独立分配，您需要尝试分配其更上一级的 PCIe 控制器。
- **显卡支持**:
    - **Nvidia**: 通常工作良好。
    - **AMD/Intel**: 未经充分测试。AMD 消费级显卡可能因不支持 [Function-Level Reset (FLR)](https://www.reddit.com/r/Amd/comments/jehkey/will_big_navi_support_function_level_reset_flr/) 而存在问题。欢迎提供测试反馈！
- **虚拟机系统**: 推荐使用 Windows，版本无特殊限制。Linux 未经测试。

#### DDA 设备状态解析
> 理解设备的三种状态对于排查问题至关重要。

1.  **主机态 (Host)**: 设备正常挂载于宿主系统，可被宿主使用。
2.  **卸除态 (Dismounted)**: 设备已从宿主卸载 (`Dismount-VMHostAssignableDevice`)，但未成功分配给虚拟机。此时设备在宿主设备管理器中不可用，可通过本工具重新挂载到宿主或分配给虚拟机。
3.  **虚拟态 (Guest)**: 设备已成功挂载于虚拟机。

#### DDA 显卡兼容性列表 (持续更新中)
> 兼容性表现需要实际在虚拟机中安装驱动后才能确认。欢迎通过 [Issues](https://github.com/Justsenger/ExHyperV/issues) 分享您的测试结果！

| 品牌 | 型号 | 架构 | 启动 | 功能层复位 (FLR) | 物理显示输出 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Nvidia** | RTX 5090 | Blackwell 2.0 | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4090 | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4080 Super | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | RTX 4070 | Ada Lovelace | ✅ | ✅ | ✅ |
| **Nvidia** | GTX 1050 | Pascal | ✅ | ✅ | ✅ |
| **Nvidia** | GT 1030 | Pascal | ✅ | ✅ | ✅ |
| **Nvidia** | GT 210 | Tesla | ✅ | ✅ | ❌ |
| **Intel** | DG1 | Xe-LP | ✅ | ❌ | [特定驱动](https://www.shengqipc.cn/d21.html) ✅ |
| **Intel** | A380 | Xe-HPG | Code 43 ❌ | ✅ | ❌ |
| **AMD** | Radeon Vega 3 | GCN 5.0 | Code 43 ❌ | ❌ | ❌ |

- **驱动正常**: 分配到虚拟机后能否成功安装驱动并被识别。
- **功能层复位 (FLR)**: 若不支持，重启虚拟机会导致宿主机也重启。
- **物理显示输出**: 虚拟机能否通过显卡的物理接口（HDMI/DP）输出画面。

---

### Ⅱ. GPU-P (GPU Paravirtualization / GPU 分区)

GPU-P (或称 GPU-PV) 是一种半虚拟化技术，它允许多个虚拟机共享使用物理 GPU 的计算能力，而无需完整直通。

- **资源限制**: 目前 Hyper-V 原生无法有效限制每个虚拟机使用的 GPU 资源。`Set-VMGpuPartitionAdapter` 中的参数并不生效 ([相关讨论](https://github.com/jamesstringerparsec/Easy-GPU-PV/issues/298))。因此，本工具暂不提供资源分配功能。
- **驱动与兼容性**: GPU-P 创建的虚拟设备虽然能调用物理 GPU，但并未完整继承其硬件特征和驱动细节。某些依赖特定硬件ID或驱动签名的软件/游戏可能无法运行。

#### WDDM 版本与 GPU-P 功能演进
> WDDM (Windows Display Driver Model) 版本越高，GPU-P 功能越完善。建议宿主和虚拟机都使用最新的 Windows 版本。

| Windows 版本 (Build) | WDDM 版本 | 主要虚拟化功能更新 |
| :--- | :--- | :--- |
| 17134 | 2.4 | 首次引入基于 IOMMU 的 GPU 隔离。 |
| 17763 | 2.5 | 优化宿主与虚拟机间的资源管理与通信。 |
| 18362 | 2.6 | 提升显存管理效率，优先分配连续物理显存。 |
| 19041 | 2.7 | 虚拟机设备管理器可正确识别物理显卡型号。 |
| 20348 | 2.9 | 支持跨适配器资源扫描输出 (CASO)，降低延迟。 |
| 22000 | 3.0 | 支持 DMA 重映射，突破 GPU 内存地址限制。 |
| 22621 | 3.1 | UMD/KMD 内存共享，减少数据复制，提升效率。 |
| 26100 | 3.2 | 引入 GPU 实时迁移、WDDM 功能查询等新特性。 |

![WDDM 架构](https://github.com/Justsenger/ExHyperV/blob/main/img/WDDM_cn.png)

#### GPU-P 显卡兼容性列表 (使用Gpu Caps Viewer+DXVA Checker测试，持续更新中)

| 品牌 | 型号 | 架构 | 识别 | DirectX 12 | OpenGL | Vulkan | Codec | CUDA/OpenCL | 备注 |
| :--- | :--- | :--- | :--- |:--- | :--- | :--- | :--- | :--- | :--- |
| **Nvidia** | GTX 1050 | Pascal | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | |
| **Intel**| Iris Xe Graphics| Xe-LP | ⚠️ | ✅ | ✅ | ✅ | ✅ | ❌ | 硬件识别残缺| 
| **AMD** | Radeon Vega 3 | GCN 5.0 | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | 硬件识别残缺|
| **AMD** | Radeon 890M | RDNA 3.5 | ❌ | ❓ | ❓ | ❓ | ❓ | ❓ | 启动会导致宿主崩溃 |
| **Moore Threads** | MTT S80 | MUSA | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | 不支持GPU-PV |

#### 如何从虚拟机输出画面？

GPU-P 模式下，物理 GPU 作为“渲染设备”，需要搭配一个“显示设备”来输出画面。有以下三种方案：

1.  **Microsoft Hyper-V 视频 (默认)**
    - **优点**: 兼容性好，开箱即用。
    - **缺点**: 分辨率最高 1080p，刷新率低 (约 62Hz)。

2.  **间接显示驱动 + 串流 (推荐)**
    - 安装 [虚拟显示驱动](https://github.com/VirtualDrivers/Virtual-Display-Driver) 创建一个高性能的虚拟显示器。
    - 使用 Parsec, Sunshine, 或 Moonlight 等串流软件，获得高分辨率、高刷新率的流畅体验。
    - ![Sunshine+PV 示例](https://github.com/user-attachments/assets/e25fce26-6158-4052-9759-6d5d1ebf1c5d)

3.  **USB 显卡 + DDA (实验性)**
    - **思路**: 通过 DDA 直通一个 USB 控制器给虚拟机，再连接一个 USB 显卡（如基于 [DisplayLink DL-6950](https://www.synaptics.com/cn/products/displaylink-graphics/integrated-chipsets/dl-6000) 或 [Silicon Motion SM768](https://www.siliconmotion.com/product/cht/Graphics-Display-SoCs.html) 芯片的产品）作为显示设备。
    - **状态**: 作者正在研究此方案与大显存显卡共存时的冲突问题，目前不推荐普通用户尝试。

## ⚙️ 工作原理

为了简化配置，本工具会自动执行以下操作：
- **驱动注入**: 自动将宿主机中的 GPU 驱动 (`HostDriverStore`) 导入到虚拟机中。
- **驱动保护**: 将导入的驱动文件设置为“只读”，防止被意外修改或删除。
- **Nvidia 注册表修复**: 自动修改虚拟机中 Nvidia 相关的注册表项，将驱动路径指向 `HostDriverStore`，确保驱动被正确加载。

## 🤝 贡献
欢迎任何形式的贡献！
- **测试与反馈**: 帮助我们完善兼容性列表。
- **报告 Bug**: 通过 [Issues](https://github.com/Justsenger/ExHyperV/issues) 提交您遇到的问题。
- **代码贡献**: Fork 项目并提交 Pull Request。

## ❤️ 支持项目
如果你觉得这个项目对你有帮助，欢迎考虑赞助我，这能激励我持续进行维护和开发！

[![Sponsor](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://afdian.com/a/saniye)
