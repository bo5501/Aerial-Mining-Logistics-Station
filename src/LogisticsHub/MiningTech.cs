using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 「矿物利用」升级（原版 <c>TechProto.INFINITE_MINE_TECH_ID = 3606</c>）对本站钻孔的加成。
    ///
    /// 原版这条科技每升一级，在 <c>GameHistoryData.UnlockTechFunction</c> 里做两件事：
    ///   · case 20：<c>miningCostRate *= 0.94</c>   —— 每次采矿作业消耗的矿脉 -6%（<b>连乘，不是线性</b>）
    ///   · case 21：<c>miningSpeedScale += 0.10</c> —— 采矿速度 +10%（线性累加）
    /// （另外还会连带抬高「残骸产出倍率」，那是黑雾掉落，跟本站钻孔无关，不处理。）
    ///
    /// 0.9.18 之前本站是自己按等级现算的：速度 1+0.10×Lv、消耗 max(5%, 1-0.06×Lv)。
    /// 消耗那一条是错的 —— 原版是 0.94^Lv：Lv6 时 0.94^6 = 0.690，而不是 1-0.36 = 0.640；
    /// 等级越高差得越多（Lv20：原版 0.29，线性公式早就压到 5% 下限了）。
    /// 现在直接读存档里的两个累计值，天然跟原版、自由模式、别的 mod 的改动保持一致。
    ///
    /// 等级每 2 秒刷新一次（科技树是低频变化，没必要每帧读字典）。
    /// </summary>
    internal static class MiningTech
    {
        /// <summary>当前「矿物利用」等级。</summary>
        public static int Level { get; private set; }

        /// <summary>采矿速度倍率 = <c>history.miningSpeedScale</c>（1 + 0.10 × 等级）。</summary>
        public static float SpeedMul { get; private set; } = 1f;

        /// <summary>矿脉消耗倍率 = <c>history.miningCostRate</c>（0.94 ^ 等级）。</summary>
        public static float CostMul { get; private set; } = 1f;

        /// <summary>
        /// 能量武器伤害倍率（0.9.38）：<c>GameMain.history.energyDamageScale</c>，
        /// 来自原版科技「升级 → 能量武器伤害」，每级 +10%（<c>UnlockTechFunction</c> case 62）。
        ///
        /// 换成这条之前（0.9.25~0.9.37）是「每级矿物利用 +5% 电磁武器」—— 自造效果，
        /// 科技页上没有对应说明。现在直接吃原版那一档：
        /// 原版 TurretComponent 的 Shoot_Laser / Shoot_Plasma / Shoot_LocalPlasma
        /// 三处都是 `damage × energyDamageScale`，也就是说高频激光塔与磁化电浆炮
        /// 在原版里本来就同吃这一条，本站的激光塔（基座 4 座）与电浆炮（塔顶 18 座）照抄。
        ///
        /// 和 <see cref="SpeedMul"/> 一样是**存档里的累计值**，不是本站自己算的等级：
        /// 玩家在科技页升一级、或者别的 mod / 沙盒改了倍率，这里下一次刷新就跟上。
        /// 刷新节流 2 s，由钻孔与面板触发（见 <see cref="Refresh"/>）。
        /// </summary>
        public static float EnergyWeaponMul { get; private set; } = 1f;

        /// <summary>
        /// 地热转换功率倍率（0.9.37）：<c>1 + 0.15 × 等级</c>，封顶
        /// <see cref="C.GeoTechMulMax"/>。
        ///
        /// 玩家要求「地热 6 MW 也要和矿物利用等级同步绑定」。地热功率 = 2 MW/强度 × 强度，
        /// 满强度 3.0 正好 6 MW；乘上这个倍率之后，Lv6 的满强度地热 = 11.4 MW、
        /// Lv20 到顶 24 MW —— 后期单靠打洞也能顶住一部分塔顶电浆炮的开销。
        /// 与采矿速度用同一条等级、同一套线性累加，存档里只有 <c>TechState</c> 一个真值。
        /// </summary>
        public static float GeoMul
        {
            get
            {
                float m = 1f + C.GeoTechPerLevel * Level;
                return m > C.GeoTechMulMax ? C.GeoTechMulMax : m;
            }
        }

        /// <summary>面板用：能量武器加成百分比（30 表示 +30%）。</summary>
        public static int WeaponPct
        {
            get { return Mathf.RoundToInt((EnergyWeaponMul - 1f) * 100f); }
        }

        /// <summary>
        /// 自动采矿格的**每格**仓储容量（0.9.19）。跟「矿物利用」等级绑定：
        /// 科技研究完成（Lv0）= 2 万，每级 +3 万，Lv16 到顶 50 万（= <c>C.MaxItemCount</c>）。
        ///
        /// 为什么要跟这条科技绑：矿物利用本身就是「挖得少、挖得快」，等级越高矿脉越耐用；
        /// 顺带给仓储加容量，等于把「后期一船一船拉矿」的压力也一起缓解掉，
        /// 不用再单独做一条仓储科技。等级是原版存档里的真值，所以它和别的 mod 天然一致。
        /// </summary>
        public static int AutoCap
        {
            get
            {
                long v = (long)C.AutoCapBase + (long)C.AutoCapPerLevel * Level;
                return v > C.AutoCapMax ? C.AutoCapMax : (int)v;
            }
        }

        /// <summary>
        /// 自动采矿格的每格容量，叠加原版「科技附加仓储」（<c>remoteStationExtraStorage</c>），
        /// 但仍然不超过 <see cref="C.AutoCapMax"/> —— 上限就是原版卡槽的 50 万。
        /// </summary>
        public static int AutoSlotCap(int extra)
        {
            long v = (long)AutoCap + (extra > 0 ? extra : 0);
            return v > C.AutoCapMax ? C.AutoCapMax : (int)v;
        }

        /// <summary>0.9.28：这条科技是否已解锁（面板用来区分「未解锁」与「Lv0」）。</summary>
        public static bool Unlocked { get; private set; }

        private static float _next;
        private static bool _logged;
        private static bool _errLogged;

        /// <summary>节流刷新；第一次调用一定刷新。</summary>
        public static void Refresh(float now)
        {
            if (_logged && now < _next)
                return;
            _next = now + 2f;

            int lv = 0;
            bool read = false;
            bool unlocked = false;
            float spd = 1f, cost = 1f;
            // 0.9.38：能量武器倍率（原版「能量武器伤害」升级的累计值，初始 1.00）
            float energy = 1f;
            int cur = 0, maxLv = 0, baseLv = 0, lvTech = 0, lvBySpeed = 0;

            try
            {
                var h = GameMain.history;
                if (h != null)
                {
                    // 【0.9.27 修正等级口径】不能直接用 curLevel —— 它的初值是
                    // `TechProto.Level`，不是 0：`GameHistoryData.Init` 里写着
                    //     new TechState(..., dataArray[j].Level, dataArray[j].MaxLevel, ...)
                    // 矿物利用（3606）的 Level 是 6（它在科技树上的层位），于是
                    // 一级都没升的存档读出来也是 Lv6 —— 和「升级」页显示的等级、
                    // 以及 miningSpeedScale×1.00 完全对不上（用户 0.9.26 截图：
                    // 面板写着 Lv6，右边却是 速度×1.00 耗矿×1.000）。
                    //
                    // 【0.9.28】用户反馈「科技页已解锁，面板还写未研究」。现在改成：
                    //   ① lvTech    = curLevel - baseLevel —— **权威值**，原版自己也是这么算的
                    //      （GameScenario 的 ABN_VeinsUtil 反作弊检查：curLevel - techProto.Level
                    //       + (unlocked ? 1 : 0)，再按级累加 miningCostRate / miningSpeedScale）；
                    //   ② lvBySpeed = round((miningSpeedScale - 1) / 0.1) —— 实际生效的级数；
                    //   ③ 两者相等时用 ①；差超过 1 级说明科技数据与效果确实不同步
                    //      （别的 mod 改了倍率 / 存档迁移），这时以实际生效值 ② 为准。
                    // 这样科技页显示 Lv1，面板也是 Lv1，不会再「已解锁却写未研究」。
                    TechProto tp = LDB.techs.Select(C.MineralTechId);
                    baseLv = tp != null ? tp.Level : 0;

                    TechState ts = h.TechState(C.MineralTechId);
                    cur = ts.curLevel;
                    maxLv = ts.maxLevel;
                    unlocked = ts.unlocked;

                    lvTech = cur - baseLv;
                    if (unlocked && lvTech < 1)
                        lvTech = 1;             // 满级解锁但 curLevel 未跨基线
                    if (lvTech < 0)
                        lvTech = 0;

                    spd = h.miningSpeedScale;
                    cost = h.miningCostRate;
                    // 0.9.38：能量武器伤害倍率 —— 原版「科技 → 升级 → 能量武器伤害」
                    // 每级 +10%（UnlockTechFunction case 62 往 energyDamageScale 累加）。
                    energy = h.energyDamageScale;

                    lvBySpeed = Mathf.RoundToInt((spd - 1f) / C.MineTechSpeedPer);
                    if (lvBySpeed < 0)
                        lvBySpeed = 0;

                    lv = lvTech;
                    if (lvBySpeed - lv > 1 || lv - lvBySpeed > 1)
                        lv = lvBySpeed;

                    read = true;
                }
            }
            catch (System.Exception e)
            {
                if (!_errLogged)
                {
                    _errLogged = true;
                    Plugin.Log.LogError("采矿科技读取失败（本次按未解锁处理，等级会一直显示未研究）: " + e);
                }
            }

            // 读不到存档（还没进游戏）时按同口径算一份兜底值：速度 1+0.1×lv、消耗 0.94^lv
            if (!read)
            {
                spd = 1f + C.MineTechSpeedPer * lv;
                cost = Mathf.Pow(1f - C.MineTechCostPer, lv);
            }

            if (spd < 0.01f)
                spd = 1f;
            if (cost < 0f)
                cost = 0f;
            // 兜底：没解锁时存档里就是 1.00；有 mod 把它改小也按 1.00 算（别让武器反而变弱），
            // 上限只为防 int 溢出。
            if (energy < 1f)
                energy = 1f;
            if (energy > C.WeaponMulMax)
                energy = C.WeaponMulMax;

            bool first = !_logged;
            bool changed = lv != Level || first;
            bool mulChanged = Mathf.Abs(energy - EnergyWeaponMul) > 0.0005f || first;
            Level = lv;
            // 对无限科技（3606）来说 `TechState.unlocked` 只有升到满级才是 true，
            // 所以「已解锁」要再加上「已经升过至少一级」这一条 —— 面板用它区分
            // 「未解锁」与「已解锁但还没升」。
            Unlocked = unlocked || lv > 0;
            SpeedMul = spd;
            CostMul = cost;
            EnergyWeaponMul = energy;
            _logged = true;

            if (changed)
            {
                Plugin.Log.LogInfo("采矿科技: 矿物利用 Lv" + lv +
                                   " → 速度 ×" + spd.ToString("0.00") +
                                   " 耗矿 ×" + cost.ToString("0.000") +
                                   "（" + (cost * 100f).ToString("0.#") + "%）" +
                                   " 地热 ×" + GeoMul.ToString("0.00") +
                                   " 自动格容量 " + AutoCap +
                                   "　[诊断 id=" + C.MineralTechId +
                                   " curLevel=" + cur + " maxLevel=" + maxLv +
                                   " protoLevel=" + baseLv + " unlocked=" + unlocked +
                                   " lvTech=" + lvTech + " lvBySpeed=" + lvBySpeed +
                                   " spd=" + spd.ToString("0.000") + "]");
            }
            // 0.9.38：能量武器单独一行 —— 它跟矿物利用是两条完全不同的科技，
            // 混在「采矿科技」那行里会让人以为升级 3606 也能提伤害。
            if (mulChanged)
            {
                Plugin.Log.LogInfo("能量武器: ×" + energy.ToString("0.00") +
                                   "（原版「能量武器伤害」升级，每级 +10%）→ 激光单发 " +
                                   Mathf.RoundToInt(C.TurretDamage * energy) +
                                   " · 电浆单发 " +
                                   Mathf.RoundToInt(C.TurretDamageSpace * energy) +
                                   "　[history.energyDamageScale=" + energy.ToString("0.000") + "]");
            }
        }
    }
}
