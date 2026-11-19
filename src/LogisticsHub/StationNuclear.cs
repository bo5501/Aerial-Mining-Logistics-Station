using System;
using System.Collections.Generic;
using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 【0.9.30】第 10 格「核电燃料格」：放燃料棒，烧它给站内电池充电。
    ///
    /// 为什么做在卡槽上而不是另造一台发电机：
    ///   · 玩家已经把塔当「基地」在用，多一台实体发电机就要多一套建造/供电/并网逻辑；
    ///   · 原版站体的卡槽本来就带「供应 / 需求」—— 把第 10 格设成燃料棒的**需求**，
    ///     本地无人机与星际运输船会自动补货，等于白拿一整套物流；
    ///   · 电站的产出直接充进 <c>StationComponent.energy</c>（和地热、电网、货船起飞
    ///     同一个池子），所以塔顶电浆炮、采矿、护盾吃到的就是这份电。
    ///
    /// 数值全部对齐原版，不凭空发电：
    ///   · 燃料热值读 <c>ItemProto.HeatValue</c>（氘核燃料棒 600 MJ）；
    ///   · 输出 <see cref="C.FusionPowerMw"/> = 100 MW（0.9.36 由 15 → 80 → 100，玩家指定）；
    ///   · 一根棒能发的电是固定的，所以输出越高烧得越快：氘棒 6 s / 反物质棒 72 s /
    ///     奇异湮灭棒 720 s（时长 = 热值 ÷ 100 MW），燃料消耗速度玩家可以自己算。
    ///
    /// 电池满了就**暂停烧燃料**（省棒），面板显示「待机·电池满」。
    ///
    /// 【0.9.32】修「第 10 格放了氘核燃料棒却提示无燃料」：
    /// 0.9.30/0.9.31 是按**名字**找燃料的（IndexOf("Deuteron")），而
    /// <c>ItemProto.name</c> 是**本地化**字段 —— 中文版里它是「氘核燃料棒」，
    /// 里面没有 "Deuteron" 也没有 "Fuel Rod"，于是查找全落空、
    /// <c>_fuelId</c> 一直是 0，整个核电机制静默停用（日志只有一行
    /// 「LDB 里找不到氘核燃料棒」）。现在改成**只看热值**，与语言无关：
    /// 凡是 <c>HeatValue ≥ 400 MJ</c> 的物品都算「燃料棒」——
    /// 原版正好命中三根：氘核 600 MJ / 反物质 7.5 GJ / 奇异湮灭 9 GJ，
    /// 而化学燃料（煤 5.4 MJ、氢棒 54 MJ）全部低于这条线，不会误认。
    /// 第 10 格里放三者中的**任意一根**都能发电，默认只请求最便宜的那根。
    /// </summary>
    internal static class StationNuclear
    {
        private static bool _looked;
        /// <summary>可当燃料的棒，按热值升序（[0] = 最便宜的那根，作为默认请求项）。</summary>
        private static readonly List<int> _rodIds = new List<int>();
        /// <summary>棒 id → 热值（J）。</summary>
        private static readonly Dictionary<int, long> _rodHeat = new Dictionary<int, long>();
        /// <summary>识别阈值：低于这个热值的一律当普通燃料（煤/氢棒等），不接进核电逻辑。</summary>
        private const long MinRodHeat = 400_000_000L;
        /// <summary>
        /// 「蓄电器（满）」的物品 id。
        ///
        /// 【0.9.33】它必须被排除在燃料棒之外，这一点是 0.9.32 翻车的直接原因：
        /// 满蓄电器也有 <c>HeatValue = 540 MJ</c>（原版允许丢进火力发电机烧掉换电），
        /// 而 540 MJ **比氘棒的 600 MJ 还低**，于是「按热值升序、取最便宜那根当默认请求项」
        /// 这条规则把**满蓄电器**排到了第一位 —— 日志里那次识别是：
        /// <code>
        /// 识别到 4 种燃料棒（蓄电器（满） 540MJ/36s、氘核燃料棒 600MJ/40s、
        ///                    反物质燃料棒 7200MJ/480s、奇异湮灭燃料棒 72000MJ/4800s）
        /// 默认请求 蓄电器（满）
        /// </code>
        /// 第 10 格于是被铺成「蓄电器（满）· 需求 100」，玩家往里放氘棒时格子已被锁在
        /// 蓄电器上（原版 UI 在 itemId 不同的情况下不放行），面板一直显示「核电无燃料」。
        /// 满蓄电器是**电池**不是燃料棒，烧掉它等于烧掉一块已经充好的电，本来也不该当核电燃料。
        /// </summary>
        private const int FullAccumulatorItemId = 2205;

        /// <summary>每帧灌进电池的余数（J），避免小 dt 被取整吃掉。</summary>
        private static readonly Dictionary<int, double> _acc = new Dictionary<int, double>();
        /// <summary>已经烧了多久（s）：到 RodSeconds 就扣一根棒。</summary>
        private static readonly Dictionary<int, double> _burn = new Dictionary<int, double>();
        /// <summary>当前输出功率（MW）：0 = 没烧（没燃料 / 电池满 / 槽被占）。</summary>
        private static readonly Dictionary<int, float> _out = new Dictionary<int, float>();
        /// <summary>第 10 格被别的货物占着、暂时没法接管的站（只警告一次）。</summary>
        private static readonly HashSet<int> _blocked = new HashSet<int>();
        /// <summary>已经打过「燃料格已就绪」日志的站（每站一次，不再是全局一次）。</summary>
        private static readonly HashSet<int> _slotLogged = new HashSet<int>();
        /// <summary>上一帧第 10 格里的物品 id —— 用来识别「玩家刚把格子取回清空」。</summary>
        private static readonly Dictionary<int, int> _lastItem = new Dictionary<int, int>();
        /// <summary>格子被清空的时刻：这段时间内不抢着铺默认棒，留给玩家换种类。</summary>
        private static readonly Dictionary<int, float> _grace = new Dictionary<int, float>();
        /// <summary>清空后留多久（s）不自动铺默认燃料棒。</summary>
        private const float EmptyGraceSec = 12f;

        /// <summary>第 10 格默认请求的燃料棒 id（热值最低的那根；找不到返回 0）。</summary>
        internal static int FuelItemId
        {
            get
            {
                if (!_looked)
                    Lookup();
                return _rodIds.Count > 0 ? _rodIds[0] : 0;
            }
        }

        /// <summary>默认燃料棒的热值（J）。</summary>
        internal static long RodHeat
        {
            get
            {
                if (!_looked)
                    Lookup();
                int id = FuelItemId;
                return id > 0 ? HeatOf(id) : C.FallbackRodHeat;
            }
        }

        /// <summary>默认燃料棒在当前输出功率下能烧多久（s）。</summary>
        internal static float RodSeconds
        {
            get { return RodHeat / (C.FusionPowerMw * 1e6f); }
        }

        // ==================== 识别燃料 ====================

        /// <summary>
        /// 扫一遍物品表，把「燃料棒」挑出来。
        ///
        /// 判据只有一个：<c>HeatValue ≥ 400 MJ</c>。理由见类头注释 ——
        /// 名字字段是本地化的，按名字匹配在中文版里必然失败；而热值不受语言影响。
        /// </summary>
        private static void Lookup()
        {
            _looked = true;
            _rodIds.Clear();
            _rodHeat.Clear();
            try
            {
                ItemProto[] arr = LDB.items.dataArray;
                if (arr == null)
                    return;

                // 【0.9.33】三段筛选，越往后越宽松：
                //   ① 名字像燃料棒 + 热值 ≥ 400 MJ —— 中/英文版都能精确命中三种棒，
                //      这是首选口径（名字是本地化字段，所以只当"加强条件"，不当唯一条件）；
                //   ② 纯热值 ≥ 400 MJ，但排除满蓄电器（540 MJ，会排到氘棒前面）；
                //   ③ 热值 ≥ 40 MJ 兜底（40 MJ 档 = 氢燃料棒 54 MJ 那一级）。
                // 满蓄电器在三段里都被排除：它是电池，烧掉等于烧掉一块已经充好的电。
                Collect(arr, 0, true);          // ①
                if (_rodIds.Count == 0)
                    Collect(arr, 0, false);     // ②
                if (_rodIds.Count == 0)
                    Collect(arr, 40_000_000L, false);   // ③

                // 弱的排前面：第 10 格默认请求最便宜的那根（排除满蓄电器后就是氘核燃料棒），
                // 玩家想用更好的棒，自己往格里放一根就行，格子里是哪种就烧哪种。
                _rodIds.Sort(delegate (int a, int b) { return _rodHeat[a].CompareTo(_rodHeat[b]); });

                if (_rodIds.Count == 0)
                {
                    // 连 40 MJ 都框不到：把热值最高的 5 件打出来，下一次看日志就知道
                    // 该按什么口径认燃料了（不猜、不写死 id，也绝不改动 ItemProto 本身）。
                    var hot = new List<ItemProto>();
                    for (int i = 0; i < arr.Length; i++)
                        if (arr[i] != null && arr[i].HeatValue > 0)
                            hot.Add(arr[i]);
                    hot.Sort(delegate (ItemProto a, ItemProto b) { return b.HeatValue.CompareTo(a.HeatValue); });

                    string top = "";
                    for (int i = 0; i < hot.Count && i < 5; i++)
                    {
                        if (i > 0)
                            top += "、";
                        top += "#" + hot[i].ID + " " + hot[i].name + " " +
                               (hot[i].HeatValue / 1_000_000.0).ToString("0.###") + "MJ";
                    }
                    Plugin.Log.LogWarning("核电燃料: 物品表里没有任何「燃料棒」（热值 ≥ 400 MJ 与 ≥ 40 MJ 两轮都空），" +
                                          "第 10 格燃料机制停用；热值最高的几件 = " + top);
                    return;
                }

                string list = "";
                for (int i = 0; i < _rodIds.Count; i++)
                {
                    int id = _rodIds[i];
                    if (i > 0)
                        list += "、";
                    list += NameOf(id) + " " + (HeatOf(id) / 1_000_000.0).ToString("0.#") + "MJ/" +
                            (HeatOf(id) / (C.FusionPowerMw * 1e6)).ToString("0") + "s";
                }
                Plugin.Log.LogInfo("核电燃料: 识别到 " + _rodIds.Count + " 种燃料棒（" + list +
                                   "），基础输出 " + C.FusionPowerMw.ToString("0.#") + " MW；" +
                                   "第 " + (C.FuelSlot + 1) + " 格任意一种都认，默认请求 " +
                                   NameOf(_rodIds[0]) + "（" + C.FuelSlotMax + " 根上限）");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("核电燃料: 扫描燃料棒失败 — " + e.Message);
            }
        }

        /// <summary>
        /// 扫一遍物品表收燃料棒。<paramref name="minHeat"/> 为 0 时用 400 MJ 这条主线；
        /// <paramref name="requireRodName"/> 为 true 时还要求名字里带「燃料棒 / Fuel Rod」。
        /// 满蓄电器（<see cref="FullAccumulatorItemId"/>）任何一段都不收。
        /// </summary>
        private static void Collect(ItemProto[] arr, long minHeat, bool requireRodName)
        {
            long floor = minHeat > 0 ? minHeat : MinRodHeat;
            for (int i = 0; i < arr.Length; i++)
            {
                ItemProto p = arr[i];
                if (p == null || p.ID == FullAccumulatorItemId || p.HeatValue < floor)
                    continue;
                if (requireRodName && !LooksLikeRod(p))
                    continue;
                if (!_rodHeat.ContainsKey(p.ID))
                {
                    _rodIds.Add(p.ID);
                    _rodHeat[p.ID] = p.HeatValue;
                }
            }
        }

        /// <summary>名字里带「燃料棒」或 "Fuel Rod"。名字是本地化字段，只用作加强条件。</summary>
        private static bool LooksLikeRod(ItemProto p)
        {
            string n = p != null ? p.name : null;
            if (string.IsNullOrEmpty(n))
                return false;
            return n.IndexOf("燃料棒", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("Fuel Rod", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>这个物品 id 是不是燃料棒。</summary>
        internal static bool IsRod(int itemId)
        {
            if (!_looked)
                Lookup();
            return itemId > 0 && _rodHeat.ContainsKey(itemId);
        }

        /// <summary>某根棒的热值（J）；认不出就退回 <see cref="C.FallbackRodHeat"/>。</summary>
        internal static long HeatOf(int itemId)
        {
            if (!_looked)
                Lookup();
            long h;
            if (itemId > 0 && _rodHeat.TryGetValue(itemId, out h) && h > 0)
                return h;
            return C.FallbackRodHeat;
        }

        private static string NameOf(int itemId)
        {
            ItemProto p = LDB.items.Select(itemId);
            return p != null ? p.name : ("#" + itemId);
        }

        /// <summary>
        /// 面板用的短名：氘核燃料棒 → 氘棒，反物质燃料棒 → 反物质棒，
        /// 奇异湮灭燃料棒 → 奇异棒。认不出来的就叫「燃料棒」。
        /// </summary>
        internal static string ShortName(int itemId)
        {
            string n = NameOf(itemId) ?? "";
            if (n.IndexOf("反物质", StringComparison.Ordinal) >= 0)
                return "反物质棒";
            if (n.IndexOf("奇异", StringComparison.Ordinal) >= 0)
                return "奇异棒";
            if (n.IndexOf("氘", StringComparison.Ordinal) >= 0)
                return "氘棒";
            if (n.IndexOf("Anti", StringComparison.OrdinalIgnoreCase) >= 0)
                return "反物质棒";
            if (n.IndexOf("Strange", StringComparison.OrdinalIgnoreCase) >= 0)
                return "奇异棒";
            if (n.IndexOf("Deuteron", StringComparison.OrdinalIgnoreCase) >= 0)
                return "氘棒";
            return "燃料棒";
        }

        /// <summary>第 10 格里现在那种燃料棒的短名（空格或不是棒 → 默认请求项的名字）。</summary>
        internal static string FuelName(StationComponent sc)
        {
            if (sc != null && sc.storage != null)
            {
                int slot = C.FuelSlot;
                if (slot >= 0 && slot < sc.storage.Length && IsRod(sc.storage[slot].itemId))
                    return ShortName(sc.storage[slot].itemId);
            }
            int id = FuelItemId;
            return id > 0 ? ShortName(id) : "燃料棒";
        }

        // ==================== 第 10 格 ====================

        /// <summary>
        /// 第 10 格现在是不是（或能不能变成）燃料格。
        ///
        /// 三种情况：
        ///   ① 格里已经是一根燃料棒（氘 / 反物质 / 奇异都行）→ 认，只校正容量与供需；
        ///   ② 空格 → 铺成「默认燃料棒 + 需求」，让物流自动补货；
        ///   ③ 格里是别的货 → **不抢、不清空玩家库存**，只警告一次，等玩家自己清空。
        /// </summary>
        internal static bool EnsureSlot(StationComponent sc)
        {
            if (sc == null || sc.storage == null)
                return false;
            int item = FuelItemId;
            if (item <= 0)
                return false;

            int slot = C.FuelSlot;
            if (slot < 0 || slot >= sc.storage.Length)
                return false;

            StationStore s = sc.storage[slot];

            // 【0.9.34】玩家刚把格子「取回」清空（上一帧还有燃料棒、这一帧 itemId == 0）时，
            // 先别急着铺回默认氘棒 —— 原版「选择物品」按钮（selectItemButton）只在 itemId == 0
            // 的瞬间可见，抢在它前面铺默认会让玩家连点都点不到。这里留 12 s 窗口，
            // 玩家可以点「选择物品」换成反物质棒 / 奇异湮灭棒，或者直接手拿另一种棒点格子
            // （后者走 Patch_FuelSlotAnyRod，不依赖这个窗口）。
            int last;
            _lastItem.TryGetValue(sc.entityId, out last);
            if (last != 0 && s.itemId == 0)
                _grace[sc.entityId] = Time.realtimeSinceStartup;
            _lastItem[sc.entityId] = s.itemId;

            // ① 已经是燃料棒（任意一种）：原样留着，只把容量/供需补正。
            //    这样玩家手动放进去的反物质棒、奇异棒也能直接烧，不用先清空再等无人机送氘棒。
            if (IsRod(s.itemId))
            {
                bool dirty = false;
                if (s.max != C.FuelSlotMax)
                {
                    s.max = C.FuelSlotMax;
                    dirty = true;
                }
                // 这个格子必须一直是「需求」，否则物流不会往这儿送货。
                if (s.localLogic != ELogisticStorage.Demand)
                {
                    s.localLogic = ELogisticStorage.Demand;
                    dirty = true;
                }
                ELogisticStorage rem = sc.isStellar ? ELogisticStorage.Demand : ELogisticStorage.None;
                if (s.remoteLogic != rem)
                {
                    s.remoteLogic = rem;
                    dirty = true;
                }
                if (dirty)
                {
                    lock (sc.storage)
                        sc.storage[slot] = s;
                }
                _blocked.Remove(sc.entityId);
                if (_slotLogged.Add(sc.id))
                {
                    Plugin.Log.LogInfo("核电燃料格: 站 id=" + sc.id + " 第 " + (slot + 1) + " 格 = " +
                                       NameOf(s.itemId) + "（" + (HeatOf(s.itemId) / 1_000_000.0).ToString("0.#") +
                                       " MJ/根，" + C.FusionPowerMw.ToString("0.#") + " MW 下每根烧 " +
                                       (HeatOf(s.itemId) / (C.FusionPowerMw * 1e6)).ToString("0") + " s）");
                }
                return true;
            }

            // ③ 格里还有别的东西：不抢，也不清空玩家库存，等玩家自己清空。
            if (s.itemId != 0 && s.count > 0)
            {
                if (_blocked.Add(sc.entityId))
                {
                    ItemProto op = LDB.items.Select(s.itemId);
                    Plugin.Log.LogWarning("核电: 站 id=" + sc.id + " 第 " + (slot + 1) +
                                          " 格还放着「" + (op != null ? op.name : ("#" + s.itemId)) +
                                          "」×" + s.count + "，暂不接管（不是燃料棒）；" +
                                          "把这一格搬空后会自动变成燃料格（" + NameOf(item) + " · 需求 " +
                                          C.FuelSlotMax + " 根）");
                }
                return false;
            }

            float emptiedAt;
            if (_grace.TryGetValue(sc.entityId, out emptiedAt) &&
                Time.realtimeSinceStartup - emptiedAt < EmptyGraceSec)
                return false;                       // 玩家换棒的窗口还没过

            // ② 空格：铺默认燃料棒 + 需求。
            var store = new StationStore(item, 0, 0, 0, 0, C.FuelSlotMax,
                                         ELogisticStorage.Demand,
                                         sc.isStellar ? ELogisticStorage.Demand : ELogisticStorage.None);
            lock (sc.storage)
                sc.storage[slot] = store;

            _blocked.Remove(sc.entityId);
            if (_slotLogged.Add(sc.id))
            {
                Plugin.Log.LogInfo("核电燃料格: 站 id=" + sc.id + " 第 " + (slot + 1) +
                                   " 格 → " + NameOf(item) + "（需求 " + C.FuelSlotMax + " 根，" +
                                   C.FusionPowerMw.ToString("0.#") + " MW / 根 " +
                                   RodSeconds.ToString("0") + " s）");
            }
            return true;
        }

        // ==================== 每帧 ====================

        /// <summary>
        /// 每帧由 <see cref="StationDrill.Tick"/> 调用：先把第 10 格校正成燃料格，
        /// 有棒就往站内电池灌 <see cref="C.FusionPowerMw"/> MW，并按热值扣棒。
        ///
        /// 放在供电判定**之前**：核电本身就是一种供电来源，必须先算它，
        /// 否则「没电网、地热也不够、但有棒」的站会被判成断电而不启动。
        /// </summary>
        internal static void Tick(PlanetFactory factory, StationComponent sc, float dt)
        {
            if (sc == null || sc.storage == null || dt <= 0f)
                return;

            int key = sc.entityId;
            if (!EnsureSlot(sc))
            {
                _out[key] = 0f;
                return;
            }

            int slot = C.FuelSlot;
            int slotItem;
            int count;
            lock (sc.storage)
            {
                slotItem = sc.storage[slot].itemId;
                count = sc.storage[slot].count;
            }

            long eMax = sc.energyMax;
            // 没燃料 / 电池满：不烧棒（省燃料）。电池满时核电「待机」，面板会写出来。
            if (!IsRod(slotItem) || count <= 0 || eMax <= 0 || sc.energy >= eMax)
            {
                _out[key] = 0f;
                if (count <= 0)
                    _burn[key] = 0.0;
                return;
            }

            float mw = C.FusionPowerMw;
            // 每根能烧多久由**这根棒自己的热值**决定，能量守恒：
            // 100 MW 下 氘棒 600 MJ → 6 s、反物质棒 7.2 GJ → 72 s、奇异湮灭 72 GJ → 720 s。
            double rodSec = HeatOf(slotItem) / (mw * 1e6);
            if (rodSec < 0.1)
                rodSec = 0.1;

            // ---- 1) 灌电（double 累加，避免 16 ms × 100 MW 被截断）----
            double acc;
            _acc.TryGetValue(key, out acc);
            acc += (double)mw * 1e6 * dt;
            long add = (long)acc;
            acc -= add;
            _acc[key] = acc;

            if (add > 0)
            {
                long e = sc.energy + add;
                sc.energy = e > eMax ? eMax : e;
            }

            // ---- 2) 扣棒 ----
            double burn;
            _burn.TryGetValue(key, out burn);
            burn += dt;
            if (burn >= rodSec)
            {
                int rods = (int)(burn / rodSec);
                burn -= rods * rodSec;

                lock (sc.storage)
                {
                    ref StationStore s = ref sc.storage[slot];
                    int take = Mathf.Min(rods, s.count);
                    s.count -= take;
                    if (s.count <= 0)
                        burn = 0.0;                 // 烧完了，余数丢掉（下次装棒重新计）
                }
            }
            _burn[key] = burn;

            _out[key] = mw;
        }

        internal static void Forget(int entityId)
        {
            _acc.Remove(entityId);
            _burn.Remove(entityId);
            _out.Remove(entityId);
            _blocked.Remove(entityId);
            _slotLogged.Remove(entityId);
            _lastItem.Remove(entityId);
            _grace.Remove(entityId);
        }

        /// <summary>换星球（工厂重建）时清空全部运行时缓存。</summary>
        internal static void Clear()
        {
            _acc.Clear();
            _burn.Clear();
            _out.Clear();
            _blocked.Clear();
            _slotLogged.Clear();
            _lastItem.Clear();
            _grace.Clear();
        }

        // ==================== 给面板 / 日志 / 供电判定 ====================

        /// <summary>这个站的第 10 格里有没有棒（有没有能力发电）。</summary>
        internal static bool HasFuel(StationComponent sc)
        {
            if (sc == null || sc.storage == null)
                return false;
            int slot = C.FuelSlot;
            if (slot < 0 || slot >= sc.storage.Length)
                return false;
            return IsRod(sc.storage[slot].itemId) && sc.storage[slot].count > 0;
        }

        /// <summary>这一帧实际输出（MW）；没烧就是 0。</summary>
        internal static float OutputMw(StationComponent sc)
        {
            if (sc == null)
                return 0f;
            float v;
            return _out.TryGetValue(sc.entityId, out v) ? v : 0f;
        }

        /// <summary>第 10 格里的棒数量（不是棒就是 0）。</summary>
        internal static int FuelCount(StationComponent sc)
        {
            if (sc == null || sc.storage == null)
                return 0;
            int slot = C.FuelSlot;
            if (slot < 0 || slot >= sc.storage.Length)
                return 0;
            return IsRod(sc.storage[slot].itemId) ? sc.storage[slot].count : 0;
        }

        /// <summary>第 10 格的最大容量（面板要显示「12 / 100」）。</summary>
        internal static int FuelMax(StationComponent sc)
        {
            if (sc == null || sc.storage == null)
                return 0;
            int slot = C.FuelSlot;
            if (slot < 0 || slot >= sc.storage.Length)
                return 0;
            return IsRod(sc.storage[slot].itemId) ? sc.storage[slot].max : 0;
        }
    }
}
