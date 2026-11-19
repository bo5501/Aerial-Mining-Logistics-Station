using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using xiaoye97;

namespace LogisticsHub
{
    /// <summary>
    /// 【0.9.16】学 ProjectGenesis：直接掐掉 LDBTool 的 <c>Bind()</c>。
    /// <para>
    /// <c>PreAddProto → Bind → IdBind</c> 会拿 <c>BepInEx/config/LDBTool/LDBTool.CustomID.cfg</c>
    /// 里同名的 ConfigEntry 覆盖我们刚设好的 <c>proto.ID</c> —— 玩家配置里只要留着旧值
    /// （0.9.13 写下的 3996），我们的科技就会被塞回 ≥2000 的升级页，而且
    /// <c>FindTech()</c> 按 1997 找不到它 → Preload2 不跑 → 科技树 NRE。
    /// </para>
    /// <para>
    /// 之前是在 <c>PreAddProto</c> 之后反射把 ID 改回来，属于事后补救；ProjectGenesis
    /// 的做法是<b>从源头不让它改</b>：<c>[HarmonyPriority(Priority.VeryHigh)]</c> 前缀返回 false，
    /// 整个 Bind（ID + GridIndex 两条）直接跳过。优先级必须最高，否则别的 mod 也可能挂前缀。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(LDBTool), "Bind")]
    [HarmonyPriority(Priority.VeryHigh)]
    internal static class Patch_LdbToolNoBind
    {
        // 默认（UseLDBToolCustomID=false）返回 false = 跳过原版 Bind；
        // 玩家确实要靠配置文件改 ID 时，把配置打开即可恢复 LDBTool 原行为。
        private static bool Prefix() => Plugin.UseLdbToolIds;
    }

    /// <summary>
    /// LDBTool 在 VFPreload.InvokeOnLoadWorkEnded 里才把 proto 追加进 dataArray，
    /// 且不会调 Preload()。这里在它跑完（同方法 Prefix 末端）补一次。
    /// </summary>
    [HarmonyPatch(typeof(VFPreload), "InvokeOnLoadWorkEnded")]
    [HarmonyAfter(LDBToolPlugin.MODGUID)]
    internal static class Patch_PostProtoPreload
    {
        private static void Prefix()
        {
            try { Protos.PreloadAll(); }
            catch (Exception e) { Plugin.Log.LogError("PreloadAll 异常: " + e); }
        }
    }

    /// <summary>保底：读档时 SkillSystem 会遍历所有 model 的 prefabDesc。</summary>
    [HarmonyPatch(typeof(SkillSystem), MethodType.Constructor, typeof(SpaceSector))]
    internal static class Patch_SkillSystemCtor
    {
        private static void Prefix()
        {
            try { Protos.PreloadAll(); }
            catch (Exception e) { Plugin.Log.LogWarning("SkillSystem 兜底预加载异常: " + e.Message); }
        }
    }

    /// <summary>
    /// ModelProto.Preload 每次都会用 Resources.Load 重建 prefabDesc（PrefabPath 为空时退化为
    /// PrefabDesc.none）。我们的 prefab 是运行时构造的，必须在这里重新挂回去。
    /// </summary>
    [HarmonyPatch(typeof(ModelProto), "Preload")]
    internal static class Patch_ModelPreload
    {
        private static void Postfix(ModelProto __instance)
        {
            if (__instance == null || __instance.ID != C.ModelId)
                return;
            try
            {
                PrefabDesc desc = StationPrefab.GetDesc();
                __instance.prefabDesc = desc;
                if (desc?.mesh != null)
                    __instance.meshBounds = desc.mesh.bounds;

                // 自检：这一行能打印出真实数值，就说明 prefabDesc 已真正挂上而不是 none
                Material m0 = (desc?.materials != null && desc.materials.Length > 0) ? desc.materials[0] : null;
                Plugin.Log.LogInfo("prefabDesc 挂载完成: mesh顶点=" + (desc?.mesh != null ? desc.mesh.vertexCount : 0) +
                                   " 接口=" + (desc?.portPoses != null ? desc.portPoses.Length : 0) +
                                   " 泊位=" + (desc?.stationMaxShipCount ?? 0) +
                                   " shader=" + (m0 != null && m0.shader != null ? m0.shader.name : "none"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("挂载 prefabDesc 失败: " + e);
            }
            finally
            {
                // 无论 desc 是否构建成功，modelArray 都必须补齐，
                // 否则 modelArray[ModelId] 为 null 会让建筑退化成 PrefabDesc.none（不可见）
                try { Patch_ModelArray.Ensure(); }
                catch (Exception e) { Plugin.Log.LogWarning("Ensure modelArray 失败: " + e.Message); }
            }
        }
    }

    /// <summary>
    /// ModelProtoSet.modelArray 是按模型 id 直接索引的数组，长度只有 dataArray.Length + 64。
    /// 必须保证我们的 id 落在数组内并指向正确的 ModelProto，否则 ItemProto.Preload 读到 null，
    /// 建筑会变成 PrefabDesc.none（不可见）。
    /// </summary>
    [HarmonyPatch(typeof(ModelProtoSet), "OnAfterDeserialize")]
    internal static class Patch_ModelArray
    {
        [HarmonyPriority(Priority.Low)]
        private static void Postfix()
        {
            Ensure();
        }

        public static void Ensure()
        {
            try
            {
                ModelProtoSet set = LDB.models;
                if (set?.dataArray == null)
                    return;

                ModelProto mine = null;
                var data = set.dataArray;
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != null && data[i].ID == C.ModelId)
                    {
                        mine = data[i];
                        break;
                    }
                }
                if (mine == null)
                    return;

                var arr = set.modelArray;
                if (arr == null || arr.Length <= C.ModelId)
                {
                    int n = Math.Max(C.ModelId + 1, (arr?.Length ?? 0) + 64);
                    var grown = new ModelProto[n];
                    if (arr != null)
                        Array.Copy(arr, grown, arr.Length);
                    set.modelArray = grown;
                    arr = grown;
                }
                if (arr[C.ModelId] == null)
                    arr[C.ModelId] = mine;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("修正 modelArray 失败: " + e.Message);
            }
        }
    }

    /// <summary>ItemProto.Preload 会读 modelArray[ModelIndex]，在它之前确保数组就绪。</summary>
    [HarmonyPatch(typeof(ItemProto), "Preload")]
    internal static class Patch_ItemPreload
    {
        private static void Prefix(ItemProto __instance)
        {
            if (__instance == null || __instance.ID != C.ItemId)
                return;
            Patch_ModelArray.Ensure();
            ModelProto model = LDB.models.Select(C.ModelId);
            if (model != null && model.prefabDesc == null)
            {
                model.prefabDesc = StationPrefab.GetDesc();
                if (model.prefabDesc?.mesh != null)
                    model.meshBounds = model.prefabDesc.mesh.bounds;
            }
        }
    }

