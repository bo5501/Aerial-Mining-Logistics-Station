using System;
using System.Collections.Generic;
using System.Reflection;
using CommonAPI.Systems;
using HarmonyLib;
using UnityEngine;
using xiaoye97;

namespace LogisticsHub
{
    /// <summary>注册 ModelProto / ItemProto / RecipeProto 与本地化。</summary>
    internal static class Protos
    {
        public static bool Registered { get; private set; }
        private static bool _preloaded;

        /// <summary>
        /// RegisterTech 建出来的那个 TechProto 实例。<b>找科技必须用它，不能按 ID 扫表</b>：
        /// LDBTool 的 <c>IdBind</c> 会用配置文件（LDBTool.CustomID.cfg）里的历史值覆盖 proto.ID，
        /// 于是表里躺着的可能是 3996（0.9.13 留下的老配置），按 1997 去找就会扑空。
        /// </summary>
        public static TechProto Tech { get; private set; }

        public static bool IsPreloaded => _preloaded;

        private static bool _stringsDone;

        /// <summary>
        /// 本地化字符串必须<b>尽早</b>注册（CommonAPI 的字符串表在插件加载阶段就被消费了），
        /// 所以 Plugin.Awake 里单独调一次，别等到 LDBTool 的 PreAddDataAction。
        /// </summary>
        public static void EnsureStrings()
        {
            if (_stringsDone)
                return;
            _stringsDone = true;
            try { RegisterStrings(); }
            catch (Exception e) { Plugin.Log.LogError("字符串注册失败: " + e); }
        }

        public static void RegisterAll()
        {
            if (Registered)
                return;

            EnsureStrings();

            var model = new ModelProto
            {
                ID = C.ModelId,
                Name = C.ItemId.ToString(),
                SID = "",
                sid = "",
                OverrideName = "",
                ObjectType = EObjectType.Entity,
                RendererType = 0,
                RotSymmetry = 12,          // 12 边形基座，建造预览按 30° 步进旋转
                HpMax = 200000,
                HpUpgrade = 0,
                HpRecover = 0,
                RuinType = 0,
                RuinId = 0,
                RuinCount = 0,
                RuinLifeTime = 0,
                PrefabPath = "",           // 空 → 不去 Resources.Load，prefabDesc 由我们挂
            };
            LDBTool.PreAddProto(model);

            var item = new ItemProto
            {
                ID = C.ItemId,
                Name = C.LItemName,
                SID = "",
                Type = EItemType.Logistics,
                SubID = 0,
                StackSize = 10,
                Grade = 0,
                Upgrades = new int[0],
                IsEntity = true,
                CanBuild = true,
                BuildInGas = false,
                ModelIndex = C.ModelId,
                ModelCount = 1,
                HpMax = 12000,
                IconPath = "",
                Description = C.LItemDesc,
                GridIndex = C.GridIndex,
                BuildIndex = C.BuildIndex,
                BuildMode = 1,
                // 0 = 不在「永远解锁/战利品」特例里，改为看配方是否已解锁，
                // 而配方由科技 TechId 解锁（见 RegisterTech）。
                UnlockKey = 0,
                PreTechOverride = 0,
                DescFields = new int[0],
            };
            ApplyIcon(item);
            LDBTool.PreAddProto(item);

            // 配方占位：原料 id 等 LDB 就绪后按名字反查再填回（见 ResolveRecipeItems）
            var recipe = new RecipeProto
            {
                ID = C.RecipeId,
                Name = C.LRecipeName,
                SID = "",
                Type = ERecipeType.Assemble,
                TimeSpend = C.RecipeTimeSpend,
                Items = new[] { ItemProto.kAccumulator, ItemProto.kAccumulator, ItemProto.kAccumulator, ItemProto.kAccumulator },
                ItemCounts = new[] { C.RecipeItemCount1, C.RecipeItemCount2, C.RecipeItemCount3, C.RecipeItemCount4 },
                Results = new[] { C.ItemId },
                ResultCounts = new[] { 1 },
                GridIndex = C.GridIndex,
                Handcraft = true,
                Explicit = true,
                Description = C.LRecipeDesc,
                IconPath = "",
                IconTag = "",
                NonProductive = false,
            };
            LDBTool.PreAddProto(recipe);

            RegisterTech();

            Registered = true;
            Plugin.Log.LogInfo("proto 注册完成: model=" + C.ModelId + " item=" + C.ItemId +
                               " recipe=" + C.RecipeId + " buildIndex=" + C.BuildIndex);
        }

        // ---------------- 科技 ----------------

