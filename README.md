# 空中采矿机物流运输站 · Aerial Mining Logistics Station

> 一座建在行星轨道层上的超大型物流枢纽：底座钻孔直接开采脚下的星球，50 个泊位停靠星际货船，
> 22 座炮塔与半球护盾负责防御，底座的地热井自己发电。
>
> A station-grade logistics hub that also mines the planet it stands on. 12 belt ports,
> 50 vessel berths, 22 turrets, a half-sphere shield, and geothermal wells under the base.

---

## 这个 Mod 加了什么

**一座建筑，替代「物流塔 + 采矿机 + 地热发电机 + 防御塔」四件套。**

- **12 个传送带接口**：绕基座 12 边形每条边的中心各 1 个，径向朝外，传送带直接插进裙边套筒。
- **50 个星际泊位**：碟形站体外面套一圈双层停靠环，上下各 25 个，间距 6.3 m；
  每站 **50 艘星际货船 + 200 架物流运输机**（原版塔是 10 船 / 100 机）。
- **22 座激光炮塔**：碟顶 18 座（电浆炮，单发 900 / 1.1 s）+ 基座 4 座（激光，单发 160 / 0.22 s），
  全部带三叉重炮模型与开火特效（能量束 + 炮口闪光）。
  伤害吃原版 **升级 → 能量武器伤害**（每级 +10%），与游戏里其他炮塔同一口径。
- **43 m 半球能量护盾**：罩住整个碟形站体，样式照原版行星护盾。
- **底座钻孔**：裙边钻齿咬进地面，钻出 **6 口地脉井**（碎石环 + 井口法兰 + 井底熔岩），
  **24 条熔岩裂缝**从基座往外辐射，三根抽取管把地热抽回基座。
- **地热发电**：底座就是一台地热机组，功率 **6 MW × 地热强度**，取法与采样点与原版地热发电机一致，
  直接充进站内电池 —— 建在热的地方不接电缆也能自己转。
  再乘「矿物利用」倍率（每级 +15%，最高 ×4）。
- **全资源自动采集**：钻孔扫描本星球的 **矿脉 / 原油油渗 / 海洋（水、硫酸）**，
  自动占满 10 格采矿仓储并开始供应，站体窗口右侧那列就是「还剩多少 / 已存多少」。
- **20 卡槽 × 50 万**：原版站体窗口里，左边 10 格留给你自己配货，右边 10 格给自动采矿，互不打架。
- **第 10 格是燃料格**：手拿 **氘核燃料棒 / 反物质燃料棒 / 奇异湮灭燃料棒** 左键点它即可换棒
  （旧棒退回背包），一格叠 100 根，满功率输出 **100 MW**。

## 怎么拿到它

1. 科技页搜 **「空中采矿枢纽」**（Aerial Mining Hub），研究材料：
   电磁矩阵 ×2400 + 能量矩阵 ×2400 + 结构矩阵 ×2400。
2. 制造配方：**星际物流运输站 ×2 + 处理器 ×20 + 钛合金 ×40 + 空间翘曲器 ×2**，
   手工 10 秒一格。

## 安装

**前置（必须）：**

| 前置 | 版本 |
|---|---|
| BepInEx | 5.4.17+ |
| [LDBTool](https://thunderstore.io/c/dyson-sphere-program/p/xiaoye97/LDBTool/) | 3.0.3+ |
| [CommonAPI](https://thunderstore.io/c/dyson-sphere-program/p/CommonAPI/CommonAPI/) | 1.6.7+ |

**用 r2modman / Thunderstore Mod Manager：** 搜索本 Mod，Download with dependencies，Start modded。

**手动：** 把压缩包解到 `Dyson Sphere Program/BepInEx/plugins/AerialMiningLogisticsStation/`，
确认里面直接是 `DSPLogisticsHub.dll`。

## 站体窗口怎么用

打开站体的方式与原版物流塔完全一样（点站体）。

- **左列 10 格**：供应 / 需求 / 容量，和原版物流塔一样操作。
- **右列**：只读的「当前采矿」表格 —— 每种资源的剩余储量、站内储量、占用地表面积。
- **第 10 格（左列最下一格）**：燃料格。放燃料棒就是核电，输出 100 MW，同时面板标题会写
  「核电 100MW · 氘核棒 N」。
- **面板顶部**：供电明细（电网 + 地热 + 核电 = 塔顶总功率）、钻孔状态、电池余量。

> 燃料消耗是能量守恒，没有加成：`秒/根 = 热值 ÷ 100 MW`。
> 氘核 600 MJ ≈ **6 秒/根**，反物质 7.2 GJ ≈ **72 秒/根**，奇异湮灭 72 GJ ≈ **720 秒/根**。
> 100 MW 很贵，燃料格叠 100 根也撑不了太久 —— 这是设计取舍，不是 bug。

## 已知限制

- **存档**：Mod 会往存档里写自定义 proto（建筑 / 物品 / 配方 / 科技各一个 ID）。
  中途卸载后，存档里已有的本站建筑会变成空壳，**请勿在重要存档上随意装卸**。
- **多人**：站体数据走原版 `StationComponent` 通道，理论上与 Nebula 兼容，
  但没有做过完整的联机回归测试，建议先备份存档再试。
- **ID 冲突**：本站占用 item 8100 / recipe 320 / model 673 / buildIndex 608。
  若与其他 Mod 撞车，日志里会报 `proto 注册失败`，请把冲突 Mod 的 ID 告诉我。

## 反馈

BepInEx 日志（`BepInEx/LogOutput.log`）里有完整的诊断行，例如：

```
采矿科技: 矿物利用 Lv12 → 速度 ×2.20 耗矿 ×0.476（47.6%） 能量武器 ×2.20 地热 ×2.80
能量武器: ×1.20（原版「能量武器伤害」升级，每级 +10%）→ 激光单发 192 · 电浆单发 1080
地热发电=11.40MW（直充站内电池，矿物利用 ×1.90）
枢纽站网格已构建: 顶点=… 三角=…
```

报问题时把这几行贴上来就行。

---

## English summary

A single building that replaces a logistics tower, miners, a geothermal plant and a turret line:

- 12 belt ports around a 24-sided base with a drill that bores 6 geothermal wells and 24 lava fissures;
- a saucer hub held 82 m up by an energy column, ringed by **50 vessel berths** (2×25),
  **50 interstellar vessels** and **200 logistics drones** per station;
- **22 turrets** (18 plasma on the saucer, 4 lasers on the base) and a **43 m** shield;
- the drill auto-mines every ore vein, crude oil seep and ocean (water / sulfuric acid) on the planet,
  filling 10 read-only mining slots shown in the vanilla station window;
- 20 cargo slots × 500,000; slot 10 takes **fuel rods** (deuteron / antimatter / strange annihilation)
  for **100 MW** of fusion power on top of the grid.

Requires BepInEx 5.4.17+, LDBTool 3.0.3+, CommonAPI 1.6.7+.
Turret damage scales with the vanilla *Energy Weapon Damage* upgrade (+10% per level);
mining speed, ore cost and geothermal output scale with *Mineral Exploitation*.
