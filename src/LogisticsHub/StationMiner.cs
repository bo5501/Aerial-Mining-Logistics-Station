using System.Collections.Generic;
using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 空中采矿：站体底部 12 个吸气钟从星球地脉取矿，直接进站点仓储。
    ///
    /// 取矿范围是整个星球（见 GetVeins 的注释），优先吸半径 40 m 内的矿脉，
    /// 半径外则按距离取最近的 —— 「打洞取星球物资」不该因为站址偏远就完全不出货。
    ///
    /// 为什么不走原版 MinerComponent：
    ///   1) 原版矿机把产物存在 MinerComponent.productCount 里，要靠传送带/分拣器取走；
    ///      本站没有输出带，产物必须直接进 StationComponent.storage。
    ///   2) 复用「轨道采集器」那种 isVeinCollector 写法会把 StationComponent 的
    ///      slots（12 个传送带接口）整段跳过（见 StationComponent 内 !isVeinCollector 分支），
    ///      等于用吸矿换掉辊道口，不可接受。
    ///   所以这里自己维护一份「脚下矿脉表」，按周期扣矿脉储量并写入站点仓储，
    ///   扣减方式与 MinerComponent 保持一致（veinPool.amount / veinGroups.amount / veinAnimPool.time）。
    ///
    /// 存档安全性：本组件不新增任何存档字段，只改游戏本来就存档的 veinPool 与 station.storage，
    /// 所以反复进出存档不会出现悬空引用。
    /// </summary>
    internal static class StationMiner
    {
        private static readonly Dictionary<int, List<int>> _veins = new Dictionary<int, List<int>>();
        /// <summary>每个站自己的结算计时器。用全局计时器会导致多站并存时只有循环里第一个站能吸到矿。</summary>
        private static readonly Dictionary<int, float> _acc = new Dictionary<int, float>();
        private static int _planetId = -1;
        private static bool _failed;
        /// <summary>0.9.27：采矿基准（原版矿机 → 本站 12 条）只打一次日志。</summary>
        private static bool _baseLogged;
        /// <summary>0.9.28：扫描到的原版采矿机（minerPeriod 最小的一台），null = 还没扫到。</summary>
        private static ItemProto _minerProto;
        /// <summary>
        /// 矿脉消耗的小数累积：矿物利用每级 -6% 消耗，倍率是小数（0.94 / 0.88 / …），
        /// 而 VeinData.amount 是整数，只能攒够 1 再扣 —— 这样长期平均正好等于倍率。
        /// </summary>
        private static readonly Dictionary<int, float> _costAcc = new Dictionary<int, float>();

        /// <summary>每帧调用；内部按 C.MinePeriod 逐站节流。</summary>
        public static void Tick(PlanetFactory factory, StationComponent sc, Vector3 pos, float dt)
        {
            if (_failed || !C.EnableSurfaceMining)
                return;
            try
            {
                if (factory.planet != null && factory.planet.id != _planetId)
                {
                    _planetId = factory.planet.id;
                    _veins.Clear();
                    _acc.Clear();
                    _costAcc.Clear();
                }

                int key = sc.entityId;
                float acc;
                _acc.TryGetValue(key, out acc);
                acc += dt;
                if (acc < C.MinePeriod)
                {
                    _acc[key] = acc;
                    return;
                }
                _acc[key] = 0f;                     // 无论是否采到都要清零，否则矿挖空后每帧都空转

                // 没电就不钻孔。供电来自电网，或来自底座钻到的地热（StationDrill.Working）。
                if (!StationDrill.Working(factory, sc))
                    return;

                List<int> list = GetVeins(factory, key, pos);
                // 注意：list 可能是空的（纯海洋星球没有矿脉），但海洋资源照样要采，
                // 所以这里不能提前 return，交给 MineMixed 自己判断。

                // 地热越强，钻孔越快；再乘「矿物利用」的速度倍率
                // （原版 miningSpeedScale = 1 + 0.10 × 等级）。
                //
                // 【0.9.27】基准从写死的 C.MinePerCycle 换成原版大型采矿机：
                //   每矿脉每秒 = 600000 / prefabDesc.minerPeriod（见 VanillaVeinPerSecond）
                //   本站 12 个吸气钟 = 12 条矿脉 → base = 12 × 该值 × 周期(1 s)
                // 面板「每 1 s 取 N 个」显示的仍是最终 cap，所以改基准后数字会跟着变。
                MiningTech.Refresh(Time.unscaledTime);
                float perVein = VanillaVeinPerSecond();
                float basePerCycle = perVein * C.MineBaseVeins * C.MinePeriod;
                int cap = CycleCap(key);

                if (!_baseLogged && VanillaMinerPeriod() > 0)
                {
                    _baseLogged = true;
                    ItemProto mp = VanillaMinerProto();
                    int period = VanillaMinerPeriod();
                    Plugin.Log.LogInfo("采矿基准: 原版" + (mp != null ? mp.name : "采矿机") +
                                       "（itemId " + (mp != null ? mp.ID : 0) +
                                       "，minerPeriod=" + period + "）每矿脉 " +
                                       perVein.ToString("0.###") + " 个/s × " +
                                       C.MineBaseVeins + " 条 = " + basePerCycle.ToString("0.#") +
                                       " 个/周期（再乘地热与矿物利用）");
                }

                MineMixed(factory, sc, list, cap);
            }
            catch (System.Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("空中采矿失败（已停用本功能）: " + e);
            }
        }

        /// <summary>
        /// 【0.9.28】扫表找原版采矿机：只有 <c>PrefabDesc.minerPeriod > 0</c> 的才是矿机。
        ///
        /// 0.9.27 把 id 写死成 2302，实测那是**电弧熔炉**（`minerPeriod = 0`），
        /// 于是基准退化成写死的 1 个/秒，玩家看到的「每 1 s 取 N 个」跟原版毫无关系。
        /// 改为扫描全部物品取 `minerPeriod` 最小的那台 —— minerPeriod 越小产出越高，
        /// 所以拿到的就是原版效率最高的矿机（大型 / 高级采矿机），不必猜 id。
        /// prefabDesc 未加载时返回 null，下次调用会重扫（不缓存失败结果）。
        /// </summary>
        internal static ItemProto VanillaMinerProto()
        {
            if (_minerProto != null)
                return _minerProto;
            try
            {
                ItemProto[] arr = LDB.items.dataArray;
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        ItemProto p = arr[i];
                        if (p == null || p.prefabDesc == null)
                            continue;
                        int period = p.prefabDesc.minerPeriod;
                        if (period <= 0)
                            continue;                       // 不是矿机（熔炉、制造台…）
                        if (_minerProto == null || period < _minerProto.prefabDesc.minerPeriod)
                            _minerProto = p;
                    }
                }
            }
            catch { }
            if (_minerProto == null)
            {
                // 兜底也要求 minerPeriod > 0：prefabDesc 还没加载时不要把这个「空壳」
                // 缓存下来，否则后面永远不会重扫（`_minerProto != null` 直接 return）。
                try
                {
                    ItemProto fb = LDB.items.Select(C.AdvMinerItemId);
                    if (fb != null && fb.prefabDesc != null && fb.prefabDesc.minerPeriod > 0)
                        _minerProto = fb;
                }
                catch { }
            }
            return _minerProto;
        }

        /// <summary>
        /// 【0.9.27】原版大型采矿机在**一条矿脉**上的每秒产出。
        ///
        /// 原版 prefab 的 `periodf`（秒/个）在 PrefabDesc 里被写成
        /// `minerPeriod = periodf × 600000`；原版 UI 的「每分每矿脉」就是
        /// <c>60 × 600000 / minerPeriod × miningSpeedScale</c>（ItemProto 生成描述时算的），
        /// 所以**每秒每矿脉** = <c>600000 / minerPeriod</c>。
        ///
        /// prefabDesc 还没加载、或该版本没有这台矿机时退回 1 个/秒（等价 periodf = 1 s）。
        /// </summary>
        internal static float VanillaVeinPerSecond()
        {
            int period = VanillaMinerPeriod();
            return period > 0 ? 600000f / period : 1f;
        }

        /// <summary>
        /// 当前周期实际取矿上限。**面板与产出共用这一个函数**，免得两处各算一遍
        /// （0.9.26 之前面板自己拿 C.MinePerCycle 算，基准一换就会显示与实际不符）。
        /// </summary>
        internal static int CycleCap(int entityId)
        {
            float basePerCycle = VanillaVeinPerSecond() * C.MineBaseVeins * C.MinePeriod;
            return Mathf.Clamp(
                Mathf.RoundToInt(basePerCycle * StationDrill.MineBoost(entityId) * MiningTech.SpeedMul),
                1, C.MinePerCycle * 32);
        }

        /// <summary>原版采矿机的 prefabDesc.minerPeriod；读不到返回 0。</summary>
        internal static int VanillaMinerPeriod()
        {
            try
            {
                ItemProto p = VanillaMinerProto();
                if (p != null && p.prefabDesc != null)
                    return p.prefabDesc.minerPeriod;
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 一个周期内按「资源种类」摊派额度：每种取一次，取 <c>ceil(cap / 种数)</c> 个。
        ///
        /// 0.6.0 之前是从列表头部连续取 cap 个 —— 而列表头的矿脉通常同属一个矿脉群，
        /// 于是每周期只挖同一种矿，面板里另外 9 格永远是 0，看起来像「没在采矿」。
        ///
        /// 0.9.5 起「海洋（水 / 硫酸）」也算一种：它不是矿脉（原版是 EMinerType.Water，
        /// 产物取 planet.waterItemId），所以单独走一条不扣储量的分支。
        /// 只有站里真的有一个海洋卡槽时才产水，免得凭空生成玩家没配的物资。
        /// </summary>
        private static void MineMixed(PlanetFactory factory, StationComponent sc,
                                      List<int> list, int cap)
        {
            VeinData[] pool = factory.veinPool;
            var seen = new HashSet<int>();
            int kinds = 0;
            if (list != null)
            {
                for (int k = 0; k < list.Count && kinds < 10; k++)
                {
                    int vi = list[k];
                    if (vi <= 0 || vi >= pool.Length)
                        continue;
                    VeinData v = pool[vi];
                    if (v.id != vi || v.productId <= 0)
                        continue;
                    if (v.amount <= 0 && !Infinite)
                        continue;
                    if (seen.Add(v.productId))
                        kinds++;
                }
                seen.Clear();
            }

            int ocean = StationDrill.OceanItem(factory);
            bool hasOcean = ocean > 0 && HasSlot(sc, ocean);
            int totalKinds = kinds + (hasOcean ? 1 : 0);
            if (totalKinds == 0)
                return;

            int perKind = Mathf.Max(1, Mathf.CeilToInt((float)cap / totalKinds));
            int taken = 0;

            if (list != null)
            {
                for (int k = 0; k < list.Count && taken < cap; k++)
                {
                    int vi = list[k];
                    if (vi <= 0 || vi >= pool.Length)
                        continue;
                    VeinData v = pool[vi];
                    if (v.id != vi || v.productId <= 0)
                        continue;
                    if (v.amount <= 0 && !Infinite)
                        continue;
                    if (!seen.Add(v.productId))
                        continue;                                  // 这一轮已经取过这个矿种

                    int n = Mathf.Min(perKind, cap - taken);
                    for (int q = 0; q < n; q++)
                    {
                        if (!Store(sc, v.productId, 1))
                            return;                                // 仓储满了就停，别把矿挖掉却存不下
                        Deplete(factory, vi);
                        taken++;
                    }
                }
            }

            // 海洋：取之不尽，不扣储量。抽水站也是这么干的（Water 分支不碰 veinPool）。
            // 只写「已有的海洋卡槽」——不走 Store 的「找空槽新建」兜底，
            // 否则水一旦把卡槽灌满，就会继续往外溢、把剩下所有空格都填成水。
            if (hasOcean && taken < cap)
            {
                int n = Mathf.Min(perKind, cap - taken);
                for (int q = 0; q < n; q++)
                    if (!StoreExisting(sc, ocean, 1))
                        return;
            }
        }

        /// <summary>只往「已经有这个物品的卡槽」里塞；没有/满了就返回 false（不会新建卡槽）。</summary>
        private static bool StoreExisting(StationComponent sc, int itemId, int count)
        {
            StationStore[] st = sc != null ? sc.storage : null;
            if (st == null)
                return false;
            for (int i = 0; i < st.Length; i++)
            {
                if (st[i].itemId != itemId || st[i].max <= 0)
                    continue;
                int room = st[i].max - (st[i].count + st[i].localOrder);
                if (room <= 0)
                    return false;
                st[i].count += Mathf.Min(room, count);
                return true;
            }
            return false;
        }

        /// <summary>站里有没有这个物品的卡槽（有 = 玩家/自动配货配了它，那就该产出）。</summary>
        private static bool HasSlot(StationComponent sc, int itemId)
        {
            StationStore[] st = sc != null ? sc.storage : null;
            if (st == null)
                return false;
            for (int i = 0; i < st.Length; i++)
                if (st[i].itemId == itemId && st[i].max > 0)
                    return true;
            return false;
        }

        /// <summary>站点被拆除时清掉缓存，避免字典无限增长。</summary>
        public static void Forget(int entityId)
        {
            _veins.Remove(entityId);
            _acc.Remove(entityId);
        }

        /// <summary>无限资源模式（该模式下矿脉储量不扣，只出货）。</summary>
        private static bool Infinite
        {
            get
            {
                return GameMain.data != null && GameMain.data.gameDesc != null &&
                       GameMain.data.gameDesc.isInfiniteResource;
            }
        }

        /// <summary>
        /// 脚下矿脉索引表，按实体缓存；矿脉被挖空后自动重建。
        ///
        /// 取矿范围是**整个星球**，而不是只取 40 m 内：
        /// 站址常常落在空地（辊道要接出来、要避开已有产线），附近根本没有矿脉，
        /// 只按半径吸就会「通电了、也配好了 10 格、但永远不出货」。
        /// 「打洞」的设定本来就是钻到地脉去，所以这里按距离排序，优先近的，
        /// 但保证每种矿物至少有一条可采 —— 面板里填上的 10 格都能真的产出。
        /// </summary>
        private static List<int> GetVeins(PlanetFactory factory, int entityId, Vector3 pos)
        {
            List<int> list;
            if (_veins.TryGetValue(entityId, out list) && HasOre(factory, list))
                return list;

            list = Collect(factory, pos);
            _veins[entityId] = list;
            return list;
        }

        private static List<int> Collect(PlanetFactory factory, Vector3 pos)
        {
            VeinData[] pool = factory.veinPool;
            bool inf = Infinite;
            var best = new Dictionary<int, KeyValuePair<float, int>>(16);   // 每种矿：最近的一条
            var near = new List<KeyValuePair<float, int>>(64);              // 半径内
            float r2 = C.MineRadius * C.MineRadius;

            for (int i = 1; i < pool.Length; i++)
            {
                VeinData v = pool[i];
                if (v.id != i || v.productId <= 0)
                    continue;
                if (v.amount <= 0 && !inf)
                    continue;
                float d = (v.pos - pos).sqrMagnitude;
                if (d <= r2)
                    near.Add(new KeyValuePair<float, int>(d, i));
                KeyValuePair<float, int> cur;
                if (!best.TryGetValue(v.productId, out cur) || d < cur.Key)
                    best[v.productId] = new KeyValuePair<float, int>(d, i);
            }

            var list = new List<int>(48);
            var kinds = new List<KeyValuePair<float, int>>(best.Values);
            kinds.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < kinds.Count && list.Count < 12; i++)
                list.Add(kinds[i].Value);

            near.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < near.Count && list.Count < 48; i++)
            {
                if (list.Contains(near[i].Value))
                    continue;
                list.Add(near[i].Value);
            }
            return list;
        }

        /// <summary>缓存里是否还有能采的矿（原判断 <c>amount &gt;= 0</c> 恒为真，导致矿挖空后永不重建）。</summary>
        private static bool HasOre(PlanetFactory factory, List<int> list)
        {
            if (list == null || list.Count == 0)
                return false;
            if (Infinite)
                return true;             // 无限资源模式下储量不会减少，缓存永久有效
            VeinData[] pool = factory.veinPool;
            for (int i = 0; i < list.Count; i++)
            {
                int vi = list[i];
                if (vi <= 0 || vi >= pool.Length)
                    continue;
                VeinData v = pool[vi];
                if (v.id == vi && v.productId > 0 && v.amount > 0)
                    return true;
            }
            return false;
        }

        /// <summary>把 count 个 itemId 塞进站点仓储：优先同种矿，其次空槽。</summary>
        private static bool Store(StationComponent sc, int itemId, int count)
        {
            StationStore[] st = sc.storage;
            if (st == null || st.Length == 0)
                return false;

            for (int i = 0; i < st.Length; i++)
            {
                if (st[i].itemId != itemId || st[i].max <= 0)
                    continue;
                int room = st[i].max - (st[i].count + st[i].localOrder);
                if (room <= 0)
                    continue;
                st[i].count += Mathf.Min(room, count);
                return true;
            }

            for (int i = 0; i < st.Length; i++)
            {
                if (st[i].itemId != 0 || st[i].max <= 0)
                    continue;
                st[i].itemId = itemId;
                st[i].count = Mathf.Min(count, st[i].max);
                // 与 UpdateVeinCollection 一致：让物流系统立刻把它看成「有货」
                if (sc.collectionIds != null && sc.collectionIds.Length > 0 && sc.collectionIds[0] == 0)
                    sc.collectionIds[0] = itemId;
                return true;
            }
            return false;
        }

        /// <summary>扣矿脉储量，写法对齐 MinerComponent.InternalUpdate。</summary>
        private static void Deplete(PlanetFactory factory, int vi)
        {
            if (Infinite)
                return;

            float cost = MiningTech.CostMul;
            if (cost <= 0f)                 // 无限资源档 / 消耗倍率已归零：只出货，不扣储量
                return;

            VeinData[] pool = factory.veinPool;
            if (pool[vi].amount <= 0)
            {
                _costAcc.Remove(vi);
                return;
            }

            // 原版 miningCostRate 是「每次采矿作业消耗的储量倍率」，0.94^等级。
            // 每出 1 个货扣 cost 点储量；take 是小数累积到整数才真扣，长期平均正好等于 cost。
            float acc;
            _costAcc.TryGetValue(vi, out acc);
            acc += cost;
            int take = (int)acc;
            if (take <= 0)
            {
                _costAcc[vi] = acc;
                return;
            }
            _costAcc[vi] = acc - take;
            if (take > pool[vi].amount)
                take = pool[vi].amount;
            if (take <= 0)
                return;

            pool[vi].amount -= take;
            if (pool[vi].amount < 0)
                pool[vi].amount = 0;

            int gi = pool[vi].groupIndex;
            if (factory.veinGroups != null && gi >= 0 && gi < factory.veinGroups.Length)
                factory.veinGroups[gi].amount -= take;

            if (factory.veinAnimPool != null && vi < factory.veinAnimPool.Length)
                factory.veinAnimPool[vi].time = pool[vi].amount >= 20000
                    ? 0f
                    : 1f - pool[vi].amount * 5e-5f;
        }
    }
}