        /// <summary>
        /// 挂到「气态行星开采」那条线上的一格科技，解锁我们的配方；它的效果则交给
        /// 原版「矿物利用」（ID 3606）的等级：每升一级，钻孔采矿速度 +10%、
        /// 每次采矿作业消耗的矿脉 -6%（见 StationMiner）。
        /// </summary>
        private static void RegisterTech()
        {
            try
            {
                PickFreeTechId();

                int pre = FindGasGiantTechId();
                TechProto preTech = pre > 0 ? LDB.techs.Select(pre) : null;
                Vector2 pos = preTech != null ? FindTechSpot(preTech.Position) : new Vector2(0f, 0f);

                // 图标不再借前置科技那张。0.9.15 借过来之后，节点上就是一张放大的气态行星，
                // 跟「气态行星开采」长得一模一样，玩家第一眼只会觉得「这节点是坏的/是个复制品」。
                // 现在改成用本站自己的建筑图标（ApplyIcon 现画的那张 80×80），
                // 在 Preload 之后直接写进 TechProto._iconSprite（见 ApplyTechIcon）。
                // IconPath 留空还有一个好处：Preload/Preload2 里的 Resources.Load 会跳过，
                // 不会把我们写进去的 sprite 又冲掉。
                const string iconPath = "";
                const string iconTag = "";

                var tech = new TechProto
                {
                    ID = C.TechId,
                    Name = C.LTechName,
                    SID = "",
                    Desc = C.LTechDesc,
                    Conclusion = C.LTechConclusion,
                    Published = true,
                    IsHiddenTech = false,
                    IsObsolete = false,
                    IsLabTech = false,
                    // 一次性科技：原版（以及 ProjectGenesis 导出的原版数据）里这类节点
                    // Level = MaxLevel = 0，研究完成时 GameHistoryData 会写
                    // curLevel = maxLevel = 0，界面不显示「Lv1」。
                    Level = 0,
                    MaxLevel = 0,
                    LevelCoef1 = 0,
                    LevelCoef2 = 0,
                    PreItem = new int[0],
                    PreTechs = pre > 0 ? new[] { pre } : new int[0],
                    PreTechsImplicit = new int[0],
                    PreTechsMax = false,
                    // 研究材料（0.9.19）：电磁矩阵 / 能量矩阵 / 结构矩阵 各 2400 个。
                    // 原版的材料数量公式是 count = HashNeeded × ItemPoints[i] / 3600
                    // （见 GameHistoryData.UploadTech / TechProto.Preload 里 itemArray 的算法），
                    // 所以 HashNeeded=3600、ItemPoints=2400 恰好等于每种 2400 个；
                    // 三个 ItemPoints 相同也意味着三种矩阵是「等权」的，少一种就研究不动。
                    Items = new[] { 6001, 6002, 6003 },
                    ItemPoints = new[] { 2400, 2400, 2400 },
                    HashNeeded = 3600L,
                    UnlockRecipes = new[] { C.RecipeId },
                    UnlockFunctions = new int[0],
                    UnlockValues = new double[0],
                    AddItems = new int[0],
                    AddItemCounts = new int[0],
                    PropertyOverrideItems = new int[0],
                    PropertyItemCounts = new int[0],
                    Position = pos,
                    IconPath = iconPath,
                    IconTag = iconTag,
                };

                // 记住这个实例：LDBTool 在 PreAddProto 里会拿配置文件里的 CustomID
                // 覆盖 proto.ID，之后就不能再靠 ID 去找它了（见 EnforceTechId）。
                Tech = tech;
                LDBTool.PreAddProto(tech);
                EnforceTechId(tech);

                Plugin.Log.LogInfo("科技注册: id=" + tech.ID + "（页 " + (tech.ID < 2000 ? 0 : 1) + "）" +
                                   " 前置=" + pre +
                                   (preTech != null ? "（" + preTech.Name + "）" : "（未找到气态行星开采，退回独立科技）") +
                                   " 位置=(" + pos.x + "," + pos.y + ")" +
                                   (preTech != null ? " 前置位置=(" + preTech.Position.x + "," + preTech.Position.y + ")" : "") +
                                   " 图标=" + (iconPath.Length > 0 ? iconPath : "（本站自绘，Preload 后写入）"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("科技注册失败: " + e);
            }
        }

        /// <summary>
        /// LDBTool 的 <c>PreAddProto → Bind → IdBind</c> 会用 <c>LDBTool.CustomID.cfg</c> 里的
        /// 同名配置项覆盖我们刚设好的 <c>proto.ID</c>。玩家升到 0.9.14 时配置文件里还留着
        /// 0.9.13 的 <c>3996</c>，于是：
        ///   ① 科技被塞回 ≥2000 → <c>page = 1</c>，节点跑到升级页；
        ///   ② <see cref="FindTech"/> 按 1997 找不到它 → <see cref="PreloadTech"/> 直接 return，
        ///      Preload2 没跑 → <c>unlockRecipeArray</c> 为 null →
        ///      原版 <c>UITechNode.UpdateInfoComplete</c> 在
        ///      <c>unlockIconBox.sizeDelta = … unlockRecipeArray.Length …</c> 处 NRE（IL_038F）。
        /// 所以这里改回来，顺手把配置项也改回来，免得每次启动都打架。
        /// </summary>
        private static void EnforceTechId(TechProto tech)
        {
            if (tech == null || tech.ID == C.TechId)
                return;

            int got = tech.ID;
            tech.ID = C.TechId;
            Plugin.Log.LogWarning("科技 ID 被 LDBTool 配置改成 " + got +
                                  "（≥2000 会落到升级页，且后续按 " + C.TechId + " 找不到它），已强制改回 " + C.TechId);
            RewriteLdbToolCustomId(tech.Name, C.TechId);
        }

        /// <summary>把 LDBTool 的 CustomID 配置项写成我们想要的 ID（失败也不影响本次运行）。</summary>
        private static void RewriteLdbToolCustomId(string name, int want)
        {
            try
            {
                // LDBTool.IDDict：List<Dictionary<string(proto.Name), ConfigEntry<int>>>
                FieldInfo fld = AccessTools.Field(typeof(LDBTool), "IDDict");
                var lists = fld?.GetValue(null) as System.Collections.IEnumerable;
                if (lists == null || string.IsNullOrEmpty(name))
                {
                    Plugin.Log.LogWarning("改 LDBTool 配置: 找不到 IDDict，配置里可能还留着旧 ID");
                    return;
                }

                foreach (object entry in lists)
                {
                    if (!(entry is System.Collections.IDictionary dict) || !dict.Contains(name))
                        continue;
                    if (dict[name] is BepInEx.Configuration.ConfigEntry<int> cfg)
                    {
                        cfg.Value = want;
                        Plugin.Log.LogInfo("已把 LDBTool 配置项 " + name + " 改回 " + want +
                                           "（下次启动就不会再覆盖了）");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("改 LDBTool 配置失败（不影响本次运行）: " + e.Message);
            }
        }

        /// <summary>
        /// 保证当前存档的 <c>techStates</c> 里有本 mod 科技。缺了会：
        ///   ① 科技永远判成未解锁；
        ///   ② 原版 UITechNode 选中节点时 <c>techStates[id]</c> 直接下标取值 → KeyNotFoundException；
        ///   ③ AlterCurrentTech 写 hashNeeded 时同样崩。
        ///
        /// **调用时机是硬约束（0.9.42 存档事故的根因）**：只能在「表已经建好」之后调，
        /// 也就是 <c>GameHistoryData.Import</c> 跑完 / <c>GameData.SetForNewGame</c> 之后，
        /// 见 Patch_GameDataImport / Patch_GameDataSetForNewGame / Patch_HistoryImport。
        /// <b>绝不能挂在 <c>GameHistoryData.Init</c> 上</b>：Init 只负责把字典清空，
        /// 紧接着的 <c>Import</c> 对存档里的条目是不判重直接 <c>Add</c> 的，
        /// 我们先塞一条进去 = 读档时 <c>ArgumentException: 已添加了具有相同键的项</c>
        /// → 整次读档失败（「无效存档」/「存档损坏」）。
        ///
        /// 另外写回一律用索引器而不是 <c>Add</c>：多一个入口（别的 mod 也补过、
        /// 或同一帧被调两次）也只是覆盖成同样的初始状态，不会抛异常把读档带崩。
        /// </summary>
        public static void EnsureTechState(GameHistoryData h = null)
        {
            try
            {
                if (h == null)
                    h = GameMain.history;
                TechProto tech = Tech ?? FindTech();
                if (h?.techStates == null || tech == null)
                    return;

                int id = tech.ID;
                if (id <= 0 || id >= 12000)   // TechProto 用 new int[12000] 递归，ID 越界会崩
                    return;
                if (h.techStates.ContainsKey(id))
                    return;

                // 0.9.13 用的是 3996（在升级页，实际点不到）。万一有玩家真解锁了，把进度搬过来。
                const int OldTechId = 3996;
                bool migrated = id != OldTechId && h.techStates.ContainsKey(OldTechId) &&
                                h.techStates[OldTechId].unlocked;
                if (migrated)
                {
                    h.techStates[id] = new TechState(true, tech.MaxLevel, tech.MaxLevel, 0L, 0L, 0)
                    { unlockTick = GameMain.gameTick };
                    Plugin.Log.LogInfo("科技状态迁移: 旧 ID " + OldTechId + " 已解锁 → 新 ID " + id);
                }
                else
                {
                    // 字段口径照 GameHistoryData.Import：curLevel = Level、maxLevel = MaxLevel、
                    // hashNeeded = GetHashNeeded(Level)（两个 coef 都是 0，等于 HashNeeded）。
                    h.techStates[id] = new TechState(false, tech.Level, tech.MaxLevel, 0L,
                                                    tech.GetHashNeeded(tech.Level), 0);
                    Plugin.Log.LogInfo("科技状态补建: id=" + id + " 需要 hash=" + tech.GetHashNeeded(tech.Level));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("补建科技状态失败: " + e.Message);
            }
        }

        /// <summary>
        /// 给 UITechNode 兜底：原版 <c>UpdateInfoComplete</c> 会直接读
        /// <c>techProto.unlockRecipeArray.Length</c> / <c>itemArray.Length</c>，这些派生数组只在
        /// <c>TechProto.Preload2()</c> 里建。少跑一次就是 NullReferenceException。
        /// </summary>
        public static void EnsureTechArrays(TechProto proto)
        {
            if (proto == null)
                return;
            if (proto.preTechArray != null && proto.postTechArray != null &&
                proto.unlockRecipeArray != null && proto.itemArray != null)
                return;

            try
            {
                Plugin.Log.LogWarning("科技 " + proto.ID + " 的派生数组缺失（Preload2 没跑过），补一次");
                proto.Preload();
                proto.Preload2();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("补跑 TechProto.Preload2 失败: " + e);
            }
        }

        /// <summary>
        /// 挑一个没被占用的科技 ID（1900~1999）。为什么不写死 3996：<c>TechProto.page</c>
        /// 是 <c>ID >= 2000 ? 1 : 0</c>，≥2000 的科技会被 UITechTree 挂到 graphGroup1
        /// （升级页），主科技树上既没有这个节点、也没有它到前置的那条线。
        /// 区间上限 1999 已被别的 mod（EnergyHub）用掉，所以从高往低找第一个空号。
        /// </summary>
        private static void PickFreeTechId()
        {
            if (C.TechId < 2000 && C.TechId >= 1000 && !TechExists(C.TechId))
                return;

            for (int id = C.TechIdMax; id >= C.TechIdMin; id--)
            {
                if (TechExists(id))
                    continue;
                Plugin.Log.LogWarning("科技 ID " + C.TechId + " 已被占用，改用 " + id);
                C.TechId = id;
                return;
            }
            Plugin.Log.LogError("科技 ID 区间 " + C.TechIdMin + "~" + C.TechIdMax +
                                " 全被占用，本站科技可能与别的 mod 冲突");
        }

        /// <summary>
        /// 不用 <c>LDB.techs.Exist()</c>：它查的是 dataIndices，而 LDBTool 只是把 proto
        /// 追加进 dataArray，索引要等 <see cref="RepairIndices"/> 才补齐。这里直接扫表。
        /// </summary>
        private static bool TechExists(int id)
        {
            var set = LDB.techs;
            if (set?.dataArray == null)
                return false;
            for (int i = 0; i < set.dataArray.Length; i++)
                if (set.dataArray[i] != null && set.dataArray[i].ID == id)
                    return true;
            return false;
        }

        /// <summary>按中英文名找「气态行星开采」。TechProto.name 是本地化名，Name 是 key。</summary>
        private static int FindGasGiantTechId()
        {
            var set = LDB.techs;
            if (set?.dataArray == null)
                return 0;
            for (int i = 0; i < set.dataArray.Length; i++)
            {
                TechProto t = set.dataArray[i];
                if (t == null || t.ID == C.TechId)
                    continue;
                string loc = t.name ?? "";
                string key = t.Name ?? "";
                if (loc.Contains("气态行星") || key.Contains("Gas Giant") ||
                    loc.Contains("Gas Giant") || key.Contains("Gas Giants"))
                    return t.ID;
            }
            return 0;
        }

        /// <summary>
        /// 在 origin 同一行往两侧找一个空格。<b>格距必须是 4</b>：统计 ProjectGenesis 导出的
        /// 原版 143 条科技坐标，相邻前置/后继的位移是 (4,0) 92 次、(4,±4) 44 次、(4,±8) 9 次，
        /// 没有一个是 2。0.9.16 之前按 2 格摆放，节点会压在前置那张展开卡的右半边，
        /// 而且原版连线的长度是
        /// <c>节点2.x - (节点1右边 + 140)</c>，2 格（≈94 px）减 140 直接变负数 ——
        /// 箭头 sizeDelta.x 为负，于是「缺流程线」。
        /// </summary>
        private static Vector2 FindTechSpot(Vector2 origin)
        {
            var set = LDB.techs;
            for (int step = 1; step <= 8; step++)
            {
                Vector2 r = new Vector2(origin.x + 4f * step, origin.y);
                if (!TechOccupied(set, r, null))
                    return r;
                Vector2 l = new Vector2(origin.x - 4f * step, origin.y);
                if (!TechOccupied(set, l, null))
                    return l;
            }
            // 整行都挤满了就按原版的斜向分支位（±4 的整数倍）往下挪
            for (int row = 1; row <= 4; row++)
            {
                Vector2 p = new Vector2(origin.x + 4f, origin.y - 4f * row);
                if (!TechOccupied(set, p, null))
                    return p;
                Vector2 q = new Vector2(origin.x + 4f, origin.y + 4f * row);
                if (!TechOccupied(set, q, null))
                    return q;
            }
            return new Vector2(origin.x, origin.y);
        }

        /// <summary>
        /// 这个坐标上是不是已经有别的科技节点了。
        ///
        /// 阈值必须 ≥ 3.5 而不是 2：原版格距是 4，节点占位半径大约 2，坐标落在格点上时
        /// 两个合法格子之间的距离恒为 4；而「距离 2」只可能是**两个已占用格子的正中间**
        /// —— 那里既压着左边那张展开卡的右边缘、又贴着自己的右边缘。
        /// 0.9.17 用 2.0 判定时，创世之书（ProjectGenesis）的 1926 在 (45,13)、1927 在 (49,13)，
        /// 中间那个 (47,13) 距离两个都是 2，于是被判成「空位」挑走了，画出来就是一个节点
        /// 骑在两个节点中间。
        /// </summary>
        private static bool TechOccupied(ProtoSet<TechProto> set, Vector2 p, TechProto skip)
        {
            if (set?.dataArray == null)
                return false;
            var data = set.dataArray;
            for (int i = 0; i < data.Length; i++)
            {
                TechProto t = data[i];
                if (t == null || ReferenceEquals(t, skip) || ReferenceEquals(t, Tech))
                    continue;
                if (Vector2.Distance(t.Position, p) < 3.5f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 优先返回 <see cref="Tech"/> 这个实例本身（只要它真的在表里）。
        /// 不能只按 ID 找：ID 会被 LDBTool 的配置覆盖（见 <see cref="EnforceTechId"/>）。
        /// </summary>
        public static TechProto FindTech()
        {
            if (Tech != null && InTechTable(Tech))
                return Tech;
            return FindInSet(LDB.techs, C.TechId, t => t.ID == C.TechId);
        }

        private static bool InTechTable(TechProto t)
        {
            var arr = LDB.techs?.dataArray;
            if (arr == null)
                return false;
            for (int i = 0; i < arr.Length; i++)
                if (ReferenceEquals(arr[i], t))
                    return true;
            return false;
        }

        private static void PreloadTech(TechProto tech)
        {
            if (tech == null)
            {
                Plugin.Log.LogError("科技预加载: 表里找不到本 mod 的科技（ID " + C.TechId + "）");
                return;
            }
            try
            {
                // LDBTool 可能在我们注册之后又用配置覆盖了 ID，进表后统一再纠一次。
                EnforceTechId(tech);
                RepairIndices(LDB.techs, "科技");

                // 冲突体检：同一个 ID 出现两条说明别的 mod 也占了 1997，科技树会错乱。
                int same = 0;
                var all = LDB.techs.dataArray;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].ID == tech.ID)
                        same++;
                if (same > 1)
                {
                    int old = tech.ID;
                    int alt = PickAltTechId(tech);
                    if (alt > 0)
                    {
                        tech.ID = alt;
                        C.TechId = alt;
                        RepairIndices(LDB.techs, "科技");
                        Plugin.Log.LogWarning("科技 ID 冲突: " + old + " 在表里出现了 " + same +
                                              " 次（别的 mod 也占了），本 mod 改用 " + alt);
                    }
                    else
                    {
                        Plugin.Log.LogError("科技 ID 冲突: " + old + " 在表里出现了 " + same +
                                            " 次（别的 mod 也占了），没有空号可换，节点可能显示异常");
                    }
                }

                // RegisterAll 那一刻 LDB.techs 可能还没装好（拿到空表），于是前置和位置都填了默认值。
                // 这里补一次：找到「气态行星开采」就挂上去，并按它的位置重排。
                if (tech.PreTechs == null || tech.PreTechs.Length == 0)
                {
                    int pre = FindGasGiantTechId();
                    TechProto preTech = pre > 0 ? LDB.techs.Select(pre) : null;
                    if (pre > 0)
                    {
                        tech.PreTechs = new[] { pre };
                        tech.Position = preTech != null ? FindTechSpot(preTech.Position) : tech.Position;
                        if (preTech != null)
                        {
                            // 图标也一并补：RegisterTech 若早于科技表建好，IconPath 会是空串，
                            // 节点在树上就是一个白方块。
                            if (string.IsNullOrEmpty(tech.IconPath))
                                tech.IconPath = preTech.IconPath;
                            if (string.IsNullOrEmpty(tech.IconTag))
                                tech.IconTag = preTech.IconTag;
                        }
                        Plugin.Log.LogInfo("科技前置补齐: 挂到 " + pre + "（" + (preTech != null ? preTech.Name : "?") +
                                           "）位置=(" + tech.Position.x + "," + tech.Position.y + ")");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("科技前置补齐: 仍然找不到「气态行星开采」，本科技保持独立");
                    }
                }

                // 所有 mod 的科技此刻都已经进表（PreAddDataAction 的回调全部跑完了），
                // 再确认一次本节点的位置：注册那一刻若有 mod 排在我们后面，
                // 它占的格子我们还看不见（创世之书就是这么撞上的，见 ResolveTechLayout）。
                ResolveTechLayout(tech);

                // ---- 照 ProjectGenesis 的 ProtoPreload() 走一遍整表 ----
                // 它不给自己那个科技单独 Preload，而是先 OnAfterDeserialize 重建索引，
                // 再全表 Preload()，最后全表 Preload2()。原因有三个：
                //   ① 原版科技（比如「气态行星开采」）的 postTechArray 是 Preload2 扫全表得来的，
                //      我们的科技是后追加的，不重跑它就没有我们这条下家，树上不画线；
                //   ② 我们的配方也是在同一次追加里进的表，原版科技的 unlockRecipeArray
                //      如果早于它建好，就少一项；
                //   ③ OnAfterDeserialize 会把 name/desc 还原成 key，必须紧跟一趟 Preload 翻译回来。
                RebuildTechs();

                ApplyTechIcon(tech);      // 必须在 Preload 之后：Preload 会重设 _iconSprite

                // 等级表：科技状态按 techStates 建，缺了会判成「不存在」而永远锁着。
                EnsureTechState();

                Plugin.Log.LogInfo("科技预加载完成: " + tech.ID + "「" + tech.name + "」 页=" + tech.page +
                                   " 位置=(" + tech.Position.x + "," + tech.Position.y + ")" +
                                   " 前置=" + Len(tech.preTechArray) + " 后置=" + Len(tech.postTechArray) +
                                   " 解锁配方=" + Len(tech.unlockRecipeArray) +
                                   " 研究材料=" + Len(tech.itemArray) +
                                   " 图标=" + (tech.iconSprite != null ? "ok" : "空") +
                                   " 树线=" + DescribeTreeLine(tech));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("科技预加载失败: " + e);
            }
        }

        /// <summary>
        /// 布局收尾：等**所有 mod** 的科技都进表之后，再确认一遍本节点没有和别人重叠。
        ///
        /// 为什么注册时算过一次还要再算：<c>LDBTool.PreAddDataAction</c> 的回调按订阅顺序执行，
        /// 我们和 ProjectGenesis（创世之书）各订阅一次。谁先 Awake 谁先跑 —— 如果它排在我们后面，
        /// 注册那一刻它的 140 多条科技还不在 <c>LDB.techs</c> 里，我们就会挑到一个
        /// 「看起来空、实际上被它占了」的格子。实测就是这样撞上的：
        ///
        ///   1926 T精密结构组装厂 (45,13)   1927 T巨型粒子对撞机 (49,13)
        ///                  ↑ 我们挑了正中间的 (47,13)，两边各剩 2 格，节点直接骑上去
        ///
        /// 现在这里扫全表（含创世之书的节点），只要距离小于 3.5 就换一个格子，
        /// 换完之后 <c>DescribeTreeLine</c> 打出来的格距会重新变成 4 的整数倍。
        /// </summary>
        private static void ResolveTechLayout(TechProto tech)
        {
            if (tech == null || LDB.techs?.dataArray == null)
                return;

            try
            {
                TechProto pre = tech.PreTechs != null && tech.PreTechs.Length > 0
                    ? LDB.techs.Select(tech.PreTechs[0])
                    : null;

                bool collides = TechOccupied(LDB.techs, tech.Position, tech);
                // 前置自己也可能被别的 mod 挪走（创世之书会把原版科技整体重排）：
                // 那种情况下我们的坐标没变，但「离前置 4 格」这条关系断了，线会被拉成一条长横线。
                bool detached = false;
                if (pre != null)
                {
                    float d = Vector2.Distance(pre.Position, tech.Position);
                    detached = Mathf.Abs(d - 4f) > 0.5f && Mathf.Abs(d - 5.66f) > 0.6f;
                }

                if (!collides && !detached)
                    return;

                Vector2 from = tech.Position;
                Vector2 anchor = pre != null ? pre.Position : tech.Position;
                tech.Position = FindTechSpot(anchor);

                Plugin.Log.LogWarning("科技布局: " + tech.ID + " (" + from.x + "," + from.y + ") " +
                                      (collides ? "与其它 mod 的节点重叠" : "离前置太远（前置被挪到 (" +
                                        anchor.x + "," + anchor.y + ")）") +
                                      " → 改到 (" + tech.Position.x + "," + tech.Position.y + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("科技布局检查失败（保持原坐标）: " + e.Message);
            }
        }

        /// <summary>
        /// 换一个空号：只在「同一个 ID 被别的 mod 也占了」时用（见 <see cref="PreloadTech"/>）。
        /// 从高往低找，跳过表里已有的号（含自己）。
        /// </summary>
        private static int PickAltTechId(TechProto self)
        {
            var set = LDB.techs;
            if (set?.dataArray == null)
                return 0;
            for (int id = C.TechIdMax; id >= C.TechIdMin; id--)
            {
                bool used = false;
                var data = set.dataArray;
                for (int i = 0; i < data.Length; i++)
                {
                    TechProto t = data[i];
                    if (t == null || ReferenceEquals(t, self))
                        continue;
                    if (t.ID == id) { used = true; break; }
                }
                if (!used)
                    return id;
            }
            return 0;
        }

        /// <summary>
        /// ProjectGenesis 式整表预加载（对应它 <c>ProtoPreload()</c> 里那三段循环）。
        /// 原版 <see cref="VFPreload"/> 的 Preload / Preload2 循环在 LDBTool 追加 proto
        /// <b>之前</b>就跑完了，所以追加进来的科技派生数组全是 null：
        /// <c>preTechArray / postTechArray / unlockRecipeArray / itemArray</c> 一个都没有，
        /// <c>UITechNode.UpdateInfoComplete</c> 一读 <c>unlockRecipeArray.Length</c> 就是 NRE。
        /// 只 Preload 自己一个也不够：前置科技的 postTechArray 要扫全表才会带上我们。
        /// </summary>
        private static void RebuildTechs()
        {
            var set = LDB.techs;
            if (set?.dataArray == null)
                return;

            try
            {
                set.OnAfterDeserialize();      // 重建 dataIndices（顺带把 name 还原成 key）
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("科技表 OnAfterDeserialize 失败: " + e.Message);
            }

            RepairIndices(set, "科技");

            var arr = set.dataArray;
            int ok1 = 0, ok2 = 0, bad1 = 0, bad2 = 0;

            for (int i = 0; i < arr.Length; i++)
            {
                TechProto t = arr[i];
                if (t == null)
                    continue;
                try { t.Preload(); ok1++; }          // name/desc/conclusion 本地化 + 科技图标
                catch (Exception e)
                {
                    bad1++;
                    if (bad1 <= 3)
                        Plugin.Log.LogWarning("科技 " + t.ID + ".Preload 失败: " + e.Message);
                }
            }

            for (int i = 0; i < arr.Length; i++)
            {
                TechProto t = arr[i];
                if (t == null)
                    continue;
                try { t.Preload2(); ok2++; }         // preTechArray / postTechArray / unlockRecipeArray
                catch (Exception e)
                {
                    bad2++;
                    if (bad2 <= 3)
                        Plugin.Log.LogWarning("科技 " + t.ID + ".Preload2 失败: " + e.Message);
                }
            }

            Plugin.Log.LogInfo("科技整表预加载: Preload " + ok1 + " 个 / Preload2 " + ok2 +
                               " 个（表长 " + arr.Length + "，失败 " + bad1 + "/" + bad2 + "）");
        }

        /// <summary>
        /// 给科技节点换上本站自己的图标。<b>必须在 Preload 之后调用</b>：
        /// <c>TechProto.Preload/Preload2</c> 只要 IconPath 非空就会 <c>Resources.Load</c> 覆盖
        /// <c>_iconSprite</c>，而我们是直接把 item 的 sprite 写进私有字段。
        /// </summary>
        private static void ApplyTechIcon(TechProto tech)
        {
            if (tech == null || _techIconRef == null)
                return;
            try
            {
                EnsureIcon();                                  // 先把建筑图标现画出来
                ItemProto item = FindItem();
                Sprite sprite = item != null ? item.iconSprite : null;
                if (sprite == null)
                {
                    Plugin.Log.LogWarning("科技图标: 建筑图标还是空的，节点会退回默认图标");
                    return;
                }
                _techIconRef(tech) = sprite;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("科技图标设置失败: " + e.Message);
            }
        }

        /// <summary>
        /// 诊断用：科技树的连线是由<b>前置节点</b>用自己的 <c>postTechArray</c> 画的
        /// （<c>UITechNode.UpdateConnDynamic</c>），所以「前置的后置里有没有我们」才是关键。
        /// 另外原版连线的长度 = 目标.x − (前置右边 + 140)，格距不足 4 时会算成负数 → 线消失。
        /// </summary>
        private static string DescribeTreeLine(TechProto tech)
        {
            if (tech?.preTechArray == null || tech.preTechArray.Length == 0)
                return "无前置（独立节点，本来就没有线）";

            string s = "";
            for (int i = 0; i < tech.preTechArray.Length; i++)
            {
                TechProto pre = tech.preTechArray[i];
                if (pre == null)
                    continue;
                TechProto[] post = pre.postTechArray ?? new TechProto[0];
                bool found = false;
                for (int j = 0; j < post.Length; j++)
                    if (post[j] != null && post[j].ID == tech.ID)
                    {
                        found = true;
                        break;
                    }
                if (s.Length > 0)
                    s += " / ";
                s += pre.ID + ":后置=" + post.Length + (found ? "✓" : "✗") +
                     " 格距=" + Vector2.Distance(pre.Position, tech.Position).ToString("0.#");
            }
            return s;
        }

        /// <summary>LDBTool 只把 proto 追加进 dataArray，不会调 Preload()，这里补齐。</summary>
        public static void PreloadAll()
        {
            if (_preloaded)
                return;

            var items = LDB.items;
            var models = LDB.models;
            var recipes = LDB.recipes;
            if (items?.dataArray == null || models?.dataArray == null || recipes?.dataArray == null)
                return;

            ModelProto model = FindModel();
            if (model == null)
                return;     // LDBTool 还没把我们的 proto 加进来

            _preloaded = true;
            Plugin.Log.LogInfo("开始预加载 mod proto…");

            // ① 先修 id→下标 索引表。LDBTool 是往 dataArray 尾部追加，ProtoSet.dataIndices
            //    仍是反序列化时那一份，追加的 proto 要么查不到、要么指到隔壁元素上
            //    —— 这会连带让 ItemUnlocked()/RecipeProto.Preload() 全部判错。
            RepairIndices(items, "物品");
            RepairIndices(recipes, "配方");
            RepairIndices(models, "模型");

            // ② 挑格子：制造窗口「建筑」页是 8×14，GridIndex 越界会被静默丢弃
            FixGridCell(FindItem(), FindRecipe());

            Patch_ModelArray.Ensure();
            try { model.Preload(); }
            catch (Exception e) { Plugin.Log.LogError("ModelProto.Preload 失败: " + e); }
            Patch_ModelArray.Ensure();

            RecipeProto recipe = FindRecipe();
            ResolveRecipeItems(recipe);

            ItemProto item = FindItem();
            if (item != null)
            {
                ApplyIcon(item);
                try
                {
                    item.Preload(IndexOf(items.dataArray, item));
                    Plugin.Log.LogInfo("ItemProto 预加载完成: index=" + item.index +
                                       " buildIndex=" + item.BuildIndex +
                                       " gridIndex=" + item.GridIndex +
                                       " canBuild=" + item.CanBuild +
                                       " prefabDesc=" + (item.prefabDesc != null ? "ok" : "null"));
                }
                catch (Exception e) { Plugin.Log.LogError("ItemProto.Preload 失败: " + e); }
            }

            if (recipe != null)
            {
                try { recipe.Preload(IndexOf(recipes.dataArray, recipe)); }
                catch (Exception e) { Plugin.Log.LogError("RecipeProto.Preload 失败: " + e); }

                // ③ 建造栏槽位：贴着原版物流类建筑所在的那一页找空格
                if (item != null)
                    item.BuildIndex = ResolveBuildIndex(item);
                Plugin.Log.LogInfo("配方/物品落位: 建造栏=" + item?.BuildIndex +
                                   " 制造窗口格子=" + (recipe != null ? recipe.GridIndex : 0) +
                                   " 图标=" + (recipe?.iconSprite != null ? "ok" : "MISSING"));
            }

            // 科技放最后：它要借 item 的图标，也要借已经进表的配方建 unlockRecipeArray。
            PreloadTech(FindTech());

            Plugin.Log.LogInfo("mod proto 预加载完成");
        }

        // ---------------- LDB 索引表修复 ----------------

        /// <summary>
        /// 用 dataArray 重建 ProtoSet&lt;T&gt;.dataIndices。原版 OnAfterDeserialize 建过一次，
        /// LDBTool 之后再追加 proto 就不会更新它，于是 Select(modId) 返回 null 或隔壁 proto。
        /// </summary>
        private static void RepairIndices<T>(ProtoSet<T> set, string tag) where T : Proto
        {
            if (set?.dataArray == null)
                return;
            try
            {
                var fld = AccessTools.Field(typeof(ProtoSet<T>), "dataIndices");
                if (fld == null)
                {
                    Plugin.Log.LogWarning("修索引: 找不到 dataIndices 字段（" + tag + "）");
                    return;
                }

                var dict = fld.GetValue(set) as Dictionary<int, int>;
                if (dict == null)
                {
                    dict = new Dictionary<int, int>();
                    fld.SetValue(set, dict);
                }

                var data = set.dataArray;

                // ① 先清掉「指向的 proto 已经不是这个 ID」的死键：LDBTool 用配置覆盖过 proto.ID
                //    之后，字典里会留一个 3996 → 某下标，Select(3996) 就会返回隔壁 proto。
                var stale = new List<int>();
                foreach (var kv in dict)
                {
                    int idx = kv.Value;
                    if (idx < 0 || idx >= data.Length || data[idx] == null || data[idx].ID != kv.Key)
                        stale.Add(kv.Key);
                }
                for (int i = 0; i < stale.Count; i++)
                    dict.Remove(stale[i]);
                int bad = stale.Count;

                // ② 再按 dataArray 重建 / 纠正
                int added = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] == null)
                        continue;
                    int id = data[i].ID;
                    int cur;
                    if (dict.TryGetValue(id, out cur) && cur == i)
                        continue;
                    dict[id] = i;
                    added++;
                }

                Plugin.Log.LogInfo("修索引[" + tag + "]: 表长=" + data.Length +
                                   " 原有错位=" + bad + " 重写/新增=" + added);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("修索引[" + tag + "] 失败: " + e.Message);
            }
        }

        // ---------------- 制造窗口 8×14 格子 ----------------

        /// <summary>
        /// 给 item / recipe 挑一个原版能画出来的格子：GridIndex = 2000 + 行×100 + 列（行、列 1 起），
        /// 行 ≤ 8、列 ≤ 14。从最底行往上找空格，尽量不挤占原版建筑。
        /// </summary>
        private static void FixGridCell(ItemProto item, RecipeProto recipe)
        {
            try
            {
                if (LDB.items?.dataArray == null || LDB.recipes?.dataArray == null)
                    return;

                bool[,] used = new bool[C.GridRows, C.GridCols];
                Mark(LDB.items.dataArray, used, item);
                Mark(LDB.recipes.dataArray, used, recipe);

                int pickRow = -1, pickCol = -1, free = 0;
                for (int r = C.GridRows - 1; r >= 0 && pickRow < 0; r--)
                    for (int c = 0; c < C.GridCols; c++)
                        if (!used[r, c])
                        {
                            free++;
                            if (pickRow < 0) { pickRow = r; pickCol = c; }
                        }
                if (pickRow < 0)
                {
                    Plugin.Log.LogError("制造窗口格子全满，无处安放");
                    return;
                }

                int gi = C.GridType * 1000 + (pickRow + 1) * 100 + (pickCol + 1);
                if (item != null) item.GridIndex = gi;
                if (recipe != null) recipe.GridIndex = gi;

                var sb = new System.Text.StringBuilder();
                for (int r = 0; r < C.GridRows; r++)
                {
                    sb.Append("\n  行" + (r + 1) + ": ");
                    for (int c = 0; c < C.GridCols; c++)
                        sb.Append(used[r, c] ? 'o' : '.');
                }
                Plugin.Log.LogInfo("制造窗口格子: 落在 行" + (pickRow + 1) + " 列" + (pickCol + 1) +
                                   " → GridIndex=" + gi + "（该页空格 " + free + " 个）" + sb);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("挑格子失败: " + e.Message);
            }
        }

        private static void Mark<T>(T[] data, bool[,] used, Proto self) where T : Proto
        {
            for (int i = 0; i < data.Length; i++)
            {
                T p = data[i];
                if (p == null || ReferenceEquals(p, self))
                    continue;
                int g = GridIndexOf(p);
                if (g < 1101)
                    continue;
                int type = g / 1000;
                if (type != C.GridType)
                    continue;
                int row = (g - type * 1000) / 100 - 1;
                int col = g % 100 - 1;
                if (row >= 0 && row < C.GridRows && col >= 0 && col < C.GridCols)
                    used[row, col] = true;
            }
        }

        private static int GridIndexOf(Proto p)
        {
            var ip = p as ItemProto;
            if (ip != null) return ip.GridIndex;
            var rp = p as RecipeProto;
            if (rp != null) return rp.GridIndex;
            return 0;
        }

        // ---------------- 建造栏槽位 ----------------

        /// <summary>
        /// 建造栏槽位 = BuildIndex = 页×100 + 格（页 ≤ 15、格 ≤ 12）。
        /// 优先放到原版同类（EItemType.Logistics）建筑最多的那一页，保证该分类页已经解锁、
        /// 玩家一定点得进去；页内挑第一个空格。
        /// </summary>
        private static int ResolveBuildIndex(ItemProto self)
        {
            try
            {
                var data = LDB.items.dataArray;
                var pageVotes = new Dictionary<int, int>();

                for (int i = 0; i < data.Length; i++)
                {
                    ItemProto p = data[i];
                    if (p == null || p.ID == self.ID || p.BuildIndex <= 0)
                        continue;
                    int pg = p.BuildIndex / 100, sl = p.BuildIndex % 100;
                    if (pg < 1 || pg > C.BuildBarMaxPage || sl < 1 || sl > 12)
                        continue;
                    int v;
                    pageVotes.TryGetValue(pg, out v);
                    pageVotes[pg] = v + 1;
                }

                int bestPage = -1, bestVotes = 0;
                foreach (var kv in pageVotes)
                    if (kv.Value > bestVotes) { bestVotes = kv.Value; bestPage = kv.Key; }

                // 同类型优先
                var typeVotes = new Dictionary<int, int>();
                for (int i = 0; i < data.Length; i++)
                {
                    ItemProto p = data[i];
                    if (p == null || p.ID == self.ID || p.BuildIndex <= 0 || p.Type != self.Type)
                        continue;
                    int pg = p.BuildIndex / 100, sl = p.BuildIndex % 100;
                    if (pg < 1 || pg > C.BuildBarMaxPage || sl < 1 || sl > 12)
                        continue;
                    int v;
                    typeVotes.TryGetValue(pg, out v);
                    typeVotes[pg] = v + 1;
                }
                int typePage = -1, typeBest = 0;
                foreach (var kv in typeVotes)
                    if (kv.Value > typeBest) { typeBest = kv.Value; typePage = kv.Key; }
                if (typePage > 0) bestPage = typePage;

                // 最稳的锚点：原版星际物流运输站自己那一页（我们就是它的升级版）
                for (int i = 0; i < data.Length; i++)
                {
                    ItemProto p = data[i];
                    if (p == null || p.ID == self.ID || p.BuildIndex <= 0)
                        continue;
                    if (p.name != C.RecipeItemName1)
                        continue;
                    int pg = p.BuildIndex / 100, sl0 = p.BuildIndex % 100;
                    if (pg < 1 || pg > C.BuildBarMaxPage || sl0 < 1 || sl0 > 12)
                        continue;
                    bestPage = pg;
                    Plugin.Log.LogInfo("建造栏锚点: 「" + p.name + "」在页" + pg + " 格" + sl0);
                    break;
                }

                if (bestPage <= 0)
                    return C.BuildIndex;

                // 该页占用位图
                var usedPage = new bool[13];
                for (int i = 0; i < data.Length; i++)
                {
                    ItemProto p = data[i];
                    if (p == null || p.ID == self.ID || p.BuildIndex <= 0)
                        continue;
                    if (p.BuildIndex / 100 != bestPage) continue;
                    int sl = p.BuildIndex % 100;
                    if (sl >= 1 && sl <= 12) usedPage[sl] = true;
                }

                for (int sl = 1; sl <= 12; sl++)
                    if (!usedPage[sl])
                    {
                        Plugin.Log.LogInfo("建造栏落位: 页" + bestPage + " 格" + sl +
                                           "（该页原版已占 " + bestVotes + " 个，" +
                                           "同类建筑最多的一页）");
                        return bestPage * 100 + sl;
                    }

                Plugin.Log.LogWarning("建造栏: 页" + bestPage + " 已满，保留 BuildIndex=" + self.BuildIndex);
                return self.BuildIndex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("挑建造栏槽位失败: " + e.Message);
                return self.BuildIndex;
            }
        }

        /// <summary>把配方塞进「已解锁配方」集合，否则制造窗口「建筑」页会按未解锁过滤掉。</summary>
        public static void UnlockRecipe()
        {
            try
            {
                var h = GameMain.history;
                if (h?.recipeUnlocked == null)
                    return;
                if (h.recipeUnlocked.Contains(C.RecipeId))
                    return;

                // 0.9.13：配方改由科技 <see cref="C.TechId"/> 解锁。以前这里无条件塞进
                // recipeUnlocked，等于绕过科技树、一开局就能造。现在只有科技真的研究完
                // 才补这一手（原版也会自己加，这里只是兜底，防止某些界面刷新早于解锁）。
                bool researched = h.techStates != null &&
                                  h.techStates.ContainsKey(C.TechId) &&
                                  h.techStates[C.TechId].unlocked;
                if (!researched)
                    return;

                h.recipeUnlocked.Add(C.RecipeId);
                Plugin.Log.LogInfo("已解锁配方 " + C.RecipeId + "（科技 " + C.TechId + " 已完成）");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("解锁配方失败: " + e.Message);
            }
        }

        private static int Len<T>(T[] arr)
        {
            return arr == null ? -1 : arr.Length;
        }

        private static int IndexOf<T>(T[] arr, T value) where T : class
        {
            for (int i = 0; i < arr.Length; i++)
                if (ReferenceEquals(arr[i], value))
                    return i;
            return -1;
        }

        public static ModelProto FindModel()
        {
            return FindInSet(LDB.models, C.ModelId, m => m.ID == C.ModelId);
        }

        public static ItemProto FindItem()
        {
            return FindInSet(LDB.items, C.ItemId, i => i.ID == C.ItemId);
        }

        private static RecipeProto FindRecipe()
        {
            return FindInSet(LDB.recipes, C.RecipeId, r => r.ID == C.RecipeId);
        }

        /// <summary>
        /// 先按 ID 扫 dataArray，最后才试 Select()。
        /// 反过来的话会踩到 LDBTool 没更新 dataIndices 的坑：Select(modId) 会返回隔壁 proto。
        /// </summary>
        private static T FindInSet<T>(ProtoSet<T> set, int id, Func<T, bool> match) where T : Proto
        {
            if (set == null || set.dataArray == null)
                return null;

            var data = set.dataArray;
            for (int i = 0; i < data.Length; i++)
                if (data[i] != null && match(data[i]))
                    return data[i];

            try
            {
                T p = set.Select(id);
                if (p != null && p.ID == id)
                    return p;
            }
            catch { }
            return null;
        }

        /// <summary>按中英文名把配方原料解析成真实 itemId（LDB 就绪后才能调）。</summary>
        private static void ResolveRecipeItems(RecipeProto recipe)
        {
            if (recipe == null || LDB.items?.dataArray == null)
                return;

            int a = FindItemIdByName(C.RecipeItemName1, "星际物流站", "Interstellar Logistics Station");
            int b = FindItemIdByName(C.RecipeItemName2, "Processor");
            int c = FindItemIdByName(C.RecipeItemName3, "Titanium Alloy");
            int d = FindItemIdByName(C.RecipeItemName4, "Space Warper");

            if (a > 0) recipe.Items[0] = a;
            if (b > 0) recipe.Items[1] = b;
            if (c > 0) recipe.Items[2] = c;
            if (d > 0) recipe.Items[3] = d;

            Plugin.Log.LogInfo("配方原料: " + recipe.Items[0] + "×" + recipe.ItemCounts[0] + " / " +
                               recipe.Items[1] + "×" + recipe.ItemCounts[1] + " / " +
                               recipe.Items[2] + "×" + recipe.ItemCounts[2] + " / " +
                               recipe.Items[3] + "×" + recipe.ItemCounts[3]);
        }

        private static int FindItemIdByName(params string[] names)
        {
            var data = LDB.items.dataArray;
            for (int n = 0; n < names.Length; n++)
            {
                if (string.IsNullOrEmpty(names[n]))
                    continue;
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] == null)
                        continue;
                    if (data[i].name == names[n] || data[i].Name == names[n])
                        return data[i].ID;
                }
            }
            Plugin.Log.LogWarning("找不到物品名: " + string.Join(" / ", names) + "（配方原料保留占位）");
            return 0;
        }

        // ---------------- 图标 ----------------

        private static readonly AccessTools.FieldRef<ItemProto, Sprite> _iconRef =
            AccessTools.FieldRefAccess<ItemProto, Sprite>("_iconSprite");

        /// <summary>TechProto.iconSprite 是只读属性，底层字段叫 <c>_iconSprite</c>。</summary>
        private static readonly AccessTools.FieldRef<TechProto, Sprite> _techIconRef =
            AccessTools.FieldRefAccess<TechProto, Sprite>("_iconSprite");

        public static void EnsureIcon()
        {
            ApplyIcon(FindItem());
        }

        private static void ApplyIcon(ItemProto item)
        {
            if (item == null || _iconRef == null)
                return;
            try
            {
                if (_iconRef(item) != null)
                    return;

                const int N = 80;   // IconSet 用 CopyTexture 拷 80×80，尺寸/格式必须一致
                var tex = new Texture2D(N, N, TextureFormat.ARGB32, false)
                {
                    name = "logistics-hub-icon",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };

                Color bg0 = new Color(0.07f, 0.12f, 0.18f);
                Color bg1 = new Color(0.16f, 0.26f, 0.36f);
                Color cyan = new Color(0.30f, 0.85f, 1.00f);
                Color hull = new Color(0.80f, 0.86f, 0.93f);
                Color amber = new Color(1.00f, 0.62f, 0.26f);
                var px = new Color[N * N];

                for (int y = 0; y < N; y++)
                {
                    for (int x = 0; x < N; x++)
                    {
                        float u = (x + 0.5f) / N - 0.5f;
                        float v = (y + 0.5f) / N - 0.5f;
                        float r = Mathf.Sqrt(u * u + v * v);
                        Color col = Color.Lerp(bg0, bg1, Mathf.Clamp01(1f - r * 1.7f));

                        // 俯视压缩：v 方向按 2.6 倍拉伸，读成"从斜上方看一个环"
                        float ev = v * 2.60f;
                        float er = Mathf.Sqrt(u * u + ev * ev);

                        // 主环（外径 56.4 m）：本体 + 内侧青色灯带
                        if (Mathf.Abs(er - 0.385f) < 0.036f) col = hull;
                        if (Mathf.Abs(er - 0.348f) < 0.010f) col = cyan;

                        // 中央枢纽（锥柱 + 碟顶）
                        if (er < 0.098f) col = Color.Lerp(hull, bg1, Mathf.Clamp01(er / 0.098f) * 0.55f);
                        if (er < 0.040f) col = cyan * 1.15f;

                        // 天线信标
                        if (Mathf.Abs(u) < 0.014f && ev > -0.06f && ev < 0.160f) col = hull;

                        // 底部 12 个吸气钟：均匀分布在 r≈0.215 的圆上（琥珀点）
                        float ang = Mathf.Atan2(ev, u);
                        float sector = Mathf.Repeat(ang / (Mathf.PI * 2f), 1f) * 12f;
                        if (Mathf.Abs(er - 0.215f) < 0.026f && Mathf.Abs(sector - Mathf.Round(sector)) < 0.13f)
                            col = amber;

                        col.a = 1f;
                        px[y * N + x] = col;
                    }
                }

                tex.SetPixels(px);
                tex.Apply(false, false);
                var sp = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f,
                    0, SpriteMeshType.FullRect);
                sp.name = "logistics-hub-icon";
                _iconRef(item) = sp;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("生成图标失败（不影响功能）: " + e.Message);
            }
        }

