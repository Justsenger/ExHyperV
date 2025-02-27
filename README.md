# ExHyperV
一个提供DDA和GPU分区等HyperV虚拟机实验功能的GUI软件，基于WPF-UI开发，目的在于简化DDA分配设备的操作。
# 代码完成度：3/10
目前，已经完成DDA的功能开发，还需要一段时间测试才能把代码端上来，暂时只更新文档。
![主界面](https://github.com/Justsenger/ExHyperV/blob/main/img/%E4%B8%BB%E7%95%8C%E9%9D%A2.png)
下面是DDA的功能界面。
![DDA功能](https://github.com/Justsenger/ExHyperV/blob/main/img/DDA.png)
![DDA功能2](https://github.com/Justsenger/ExHyperV/blob/main/img/DDA2.png)
# DDA显卡兼容性（更新中）
如果您有更多的测试案例，请联系我！核实后将更新在下表，为大家提供更好的指导。
| 品牌 | 型号 | 直通 | 功能层复位（FLR） | VM显示输出 |
| -------- | -------- | -------- | -------- | -------- |
| Nvidia   | RTX 4070 |✔️ |✔️ | ✔️|
| Nvidia   | GT 1030 |✔️ |✔️ | ✔️|
| Nvidia   | GT 210 |✔️ | ✔️ | ✖️|
| Intel   |  Intel DG1 |✔️ | ✖️ | 特定驱动✔️|


