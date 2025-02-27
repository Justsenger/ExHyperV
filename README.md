# ExHyperV
一个提供DDA和GPU分区等HyperV虚拟机实验功能的GUI软件，基于WPF-UI开发，目的在于简化DDA分配设备的操作。
# 代码完成度：3/10
目前，已经完成DDA的功能开发，还需要一段时间测试才能把代码端上来，暂时只更新文档。
![主界面](https://github.com/Justsenger/ExHyperV/blob/main/img/%E4%B8%BB%E7%95%8C%E9%9D%A2.png)
下面是DDA的功能界面。
![DDA功能](https://github.com/Justsenger/ExHyperV/blob/main/img/DDA.png)
![DDA功能2](https://github.com/Justsenger/ExHyperV/blob/main/img/DDA2.png)
# DDA显卡兼容性（更新中）
如果您有更多的测试案例，请联系我！确定兼容性后将更新在下表，为各位选择显卡提供更好的指导。
1.识别：显卡分配到虚拟机后可被GPU-Z正常识别。
2.功能层复位（FLR）：N卡通常完备，AMD和Intel未经广泛测试，可能存在硬件缺陷。若不具备此功能，分配此显卡的虚拟机重启将导致宿主机重启。
| 品牌 | 型号 | 识别 | 功能层复位（FLR） | VM物理显示输出 |
| -------- | -------- | -------- | -------- | -------- |
| Nvidia   | RTX 4070 |✔️ |✔️ | ✔️|
| Nvidia   | GT 1030 |✔️ |✔️ | ✔️|
| Nvidia   | GT 210 |✔️ | ✔️ | ✖️|
| Intel   |  Intel DG1 |✔️ | ✖️ | ✔️特定驱动|
# DDA设备状态
设备共有三种状态：主机态、卸除态、虚拟态。
1.处于主机态时，设备正常安装在宿主系统；
2.处于卸除态（#已卸除）时，通常是由于执行了"Dismount-VMHostAssignableDevice"，然而由于各种原因没有正确分配到虚拟机，导致设备处于一个中间态，无法被宿主的设备管理器正常识别，可以在软件中选择挂载到宿主或者重新挂载到虚拟机。
3.处于虚拟态时，设备正常安装在虚拟机系统。