        private static void RegisterStrings()
        {
            ProtoRegistry.RegisterString(C.LItemName,
                "Aerial Mining Logistics Station", "空中采矿机物流运输站");
            ProtoRegistry.RegisterString(C.LItemDesc,
                "A station-grade logistics hub that also mines the planet it stands on.\n" +
                "12 belt ports around the base; 50 vessel berths on two docking decks (2 x 25, " +
                "6.3 m apart); 16 low-altitude drone launch lanes; 22 laser turrets (18 on the " +
                "saucer, 4 on the base) and a 43 m half-sphere shield. Turrets only engage " +
                "enemies inside their watch range - 140 m for the 4 base lasers (ground), " +
                "and the vanilla Plasma Turret's own space range (6500 m) for the 18 saucer " +
                "plasma cannons (air and Dark Fog space fleets). " +
                "50 interstellar vessels and 200 logistics drones per station.\n" +
                "A thin energy column rises from the drill base and holds the saucer at 82 m, " +
                "matching the Dark Fog relay station's hovering height; four thinner bands circle " +
                "it and carry cargo and command pulses between base and hub.\n" +
                "The base bores six geothermal wells into the crust and feeds them into the power " +
                "grid as a geothermal generator (6 MW x ground heat, same sampling as the vanilla " +
                "geothermal power plant), so a station parked on a hot spot can run off-grid. The " +
                "drill also scans every resource on the planet - ore veins, crude oil seeps and the " +
                "ocean (water / sulfuric acid) - auto-fills the mining slots and starts mining. " +
                "The station window shows 10 cargo slots; its right column lists, per resource, how " +
                "much is left in the ground and how much is already in the station. " +
                "20 slots x 500,000 items.",
                "建在行星轨道层上的超大型物流枢纽，底部钻孔直接开采脚下星球的矿脉。\n" +
                "基座 12 个传送带接口（每条边中心 1 个，径向朝外）；上下两层停靠环各 25 个泊位" +
                "（间距 6.3 m）；低空 16 条运输机发射道；" +
                "22 座激光炮塔（碟顶 18 + 基座 4）与半径 43 m 的半球能量护盾。" +
                "炮塔只攻击进入监控范围的黑雾：基座 4 座激光塔 140 m（对地），" +
                "碟顶 18 座电浆炮按原版磁化电浆炮的太空射程 6500 m（对空，含太空中的黑雾舰队；" +
                "中继站默认不打，可在配置 Turret/AttackRelay 打开）。" +
                "每站 50 艘星际货船、200 架物流运输机。\n" +
                "基座是一台钻孔机：裙边钻齿咬进地面、地面被钻出 6 口地脉井（碎石环 + 井口法兰 + " +
                "井底熔岩），井口三根抽取管斜插回基座，24 条熔岩裂缝从基座往外辐射；" +
                "中央一道细光柱把碟形站体托到 82 m 高（与黑雾中继站同高，中间没有实体塔身），" +
                "光柱外围还有 4 条细能量带在底座与塔顶之间来回传送物资与指令。\n" +
                "底座本身就是一台地热发电机组：取地热的方式与原版地热发电机完全一致" +
                "（基座周围 5 点地形采样），功率 6 MW × 地热强度（再乘「矿物利用」倍率：" +
                "每级 +15%，最高 ×4），直接充进站内电池；建在热的地方甚至不用接电缆也能自己转。\n" +
                "钻孔会扫描本星球的全部资源 —— 矿脉、原油油渗、以及海洋（水 / 硫酸），" +
                "自动占用 10 格采矿仓储并开始供应。\n" +
                "站体窗口里就是全部物资界面：左边 10 格卡槽留给你自己配货，右边一列是只读的" +
                "采矿情况（每种资源还剩多少、站里已存多少）。每格 50 万。");
            ProtoRegistry.RegisterString(C.LTechName,
                "Aerial Mining Hub",
                "空中采矿枢纽");
            ProtoRegistry.RegisterString(C.LTechDesc,
                "Unlocks the Aerial Mining Logistics Station.\n" +
                "The station's drill also inherits Mineral Exploitation: every level of that " +
                "upgrade gives +10% mining speed and -6% ore consumed per mining cycle, " +
                "plus +15% geothermal power conversion (capped at 4x).\n" +
                "Turret damage follows the vanilla Energy Weapon Damage upgrade instead " +
                "(+10% per level, laser turrets and plasma cannons alike).",
                "解锁「空中采矿机物流运输站」。\n" +
                "站体钻孔同时继承「矿物利用」升级：该升级每提升一级，" +
                "采矿作业速度 +10%，每次采矿作业消耗的矿物 -6%，" +
                "以及地热能量转换 +15%（底座打洞取到的地热发电量，最高 ×4）。\n" +
                "炮塔伤害不跟这条科技走：基座激光塔与塔顶电浆炮都吃原版" +
                "「升级 → 能量武器伤害」（每级 +10%，与原版炮塔同一口径）。");
            ProtoRegistry.RegisterString(C.LTechConclusion,
                "The drill under the hub reads the crust like a book.",
                "枢纽底下的钻孔，把地壳读成一本书。");
            ProtoRegistry.RegisterString(C.LRecipeName,
                "Aerial Mining Logistics Station", "空中采矿机物流运输站");
            ProtoRegistry.RegisterString(C.LRecipeDesc,
                "Assemble an Aerial Mining Logistics Station.",
                "组装空中采矿机物流运输站。");
        }
    }
}