    /// <summary>
    /// 50 个泊位的停靠点：原版把 stationMaxShipCount 艘船均匀摆在半径 11.5 m 的单层圆盘上，
    /// 50 艘时相邻只隔 7.2°（弧长 1.44 m），船会叠在一起。
    ///
    /// 这里按 C.RingR / C.RingBerths 重铺泊位（当前是单圈 R=25 的 50 泊位），高度取
    /// C.ShipYOn，所以船正好停在模型环顶的发射座上。数组写成多维是留给以后加环的余地。
    /// —— 只处理「泊位数 = 50」的站，不动原版站与别的 mod 的站。
    /// </summary>
    [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.Init))]
    internal static class Patch_StationInit
    {
        private static void Postfix(StationComponent __instance, PrefabDesc _desc)
        {
            try
            {
                if (_desc == null || !_desc.isStellarStation || _desc.stationMaxShipCount != C.MaxShips)
                    return;

                int n = Mathf.Min(C.MaxShips, __instance.shipDiskPos.Length);
                int idx = 0;
                for (int ring = 0; ring < C.RingR.Length && idx < n; ring++)
                {
                    int berths = Mathf.Min(C.RingBerths[ring], n - idx);
                    float ry = C.ShipYOn(ring) - C.ShipDockY;   // 相对 stationShipPos 的高度
                    float rr = C.RingR[ring];
                    for (int j = 0; j < berths; j++, idx++)
                    {
                        float angDeg = 360f / C.RingBerths[ring] * j +
                                       (ring % 2 == 0 ? 0f : 180f / C.RingBerths[ring]);
                        float a = angDeg * Mathf.Deg2Rad;
                        var local = new Vector3(Mathf.Cos(a) * rr, ry, Mathf.Sin(a) * rr);
                        __instance.shipDiskPos[idx] = __instance.shipDockPos + __instance.shipDockRot * local;
                        __instance.shipDiskRot[idx] = __instance.shipDockRot *
                                                      Quaternion.Euler(0f, angDeg, 0f);
                    }
                }

                if (__instance.id == 1 || __instance.id % 20 == 0)
                    Plugin.Log.LogInfo("物流站泊位已铺开: id=" + __instance.id +
                                       " 泊位=" + idx + " 环=" + C.RingR.Length);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("重排泊位失败: " + e);
            }
        }
    }

    /// <summary>
    /// 建造栏（UIBuildMenu）诊断 + 自愈。
    ///
    /// 原版 StaticLoad(): buildIndex > 0 时 页 = buildIndex / 100、格 = buildIndex % 100
    /// （页 ≤ 15、格 ≤ 12）填进 private static protos[16,13]；staticLoaded 之后永久跳过。
    /// 一旦它在我们 proto 入表之前跑过一次，我们就永远不在表里。
    ///
    /// 所以这里记下 staticLoaded 的位置，并在每次打开建造栏时重跑一次 StaticLoad，
    /// 顺手确认我们确实占到了某一格。
    /// </summary>
    [HarmonyPatch(typeof(UIBuildMenu), "StaticLoad")]
    internal static class Patch_BuildMenuDiag
    {
        internal static readonly System.Reflection.FieldInfo ProtosField =
            AccessTools.Field(typeof(UIBuildMenu), "protos");

        internal static readonly System.Reflection.FieldInfo StaticLoadedField =
            AccessTools.Field(typeof(UIBuildMenu), "staticLoaded");

        private static bool _quiet;

        private static void Postfix()
        {
            try
            {
                ItemProto item = Protos.FindItem();
                if (item == null)
                {
                    Plugin.Log.LogError("建造栏: 找不到 ItemProto " + C.ItemId + "（LDBTool 未加入？）");
                    return;
                }

                var protos = ProtosField != null ? ProtosField.GetValue(null) as ItemProto[,] : null;
                if (protos == null)
                {
                    Plugin.Log.LogWarning("建造栏: 读不到 UIBuildMenu.protos 字段");
                    return;
                }

                if (_quiet && Placed(protos) >= 0)
                    return;     // 后续每次开 UI 都重跑 StaticLoad，没变化就不吵了

                int pgN = protos.GetLength(0), slN = protos.GetLength(1);
                int page = item.BuildIndex / 100, slot = item.BuildIndex % 100;

                bool unlocked = GameMain.history != null && GameMain.history.ItemUnlocked(C.ItemId);
                Plugin.Log.LogInfo("=== 建造栏诊断 ===");
                Plugin.Log.LogInfo("item: id=" + item.ID + " name=" + item.name +
                                   " buildIndex=" + item.BuildIndex +
                                   " gridIndex=" + item.GridIndex +
                                   " unlockKey=" + item.UnlockKey +
                                   " canBuild=" + item.CanBuild +
                                   " isEntity=" + item.IsEntity +
                                   " icon=" + (item.iconSprite != null) +
                                   " prefabDesc=" + (item.prefabDesc != null && item.prefabDesc.mesh != null ? "ok" : "MISSING"));
                Plugin.Log.LogInfo("history.ItemUnlocked(" + C.ItemId + ") = " + unlocked);

                if (Placed(protos) >= 0)
                {
                    Plugin.Log.LogInfo("建造栏: 已在表中（页" + page + " 格" + slot + "）");
                    _quiet = true;
                    return;
                }

                // 表里没有：先看目标格空不空，再顺延，最后全表找空格
                bool placed = false;
                if (page >= 1 && page < pgN && slot >= 1 && slot < slN && protos[page, slot] == null)
                {
                    protos[page, slot] = item;
                    placed = true;
                }
                if (!placed && page >= 1 && page < pgN)
                {
                    for (int s = 1; s <= 12 && !placed; s++)
                        if (protos[page, s] == null)
                        {
                            protos[page, s] = item;
                            placed = true;
                            Plugin.Log.LogWarning("建造栏: [" + page + "," + slot + "] 被占，改放 [" + page + "," + s + "]");
                        }
                }
                if (!placed)
                {
                    for (int p = 1; p < pgN && !placed; p++)
                        for (int s = 1; s <= 12 && !placed; s++)
                            if (protos[p, s] == null)
                            {
                                protos[p, s] = item;
                                placed = true;
                                Plugin.Log.LogWarning("建造栏: 页" + page + " 已满，改放 [" + p + "," + s + "]");
                            }
                }
                Plugin.Log.LogInfo("建造栏注册: 目标[" + page + "," + slot + "] → " + (placed ? "OK" : "失败"));

                for (int p = 1; p < pgN && p <= 10; p++)
                {
                    int used = 0;
                    var sb = new System.Text.StringBuilder(13);
                    for (int s = 1; s <= 12; s++)
                    {
                        ItemProto q = protos[p, s];
                        if (q == null) sb.Append('.');
                        else { used++; sb.Append(q.ID == C.ItemId ? '#' : 'o'); }
                    }
                    Plugin.Log.LogInfo("  页" + p + ": 占" + used + "  " + sb);
                }
                _quiet = placed;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("建造栏诊断异常: " + e.Message);
            }
        }

        /// <summary>返回我们所在的 [页,格]（找不到返回 -1）。</summary>
        internal static int Placed(ItemProto[,] protos)
        {
            for (int p = 0; p < protos.GetLength(0); p++)
                for (int s = 0; s < protos.GetLength(1); s++)
                {
                    ItemProto q = protos[p, s];
                    if (q != null && q.ID == C.ItemId)
                        return p * 100 + s;
                }
            return -1;
        }
    }

    /// <summary>
    /// 建造栏每次打开都把 staticLoaded 复位、重跑 StaticLoad。
    /// 这样不管开局那次 StaticLoad 跑得多早，只要 LDB 已经就绪，我们就能进表。
    /// </summary>
    [HarmonyPatch(typeof(UIBuildMenu), "_OnOpen")]
    internal static class Patch_BuildMenuOpen
    {
        private static void Prefix()
        {
            try
            {
                if (Patch_BuildMenuDiag.StaticLoadedField != null)
                    Patch_BuildMenuDiag.StaticLoadedField.SetValue(null, false);
                UIBuildMenu.StaticLoad();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("重跑 StaticLoad 失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 制造窗口「建筑」页是按 recipe.GridIndex 铺格的，且只画
    /// history.RecipeUnlocked(recipe.ID) 为真的配方。我们的配方没有科技前置，
    /// 必须手动塞进 recipeUnlocked，否则那格里永远是空的。
    /// </summary>
    [HarmonyPatch(typeof(UIReplicatorWindow), "RefreshRecipeIcons")]
    internal static class Patch_RecipeUnlock
    {
        private static void Prefix()
        {
            Protos.UnlockRecipe();
        }
    }

    [HarmonyPatch(typeof(UIItemPicker), "RefreshIcons")]
    internal static class Patch_ItemPickerUnlock
    {
        private static void Prefix()
        {
            Protos.UnlockRecipe();
        }
    }

    /// <summary>
    /// 【崩溃修复】UIEntityBriefInfo（鼠标悬停在建筑上时的左下角简报）在 _OnCreate 里按
    /// 预制体序列化长度建 icons 数组（原版 12 个）。而 _OnUpdate 里对物流站会算：
    ///     num8  = isStellar ? 3 : 4;
    ///     index1 = ((storage.Length - 1) / num8 + 1) * num8;      // 20 槽、num8=3 → 21
    ///     for (int length = storage.Length; length &lt; index1; ++length)
    ///         this.icons[length].visible = false;                 // → icons[20] 越界
    /// 我们的站有 20 个卡槽，于是每次悬停都抛 IndexOutOfRangeException，
    /// 异常持续刷屏并让简报 UI 卡死。这里把 icons 数组扩到 MaxItemKinds + 8。
    /// </summary>
    [HarmonyPatch(typeof(UIEntityBriefInfo), "_OnCreate")]
    internal static class Patch_EntityBriefIcons
    {
        private const int Extra = 8;      // 预留给装备（翘曲器等）图标

        private static void Postfix(UIEntityBriefInfo __instance)
        {
            try
            {
                var old = __instance.icons;
                if (old == null || old.Length == 0)
                    return;
                int need = C.MaxItemKinds + Extra;
                if (old.Length >= need)
                    return;

                var nw = new UIIconCountInc[need];
                Array.Copy(old, nw, old.Length);
                UIIconCountInc proto = old[0];
                Transform parent = proto.transform.parent;
                for (int i = old.Length; i < need; i++)
                {
                    UIIconCountInc com = UnityEngine.Object.Instantiate(proto, parent);
                    com.SetTransformIdentity();
                    com.visible = false;
                    nw[i] = com;
                }
                __instance.icons = nw;
                Plugin.Log.LogInfo("物品简报图标位: " + old.Length + " → " + need +
                                   "（卡槽 " + C.MaxItemKinds + " + 装备 " + Extra + "）");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("扩充物品简报图标位失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 原版窗口高度口径是 `常数 + 76 × station.storage.Length + 36`。我们把
    /// stationMaxItemKinds 提到 20 之后，原版窗口会被算成 1900+ px 高、下半截配置面板
    /// 直接顶出屏幕。
    ///
    /// 0.9.4 起窗口里真正铺了 <see cref="C.ManualSlots"/> 个卡槽（见 <see cref="Patch_StationSlots"/>），
    /// 行距压到 <see cref="C.SlotPitch"/>，所以这里把「按 20 行 76 px 算出来的高度」
    /// 换成「按 10 行 54 px 算出来的高度」：
    ///
    /// <code>
    /// delta = 76 × n − ManualSlots × SlotPitch
    /// </code>
    ///
    /// 幂等性：RefreshTrans 每帧都会先把 sizeDelta 重设成原版口径（用当前 storage.Length 算），
    /// 所以这里的减法每帧都作用在「原版刚算完的值」上，不会累积。
    /// 底部那块配置面板是锚在窗口底边的，窗口变矮它跟着上移，间距与 6 行时完全一致。
    ///
    /// 只对**本站**生效：别的物流塔（原版 5 格 / 其它 mod 的站）一个字节都不碰。
    /// </summary>
    [HarmonyPatch(typeof(UIStationWindow), "RefreshTrans")]
    internal static class Patch_StationWindowSize
    {
        /// <summary>原版 storageUIs 的数量。UIStationWindow._OnCreate 里硬编码 new UIStationStorage[6]。</summary>
        internal const int VanillaSlots = 6;
        private static int _loggedN = -1;
        /// <summary>本次开窗是否已经做过「整窗可见」的垂直校正（避免每帧把玩家拖的窗口拽回去）。</summary>
        private static bool _vFixed;

        internal static void ResetVertical()
        {
            _vFixed = false;
        }

        /// <summary>
        /// 高度夹正的主体。既被 `RefreshTrans` 的 Postfix 每帧调用，也被
        /// <see cref="StationPanel.Reflow"/> 在开窗那一帧调一次 ——
        /// 否则「OnStationIdChange 按 20 行算完高度」到「下一帧 RefreshTrans」之间，
        /// 会有一帧 1900 px 高的窗口闪过去。
        /// </summary>
        internal static void FixHeight(UIStationWindow sw, StationComponent station)
        {
            if (station == null || station.storage == null || sw == null)
                return;
            int n = station.storage.Length;
            if (n <= VanillaSlots)
                return;                      // 原版站：一个字节都不碰
            if (!StationPanel.IsOurs(sw.factory, station))
                return;

            RectTransform win = sw.windowTrans;
            if (win == null)
                return;

            float delta = 76f * n - C.ManualSlots * C.SlotPitch;

            // 【0.9.6】改成「按原版公式算出目标高度」而不是「在当前值上减 delta」。
            // 原因：Reflow 是在 OnStationIdChange 里调的，而 OnStationIdChange **不会**
            // 重算高度（高度只在 _OnUpdate → RefreshTrans 里算）。所以「当前 sizeDelta」
            // 可能是上一帧刚被我们减过的 936，再减 980 就成负数 —— 窗口整体塌成一条线，
            // 玩家看到的就是「面板没弹出来」。
            float target = VanillaHeight(sw, station) - delta;
            if (target < 240f)
                target = win.sizeDelta.y - delta;      // 公式对不上（改版）时退回相对算法
            win.sizeDelta = new Vector2(win.sizeDelta.x, target);

            float canvasH = 1080f;
            RectTransform host = win.parent as RectTransform;
            if (host != null && host.rect.height > 1f)
                canvasH = host.rect.height;

            float h = win.sizeDelta.y;
            if (h > canvasH - 20f)
            {
                h = canvasH - 20f;
                win.sizeDelta = new Vector2(win.sizeDelta.x, h);
            }

            // 原版窗口锚点是 (0.5,0.5)、pivot 是 (0.5,1)：anchoredPosition.y 是「顶边相对中心」。
            // 10 行卡槽把窗口撑高之后，底边可能落到屏幕外（窗口顶边默认离屏幕顶只有 ~90 px），
            // 所以开窗时向上抬一次，保证底边 ≥ 20 px 内边距。只校正一次，
            // 之后玩家自己拖到哪就是哪。
            if (!_vFixed)
            {
                _vFixed = true;
                float top = win.anchoredPosition.y;
                float bottom = top + canvasH * 0.5f - h;          // 顶边 − 高 = 底边（相对中心）
                float minBottom = -canvasH * 0.5f + 20f;
                if (bottom < minBottom)
                {
                    float raise = minBottom - bottom;
                    float maxTop = canvasH * 0.5f - 16f;
                    float ny = Mathf.Min(top + raise, maxTop);
                    if (ny > top)
                        win.anchoredPosition = new Vector2(win.anchoredPosition.x, ny);
                }
            }

            if (_loggedN != n)
            {
                _loggedN = n;
                Plugin.Log.LogInfo("站体窗口: storage=" + n + " 格 → 铺 " + C.ManualSlots +
                                   " 行 × " + C.SlotPitch.ToString("0") + "px（-" +
                                   delta.ToString("0") + "px）→ " +
                                   win.sizeDelta.x.ToString("0") + "×" +
                                   win.sizeDelta.y.ToString("0") + " pos=" +
                                   win.anchoredPosition.x.ToString("0") + "," +
                                   win.anchoredPosition.y.ToString("0") +
                                   "；第 " + (C.AutoSlotStart + 1) + " 格起为自动矿，只在右侧清单里显示");
            }
        }

        private static void Postfix(UIStationWindow __instance, StationComponent station)
        {
            try
            {
                FixHeight(__instance, station);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("夹正原版窗口高度失败: " + e.Message);
            }
        }

        private static System.Reflection.FieldInfo _tabField;

        /// <summary>原版 `UIStationWindow.tabIndex`（0 = 物流设置，1 = 优先级设置）。</summary>
        private static int TabIndex(UIStationWindow sw)
        {
            try
            {
                if (_tabField == null)
                    _tabField = AccessTools.Field(typeof(UIStationWindow), "tabIndex");
                return _tabField != null ? (int)_tabField.GetValue(sw) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool PilerTech()
        {
            try
            {
                if (GameMain.history == null || !GameMain.history.TechUnlocked(3803))
                    return false;
                TechProto tp = LDB.techs.Select(3803);
                return tp != null && tp.UnlockValues != null && tp.UnlockValues.Length > 0 &&
                       (int)tp.UnlockValues[0] >= 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 原版 `RefreshTrans` 算出来的窗口高度（逐分支照抄 0.10.35 的实现）。
        /// 复制一份是为了让 <see cref="FixHeight"/> 能给出**绝对值**，从而幂等 ——
        /// 不管被调用几次、上一帧的 sizeDelta 是多少，结果都一致。
        /// </summary>
        private static float VanillaHeight(UIStationWindow sw, StationComponent sc)
        {
            int n = sc.storage != null ? sc.storage.Length : 0;
            bool piler = PilerTech();
            if (sc.isCollector)
                return 100f + 76f * n + 36f;
            if (sc.isVeinCollector)
                return (piler ? 268f : 248f) + 76f * n + 36f;
            if (sc.isStellar)
            {
                if (TabIndex(sw) == 1)
                    return 360f + 76f * n + 36f + 94f;
                return (piler ? 360f : 340f) + 76f * n + 36f;
            }
            return (piler ? 300f : 280f) + 76f * n + 36f;
        }
    }

    /// <summary>
    /// 把原版 `UIStationWindow.storageUIs` 从 6 个补造到 <see cref="C.ManualSlots"/> 个。
    ///
    /// 原版 `_OnCreate` 里写死了 `new UIStationStorage[6]`，`OnStationIdChange` / `_OnOpen` /
    /// `_OnUpdate` / `_OnClose` / `_OnInit` / `_OnFree` 全部是遍历这个数组的，所以只要在
    /// `_OnCreate` 之后把数组换成长度 10 的版本，原版逻辑就会自动接管新增的 4 个卡槽 ——
    /// 换货、供需下拉、tooltip、取货全靠原版，我们一行交互代码都不用写。
    ///
    /// 补造的写法照抄原版：`Instantiate(storageUIPrefab, storageUIPrefab.transform.parent)`
    /// + 设 `stationWindow` + `_Create()`；`_Init(data)` 由随后的 `_OnInit` 循环补上。
    /// 位置与缩放由 <see cref="StationPanel"/> 每次开窗时统一重排（原版站会被摆回 76 px 原样）。
    /// </summary>
    [HarmonyPatch(typeof(UIStationWindow), "_OnCreate")]
    internal static class Patch_StationSlots
    {
        private static void Postfix(UIStationWindow __instance)
        {
            try
            {
                if (StationPanel.SlotsField == null || __instance.storageUIPrefab == null)
                    return;

                var old = StationPanel.SlotsField.GetValue(__instance) as UIStationStorage[];
                if (old == null || old.Length >= C.ManualSlots)
                    return;

                Transform parent = __instance.storageUIPrefab.transform.parent;
                if (parent == null)
                    return;

                var arr = new UIStationStorage[C.ManualSlots];
                Array.Copy(old, arr, old.Length);
                for (int i = old.Length; i < arr.Length; i++)
                {
                    UIStationStorage u = UnityEngine.Object.Instantiate(__instance.storageUIPrefab, parent);
                    if (u == null)
                        continue;
                    var rt = u.transform as RectTransform;
                    if (rt != null)
                        rt.anchoredPosition = new Vector2(40f, -90f - 76f * i);
                    u.name = "storage-ui-" + i;
                    u.stationWindow = __instance;
                    u._Create();
                    arr[i] = u;
                }
                StationPanel.SlotsField.SetValue(__instance, arr);
                Plugin.Log.LogInfo("站体窗口卡槽: " + old.Length + " → " + arr.Length +
                                   " 个（补造 " + (arr.Length - old.Length) +
                                   " 个克隆件，交互仍走原版 UIStationStorage）");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("补造卡槽失败（保持原版 6 格）: " + e);
            }
        }
    }

    /// <summary>
    /// 老存档的卡槽扩容。
    ///
    /// <c>StationComponent.Import</c> 是按存档里写的长度重建 <c>storage</c> 数组的
    /// （0.6.x 建的老站只有 10 格）。而站体窗口会铺 <see cref="C.ManualSlots"/> 个卡槽、
    /// 右侧采矿列按 <c>[AutoSlotStart, MaxItemKinds)</c> 找自动矿，
    /// 一旦 storage 只有 10 格：
    ///   · 后 4 个卡槽永远不刷新（显示别人/空的残留数据）；
    ///   · <c>StationDrill.AutoFill</c> 的自动段起点 10 越界，自动矿完全不工作。
    /// 所以读档后立刻把数组补齐到 MaxItemKinds，新增的格子按「空槽 + 满容量」初始化。
    /// 之后 Export 写的是新长度，存档自然升级。
    /// </summary>
    [HarmonyPatch(typeof(StationComponent), "Import")]
    internal static class Patch_StationStorageGrow
    {
        private static bool _logged;

        private static void Postfix(StationComponent __instance)
        {
            try
            {
                StationStore[] st = __instance.storage;
                if (st == null || st.Length >= C.MaxItemKinds)
                    return;

                var bigger = new StationStore[C.MaxItemKinds];
                System.Array.Copy(st, bigger, st.Length);
                int cap = C.MaxItemCount +
                          (GameMain.history != null ? GameMain.history.remoteStationExtraStorage : 0);
                // 右列（自动矿）的容量跟矿物利用等级走，左列（手动）保持原版满容量。
                int autoCap = MiningTech.AutoSlotCap(
                    GameMain.history != null ? GameMain.history.remoteStationExtraStorage : 0);
                for (int i = st.Length; i < bigger.Length; i++)
                {
                    bigger[i].itemId = 0;
                    bigger[i].count = 0;
                    bigger[i].max = i >= C.AutoSlotStart ? autoCap : cap;
                    bigger[i].localLogic = ELogisticStorage.None;
                    bigger[i].remoteLogic = ELogisticStorage.None;
                }
                __instance.storage = bigger;

                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogInfo("老存档卡槽扩容: " + st.Length + " → " + C.MaxItemKinds +
                                       "（1~" + C.ManualSlots + " 手动 / " +
                                       (C.AutoSlotStart + 1) + "~" + C.MaxItemKinds + " 自动矿）");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("卡槽扩容失败（保持原长度）: " + e.Message);
            }
        }
    }

    /// <summary>
    /// StationComponent.UpdateNeeds：把「本站要什么」写进 <c>needs[]</c>（传送带进货用）。
    ///
    /// 原版只认 storage[0..4]；本站卡槽扩到 20 个之后，这里仍然**保持下标对齐**
    /// （needs[i] ↔ storage[i]），只写前 5 格 —— 原因见方法里的注释：needIdx 会被
    /// InputItem 直接当 storage 下标用，错位就会吞货。
    ///
    /// 第 6 格以后（含 0.9.30 的核电燃料格）由无人机 / 运输船的供需配对负责，
    /// 配对逻辑是遍历整个 storage 的，不受 needs 长度限制
    /// （`HasLocalDemand` / `HasRemoteSupply` 等已由本文件补上 6~9 格的扫描）。
    ///
    /// 右列 <c>[AutoSlotStart, MaxItemKinds)</c> 是自动矿（固定供应、由钻孔填），
    /// 刻意排除在 needs 之外 —— 否则运输机会把传送带上的同类矿石再倒回来，
    /// 白白占掉运力，也看不出「自动矿在产」这个效果。
    /// </summary>
    [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.UpdateNeeds))]
    internal static class Patch_UpdateNeeds
    {
        private static bool Prefix(StationComponent __instance)
        {
            try
            {
                var st = __instance.storage;
                if (st == null || st.Length <= 6)
                    return true;      // 原版站（≤5 卡槽）走原逻辑

                lock (st)
                {
                    // 【0.9.30 修正】needs[] 的下标**就是** storage 的下标：
                    // 传送带进货走 `cargoPath.TryPickItemAtRear(needs, out needIdx, …)`
                    // → `InputItem(itemId, needIdx, …)`，而 InputItem 只在
                    // `storage[needIdx].itemId == itemId` 时才加货。
                    // 0.9.4~0.9.29 这里是把「前 5 个欠料槽」按顺序塞进 needs[0..4]，
                    // 一旦欠料的是第 6~10 格，needIdx 就指向错的格子 —— 货被从传送带上
                    // 取下来却存不进去，等于**凭空吞货**。所以这里恢复「下标对齐」：
                    // 只写 storage[0..4] 对应的需求，第 6 格以后交给无人机/运输船
                    // （它们的配对是遍历整个 storage，不受 needs 限制，见 HasLocalDemand 等）。
                    int manual = Mathf.Min(5, Mathf.Min(C.AutoSlotStart, st.Length));
                    for (int i = 0; i < manual; i++)
                        __instance.needs[i] = st[i].itemId > 0 && st[i].count < st[i].max
                            ? st[i].itemId : 0;
                    for (int i = manual; i < 5; i++)
                        __instance.needs[i] = 0;
                    __instance.needs[5] = !__instance.isStellar ||
                                          __instance.warperCount >= __instance.warperMaxCount
                        ? 0 : 1210;
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("UpdateNeeds 改写失败，回退原逻辑: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// 【0.9.30】把「只有前 6 格参与物流」这条原版硬编码扩到全部手动格。
    ///
    /// 原版 `StationComponent.HasLocalDemand` / `HasLocalSupply` / `HasRemoteDemand` /
    /// `HasRemoteSupply` 都是把 `storage[0]`..`storage[5]` 一段段写死的（反编译里能看到
    /// 六段 if）。卡槽扩到 10 个以后，第 7~10 格即使设了「供应 / 需求」也**永远配不上**：
    /// 核电站的氘棒需求放在第 10 格，无人机/运输船会当它不存在。
    ///
    /// 这里只做「兜底」：原版那 6 格查不到（返回 -1）时，再往后扫到
    /// <see cref="C.ManualSlots"/>，语义与返回值完全一致（返回格号，调用方照原逻辑取货/放货）。
    /// </summary>
    internal static class StationSlotScan
    {
        internal static int Demand(StationComponent sc, int from, int itemId, int need, bool remote)
        {
            StationStore[] st = sc != null ? sc.storage : null;
            if (st == null)
                return -1;
            int to = Mathf.Min(C.ManualSlots, st.Length);
            for (int i = from; i < to; i++)
            {
                if (st[i].itemId != itemId)
                    continue;
                if ((remote ? st[i].remoteLogic : st[i].localLogic) != ELogisticStorage.Demand)
                    continue;
                if (st[i].max - st[i].count >= need)
                    return i;
            }
            return -1;
        }

        internal static int Supply(StationComponent sc, int from, int itemId, int least, bool remote)
        {
            StationStore[] st = sc != null ? sc.storage : null;
            if (st == null)
                return -1;
            int to = Mathf.Min(C.ManualSlots, st.Length);
            for (int i = from; i < to; i++)
            {
                if (st[i].itemId != itemId)
                    continue;
                if ((remote ? st[i].remoteLogic : st[i].localLogic) != ELogisticStorage.Supply)
                    continue;
                if (st[i].count >= least)
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// 【0.9.30】<c>StationComponent.AddItem(itemId, count, inc)</c> 同样只认 storage[0..5]
    /// （反编译里是六段 if）。
    ///
    /// 这一条比供需查询更致命：无人机 / 星际货船到站卸货时走的就是它
    /// （`PlanetTransport` 里 `stationComponent.AddItem(...)`），第 7~10 格即使
    /// 设了需求、船也飞到了，货还是会**凭空消失**。
    /// 核电燃料格正好在第 10 格，所以这里必须把 6~9 格补上 —— 语义与原版一致：
    /// 找到同 itemId 的格子就累加，不看 max（原版也不看）。
    /// </summary>
    [HarmonyPatch(typeof(StationComponent), "AddItem",
                  new[] { typeof(int), typeof(int), typeof(int) })]
    internal static class Patch_StationAddItem
    {
        private static void Postfix(StationComponent __instance, int itemId, int count, int inc, ref int __result)
        {
            if (__result > 0 || itemId <= 0 || count <= 0)
                return;
            StationStore[] st = __instance.storage;
            if (st == null)
                return;
            int to = Mathf.Min(C.ManualSlots, st.Length);
            lock (st)
            {
                for (int i = 6; i < to; i++)
                {
                    if (st[i].itemId != itemId)
                        continue;
                    st[i].count += count;
                    st[i].inc += inc;
                    __result = count;
                    return;
                }
            }
        }
    }

    [HarmonyPatch(typeof(StationComponent), "HasLocalDemand")]
    internal static class Patch_HasLocalDemand
    {
        private static void Postfix(StationComponent __instance, int itemId, int countAtLeast, ref int __result)
        {
            if (__result >= 0)
                return;
            __result = StationSlotScan.Demand(__instance, 6, itemId, countAtLeast, false);
        }
    }

    [HarmonyPatch(typeof(StationComponent), "HasLocalSupply")]
    internal static class Patch_HasLocalSupply
    {
        private static void Postfix(StationComponent __instance, int itemId, int countAtLeast, ref int __result)
        {
            if (__result >= 0)
                return;
            __result = StationSlotScan.Supply(__instance, 6, itemId, countAtLeast, false);
        }
    }

    [HarmonyPatch(typeof(StationComponent), "HasRemoteDemand")]
    internal static class Patch_HasRemoteDemand
    {
        private static void Postfix(StationComponent __instance, int itemId, int countAtLeast, ref int __result)
        {
            if (__result >= 0)
                return;
            __result = StationSlotScan.Demand(__instance, 6, itemId, countAtLeast, true);
        }
    }

    [HarmonyPatch(typeof(StationComponent), "HasRemoteSupply")]
    internal static class Patch_HasRemoteSupply
    {
        private static void Postfix(StationComponent __instance, int itemId, int countAtLeast, ref int __result)
        {
            if (__result >= 0)
                return;
            __result = StationSlotScan.Supply(__instance, 6, itemId, countAtLeast, true);
        }
    }

    /// <summary>
    /// 【0.9.7】`LDB.items.Select(8100)` 的兜底。
    ///
    /// `ProtoSet&lt;T&gt;.Select` 只查 `dataIndices` 字典，而这个字典是 `OnAfterDeserialize`
    /// 里按 `dataArray` 建的 —— LDBTool 之后追加的 proto 要靠 <see cref="Protos.RepairIndices{T}"/>
    /// 补进去。一旦某条路径（重载 LDB、别的 mod 重建索引）把我们的条目弄丢，
    /// `Select` 就返回 null，而原版 `UIStationWindow.OnStationIdChange` 里正好有：
    /// <code>
    /// ItemProto itemProto = LDB.items.Select((int)factory.entityPool[station.entityId].protoId);
    /// if (itemProto == null) { this._Close(); return; }      // ← 窗口「一开就关」
    /// </code>
    /// 于是整扇站体窗口直接 `_Close()`，玩家看到的就是「点了没反应」。
    /// 这里只对**我们自己的 id** 兜底，其余 id 原样返回。
    /// </summary>
    [HarmonyPatch(typeof(ProtoSet<ItemProto>), "Select")]
    internal static class Patch_ItemSelectFallback
    {
        private static void Postfix(int id, ref ItemProto __result)
        {
            if (__result != null || id != C.ItemId)
                return;
            try
            {
                ItemProto item = Protos.FindItem();
                if (item != null)
                {
                    __result = item;
                    if (!_logged)
                    {
                        _logged = true;
                        Plugin.Log.LogWarning("LDB.items.Select(" + id + ") 查不到（dataIndices 丢条目），" +
                                              "已用 FindItem 兜底；原版站体窗口会因此直接关窗");
                    }
                }
            }
            catch
            {
                // 兜底失败就走原版结果（null）
            }
        }

        private static bool _logged;
    }

    /// <summary>
    /// 站体窗口切站时的「本站改造」：卡槽排布 / 窗口宽度 / 采矿列挂载。
    ///
    /// 0.9.0~0.9.3 这里挂的是「在标题栏加一颗『物资』按钮 + 首次开站自动弹出独立面板」，
    /// 结果就是两个面板同时出现。现在只保留原版窗口一个面板，这块改成幂等的排布调用 ——
    /// 原版每次开窗 / 换站都会调 `OnStationIdChange`，正好是重新排布的时机。
    /// </summary>
    [HarmonyPatch(typeof(UIStationWindow), "OnStationIdChange")]
    internal static class Patch_StationPanelHint
    {
        private static void Postfix(UIStationWindow __instance)
        {
            try
            {
                if (__instance == null || !__instance.active)
                    return;
                StationPanel.Reflow(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("站体窗口排布失败: " + e.Message);
            }
        }
    }

    /// <summary>原版窗口每帧刷新完后，顺手刷新窗口内的采矿列（内部 0.25 s 节流）。</summary>
    [HarmonyPatch(typeof(UIStationWindow), "_OnUpdate")]
    internal static class Patch_StationPanelTick
    {
        private static void Postfix(UIStationWindow __instance)
        {
            StationPanel.Tick(__instance);
        }
    }

    /// <summary>
    /// 「点开站体窗口」的体检日志（0.9.6）。
    ///
    /// 用户反馈「UI 面板没弹出来」时，必须能区分三种情况，否则只能瞎猜：
    ///   ① 点击根本没被识别 —— <c>PlayerAction_Inspect.InspectSomething</c> 的距离判定
    ///      （`selectDistance`）静默 return，这条日志**不会**出现；
    ///   ② 点击到了、`OpenStationWindow` 也调了，但 `inited == false`
    ///      （`_OnInit` 没成功）→ `_Open()` 直接 return，窗口永远不出现；
    ///   ③ 窗口正常打开 —— active=true，随后 `OnStationIdChange` → 站体面板日志。
    /// 看到 ① 就该查拾取距离，看到 ② 就该查 UIStationStorage 的补造。
    /// </summary>
    [HarmonyPatch(typeof(UIGame), "OpenStationWindow")]
    internal static class Patch_OpenStationDiag
    {
        private static void Postfix()
        {
            try
            {
                UIGame ui = UIRoot.instance != null ? UIRoot.instance.uiGame : null;
                UIStationWindow sw = ui != null ? ui.stationWindow : null;
                if (sw == null)
                {
                    Plugin.Log.LogWarning("开站窗口: stationWindow 为空（UIRoot 未就绪）");
                    return;
                }
                Plugin.Log.LogInfo("开站窗口: active=" + sw.active +
                                   " inited=" + sw.inited +
                                   " stationId=" + sw.stationId +
                                   " 卡槽=" + StationPanel.SlotCount(sw) +
                                   " 窗口=" + (sw.windowTrans != null
                                       ? sw.windowTrans.sizeDelta.x.ToString("0") + "×" +
                                         sw.windowTrans.sizeDelta.y.ToString("0")
                                       : "?"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("开站窗口日志失败: " + e.Message);
            }
        }
    }

    /// <summary>`_OnOpen` 结束后的状态快照（配合 <see cref="Patch_OpenStationDiag"/> 定位）。</summary>
    [HarmonyPatch(typeof(UIStationWindow), "_OnOpen")]
    internal static class Patch_StationOnOpenDiag
    {
        private static int _enterId;

        private static void Prefix(UIStationWindow __instance)
        {
            try
            {
                _enterId = __instance != null ? __instance.stationId : -1;
                // 顺便比对「原版 _OnOpen 用的 factory」和「UIGame 算 num17 用的 factory」是不是同一个。
                // 二者若不同（例如人在太空、localPlanet 与 mainPlayer.factory 不一致），
                // OnStationIdChange 就会在 stationPool 里查不到站，直接把窗口 _Close() 掉。
                PlanetFactory byPlanet = GameMain.localPlanet != null ? GameMain.localPlanet.factory : null;
                PlanetFactory byPlayer = GameMain.mainPlayer != null ? GameMain.mainPlayer.factory : null;
                Plugin.Log.LogInfo("站体窗口 _OnOpen 进入: stationId=" + _enterId +
                                   " active=" + (__instance != null && __instance.active) +
                                   " factory=" + (__instance != null && __instance.factory != null) +
                                   " localPlanet=" + (GameMain.localPlanet != null
                                       ? GameMain.localPlanet.id.ToString() : "null") +
                                   " planetFactory=" + (byPlanet != null ? byPlanet.planet.id.ToString() : "null") +
                                   " playerFactory=" + (byPlayer != null ? byPlayer.planet.id.ToString() : "null") +
                                   " 同一个=" + (byPlanet != null && ReferenceEquals(byPlanet, byPlayer)));
            }
            catch { }
        }

        private static void Postfix(UIStationWindow __instance)
        {
            try
            {
                Plugin.Log.LogInfo("站体窗口 _OnOpen 完成: 进入时 stationId=" + _enterId +
                                   " → 现在 active=" + __instance.active +
                                   " inited=" + __instance.inited +
                                   " stationId=" + __instance.stationId +
                                   " factory=" + (__instance.factory != null) +
                                   " 卡槽=" + StationPanel.SlotCount(__instance));
            }
            catch
            {
                // 日志失败不影响功能
            }
        }
    }

    /// <summary>
    /// 【0.9.7 诊断】站体窗口「一开就关」的定位器。
    ///
    /// 现象：日志里 4 次 `_OnOpen 完成: active=False stationId=0 factory=False`，
    /// 而 `UIGame.OpenStationWindow` 在整个游戏里只有 `UIGame._OnUpdate`
    /// （`inspectStationId == 0 &amp;&amp; num17 &gt; 0`，即真的点到站）一个调用点，
    /// 所以点击是有效的 —— 是 `OnStationIdChange` 里某个早退分支把窗口 `_Close()` 掉了。
    /// `_OnClose` 会把 stationId 清 0、factory 清空，事后从日志看不出是哪条分支，
    /// 所以这里把**退出之前的所有判据**打出来，并在 `_Close` 上挂调用栈。
    ///
    /// 三条早退分支（`UIStationWindow.OnStationIdChange`）：
    ///   ① `stationId == 0 || factory == null`
    ///   ② `transport.stationPool[stationId] == null || station.id != stationId`
    ///   ③ `LDB.items.Select(entityPool[station.entityId].protoId) == null`
    /// 日志里 `sc=…` / `protoId=…` / `itemOk=…` 一眼就能指到其中一条。
    /// </summary>
    [HarmonyPatch(typeof(UIStationWindow), "OnStationIdChange")]
    internal static class Patch_StationIdDiag
    {
        private static float _next;

        private static void Prefix(UIStationWindow __instance)
        {
            try
            {
                if (__instance == null)
                    return;
                float now = Time.realtimeSinceStartup;
                if (now < _next)
                    return;
                _next = now + 0.5f;

                PlanetTransport tr = __instance.transport;
                int id = __instance.stationId;
                int len = tr != null && tr.stationPool != null ? tr.stationPool.Length : -1;
                StationComponent sc = (id > 0 && id < len) ? tr.stationPool[id] : null;

                string detail = "";
                int protoId = -1, modelId = -1;
                bool itemOk = false;
                if (sc != null && __instance.factory != null && __instance.factory.entityPool != null)
                {
                    EntityData[] ep = __instance.factory.entityPool;
                    if (sc.entityId > 0 && sc.entityId < ep.Length)
                    {
                        protoId = ep[sc.entityId].protoId;
                        modelId = ep[sc.entityId].modelIndex;
                        itemOk = LDB.items.Select(protoId) != null;
                    }
                    else detail += " entityId越界(" + sc.entityId + "/" + ep.Length + ")";
                }

                Plugin.Log.LogInfo("[诊断] OnStationIdChange: active=" + __instance.active +
                                   " inited=" + __instance.inited +
                                   " stationId=" + id +
                                   " poolLen=" + len +
                                   " factory=" + (__instance.factory != null) +
                                   " transport=" + (tr != null) +
                                   " sc=" + (sc != null) +
                                   (sc != null
                                       ? " sc.id=" + sc.id +
                                         " entity=" + sc.entityId +
                                         " stellar=" + sc.isStellar +
                                         " pcId=" + sc.pcId +
                                         " storage=" + (sc.storage != null ? sc.storage.Length : -1)
                                       : "") +
                                   " protoId=" + protoId + " modelIndex=" + modelId +
                                   " itemOk=" + itemOk + detail +
                                   " 卡槽=" + StationPanel.SlotDescribe(__instance));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[诊断] OnStationIdChange 前缀失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 【0.9.42 修「存档显示无效 / 存档损坏」】**绝对不能在 <c>GameHistoryData.Init</c> 里补建
    /// <c>techStates</c>**（0.9.15 起就是这么干的，0.9.41 之前一直埋着这颗雷）。
    ///
    /// 原版 <c>GameData.Import</c> 的顺序是：
    /// <code>
    /// this.history = new GameHistoryData();
    /// this.history.Init(this);    // 只把 techStates 换成空字典
    /// this.history.Import(r);     // 逐条 techStates.Add(...)
    /// </code>
    /// 而 <c>GameHistoryData.Import</c> 对存档里每条科技状态是**不判重、直接 Add**：
    /// <code>
    /// if (techProto != null) this.techStates.Add(num9, techState);
    /// </code>
    /// 我们在 Init 之后先把 1997 塞进去，Import 再读到存档里那条 1997，就是
    /// <c>ArgumentException: An item with the same key has already been added</c>：
    ///   · 点「加载」：异常冒到 <c>GameSave.LoadCurrentGame</c> 被 catch → 返回 false →
    ///     GameMain 自毁 → GameLoader 弹 <b>「无效存档 / 加载存档失败」</b>；
    ///   · 读档窗口选中存档时走 <c>GameData.ImportGamePropertyData</c>（预览导入，同样是
    ///     Init + Import）→ 异常 → 右侧详情显示 <b>「存档损坏」</b>。
    ///
    /// 存档里有没有 1997，取决于「这份存档是不是本 mod 写过的」：本 mod 写过的存档一定有
    /// （Init 补建的那一条会被 <c>GameHistoryData.Export</c> 原样写进去），所以
    /// <b>用本 mod 存一次档，之后就读不开了</b>；反过来 0.9.39 之前写下、还没被本 mod
    /// 覆写过的老存档能读 —— 这就是「0.9.39 看着正常」的假象。
    ///
    /// 现在的时机照 ProjectGenesis：<c>Init</c> 保持原样（空字典交给 Import 填），
    /// 在整个 <c>GameData.Import</c> 跑完之后再兜底补建（<see cref="Patch_GameDataImport"/>，
    /// 对应它的 <c>InitialTechPatches.Import</c>），新档用 <c>SetForNewGame</c> 收尾。
    /// </summary>
    [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
    internal static class Patch_GameDataImport
    {
        private static void Postfix(GameData __instance)
        {
            if (__instance != null && !DSPGame.IsMenuDemo)
                Protos.EnsureTechState(__instance.history);
        }
    }

    /// <summary>
    /// 新档：原版 <c>SetForNewGame</c> 会按 <c>LDB.techs.dataArray</c> 逐条建状态，本 mod
    /// 科技只要那时已经在表里就自然带上；这里是「科技注册晚于新档初始化」时的兜底。
    /// </summary>
    [HarmonyPatch(typeof(GameData), nameof(GameData.SetForNewGame))]
    internal static class Patch_GameDataSetForNewGame
    {
        private static void Postfix(GameData __instance)
        {
            if (__instance != null && !DSPGame.IsMenuDemo)
                Protos.EnsureTechState(__instance.history);
        }
    }

    /// <summary>
    /// 读档：<c>GameHistoryData.Import</c> 正常跑完后兜底一次（原版已经按 LDB 全表补过，
    /// 这里只兜「Import 时科技还不在表里」的极端情况）。
    ///
    /// 只对**真正的游戏历史**生效：读档窗口预览会 <c>new</c> 一个临时 GameHistoryData
    /// （<c>GameData.ImportGamePropertyData</c>），往那上面写没有意义，还会让
    /// 「科技状态补建」日志刷屏 —— 这个预览实例必须和真实存档数据完全隔离。
    /// </summary>
    [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.Import))]
    internal static class Patch_HistoryImport
    {
        private static void Postfix(GameHistoryData __instance)
        {
            GameData d = GameMain.data;
            if (d != null && ReferenceEquals(__instance, d.history))
                Protos.EnsureTechState(__instance);
        }
    }

    /// <summary>
    /// 【0.9.15】UITechNode 的 NullReferenceException 兜底。
    ///
    /// 原版 <c>UITechNode.UpdateInfoComplete()</c> 会直接读 <c>techProto.unlockRecipeArray.Length</c>、
    /// <c>itemArray.Length</c>、<c>preTechArray</c> 这些派生字段，而它们只有
    /// <c>TechProto.Preload2()</c> 会建。任何 mod 的科技只要漏跑一次 Preload2，一开科技树就是：
    /// <code>
    /// NullReferenceException
    ///   at UITechNode.UpdateInfoComplete () [0x0038f] ;IL_038F
    ///   at UITechNode.SetTechProto (TechProto proto)
    ///   at UITechNode._OnInit ()
    /// </code>
    /// 0.9.14 正是被这个坑死的：LDBTool 用配置文件里的旧 ID（3996）覆盖了 proto.ID，
    /// 预加载按 1997 找不到它，整个 PreloadTech 被跳过。
    /// 这里在节点初始化前把派生数组补齐 —— 不管是谁的科技都不会再炸。
    /// </summary>
    [HarmonyPatch(typeof(UITechNode), "SetTechProto")]
    internal static class Patch_TechNodePreload
    {
        private static void Prefix(TechProto __0)
        {
            try
            {
                Protos.EnsureTechArrays(__0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("科技节点兜底失败: " + e.Message);
            }
        }
    }

    /// <summary>【0.9.7 诊断】谁把「还开着」的站体窗口关了 —— 打托管调用栈。</summary>
    [HarmonyPatch(typeof(UIStationWindow), "_OnClose")]   // 0.10.35 起原版只有 _OnClose，_Close 已不存在
    internal static class Patch_StationCloseDiag
    {        private static void Prefix(UIStationWindow __instance)
        {
            try
            {
                if (__instance == null || !__instance.active)
                    return;
                var sb = new System.Text.StringBuilder(512);
                sb.Append("[诊断] 站体窗口被关闭: stationId=").Append(__instance.stationId)
                  .Append(" factory=").Append(__instance.factory != null)
                  .Append('\n');
                string[] lines = (Environment.StackTrace ?? "").Split('\n');
                for (int i = 0; i < lines.Length && i < 10; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0)
                        continue;
                    sb.Append("    ").Append(t).Append('\n');
                }
                Plugin.Log.LogWarning(sb.ToString());
            }
            catch
            {
                // 诊断失败不影响功能
            }
        }
    }

    /// <summary>
    /// 【0.9.7 兜底】「玩家正看着一个站，站体窗口却是关的」→ 重新开一次。
    ///
    /// 原版 `UIGame._OnUpdate` 里每个建筑类别都有一段
    /// `ShutAllFunctionWindow(); OpenXxxWindow();`，而 `ShutAllFunctionWindow()` 会连带
    /// `ShutStationWindow()`。只要同一个实体身上多挂了一个组件 id
    /// （例如站点实体被补了 `powerGenId` / `powerNodeId`），就会先关掉站体窗口
    /// 再开「发电机 / 电力节点」窗口 —— 表现就是「面板弹不出来 / 弹出来是别的窗口」。
    ///
    /// 正常情况这一层永远不会触发（日志里不会有「被原版关掉了」）；
    /// 一旦触发，说明还有别的分支在抢窗口：最多重开 3 次后放弃，
    /// 真正关窗的调用栈由 <see cref="Patch_StationCloseDiag"/> 打出来。
    /// 只对本站生效（原版站一个字节都不碰）。
    /// </summary>
    [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
    internal static class Patch_StationWindowWatchdog
    {
        private static readonly System.Reflection.FieldInfo InspectField =
            AccessTools.Field(typeof(UIGame), "inspectStationId");

        private static int _lastInspect = -1;
        private static int _tries;

        private static void Postfix(UIGame __instance)
        {
            try
            {
                if (__instance == null || InspectField == null)
                    return;

                int inspect = (int)InspectField.GetValue(__instance);
                if (inspect != _lastInspect)
                {
                    _lastInspect = inspect;
                    _tries = 0;
                }
                if (inspect <= 0)
                    return;

                UIStationWindow sw = __instance.stationWindow;
                if (sw == null || sw.active || !sw.inited)
                    return;

                PlanetFactory f = GameMain.mainPlayer != null ? GameMain.mainPlayer.factory : null;
                if (f == null || f.transport == null || f.transport.stationPool == null)
                    return;
                if (inspect >= f.transport.stationPool.Length)
                    return;
                StationComponent sc = f.transport.stationPool[inspect];
                if (sc == null || sc.id != inspect || !StationPanel.IsOurs(f, sc))
                    return;                     // 原版站不管

                if (_tries >= 3)
                    return;
                _tries++;

                Plugin.Log.LogWarning("站体窗口被原版关掉了，重开第 " + _tries + " 次: stationId=" + inspect);
                sw.stationId = inspect;         // 窗口此时 inactive，setter 只写字段
                sw._Open();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[诊断] 站体窗口兜底重开失败: " + e.Message);
            }
        }
    }
    /// <summary>
    /// 【0.9.34】让第 10 格（核电燃料格）能放「任意一种燃料棒」。
    ///
    /// 原版 <c>UIStationStorage.OnItemIconMouseDown</c> 的第一道判断是
    /// <c>mainPlayer.inhandItemId != station.storage[index].itemId → return</c>，
    /// 也就是「手上拿的物品必须和格子里已锁定的物品同种」才允许放。而物流站的格子
    /// 一旦 itemId 被设成氘核燃料棒，就再没有第二个入口换成反物质棒 / 奇异湮灭棒
    /// —— 「选择物品」按钮（<c>selectItemButton</c>）只在 <c>itemId == 0</c> 的
    /// <c>emptyRelatedGos</c> 里可见。于是玩家反馈「只能放入氘棒 40 s，
    /// 反物质棒和奇异湮灭棒放不进去」。
    ///
    /// 这里在鼠标按下时先把格子换成手上那种棒，再交给原版继续走：
    ///   · 换种类调用原版自己的 <see cref="PlanetTransport.SetStationStorage"/>，
    ///     它在 itemId 变化时会自动把格子里原有的棒退回玩家背包
    ///     （<c>player.TryAddItemToPackage(..., throwTrash: true)</c> + 背包满提示），
    ///     不吞货、不丢 inc，多人同步也走原版通道；
    ///   · 换完之后原版那道 <c>inhandItemId != itemId</c> 自然成立，
    ///     塞货、扣手、inc 处理全部沿用原版代码，我们不重复实现。
    /// </summary>
    [HarmonyPatch(typeof(UIStationStorage), "OnItemIconMouseDown")]
    internal static class Patch_FuelSlotAnyRod
    {
        private static bool Prefix(UIStationStorage __instance,
                                   UnityEngine.EventSystems.BaseEventData evt)
        {
            try
            {
                if (__instance == null || __instance.station == null || __instance.stationWindow == null)
                    return true;

                int idx = __instance.index;
                if (idx != C.FuelSlot)
                    return true;                    // 只动第 10 格

                var ped = evt as UnityEngine.EventSystems.PointerEventData;
                if (ped == null ||
                    ped.button != UnityEngine.EventSystems.PointerEventData.InputButton.Left)
                    return true;                    // 右键是「取出」，走原版

                Player p = GameMain.mainPlayer;
                if (p == null || p.inhandItemId <= 0 || p.inhandItemCount <= 0)
                    return true;                    // 手上没东西（= 取回/投放模式）

                if (!StationNuclear.IsRod(p.inhandItemId))
                    return true;                    // 手上不是燃料棒，原版该怎么拒就怎么拒

                StationComponent sc = __instance.station;
                if (sc.storage == null || idx >= sc.storage.Length)
                    return true;

                int cur = sc.storage[idx].itemId;
                if (cur == p.inhandItemId)
                    return true;                    // 同种：原版自己会累加

                PlanetTransport tp = __instance.stationWindow.transport;
                if (tp == null)
                    return true;

                tp.SetStationStorage(sc.id, idx, p.inhandItemId, C.FuelSlotMax,
                                     ELogisticStorage.Demand,
                                     sc.isStellar ? ELogisticStorage.Demand : ELogisticStorage.None,
                                     p);

                Plugin.Log.LogInfo("核电燃料格: 站 id=" + sc.id + " 手动换棒 " +
                                   (cur > 0 ? StationNuclear.ShortName(cur) : "空") + " → " +
                                   StationNuclear.ShortName(p.inhandItemId));
                return true;                        // 交给原版：此时 itemId 已匹配，正常放入
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("核电燃料格: 换棒补丁异常 — " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// 【0.9.41】开局「跳过序章 / 开始序章」的就绪守卫。
    /// <para>
    /// 崩溃现场（ErrorAnalyzer 报告，13 mod 环境）：
    /// <c>GameLoader.FixedUpdate → GameData.SkipStandardModeGuide →
    /// GuideMissionStandardMode.Skip → NullReferenceException</c>。
    /// </para>
    /// <para>
    /// 根因不在我们，但和我们同环境的组合会触发：BulletTime 的「快速加载」Transpiler 把
    /// frame 7 的等待条件 <c>localStar != null &amp;&amp; !localStar.loaded</c> 里的
    /// <c>StarData.loaded</c> 换成了 <c>GameMain.localPlanet?.loaded ?? true</c> ——
    /// <b>localPlanet 尚未赋值时该条件恒为 true，frame 7 的等待被完全绕过</b>；
    /// 原版第二道闸（<c>localPlanet != null &amp;&amp; !localPlanet.factoryLoaded</c>）
    /// 在 localPlanet 为 null 时同样放行。于是 frame 9 一到就执行
    /// <c>SkipStandardModeGuide()</c>，而 <c>GuideMissionStandardMode.Skip</c> 第一段就要
    /// <c>localPlanet.birthPoint</c> / <c>localPlanet.factory.FlattenTerrain(...)</c> → NRE。
    /// </para>
    /// <para>
    /// 修法：前缀里检查就绪条件（mainPlayer / localPlanet / factory 都非空），不就绪就
    /// 拦下这次调用并登记 pending；<c>GameMain.FixedUpdate</c> 的 postfix 里每帧重试，
    /// 星球一就绪立刻补发（此时仍在游戏最初几 tick，行为与原版 frame 9 等价）。
    /// 两个入口（Skip / Start）都守，Start 的 NRE 风险相同。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GameData), "SkipStandardModeGuide")]
    internal static class Patch_GuideSkipGuard
    {
        internal static Action _pending;     // 待补发的原版调用
        internal static GameData _owner;     // pending 归属的 GameData（防止回主菜单/换档后串档）
        internal static float _expireAt;     // 超过 30 秒仍不就绪就放弃并报错

        internal static bool Ready(GameData g)
        {
            return g != null && g.mainPlayer != null &&
                   g.localPlanet != null && g.localPlanet.factory != null;
        }

        internal static void Defer(GameData g, Action vanillaCall, string what)
        {
            _pending = vanillaCall;
            _owner = g;
            _expireAt = Time.unscaledTime + 30f;
            Plugin.Log.LogWarning("开局引导: localPlanet/factory 尚未就绪，" + what +
                                 "的调用已推迟到星球加载完成后自动执行" +
                                 "（防 BulletTime 快速加载抢跑 NRE）");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(GameData __instance)
        {
            try
            {
                if (Ready(__instance))
                    return true;                     // 正常路径：放行原版

                // 拦下这次调用；postfix 补发时直接调 __instance.SkipStandardModeGuide()，
                // 会再次进本前缀 —— 届时 Ready 已成立，自然放行，不会死循环。
                Defer(__instance, () => __instance.SkipStandardModeGuide(), "跳过序章");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("开局引导守卫异常: " + e);
                return true;
            }
        }
    }

    /// <summary>每帧重试挂点：GameMain.FixedUpdate 只在 Begin 之后跑，正好覆盖补发窗口。</summary>
    [HarmonyPatch(typeof(GameMain), "FixedUpdate")]
    internal static class Patch_GuideRetry
    {
        private static void Postfix()
        {
            if (Patch_GuideSkipGuard._pending == null)
                return;

            Action act = Patch_GuideSkipGuard._pending;
            GameData d = GameMain.data;
            if (d == null || !ReferenceEquals(d, Patch_GuideSkipGuard._owner))
            {
                Patch_GuideSkipGuard._pending = null;    // 回主菜单 / 换档：作废
                return;
            }

            if (GameMain.gameTick > 1 || Time.unscaledTime > Patch_GuideSkipGuard._expireAt)
            {
                Patch_GuideSkipGuard._pending = null;
                Plugin.Log.LogError("开局引导: 星球迟迟未就绪，放弃补发开局引导" +
                                    "（gameTick=" + GameMain.gameTick + "）。如复现请反馈日志。");
                return;
            }

            if (!Patch_GuideSkipGuard.Ready(d))
                return;                              // 继续等

            Patch_GuideSkipGuard._pending = null;
            Plugin.Log.LogInfo("开局引导: 星球已就绪，补发开局引导调用");
            try { act(); }
            catch (Exception e) { Plugin.Log.LogError("开局引导补发失败: " + e); }
        }
    }

    /// <summary>StartStandardModeGuide 与 Skip 同一条链、同样的 NRE 风险，走同一套守卫。</summary>
    [HarmonyPatch(typeof(GameData), "StartStandardModeGuide")]
    internal static class Patch_GuideStartGuard
    {
        private static bool Prefix(GameData __instance)
        {
            try
            {
                if (Patch_GuideSkipGuard.Ready(__instance))
                    return true;

                Patch_GuideSkipGuard.Defer(__instance,
                    () => __instance.StartStandardModeGuide(), "开始序章");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("开局引导守卫异常: " + e);
                return true;
            }
        }
    }

}
