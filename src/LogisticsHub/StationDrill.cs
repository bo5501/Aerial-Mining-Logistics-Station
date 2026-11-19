using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 底座「打洞」模块：量脚下地热、把地热换成站点能量、把本星球矿脉自动配进卡槽。
    ///
    /// 三件事，按发生顺序：
    ///   1) **地热采样**（<c>PowerSystem.CalculateGeothermalStrength</c>，与地热发电机同款，
    ///      基座周围 5 点地形采样，实测取值约 0.55~1.36）。采样与电网状态无关 ——
    ///      需要先知道这儿烫不烫，才能判断能不能靠地热自己跑起来。
    ///   2) **地热发电**：钻孔在工作时，按 <c>GeoPowerPerSec × 强度</c> 直接充进
    ///      <c>StationComponent.energy</c>（0.6.0 删掉的 30 MW 回来了，但这次是按地形强度算，
    ///      不是凭空一个定值）。能量池是货船起飞的燃料池，所以这条收益是实打实可见的。
    ///   3) **能不能工作**：电网通电（<c>consumerRatio &gt; 0.01</c>）**或** 地热强度够高
    ///      （<c>≥ GeoSelfPowerMin</c>，即钻到了够烫的地缝）→ 钻孔 / 采矿 / 护盾全部启动。
    ///      地热不够、又没接电缆 → 什么都不做，通电才工作的设定保留。
    ///
    /// 「通电」判定照抄 UIStationWindow._OnUpdate：
    ///   <c>pcId &gt; 0</c> 且 <c>powerSystem.netPool[consumerPool[pcId].networkId].consumerRatio &gt; 0</c>。
    ///   原版面板就是靠 consumerRatio 显示「断电 / 充电中」，二者口径一致。
    ///
    /// 存档安全性：只写运行时字典；station.storage / station.energy 本来就是存档数据。
    /// </summary>
    internal static class StationDrill
    {
        private static readonly Dictionary<int, bool> _powered = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> _grid = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> _filled = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> _migrated = new Dictionary<int, bool>();
        private static readonly Dictionary<int, float> _strength = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _acc = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _fillAcc = new Dictionary<int, float>();
        /// <summary>自动矿格容量的刷新计时器（1 s 一轮，独立于首次配货）。</summary>
        private static readonly Dictionary<int, float> _capAcc = new Dictionary<int, float>();
        /// <summary>每个站上一次写进去的自动格容量，用来判断「要不要打日志 / 要不要重写」。</summary>
        private static readonly Dictionary<int, int> _capNow = new Dictionary<int, int>();
        /// <summary>地热充能的余数累加器 —— 每帧的功率×dt 远小于 1 J 之外，不累加会全被截断成 0。</summary>
        private static readonly Dictionary<int, double> _geoAcc = new Dictionary<int, double>();
        private static int _planetId = -1;
        private static bool _failed;
        private static bool _loggedPower;

        /// <summary>每帧调用（在 StationMiner.Tick 之前，采矿要用这里的发电/强度结果）。</summary>
        public static void Tick(PlanetFactory factory, StationComponent sc, Vector3 pos, Quaternion rot, float dt)
        {
            if (_failed || !C.EnableDrill || factory == null || sc == null || sc.id <= 0)
                return;

            try
            {
                int pid = factory.planet != null ? factory.planet.id : 0;
                if (pid != _planetId)
                {
                    _planetId = pid;
                    _powered.Clear();
                    _grid.Clear();
                    _filled.Clear();
                    _migrated.Clear();
                    _strength.Clear();
                    _acc.Clear();
                    _fillAcc.Clear();
                    _capAcc.Clear();
                    _capNow.Clear();
                    _geoAcc.Clear();
                    StationNuclear.Clear();
                }

                int key = sc.entityId;

                // ---- 0) 能量池上限：0.9.9 从 800 MJ 提到 30 GJ ----
                // 新站走 PrefabDesc.stationMaxEnergyAcc，老存档的 energyMax 是从存档读回来的，
                // 所以这里每帧对一次（一个 long 比较，代价可以忽略）。
                if (sc.energyMax != C.MaxEnergyAcc)
                {
                    Plugin.Log.LogInfo("站内能量池: 站 id=" + sc.id + " " + sc.energyMax +
                                       " J → " + C.MaxEnergyAcc + " J");
                    sc.energyMax = C.MaxEnergyAcc;
                }

                // ---- 0b) 接电网：修 plug 点 + 无线覆盖兜底（详见 StationPower）----
                StationPower.Ensure(factory, sc);

                // ---- 1) 地热采样（2 s 一次）。放在通电判定之前：
                //     没有这个值就没法判断「能不能靠地热自供电」。
                float acc;
                _acc.TryGetValue(key, out acc);
                acc += dt;
                if (acc >= C.GeoPeriod || !_strength.ContainsKey(key))
                {
                    acc = 0f;
                    _strength[key] = Sample(factory, pos, rot);
                }
                _acc[key] = acc;

                float geo = _strength[key];

                // ---- 1b) 回收 0.9.5/0.9.6 给站点实体补上去的发电机组 / 电力节点。
                //      那两个组件会让原版 UIGame 把本站当成「发电机建筑」，
                //      点击时去开「地热发电站 / 电力节点」窗口，并在同一帧里
                //      ShutAllFunctionWindow() 把站体窗口关掉 —— 表现就是「面板弹不出来」。
                //      地热收益不受影响：下面第 3 步 ChargeGeo 直接充站内电池。
                StationGeo.Ensure(factory, sc);

                // ---- 1c) 自动矿格容量：跟「矿物利用」等级走（1 s 一轮）----
                //      放在通电判定之前：断电的站也把容量刷对，面板一打开看到的就是真值。
                SyncAutoCap(sc, key, dt);

                // ---- 1d) 【0.9.30】第 10 格核电：氘核燃料棒 → 站内电池 ----
                //      必须放在供电判定之前 —— 「没电网、地热也不够、但有氘棒」的站
                //      要靠它把自己顶起来（这是玩家要的「核电发电机」）。
                StationNuclear.Tick(factory, sc, dt);

                // ---- 2) 供电判定：电网优先，其次地热，最后核电 ----
                bool grid = IsPowered(factory, sc);
                bool self = !grid && geo >= C.GeoSelfPowerMin;
                bool nuke = !grid && !self && StationNuclear.HasFuel(sc);
                bool on = grid || self || nuke;

                _grid[key] = grid;
                bool was;
                _powered.TryGetValue(key, out was);
                _powered[key] = on;

                if (!on)
                    return;                      // 断电且地热不够：不采矿、不开盾

                if (!was)
                    _loggedPower = false;

                if (!_loggedPower)
                {
                    _loggedPower = true;
                    Plugin.Log.LogInfo("物流站已启动: id=" + sc.id +
                                       " 供电=" + (grid
                                           ? "电网(" + Ratio(factory, sc).ToString("0.00") + ")"
                                           : (self ? "地热自供电" : "核电（" + StationNuclear.FuelName(sc) +
                                              " " + StationNuclear.FuelCount(sc) + " 根）")) +
                                       " 地热强度=" + geo.ToString("0.00") +
                                       " 地热发电=" + GeoPowerMw(key).ToString("0.00") + "MW（直充站内电池，" +
                                       "矿物利用 ×" + MiningTech.GeoMul.ToString("0.00") + "）" +
                                       (nuke ? " 核电=" + C.FusionPowerMw.ToString("0.#") + "MW" : ""));
                }

                // ---- 3) 地热直充：除了并网发电，还直接往站内能量池灌一份，
                //      这样即使节点没挂上（或旁边电网不给电）货船也起得来。
                ChargeGeo(sc, key, geo, dt);

                // 首次通电：把本星球矿物配进卡槽。独立于地热采样节流，
                // 0.5 s 一轮 —— 通电后玩家应该「马上就看见面板被填满」，
                // 而不是等下一次地形采样（2 s）才有动静。
                bool done;
                _filled.TryGetValue(key, out done);
                if (!done)
                {
                    float facc;
                    _fillAcc.TryGetValue(key, out facc);
                    facc += dt;
                    if (facc >= 0.5f)
                    {
                        facc = 0f;
                        if (AutoFill(factory, sc))
                            _filled[key] = true;
                    }
                    _fillAcc[key] = facc;
                }
                else
                {
                    // 0.9.5 迁移：0.9.4 之前配好的站，右列只有矿脉，没有水 / 原油。
                    // 只往「空槽」或「货量为 0 的槽」里补，绝不覆盖已有库存。
                    bool migrated;
                    _migrated.TryGetValue(key, out migrated);
                    if (!migrated)
                    {
                        float macc;
                        _fillAcc.TryGetValue(key, out macc);
                        macc += dt;
                        if (macc >= 1.0f)
                        {
                            _fillAcc[key] = 0f;
                            if (MigrateAutoSlots(factory, sc))
                                _migrated[key] = true;
                        }
                        else
                            _fillAcc[key] = macc;
                    }
                }
            }
            catch (System.Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("钻孔模块失败（已停用）: " + e);
            }
        }

        public static void Forget(int entityId)
        {
            _powered.Remove(entityId);
            _grid.Remove(entityId);
            _filled.Remove(entityId);
            _migrated.Remove(entityId);
            _strength.Remove(entityId);
            _acc.Remove(entityId);
            _fillAcc.Remove(entityId);
            _capAcc.Remove(entityId);
            _capNow.Remove(entityId);
            _geoAcc.Remove(entityId);
            StationNuclear.Forget(entityId);
            StationPower.Forget(entityId);
        }

        /// <summary>这个站现在能不能工作（电网通电 或 地热自供电）。</summary>
        public static bool Powered(int entityId)
        {
            bool v;
            return _powered.TryGetValue(entityId, out v) && v;
        }

        /// <summary>电网是不是在给它供电（不看地热）。面板要区分「电网友好 / 靠地热自己顶」。</summary>
        public static bool GridOn(int entityId)
        {
            bool v;
            return _grid.TryGetValue(entityId, out v) && v;
        }

        /// <summary>
        /// 组合判定：能工作吗？钻孔模块被关掉时退回「只看电网」，与 0.9.3 之前一致。
        /// StationMiner / StationTurrets 都走这个口，避免两处各判一套。
        /// </summary>
        public static bool Working(PlanetFactory factory, StationComponent sc)
        {
            if (sc == null)
                return false;
            if (C.EnableDrill && _powered.ContainsKey(sc.entityId))
                return Powered(sc.entityId);
            return IsPowered(factory, sc);
        }

        /// <summary>当前站的地热强度（未采样时为 0）。断电也会采样，面板据此给建议。</summary>
        public static float StrengthOf(int entityId)
        {
            float s;
            return _strength.TryGetValue(entityId, out s) ? s : 0f;
        }

        /// <summary>
        /// 地热发电功率（MW）：<c>GeoPowerPerSec × 强度 × 矿物利用倍率</c>。
        ///
        /// 【0.9.37】第三项是玩家要求的「地热 6 MW 也要跟矿物利用等级同步」——
        /// 满强度 3.0 在 Lv0 时正好 6 MW，Lv6 变 11.4 MW（×1.90），Lv20 到顶 24 MW（×4）。
        /// 面板读数、功率算式、`物流站已启动` 日志都读这里，所以改倍率不会有两份口径。
        /// </summary>
        public static float GeoPowerMw(int entityId)
        {
            return C.GeoPowerPerSec * MiningTech.GeoMul * StrengthOf(entityId) / 1e6f;
        }

        /// <summary>采矿倍率：1.0 ~ C.GeoMineBoostMax（地热越强钻孔越快）。</summary>
        public static float MineBoost(int entityId)
        {
            float s = StrengthOf(entityId);
            return Mathf.Clamp(s, 1f, C.GeoMineBoostMax);
        }

        // ==================== 地热发电 ====================

        /// <summary>
        /// 把地热换成站点能量。写的是 <c>StationComponent.energy</c> —— 和电网充电、
        /// 货船起飞扣能量用的是同一个池子（原版 <c>InternalTickLocal</c> 里
        /// <c>energy += energyPerTick*power; energy -= 1000</c>），所以这里的收益
        /// 立刻能体现在「充电更快 / 没电缆也能起飞」上。
        ///
        /// 为什么用 double 累加：一帧只有 16 ms，2 MW × 0.016 = 32 000 J，虽然够大，
        /// 但低强度时（0.5 MW）也常有被截断的情况；累加器能保证长期不漏算。
        /// </summary>
        private static void ChargeGeo(StationComponent sc, int key, float geo, float dt)
        {
            if (sc.energyMax <= 0 || sc.energy >= sc.energyMax)
                return;

            // 【0.9.37】地热转换功率同样吃「矿物利用」等级：×（1 + 0.15 × Lv）。
            // 这里自己刷一次科技等级，不指望 SyncAutoCap 那条路径先刷过 ——
            // 它在 storage 长度不足时会提前 return，而地热收益不该因此掉回 Lv0。
            // Refresh 内部 2 s 节流，每帧调也不心疼。
            MiningTech.Refresh(Time.unscaledTime);
            double geoW = (double)C.GeoPowerPerSec * MiningTech.GeoMul;

            double acc;
            _geoAcc.TryGetValue(key, out acc);
            acc += geoW * geo * dt;
            if (acc < 1.0)
            {
                _geoAcc[key] = acc;
                return;
            }

            long add = (long)acc;
            acc -= add;
            _geoAcc[key] = acc;

            long e = sc.energy + add;
            sc.energy = e > sc.energyMax ? sc.energyMax : e;
            if (sc.energy < 0)
                sc.energy = 0;
        }

        // ==================== 通电判定 ====================

        /// <summary>电网是不是在给它供电（consumerRatio &gt; 0.01，口径同原版面板的「断电/充电中」）。</summary>
        public static bool IsPowered(PlanetFactory factory, StationComponent sc)
        {
            if (sc.pcId <= 0)
                return false;
            PowerSystem ps = factory != null ? factory.powerSystem : null;
            if (ps == null || ps.consumerPool == null || ps.netPool == null)
                return false;
            if (sc.pcId >= ps.consumerPool.Length)
                return false;

            int net = ps.consumerPool[sc.pcId].networkId;
            if (net <= 0 || net >= ps.netPool.Length)
                return false;
            PowerNetwork pn = ps.netPool[net];
            return pn != null && pn.consumerRatio > 0.01;
        }

        /// <summary>电网供电比供面板显示。</summary>
        internal static float GridRatio(PlanetFactory factory, StationComponent sc)
        {
            return factory == null || sc == null ? 0f : Ratio(factory, sc);
        }

        /// <summary>
        /// 【0.9.25】当前**实际从电网取用**的功率（MW）。
        ///
        /// 玩家反馈「接入电网之后，看不到当前用了多少电网功率」—— 原版站体窗口那一行
        /// 本来是会显示这个数的（`UIStationWindow._OnUpdate` 里
        /// <c>powerText = requiredEnergy × 60 × consumerRatio</c>），但 0.9.12 起我们把
        /// 那两格换成了「地热功率 / 地热强度」，电网一侧的数字就整条丢了。
        ///
        /// 口径和原版一字不差：
        ///   · <c>requiredEnergy</c> 是**每 tick 的所需能量**（J/tick），由
        ///     <c>StationComponent.SetPCState</c> 按电量比例写进消费者组件；
        ///   · × 60 换成 J/s（= W），再 × 网络的 <c>consumerRatio</c> 才是真正拿到的部分
        ///     （供电不足时电网只给一部分，这个比例同时决定站体能不能工作）。
        /// 所以它的上限就是 <c>workEnergyPerTick × 60</c> = 120 MW，正好和面板上的
        /// 「电池 … / +x MW」对得上。
        /// </summary>
        public static float GridDrawMw(PlanetFactory factory, StationComponent sc)
        {
            if (factory == null || sc == null || sc.pcId <= 0)
                return 0f;
            PowerSystem ps = factory.powerSystem;
            if (ps == null || ps.consumerPool == null || ps.netPool == null)
                return 0f;
            if (sc.pcId >= ps.consumerPool.Length)
                return 0f;

            PowerConsumerComponent c = ps.consumerPool[sc.pcId];
            int net = c.networkId;
            if (net <= 0 || net >= ps.netPool.Length)
                return 0f;
            PowerNetwork pn = ps.netPool[net];
            if (pn == null || pn.consumerRatio <= 0.0)
                return 0f;

            double w = (double)c.requiredEnergy * 60.0 * pn.consumerRatio;
            if (w <= 0.0)
                return 0f;
            return (float)(w / 1e6);
        }

        private static int NetworkId(PlanetFactory factory, StationComponent sc)
        {
            PowerSystem ps = factory.powerSystem;
            return ps?.consumerPool != null && sc.pcId > 0 && sc.pcId < ps.consumerPool.Length
                ? ps.consumerPool[sc.pcId].networkId
                : 0;
        }

        private static float Ratio(PlanetFactory factory, StationComponent sc)
        {
            PowerSystem ps = factory.powerSystem;
            int net = NetworkId(factory, sc);
            return ps?.netPool != null && net > 0 && net < ps.netPool.Length && ps.netPool[net] != null
                ? (float)ps.netPool[net].consumerRatio
                : 0f;
        }

        /// <summary>
        /// 自动矿格（右列）的每格容量跟「矿物利用」等级走：科技解锁 2 万，每级 +3 万，Lv16 到顶 50 万
        /// （曲线见 <see cref="MiningTech.AutoCap"/>）。
        ///
        /// 三个注意点：
        ///   ① 只动右列 <c>[AutoSlotStart, MaxItemKinds)</c>；左列是玩家手动配货区，永远按原版满容量；
        ///   ② <c>max</c> 是**会存档**的字段，所以不做「每帧刷」：1 s 一轮，值没变就一次都不写；
        ///   ③ 老存档的格子里可能已经存着超过新容量的货 —— 库存不动，只是不再进新货，
        ///      显示成「满格」，和原版把 max 调小是同一种表现。
        /// </summary>
        private static void SyncAutoCap(StationComponent sc, int key, float dt)
        {
            float acc;
            _capAcc.TryGetValue(key, out acc);
            acc += dt;
            if (acc < 1f)
            {
                _capAcc[key] = acc;
                return;
            }
            _capAcc[key] = 0f;

            StationStore[] st = sc.storage;
            if (st == null || st.Length <= C.AutoSlotStart)
                return;

            MiningTech.Refresh(Time.unscaledTime);

            int extra = GameMain.history != null ? GameMain.history.remoteStationExtraStorage : 0;
            int cap = MiningTech.AutoSlotCap(extra);

            int from = Mathf.Clamp(C.AutoSlotStart, 0, st.Length);
            bool wrote = false;
            for (int i = from; i < st.Length; i++)
            {
                if (st[i].max == cap)
                    continue;
                st[i].max = cap;
                wrote = true;
            }
            if (!wrote)
                return;

            int before;
            _capNow.TryGetValue(key, out before);
            _capNow[key] = cap;
            if (before != cap)
                Plugin.Log.LogInfo("自动矿格容量: 站 id=" + sc.id + " 每格 " + before + " → " + cap +
                                   "（矿物利用 Lv" + MiningTech.Level + "，" + (st.Length - from) +
                                   " 格合计 " + Short((long)cap * (st.Length - from)) + "）");
        }

        // ==================== 自动配货 ====================

        /// <summary>
        /// 本星球的**海洋资源**物品 id（水 / 硫酸），没有海洋返回 0。
        ///
        /// 原版抽水站不是矿机的一种矿脉：它是 <c>EMinerType.Water</c>，产物直接取
        /// <c>planet.waterItemId</c>（见 MinerComponent 里 Water 分支）。
        /// 底座「打洞取本星球物资」当然应该把海洋也算一份 —— 之前只扫 veinPool，
        /// 水星球上 20 个卡槽里永远不会有水。
        /// </summary>
        public static int OceanItem(PlanetFactory factory)
        {
            if (!C.EnableOceanMining || factory == null)
                return 0;
            PlanetData pd = factory.planet;
            return pd != null && pd.waterItemId > 0 ? pd.waterItemId : 0;
        }

        /// <summary>
        /// 通电后把本星球矿物按储量降序配进**右列**卡槽（<c>[AutoSlotStart, MaxItemKinds)</c>，
        /// 从 10 格起，每格 50 万）。
        ///
        /// 0.8.0 起卡槽分两列语义（用户要求）：
        ///   左列 <c>[0, 10)</c> —— 玩家自己配货，本模块**绝不触碰**；
        ///   右列 <c>[10, 20)</c> —— 自动扫描本星球矿物填入，专供「采矿 + 存储」。
        /// 所以「已配货」的判定只看右列：玩家往左列放过东西，不该阻止自动填充。
        /// 写法对齐 StationComponent.Init 里 isCollector 分支填 collectionIds 的方式。
        /// </summary>
        private static bool AutoFill(PlanetFactory factory, StationComponent sc)
        {
            StationStore[] st = sc.storage;
            if (st == null || st.Length == 0)
                return false;

            int from = Mathf.Clamp(C.AutoSlotStart, 0, st.Length);
            int slots = st.Length - from;
            if (slots <= 0)
                return false;                     // 卡槽没扩到 20（老存档）：不自动填

            for (int i = from; i < st.Length; i++)
                if (st[i].itemId > 0)
                    return false;                 // 右列已有货：不再覆盖

            List<int> items = PlanetVeins(factory);
            if (items.Count == 0)
                return false;                     // 本星球没矿脉：留空，等玩家手动配

            int extra = GameMain.history != null ? GameMain.history.remoteStationExtraStorage : 0;
            // 右列：容量跟「矿物利用」等级走（2 万起，每级 +3 万，封顶 50 万）；见 MiningTech.AutoSlotCap。
            // 左列是玩家手动配货区，仍按原版满容量，不受科技限制。
            MiningTech.Refresh(Time.unscaledTime);        // 首次配货可能早于 SyncAutoCap 的 1 s 节流
            int cap = MiningTech.AutoSlotCap(extra);
            int full = C.MaxItemCount + extra;
            int n = Mathf.Min(items.Count, slots);

            for (int i = 0; i < n; i++)
            {
                // 本地点对点喂给传送带、星际靠运输机出货：两路都设成「供应」，
                // 站里一有矿就能被拉走（这正是「开始采矿供应」要的效果）。
                // 非星际站的原版口径是把 remoteLogic 压成 None（见 PlanetTransport.SetStationStorage），
                // 这里照同一条规则来，免得存档里留下一个永不生效的「星际供应」。
                ELogisticStorage rem = sc.isStellar ? ELogisticStorage.Supply : ELogisticStorage.None;
                st[from + i] = new StationStore(items[i], 0, 0, 0, 0, cap,
                                                ELogisticStorage.Supply, rem);
            }
            for (int i = from + n; i < st.Length; i++)
            {
                st[i].itemId = 0;
                st[i].count = 0;
                st[i].max = cap;
                st[i].localLogic = ELogisticStorage.None;
                st[i].remoteLogic = ELogisticStorage.None;
            }
            // 左列空槽补容量：玩家往里放货时 max 是 0 会显示成满格
            for (int i = 0; i < from && i < st.Length; i++)
                if (st[i].max <= 0)
                    st[i].max = full;

            var sb = new StringBuilder(160);
            int ocean = OceanItem(factory);
            int oil = OilItem(factory);
            sb.Append("钻孔已启动: 本星球资源 ").Append(items.Count).Append(" 种 → 自动填充右列 ")
              .Append(n).Append(" 格（第 ").Append(from + 1).Append("~").Append(from + n)
              .Append(" 格，每格 ").Append(cap).Append("）: ");
            for (int i = 0; i < n; i++)
            {
                ItemProto ip = LDB.items.Select(items[i]);
                if (i > 0) sb.Append(" · ");
                sb.Append(ip != null ? ip.name : ("#" + items[i]));
                if (ocean > 0 && items[i] == ocean) sb.Append("（海洋·无限）");
                else if (oil > 0 && items[i] == oil) sb.Append("（油渗）");
            }
            sb.Append("　| 左列 1~").Append(from).Append(" 格留给玩家手动配货");
            Plugin.Log.LogInfo(sb.ToString());
            return true;
        }

        /// <summary>
        /// 老存档迁移：0.9.4 之前右列只配了矿脉，海洋（水 / 硫酸）和原油没进去。
        /// 这里把缺的那几种补进右列，规则很保守 ——
        ///   · 只往「空槽（itemId=0）」或「货量为 0 的槽」里写，**绝不动已有库存**；
        ///   · 没有可用槽位就放弃（返回 false，下一轮再试，玩家腾出槽位就能补上）。
        /// 返回 true = 没什么可补的了（不再重试）。
        /// </summary>
        private static bool MigrateAutoSlots(PlanetFactory factory, StationComponent sc)
        {
            StationStore[] st = sc.storage;
            if (st == null)
                return true;
            int from = Mathf.Clamp(C.AutoSlotStart, 0, st.Length);
            if (from >= st.Length)
                return true;

            int ocean = OceanItem(factory);
            int oil = OilItem(factory);

            bool needOcean = ocean > 0 && !HasColumn(st, from, ocean);
            bool needOil = oil > 0 && !HasColumn(st, from, oil);
            if (!needOcean && !needOil)
                return true;

            int extra = GameMain.history != null ? GameMain.history.remoteStationExtraStorage : 0;
            int cap = MiningTech.AutoSlotCap(extra);
            ELogisticStorage rem = sc.isStellar ? ELogisticStorage.Supply : ELogisticStorage.None;

            bool ok = true;
            if (needOcean && !PutIntoFreeSlot(st, from, ocean, cap, rem))
                ok = false;
            if (needOil && !HasColumn(st, from, oil) && !PutIntoFreeSlot(st, from, oil, cap, rem))
                ok = false;

            if (ok)
            {
                var sb = new StringBuilder(120);
                sb.Append("钻孔迁移: 右列补配 ");
                if (needOcean) sb.Append("海洋(").Append(ocean).Append(") ");
                if (needOil) sb.Append("原油(").Append(oil).Append(") ");
                sb.Append("→ 现有库存未改动");
                Plugin.Log.LogInfo(sb.ToString());
            }
            return ok;
        }

        private static bool HasColumn(StationStore[] st, int from, int itemId)
        {
            for (int i = from; i < st.Length; i++)
                if (st[i].itemId == itemId)
                    return true;
            return false;
        }

        /// <summary>往右列找一个能安全写入的槽：先空槽，再「有物品但货量为 0」的槽。</summary>
        private static bool PutIntoFreeSlot(StationStore[] st, int from, int itemId,
                                            int cap, ELogisticStorage rem)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = from; i < st.Length; i++)
                {
                    bool usable = pass == 0 ? st[i].itemId == 0 : (st[i].itemId > 0 && st[i].count <= 0);
                    if (!usable)
                        continue;
                    st[i] = new StationStore(itemId, 0, 0, 0, 0, cap,
                                             ELogisticStorage.Supply, rem);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 本星球「底座能取到的物资源」的物品 id 列表 —— <see cref="AutoFill"/> 用它决定自动格填什么。
        ///
        /// 顺序是有讲究的（右列只有 10 格，挤不下时会被截断）：
        ///   ① 海洋资源（水 / 硫酸）—— 无限，而且没有它很多产线直接停摆；
        ///   ② 原油 —— 星际物流的硬通货，且只在少数星球有油渗；
        ///   ③ 其余矿脉，按总储量降序。
        /// 之前只按储量排序，水星球上「水」压根不在列表里、原油常常被挤到第 11 位之后，
        /// 于是面板上一格都不显示 —— 这就是用户说的「水和原油都是物资资源，需要采集」。
        /// </summary>
        public static List<int> PlanetVeins(PlanetFactory factory)
        {
            var result = new List<int>(Mathf.Max(1, C.AutoSlotStart));

            int ocean = OceanItem(factory);
            if (ocean > 0)
                result.Add(ocean);

            VeinStats s = PlanetVeinStats(factory);
            if (s == null)
                return result;

            // 原油优先：它也是一种矿脉（EVeinType.Oil），储量大但不该排在最后
            int oil = OilItem(factory);
            if (oil > 0 && !result.Contains(oil))
                result.Add(oil);

            if (s.Items != null)
                for (int i = 0; i < s.Items.Count; i++)
                    if (!result.Contains(s.Items[i]))
                        result.Add(s.Items[i]);
            return result;
        }

        /// <summary>
        /// 本星球**原油**的物品 id（有油渗返回原版原油 id，否则 0）。
        /// 油渗在 <c>veinPool</c> 里是 <c>EVeinType.Oil</c> 的矿脉，产物就是原油
        /// （PlanetAlgorithm 里 <c>vein.productId = veinProducts[...]</c>）。
        /// </summary>
        public static int OilItem(PlanetFactory factory)
        {
            if (factory == null || factory.veinPool == null)
                return 0;
            VeinData[] pool = factory.veinPool;
            for (int i = 1; i < pool.Length; i++)
            {
                VeinData v = pool[i];
                if (v.id == i && v.type == EVeinType.Oil && v.productId > 0)
                    return v.productId;
            }
            return 0;
        }

        /// <summary>
        /// 调用原版采样（<c>PowerSystem.CalculateGeothermalStrength</c>，与地热发电机同一条公式）。
        ///
        /// 0.9.5 去掉了「水星球固定 0.60」的兜底：那条兜底会让面板读数与真正的发电机组
        /// （NewGeneratorComponent 内部也是调这个函数）对不上 —— 同一个站，面板显示 0.60、
        /// 电网里按 3.00 发电。现在两边同源，显示多少就发多少。
        /// 原版这条函数对「有水 + baseRuinId=0」会走遗迹分支返回 3.0，等于水星球地热极好，
        /// 这与游戏里地热发电机在水星球上的行为一致，照收。
        /// </summary>
        private static float Sample(PlanetFactory factory, Vector3 pos, Quaternion rot)
        {
            float s = 1f;
            try
            {
                if (factory.powerSystem != null)
                    s = factory.powerSystem.CalculateGeothermalStrength(pos, rot, 0);
            }
            catch
            {
                s = 1f;
            }
            return Mathf.Clamp(s, C.GeoMinStrength, C.GeoMaxStrength);
        }

        // ==================== 星球矿产统计 ====================

        /// <summary>
        /// 本星球矿脉的一张快照。面板右列要逐矿展示「储量 / 处数 / 站内库存」，
        /// 状态行要「种类数 / 总储量」，都从这里取，避免每次刷新重算字典。
        /// </summary>
        internal sealed class VeinStats
        {
            /// <summary>物品 id 列表：第 0 位是海洋资源（有的话），其后按剩余总储量降序。</summary>
            public readonly List<int> Items = new List<int>(16);
            public readonly Dictionary<int, long> Amount = new Dictionary<int, long>(16);
            public readonly Dictionary<int, int> Areas = new Dictionary<int, int>(16);
            /// <summary>海洋资源（水 / 硫酸）的物品 id，0 = 本星球没有海洋。</summary>
            public int OceanItem;
            /// <summary>矿脉总储量（不含海洋 —— 海洋是无限的，加进去会让总量失真）。</summary>
            public long Total;
            public bool HasOre { get { return Items.Count > 0; } }

            /// <summary>这种资源是不是「取之不尽」（海洋）。面板显示「∞」而不是具体储量。</summary>
            public bool IsInfinite(int itemId)
            {
                return OceanItem > 0 && itemId == OceanItem;
            }
        }

        private static VeinStats _stats;
        private static int _statsPlanet = -1;
        private static float _statsAt = -99f;

        /// <summary>
        /// 统计本星球可采资源（按星球 + 1.5 s 缓存）。
        ///
        /// 三个数据源：
        ///   ① <c>veinGroups</c> —— 探矿系统维护的矿脉群表，含量最准，还带「处数」；
        ///   ② <c>veinPool</c>  —— 单条矿脉。气体/液体巨星、以及部分星球的群表为空，
        ///      只读群表会得出「本星球没有矿」的错误结论；另外群表漏掉的矿（例如油渗）
        ///      也要靠它补进来 —— 所以现在是「群表 + 逐条补齐」，不是二选一；
        ///   ③ <c>planet.waterItemId</c> —— 海洋资源（水 / 硫酸），原版抽水站的口径。
        /// </summary>
        public static VeinStats PlanetVeinStats(PlanetFactory factory)
        {
            if (factory == null)
                return null;

            int pid = factory.planet != null ? factory.planet.id : 0;
            float now = Time.realtimeSinceStartup;
            if (_stats != null && _statsPlanet == pid && now - _statsAt < 1.5f)
                return _stats;

            var s = new VeinStats();
            try
            {
                VeinGroup[] groups = factory.veinGroups;
                if (groups != null)
                {
                    for (int i = 0; i < groups.Length; i++)
                    {
                        VeinGroup g = groups[i];
                        if (g.isEmpty || g.amount <= 0)
                            continue;
                        int item = 0;
                        VeinProto vp = LDB.veins.Select((int)g.type);
                        if (vp != null)
                            item = vp.MiningItem;
                        if (item <= 0)
                            continue;

                        long a;
                        s.Amount.TryGetValue(item, out a);
                        s.Amount[item] = a + g.amount;
                        int c;
                        s.Areas.TryGetValue(item, out c);
                        s.Areas[item] = c + g.count;
                    }
                }

                // 群表漏掉的矿（或群表为空）用逐条矿脉补齐：只补「还没统计过的物品」，
                // 不会把已经统计过的矿重复累加。
                if (factory.veinPool != null)
                {
                    VeinData[] pool = factory.veinPool;
                    for (int i = 1; i < pool.Length; i++)
                    {
                        VeinData v = pool[i];
                        if (v.id != i || v.productId <= 0)
                            continue;
                        if (s.Amount.ContainsKey(v.productId))
                            continue;
                        s.Amount[v.productId] = Mathf.Max(1, v.amount);
                        s.Areas[v.productId] = 1;
                    }
                }

                s.Items.AddRange(s.Amount.Keys);
                s.Items.Sort((x, y) => s.Amount[y].CompareTo(s.Amount[x]));
                for (int i = 0; i < s.Items.Count; i++)
                    s.Total += s.Amount[s.Items[i]];

                // 海洋资源插到最前：它是无限的、也是水星球唯一能「就地取」的物资
                s.OceanItem = OceanItem(factory);
                if (s.OceanItem > 0)
                {
                    s.Items.Remove(s.OceanItem);
                    s.Items.Insert(0, s.OceanItem);
                    s.Amount[s.OceanItem] = -1L;      // -1 = 无限（面板据此显示 ∞）
                    if (!s.Areas.ContainsKey(s.OceanItem))
                        s.Areas[s.OceanItem] = 1;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("统计星球矿产失败: " + e.Message);
            }

            _stats = s;
            _statsPlanet = pid;
            _statsAt = now;
            return s;
        }

        /// <summary>站内该物品的总库存（左右两列一起算 —— 都是「这个站存了多少这种矿」）。</summary>
        public static long StationStock(StationComponent sc, int itemId)
        {
            if (sc == null || sc.storage == null || itemId <= 0)
                return 0L;
            long sum = 0L;
            StationStore[] st = sc.storage;
            for (int i = 0; i < st.Length; i++)
                if (st[i].itemId == itemId)
                    sum += st[i].count;
            return sum;
        }

        // ==================== 面板文案 ====================

        /// <summary>万 / 亿 缩写。</summary>
        internal static string Short(long v)
        {
            if (v >= 100000000L)
                return (v / 100000000.0).ToString("0.##") + "亿";
            if (v >= 10000L)
                return (v / 10000.0).ToString("0.#") + "万";
            return v.ToString();
        }
    }
}
