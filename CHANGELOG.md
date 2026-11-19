# 更新日志 · Changelog

版本号遵循 `MAJOR.MINOR.PATCH`，与 `manifest.json` 的 `version_number` 一致。

---

## 0.9.42

**修：加载本 MOD 后存档显示「无效 / 损坏」——本 mod 写过的存档读不回来**

- 现象：装上本 MOD 后，存档列表里选中某份存档右侧显示「存档损坏」，
  点「加载」则弹「无效存档 / 加载存档失败」，只能停在主菜单。
  用 0.9.39 时正常，0.9.40 / 0.9.41 反复出现 —— 其实是**存档只要被本 mod 写过一次就会中招**。
- 根因（`GameHistoryData.Init` 补建科技状态的时机错了）：
  原版 `GameData.Import` 的顺序是 `history.Init()`（只把 `techStates` 换成空字典）
  → `history.Import()`（把存档里的条目**不判重直接 `Add`**）。
  本 mod 0.9.15 起在 `Init` 之后就把自己的科技 1997 塞进 `techStates`，而
  `GameHistoryData.Export` 会把这张表原样写进存档，于是下一次读档：
  `Init` 先补一条 1997 → `Import` 又读到存档里的 1997 → `Add` 撞键抛
  `ArgumentException: 已添加了具有相同键的项`：
  - 正常读档：异常被 `GameSave.LoadCurrentGame` 吞掉 → 返回 false → 弹「无效存档」；
  - 读档窗口选中存档（`GameData.ImportGamePropertyData` 预览导入，同一套 Init+Import）
    → 详情显示「存档损坏」。
  所以老存档（之前没被本 mod 写过的）能读、刚存的读不回来，看起来就像「升到 0.9.40 才坏」。
- 修法（照 ProjectGenesis 的时机）：
  - **删掉 `GameHistoryData.Init` 的后缀**，`Init` 恢复成原版语义（交给 `Import` 填表）；
  - 改成 `GameData.Import` / `GameData.SetForNewGame` 结束后兜底补建；
  - `GameHistoryData.Import` 的后缀只对**真实游戏历史**生效，不再碰读档窗口的预览实例；
  - 写回改用索引器，重复调用只会覆盖同样的初始值，不会再抛异常把读档带崩。
- 已存在的存档**没有损坏**，装 0.9.42 后可直接读回来。

## 0.9.41

**修：开局跳过序章 NullReferenceException（与 BulletTime 快速加载冲突）**

- 崩溃点：`GameLoader.FixedUpdate → GameData.SkipStandardModeGuide →
  GuideMissionStandardMode.Skip`。
- 根因：BulletTime 的「快速加载」把 frame 7 的等待条件换成
  `GameMain.localPlanet?.loaded ?? true`——localPlanet 尚未赋值时恒为 true，
  星球加载等待被完全绕过，frame 9 就执行跳过序章，此时 `localPlanet`/`factory`
  还没就绪 → NRE。
- 修复：本 mod 加就绪守卫——`mainPlayer`/`localPlanet`/`factory` 任一为空时
  拦下这次调用，等星球加载完成（`GameMain.FixedUpdate` 内重试）后自动补发，
  行为与原版 frame 9 等价；「开始序章」入口同样守护。
- 30 秒仍不就绪则放弃补发并报错，不会卡死加载。

## 0.9.40

**炮塔监控范围 + 修「打太空黑雾舰队没有任何效果」**

- 底座激光武器：监控范围写明为 **140 m**（碟顶电浆炮 600 m）——黑雾只有进入对应范围
  才会被锁定攻击，圈外只做待机扫掠。
- **修 bug**：塔顶电浆炮原来打不到太空黑雾舰队——星区敌人（母巢舰队）存在
  `sector.enemyPool`，与行星本地池是两个独立的池，旧版只扫后者。
  现在与原版同一条换算链（`TransformFromAstro → InverseTransformToAstro`），
  舰队进入 600 m 内即接战；伤害走原版 `SkillSystem.DamageObject`，
  巢等级减伤、巢经验自动生效。
- 舰队俯冲到 95 m 以下时，基座激光塔也会在 140 m 内接战。

## 0.9.39

**底座与塔顶造型精细化**

- 底座 12 段截锥 → **24 段六级退台**（埋地台 / 台阶 / 主裙 / 内凹暗带 / 肩台倒角 / 腰部灯带 / 顶环），
  转角 15° → 7.5°，12 个辊道口与 12 个吸气钟都落在面心，不再被竖棱劈开。
- 新增 3 道接缝环、两排 24 颗铆钉、12 条贴面线管、12 段琥珀警示条、24 颗面螺栓；
  二级平台 8 组设备箱 + 护栏，顶环平台 12 组设备箱 / 缆盘 / 格栅。
- **修 bug**：基座 4 座激光炮的炮座盘原来有半圈悬空，补了 4 块外伸托架。
- 碟顶碟缘改成四层（上/下倒角环 + 深色压边 + 细灯缝），缘灯 0.42 → 0.09；
  盘面 24 分格缝 + 24 凸筋 + 24 螺栓；核心塔从 0.75 m「井盖」改成四级收分塔身
  （3.36→3.02→2.62→2.05）+ 3 道灯环 + 桅杆 / 双横臂 / 斜拉索 / 信标球，塔顶 31.3 → 34.96。
- 同步 `TotalHeight` 32.40 → 35.00（否则建筑会被裁切）、`ShieldRadius` 40 → 43（否则天线戳穿护盾）。
- 三角数约 8.3 万（上一版 6.9 万）。
