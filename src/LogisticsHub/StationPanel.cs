using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace LogisticsHub
{
    /// <summary>
    /// 站体窗口里的「本站改造」：卡槽排布 + 右侧只读的采矿情况。
    ///
    /// 为什么不再有独立面板：0.9.0~0.9.3 把 20 格物资拆进了一个自建窗口（Alt+H），
    /// 结果玩家点开站体时屏幕上是**两个**面板 —— 原版窗口（6 个空栏位 + 设置）
    /// 和自建窗口（物资 + 采矿），内容重复、还互相压住。用户实测反馈的正是这一点。
    ///
    /// 0.9.4 改成「只有一个面板，就是原版站体窗口本身」：
    ///   ① <see cref="Patch_StationSlots"/> 把原版 `storageUIs` 从 6 个补造到
    ///      <see cref="C.ManualSlots"/> 个，并把行距压到 <see cref="C.SlotPitch"/>、
    ///      卡槽等比缩到 <see cref="C.SlotScale"/> —— 10 行 580 px 装得进 1080p；
    ///   ② 本类在窗口内容区右侧挂一列只读清单（每种矿：图标 / 名称 / 剩余储量 / 站内库存），
    ///      并把窗口横向加宽同样的宽度；
    ///   ③ 原版站（别的物流塔）一个像素都不动：卡槽恢复 76 px / 1.0 缩放，窗口恢复原宽，
    ///      采矿列隐藏。
    ///
    /// 为什么把采矿列画在**原版窗口里**而不是另开窗口：原版窗口每帧 `RefreshTrans`
    /// 只重算高度，宽度由我们设一次就能稳定；而且窗口被玩家拖着走时，列跟着走，
    /// 不会有「面板飘在别处」的问题。z 序上本列被 `SetAsFirstSibling` 放在最底层，
    /// 所以卡槽自带的供需下拉弹层（`popupBoxRect` 锚在卡槽右外侧）仍然浮在它上面。
    /// </summary>
    internal class StationPanel : MonoBehaviour
    {
        // ---------------- 版面（0.9.31 表格化） ----------------
        // 列高 = ManualSlots × SlotPitch = 540，和左侧 10 行卡槽同高。
        //
        // 0.9.9~0.9.30 是「一段说明文字 + 一堆 13 px 小字 + 10 行两行式矿种」，
        // 玩家实测的两条反馈（「换成表格」「供电/地热/钻孔/电池这些字太小、
        // 背景颜色也不搭」）其实是同一个根因：340 px 宽的列里挤了三层信息，
        // 想放大任何一个字都会顶边，所以只能一路 13 px；而列底又是 0.62 alpha
        // 的半透明深蓝，压在游戏那张亮蓝色行星背景上，13 px 的字自然发糊。
        //
        // 0.9.31 重排成两张表：
        //   ① 上半是四行键值表（供电 / 功率 / 钻孔 / 电池）+ 两行科技表，
        //      标签列固定 46 px、读数列 17~20 px，所有数值左边缘对齐；
        //   ② 下半是真正的四列表格（资源 / 储量 / 处 / 库存），带深色表头条、
        //      斑马纹行底、右对齐数字列 —— 数字列对齐之后「一眼扫完」才成立。
        // 字号整体抬一档：标签 16、读数 17、状态 20、标题 24。
        private const float Pad = 12f;
        private const float KeyW = 46f;          // 标签列宽（供电/科技… 两个汉字 = 32 px）
        private const float ValX = Pad + KeyW;   // 读数列左边缘
        private const float RowPitch = 28.5f;    // 资源表行距：10 行 = 285 px
        private const int MaxRows = 10;
        private const float ListTop = 247f;      // 资源表首行
        private const float WindowPadR = 20f;    // 加宽后右侧至少留这么多
        private const float SlotX = 40f;         // 原版卡槽起始 x（storage-box-0 实测 40）
        private const float SlotY = -90f;        // 原版首行 y

        // 资源表的列边界（相对行左边缘，行宽 = MineColW - 2×Pad = 416）
        //
        // 【0.9.37】列宽 380 → 440（行宽 356 → 416）。原因：科技行要加第三条
        // 「地热×1.9」，实测 226 px；加上左格「矿物利用 已解锁」124 px 与标签列 46 px，
        // 356 px 的旧列怎么分都差 40 px（0.9.31 那版把 KeyW 漏算进右格宽度，
        // 实机上右侧文字其实已经挤到左格上）。加宽 60 px 后：
        //   科技行 0.30 → 左 124.8（刚好装「已解锁」）/ 右 245.2（226 有 19 px 余量）；
        //   武器行 0.30 → 左 124.8（「能量+30%」121）/ 右 245.2（「激光…弹10MJ」211）。
        // 四列按新宽度重新分配：名称 +16、储量 +16、处 +4、库存 +12。
        private const float CNameX = 32f;        // 图标 26 px + 6 px 间隙
        private const float CNameW = 112f;       // → 右边缘 144
        private const float CAmtX = 148f;        // 储量
        private const float CAmtW = 100f;        // → 右边缘 248
        private const float CAreaX = 252f;       // 处
        private const float CAreaW = 44f;        // → 右边缘 296
        private const float CStockX = 300f;      // 库存
        private const float CStockW = 116f;      // → 右边缘 416 = 行宽

        // ---------------- 配色 ----------------
        // 0.9.5 把配色从「深底+金色」改成冷色系，但字号只有 10~13 px，冷色又压得偏灰，
        // 实机看就是「字小、颜色和字不搭」（用户 0.9.7 反馈）。
        // 0.9.8 的原则：
        //   · 底部加一层稍实的冷蓝黑，让 14 px 的字有对比度；
        //   · 所有正文统一一个冷色族（白 → 浅青 → 蓝灰），只在末尾降一档亮度，不用五种灰；
        //   · 状态色只给「状态行」三个词用，且都抬高亮度，避免灰扑扑。
        //
        // 0.9.31：底色从 0.62 alpha 提到 0.92 —— 列底压的是游戏里那张亮蓝色行星背景，
        // 半透明底 + 小字 = 字被背景吃掉（玩家反馈「是不是背景颜色搭配的问题」）。
        // 现在整列是一张近实心的深色卡：表头再深一档、偶数行铺一层淡膜做斑马纹，
        // 行与行之间有明确的横向边界，读数字时眼睛不会串行。
        private static readonly Color BgMine = new Color(0.035f, 0.055f, 0.075f, 0.92f);
        private static readonly Color Edge = new Color(0.45f, 0.78f, 0.95f, 0.42f);
        private static readonly Color HeaderBg = new Color(0.10f, 0.16f, 0.22f, 0.96f);  // 表头条
        private static readonly Color RowEven = new Color(0.09f, 0.14f, 0.19f, 0.60f);   // 斑马纹
        private static readonly Color TextMain = new Color(0.94f, 0.98f, 1.00f, 1f);   // 物品名：近白
        private static readonly Color TextValue = new Color(0.66f, 0.90f, 1.00f, 1f);  // 数值：浅青
        private static readonly Color TextDim = new Color(0.74f, 0.84f, 0.91f, 1f);    // 次要说明
        private static readonly Color TextFaint = new Color(0.56f, 0.67f, 0.76f, 1f);  // 表头 / 占位
        private static readonly Color TextKey = new Color(0.60f, 0.77f, 0.88f, 1f);    // 键值表的标签列
        private static readonly Color TitleCol = new Color(0.90f, 0.98f, 1.00f, 1f);   // 列标题
        private static readonly Color Accent = new Color(0.40f, 0.80f, 0.98f, 0.85f);  // 标题下划线 / 强调
        private static readonly Color StGrid = new Color(0.46f, 0.96f, 0.84f, 1f);     // 运行中（电网）
        // 地热的琥珀：0.9.8 用的 (1.00,0.80,0.42) 在深蓝底上太跳，和窗口不是一套色。
        // 压到偏暖的浅金，既还认得出「热」，又不是告警色。
        private static readonly Color StGeo = new Color(0.98f, 0.87f, 0.66f);          // 地热自供电
        private static readonly Color StOff = new Color(0.95f, 0.62f, 0.58f, 1f);      // 未通电
        // 核电（0.9.30）：偏冷绿，和电网的青绿、地热的琥珀区分开 —— 玩家一眼能分出
        // 「现在是谁在供电」，不用读文字。
        private static readonly Color StNuke = new Color(0.66f, 0.96f, 0.72f, 1f);
        // 电力行（原版窗口那一行）专用的低饱和色：截图里 0.9.9 用的亮青 + 亮金
        // 在描边叠伪粗体之后过曝成光斑，这里把明度和饱和度都退一档，让它只当「读数」。
        private static readonly Color PowerValue = new Color(0.62f, 0.78f, 0.88f, 1f);
        private static readonly Color PowerGrid = new Color(0.48f, 0.76f, 0.66f, 1f);
        private static readonly Color PowerGeo = new Color(0.82f, 0.71f, 0.50f, 1f);
        // 「矿物利用」等级：浅金，比地热状态暗一档 —— 它是长期加成，不该比运行状态更抢眼。
        private static readonly Color TechLv = new Color(0.86f, 0.80f, 0.62f, 1f);
        private static readonly Color Line = new Color(0.45f, 0.72f, 0.88f, 0.22f);
        private static readonly Color BattBg = new Color(0.20f, 0.27f, 0.33f, 0.85f);
        // 资源图标底板：比列底再暗半档、偏青，给 80 px 图标一块「槽」的边界。
        // 0.9.26 之前图标是直接贴在深色列上的 22 px 小图，实机看像一块色斑
        // （用户截图反馈「不是实际资源小图标」），加底槽 + 放大后才有图标感。
        private static readonly Color IconBg = new Color(0.10f, 0.155f, 0.20f, 0.70f);

        // ---------------- 静态状态 ----------------
        private static StationPanel _inst;
        private static Sprite _white;
        private static Font _font;
        private static bool _wide;          // 窗口当前是否处于「本站的加宽态」
        private static float _baseW;        // 原版窗口宽度（第一次见到的 sizeDelta.x）
        private static float _slotW = 520f;

        internal static readonly FieldInfo SlotsField =
            AccessTools_Field(typeof(UIStationWindow), "storageUIs");

        /// <summary>面板是否已经建好（诊断用）。</summary>
        internal static bool Ready { get { return _inst != null; } }

        // ---------------- 实例状态 ----------------
        private UIStationWindow _sw;
        private RectTransform _root;
        /// <summary>键值表第 1 行：谁在供电（● 电网 / ● 地热 / ● 核电 / ○ 未通电）。</summary>
        private Text _state;
        /// <summary>键值表第 2 行：功率构成 + 塔顶合计（电网45.2 + 地热6.00 = 塔顶51MW）。</summary>
        private Text _power;
        /// <summary>键值表第 3 行：钻孔节拍（43 个 / 1.0s · ×3.00）。</summary>
        private Text _drill;
        /// <summary>键值表第 4 行：电池（33%　202.8MJ / 30.0GJ　+6.0MW）。</summary>
        private Text _batt;
        private Image _battImg;
        private RectTransform _battFill;
        private bool _powerStyled;
        // 0.9.12：电力行不再往原版那两个 Text 里写字，而是在它们上面盖两个**自己的**
        // Text（普通 UnityEngine.UI.Text）。原版那两格不管怎么调属性都会同色多遍绘制，
        // 亮色字自己把自己糊成光斑 —— 只有自己造 Text 才能确定它长什么样。
        private Text _geoMw;
        private Text _geoMul;
        private bool _hostHidden;
        /// <summary>科技表第 1 行左格：矿物利用等级。</summary>
        private Text _techLv;
        /// <summary>科技表第 1 行右格：速度 / 耗矿倍率。</summary>
        private Text _techMul;
        /// <summary>科技表第 2 行左格：能量武器加成（0.9.38 起 = 原版「能量武器伤害」升级）。</summary>
        private Text _weapon;
        /// <summary>科技表第 2 行右格：激光 / 电浆伤害与每发能耗（0.9.31 拆成独立 Text，
        /// 之前挤在同一行 340 px 里必然顶边，只能压到 13 px）。</summary>
        private Text _weaponR;
        /// <summary>0.9.30：标题行右侧的核电读数（第 10 格燃料棒 → FusionPowerMw）。</summary>
        private Text _nuke;
        /// <summary>资源表上方的一行概况：种类 / 总储量。</summary>
        private Text _sumL;
        /// <summary>资源表上方的一行概况：自动格占用 / 每格容量。</summary>
        private Text _sumR;
        private Text _hint;
        private bool _iconLogged;
        private OreRow[] _rows;
        private int _stationId = -1;
        private float _next;

        private class OreRow
        {
            public RectTransform Root;
            public RawImage Icon;
            /// <summary>当前图标对应的物品 id：没变就不重设 texture/uvRect（两者都会重建网格）。</summary>
            public int IconItem;
            public Text Name;
            /// <summary>储量列（海洋 = 取之不尽）。</summary>
            public Text Amount;
            /// <summary>矿脉处数列。</summary>
            public Text Areas;
            /// <summary>站内库存列。</summary>
            public Text Stock;
        }

        private static FieldInfo AccessTools_Field(Type t, string n)
        {
            try { return t.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); }
            catch { return null; }
        }

        // ==================== 对外入口 ====================

        /// <summary>
        /// 每次站体窗口切换站点（`OnStationIdChange`）时调用：
        /// 判定是不是本站 → 排卡槽 / 调窗口宽度 / 挂采矿列。
        /// 必须幂等 —— 原版每次开窗都会调它，玩家拖过窗口位置不能被打乱。
        /// </summary>
        internal static void Reflow(UIStationWindow sw)
        {
            try
            {
                if (sw == null || sw.windowTrans == null || sw.transport == null || sw.transport.stationPool == null)
                    return;
                int id = sw.stationId;
                if (id <= 0 || id >= sw.transport.stationPool.Length)
                    return;
                StationComponent sc = sw.transport.stationPool[id];
                if (sc == null || sc.id != id)
                    return;

                bool ours = IsOurs(sw.factory, sc);

                MeasureSlot(sw);
                LayoutSlots(sw, ours);
                LogSwitch(sw, sc, ours);
                if (ours && !Ensure(sw))
                    return;
                FitWidth(sw, ours);
                Patch_StationWindowSize.ResetVertical();
                // 开窗那一帧就把高度夹正：原版 OnStationIdChange 是按 20 行算的高度，
                // 等到下一帧 RefreshTrans 才减会闪一帧 1900 px 高的窗口。
                Patch_StationWindowSize.FixHeight(sw, sc);

                if (!ours)
                {
                    Show(false);
                    return;
                }

                _inst._stationId = -1;          // 强制重画一次
                _inst.Bind(sw, sc);
                _inst.Refresh(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("站体面板: 排布失败 — " + e.Message);
            }
        }

        /// <summary>
        /// 每次「窗口切到某个站」打印一行 —— 排查「面板没弹出来」这类问题，
        /// 第一步要能区分「窗口压根没开」和「窗口开了但没认出本站」。
        /// 同一站 + 同一判定结果只打一次，不刷屏。
        /// </summary>
        private static int _logId = int.MinValue;
        private static bool _logOurs;

        private static void LogSwitch(UIStationWindow sw, StationComponent sc, bool ours)
        {
            if (_logId == sc.id && _logOurs == ours)
                return;
            _logId = sc.id;
            _logOurs = ours;
            string win = sw.windowTrans != null
                ? sw.windowTrans.sizeDelta.x.ToString("0") + "×" + sw.windowTrans.sizeDelta.y.ToString("0")
                : "?";
            Plugin.Log.LogInfo("站体面板: 切到站 id=" + sc.id + " entity=" + sc.entityId +
                               (ours ? "（本站）" : "（原版站，保持原样）") +
                               " 卡槽=" + SlotCount(sw) + "/" + (sc.storage != null ? sc.storage.Length : 0) +
                               " 窗口=" + win);
        }

        /// <summary>窗口当前绑定的站（无效时返回 null）。</summary>
        private static StationComponent Current(UIStationWindow sw)
        {
            if (sw == null || sw.transport == null || sw.transport.stationPool == null)
                return null;
            int id = sw.stationId;
            if (id <= 0 || id >= sw.transport.stationPool.Length)
                return null;
            StationComponent sc = sw.transport.stationPool[id];
            return sc != null && sc.id == id ? sc : null;
        }

        /// <summary>原版窗口每帧 `_OnUpdate` 之后调用（0.25 s 节流刷新数值）。</summary>
        internal static void Tick(UIStationWindow sw)
        {
            // 【0.9.6】这里多了一层「自愈」：
            // 正常路径是 OnStationIdChange → Reflow 把采矿列挂上去，但只要那条路径没走到
            // （换站、重开窗口、别的 mod 抢先改了窗口状态、异常被上游吞掉…），
            // _inst 就是 null，整个面板永远不出现，玩家看到的就是「点开站体，右边什么都没有」。
            // _OnUpdate 只在窗口 active 时才被调用，所以它是最可靠的「窗口现在开着」信号：
            // 在这里补一次 Reflow，就能保证「只要窗口开得出来，采矿列就一定在」。
            if (sw == null || !sw.active)
                return;

            if (_inst == null || _inst._sw != sw)
            {
                // 先确认窗口里确实是我们自己的站 —— 原版物流塔不要每帧重排一遍。
                StationComponent cur = Current(sw);
                if (cur == null || !IsOurs(sw.factory, cur))
                {
                    Show(false);
                    return;
                }
                Reflow(sw);
                if (_inst == null || _inst._sw != sw)
                    return;
            }

            try
            {
                _inst.Step(sw);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("站体面板刷新失败: " + e.Message);
            }
        }

        /// <summary>本站判定：protoId / modelIndex 任一命中即可（与 StationTurrets 同口径）。</summary>
        internal static bool IsOurs(PlanetFactory factory, StationComponent sc)
        {
            if (factory == null || sc == null || sc.entityId <= 0 || factory.entityPool == null)
                return false;
            if (sc.entityId >= factory.entityPool.Length)
                return false;
            EntityData ed = factory.entityPool[sc.entityId];
            return ed.protoId == C.ItemId || ed.modelIndex == C.ModelId;
        }

        /// <summary>卡槽数（原版 6 + 我们补造的）。</summary>
        internal static int SlotCount(UIStationWindow sw)
        {
            if (sw == null || SlotsField == null)
                return 0;
            var arr = SlotsField.GetValue(sw) as UIStationStorage[];
            return arr == null ? 0 : arr.Length;
        }

        /// <summary>诊断串：卡槽数量 + 空引用/未初始化数量（0.9.7 定位「一开就关」用）。</summary>
        internal static string SlotDescribe(UIStationWindow sw)
        {
            try
            {
                var arr = sw != null && SlotsField != null
                    ? SlotsField.GetValue(sw) as UIStationStorage[]
                    : null;
                if (arr == null)
                    return "null";
                int nul = 0, uninited = 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == null) { nul++; continue; }
                    if (!arr[i].inited) uninited++;
                }
                return arr.Length + "(空" + nul + "/未初始化" + uninited + ")";
            }
            catch (Exception e)
            {
                return "err:" + e.Message;
            }
        }

        private static UIStationStorage[] Slots(UIStationWindow sw)
        {
            if (sw == null || SlotsField == null)
                return null;
            return SlotsField.GetValue(sw) as UIStationStorage[];
        }

        // ==================== 卡槽排布 / 窗口宽度 ====================

        /// <summary>
        /// 卡槽排布。原版每帧只把 `storageUIs[0]` 摆回 (40,-90)，其余位置是 `_OnCreate` 时定的，
        /// 所以这里直接整排重设 —— 原版站恢复 76 px / 1.0，本站改成 58 px / 0.76。
        /// </summary>
        private static void LayoutSlots(UIStationWindow sw, bool ours)
        {
            UIStationStorage[] arr = Slots(sw);
            if (arr == null || arr.Length == 0)
                return;

            float pitch = ours ? C.SlotPitch : 76f;
            float scale = ours ? C.SlotScale : 1f;

            for (int i = 0; i < arr.Length; i++)
            {
                UIStationStorage u = arr[i];
                if (u == null)
                    continue;
                var rt = u.transform as RectTransform;
                if (rt == null)
                    continue;
                rt.anchoredPosition = new Vector2(SlotX, SlotY - pitch * i);
                rt.localScale = new Vector3(scale, scale, 1f);
            }
        }

        /// <summary>卡槽 prefab 的实际尺寸（520×70），面板宽高与行距全跟着它走，不写死。</summary>
        private static void MeasureSlot(UIStationWindow sw)
        {
            if (sw == null || sw.storageUIPrefab == null)
                return;
            var rt = sw.storageUIPrefab.transform as RectTransform;
            if (rt != null && rt.sizeDelta.x > 100f)
                _slotW = rt.sizeDelta.x;
        }

        /// <summary>采矿列的左边界（相对窗口内容区左上角）。</summary>
        private static float ColX()
        {
            return SlotX + _slotW * C.SlotScale + C.MineColGap;
        }

        private static float ExtraW()
        {
            if (_baseW <= 0f)
                return 0f;
            float need = ColX() + C.MineColW + WindowPadR;
            return Mathf.Max(0f, need - _baseW);
        }

        /// <summary>
        /// 窗口宽度：本站 = 原宽 + 采矿列宽；原版站 = 原宽。
        /// 只在「模式切换」时动一次 `anchoredPosition`（按 pivot 折算，保持左边界不动），
        /// 玩家自己拖过的位置在同一个站上不会被反复覆盖。
        /// </summary>
        private static void FitWidth(UIStationWindow sw, bool ours)
        {
            RectTransform win = sw.windowTrans;
            if (win == null)
                return;

            if (_baseW <= 0f)
            {
                _baseW = win.sizeDelta.x;
                if (_baseW < 200f)          // 窗口宽度不是 sizeDelta 语义时不要乱加
                {
                    Plugin.Log.LogWarning("站体面板: 窗口宽度异常（" + _baseW.ToString("0") + "），跳过加宽");
                    _baseW = -1f;
                    return;
                }
            }
            if (_baseW < 0f || ours == _wide)
                return;

            float extra = ExtraW();
            if (extra <= 0f)
                return;

            float dx = ours ? extra : -extra;
            win.sizeDelta = new Vector2(win.sizeDelta.x + dx, win.sizeDelta.y);
            win.anchoredPosition = new Vector2(win.anchoredPosition.x + dx * win.pivot.x,
                                               win.anchoredPosition.y);
            _wide = ours;

            Plugin.Log.LogInfo("站体面板: " + (ours ? "本站" : "原版站") +
                               " → 窗口宽 " + win.sizeDelta.x.ToString("0") +
                               "（原宽 " + _baseW.ToString("0") + "，采矿列 " + C.MineColW.ToString("0") +
                               " @x=" + ColX().ToString("0") + "）");
        }

        // ==================== 构建 ====================

        private static bool Ensure(UIStationWindow sw)
        {
            if (_inst != null && _inst._sw == sw)
                return true;

            if (_inst != null)
            {
                Destroy(_inst.gameObject);
                _inst = null;
            }

            if (sw.storageUIPrefab == null)
            {
                Warn("拿不到 storageUIPrefab，无法定位卡槽容器");
                return false;
            }
            Transform parent = sw.storageUIPrefab.transform.parent;
            if (parent == null)
            {
                Warn("卡槽容器为空，无法挂载采矿列");
                return false;
            }
            if (!EnsureFont(sw))
            {
                Warn("取不到原版字体，无法绘制采矿列");
                return false;
            }

            MeasureSlot(sw);

            var go = new GameObject("hub-mine-column");
            go.layer = parent.gameObject.layer;
            var root = go.AddComponent<RectTransform>();
            root.SetParent(parent, false);
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 1f);
            root.anchoredPosition = new Vector2(ColX(), SlotY);
            root.sizeDelta = new Vector2(C.MineColW, C.ManualSlots * C.SlotPitch);
            root.localScale = Vector3.one;

            _inst = go.AddComponent<StationPanel>();
            _inst._sw = sw;
            _inst._root = root;
            _inst.Build();

            // z 序很关键，这里既不能放最前也不能放最后：
            //   · 窗口的 panel-bg / shadow 是**拉伸铺满**的兄弟节点，画在后面会把本列盖住；
            //   · 卡槽自带的「本地/星际供应」下拉弹层是卡槽的子节点，若本列排在卡槽之后，
            //     弹层就会被本列压住（它锚在卡槽右外侧，正好落在本列范围内）。
            // 所以插到「第一个卡槽」的位置：背景在它之前（在下面），卡槽与弹层在它之后（在上面）。
            UIStationStorage[] arr = Slots(sw);
            int idx = arr != null && arr.Length > 0 && arr[0] != null
                ? arr[0].transform.GetSiblingIndex()
                : root.GetSiblingIndex();
            root.SetSiblingIndex(idx);

            Plugin.Log.LogInfo("站体面板: 采矿列已挂到窗口内（" + C.MineColW.ToString("0") + "×" +
                               (C.ManualSlots * C.SlotPitch).ToString("0") + " @x=" +
                               ColX().ToString("0") + "，卡槽 " + C.ManualSlots + " 格 " +
                               (C.SlotPitch).ToString("0") + "px/" + C.SlotScale.ToString("0.00") + " 缩放）");
            return true;
        }

        private static float _nextWarn;

        /// <summary>
        /// 挂载失败的提示。原来只打第一次，结果「第一次失败、后来每次都失败」时日志里只有一行，
        /// 很难判断是不是持续失败。现在改成 5 s 一次：既不刷屏，也看得出还在失败。
        /// </summary>
        private static void Warn(string msg)
        {
            float now = Time.unscaledTime;
            if (now < _nextWarn)
                return;
            _nextWarn = now + 5f;
            Plugin.Log.LogWarning("站体面板: " + msg);
        }

        private static bool EnsureFont(UIStationWindow sw)
        {
            if (_font != null)
                return true;
            if (sw.titleText != null)
                _font = sw.titleText.font;
            if (_font == null && sw.namePlaceHolder != null)
                _font = sw.namePlaceHolder.font;
            if (_font == null && sw.storageUIPrefab != null && sw.storageUIPrefab.countValueText != null)
                _font = sw.storageUIPrefab.countValueText.font;
            return _font != null;
        }

        private void Build()
        {
            var bg = _root.gameObject.AddComponent<Image>();
            bg.sprite = White();
            bg.type = Image.Type.Sliced;
            bg.color = BgMine;

            var outline = _root.gameObject.AddComponent<Outline>();
            outline.effectColor = Edge;
            outline.effectDistance = new Vector2(1f, -1f);
            outline.useGraphicAlpha = false;

            float w = C.MineColW;
            float iw = w - Pad * 2f;
            float colH = C.ManualSlots * C.SlotPitch;      // 540，和左侧卡槽同高

            // 左侧分隔线：2 px 淡青竖线（比整块色块更接近原版窗口的视觉语言）。
            RectTransform edge = Node(_root, "edge", 0f, 4f, 2f, colH - 8f);
            var ei = edge.gameObject.AddComponent<Image>();
            ei.sprite = White();
            ei.color = Edge;
            ei.raycastTarget = false;

            // ---- 标题：24 px 粗体 + 44 px 短下划线 ----
            Text head = Label(_root, "head", Pad, 2f, iw, 32f, 24, TitleCol, TextAnchor.MiddleLeft);
            head.text = "当前采矿";
            head.fontStyle = FontStyle.Bold;
            RectTransform hl = Node(_root, "head-line", Pad, 37f, 58f, 2f);
            var hli = hl.gameObject.AddComponent<Image>();
            hli.sprite = White();
            hli.color = Accent;
            hli.raycastTarget = false;

            // 【0.9.30】标题行右侧：核电读数（第 10 格燃料棒 → FusionPowerMw MW）。
            // 【0.9.31】字号 13 → 14，右边缘和下面所有读数对齐（都是 iw 右边）。
            _nuke = Label(_root, "nuke", Pad + 112f, 2f, iw - 112f, 32f, 14, TextFaint,
                          TextAnchor.MiddleRight);

            // ---- 键值表 1：供电 / 功率 / 钻孔 / 电池 ----
            // 标签列固定 46 px（两个汉字 16 px = 32 px，留 14 px 呼吸），
            // 读数一律从 ValX 起排 —— 四行的读数左边缘在一条竖线上，这就是「表格」。
            KeyLabel("供电", 44f, 26f);
            _state = Label(_root, "state", ValX, 44f, iw - KeyW, 26f, 20, StOff, TextAnchor.MiddleLeft);
            _state.fontStyle = FontStyle.Bold;

            KeyLabel("功率", 70f, 24f);
            _power = Label(_root, "power", ValX, 70f, iw - KeyW, 24f, 16, TextValue,
                           TextAnchor.MiddleLeft);

            KeyLabel("钻孔", 94f, 24f);
            _drill = Label(_root, "drill", ValX, 94f, iw - KeyW, 24f, 17, TextDim,
                           TextAnchor.MiddleLeft);

            KeyLabel("电池", 118f, 24f);
            _batt = Label(_root, "batt", ValX, 118f, iw - KeyW, 24f, 17, TextDim,
                          TextAnchor.MiddleLeft);

            // 站内电池细条（和原版能量条同一个池子：StationComponent.energy）。
            RectTransform bb = Node(_root, "batt-bg", Pad, 142f, iw, 4f);
            var bbi = bb.gameObject.AddComponent<Image>();
            bbi.sprite = White();
            bbi.color = BattBg;
            bbi.raycastTarget = false;
            RectTransform bf = Node(bb, "batt-fill", 0f, 0f, iw, 4f);
            var bfi = bf.gameObject.AddComponent<Image>();
            bfi.sprite = White();
            bfi.color = TextValue;
            bfi.raycastTarget = false;
            _battFill = bf;
            _battImg = bfi;

            Rule(149f, iw);

            // ---- 键值表 2：科技 / 武器 ----
            // 每行拆成「左值 + 右值」两格各自对齐：0.9.14 之前把一整串拼在一行，
            // 340 px 装不下只能压到 13 px（玩家反馈的字小），拆开后两格互不挤压。
            KeyLabel("科技", 152f, 22f);
            // 【0.9.37】右格多了「地热×1.9」（地热转换功率也跟矿物利用等级走）。
            // 分割 0.30/0.70（列加宽到 440 之后）：左格 124.8 px 刚好装下
            // 「矿物利用 已解锁」（实测 124），右格 245.2 px 装下
            // 「速度×1.60 耗矿×0.69 地热×1.9」（实测 226），留 19 px 余量。
            _techLv = Label(_root, "tech-lv", ValX, 152f, iw * 0.30f, 22f, 17, TechLv,
                            TextAnchor.MiddleLeft);
            _techMul = Label(_root, "tech-mul", ValX + iw * 0.30f, 152f, iw - KeyW - iw * 0.30f,
                             22f, 16, TextDim, TextAnchor.MiddleRight);

            KeyLabel("武器", 175f, 22f);
            // 0.9.37 列加宽后这一行终于不挤了：左格 124.8 px 装「能量+30%」（与原来
            // 「电磁+30%」同为 2 汉字 + 百分比，121 px），
            // 右格 245.2 px 装「激光208 · 电浆1170 · 弹10MJ」（211）。
            _weapon = Label(_root, "weapon", ValX, 175f, iw * 0.30f, 22f, 17, TechLv,
                            TextAnchor.MiddleLeft);
            _weaponR = Label(_root, "weapon-r", ValX + iw * 0.30f, 175f,
                             iw - KeyW - iw * 0.30f, 22f, 16, TextDim, TextAnchor.MiddleRight);

            Rule(199f, iw);

            // ---- 概况行：左边「几种 / 多少储量」，右边「自动格占用 / 每格容量」 ----
            _sumL = Label(_root, "sum-l", Pad, 201f, iw * 0.56f, 20f, 15, TextFaint,
                          TextAnchor.MiddleLeft);
            _sumR = Label(_root, "sum-r", Pad + iw * 0.56f, 201f, iw * 0.44f, 20f, 15, TextFaint,
                          TextAnchor.MiddleRight);

            // ---- 资源表头：深色条，四列标题各自和数字列同一条右边缘 ----
            RectTransform th = Node(_root, "thead", Pad, 222f, iw, 24f);
            var thi = th.gameObject.AddComponent<Image>();
            thi.sprite = White();
            thi.color = HeaderBg;
            thi.raycastTarget = false;
            Label(th, "h-name", CNameX, 0f, CNameW, 24f, 16, TextFaint, TextAnchor.MiddleLeft).text = "资源";
            Label(th, "h-amt", CAmtX, 0f, CAmtW, 24f, 16, TextFaint, TextAnchor.MiddleRight).text = "储量";
            Label(th, "h-area", CAreaX, 0f, CAreaW, 24f, 16, TextFaint, TextAnchor.MiddleRight).text = "处";
            Label(th, "h-stock", CStockX, 0f, CStockW, 24f, 16, TextFaint, TextAnchor.MiddleRight).text = "库存";

            _rows = new OreRow[MaxRows];
            for (int i = 0; i < MaxRows; i++)
                _rows[i] = BuildRow(i, Pad, ListTop + i * RowPitch, iw);

            _hint = Label(_root, "hint", Pad, ListTop, iw, 110f, 16, TextFaint, TextAnchor.UpperLeft);
            _hint.horizontalOverflow = HorizontalWrapMode.Wrap;
            _hint.lineSpacing = 1.35f;
            _hint.text = "";
        }

        /// <summary>键值表的标签列（供电 / 功率 / 钻孔 / 电池 / 科技 / 武器）。</summary>
        private void KeyLabel(string s, float y, float h)
        {
            Label(_root, "k-" + s, Pad, y, KeyW - 4f, h, 16, TextKey, TextAnchor.MiddleLeft).text = s;
        }

        /// <summary>一条 1 px 横向分隔线。</summary>
        private void Rule(float y, float iw)
        {
            RectTransform r = Node(_root, "rule" + y.ToString("0"), Pad, y, iw, 1f);
            var ri = r.gameObject.AddComponent<Image>();
            ri.sprite = White();
            ri.color = Line;
            ri.raycastTarget = false;
        }

        private OreRow BuildRow(int i, float x, float y, float w)
        {
            RectTransform rt = Node(_root, "ore" + i, x, y, w, RowPitch);
            var row = new OreRow { Root = rt };

            // 斑马纹：偶数行铺一层淡膜。表格「不串行」靠的就是这条 ——
            // 比画 10 条横线便宜，也不会和窗口本身的渐变底打架。
            if ((i & 1) == 0)
            {
                var rb = rt.gameObject.AddComponent<Image>();
                rb.sprite = White();
                rb.color = RowEven;
                rb.raycastTarget = false;
            }

            // 【0.9.26】资源图标用原版 iconSet 图集 + RawImage。
            //
            // 为什么不用 `Image.sprite = item.iconSprite`：
            //   · `iconSprite.texture` 是单张 80×80、**没有 mipmap** 的图标，
            //     塞进 20 多像素的行里相当于缩到 1/3.6，UI 采样直接走样，
            //     远看就是一块颜色斑 —— 用户 0.9.25 截图反馈的正是这个；
            //   · 原版自己在小尺寸处（UIStorageGrid / UIItemPicker）从不直接画 iconSprite，
            //     而是 `iconImage.texture = GameMain.iconSet.texture` + 索引取格子。
            //     `IconSet.texture` 是 2000×2000、**带 mipchain** 的 25×25 图集，
            //     缩小时走 mip 过滤，图标轮廓干净得多。
            RectTransform ib = Node(rt, "ic-bg", 0f, (RowPitch - 26f) * 0.5f, 26f, 26f);
            var ibi = ib.gameObject.AddComponent<Image>();
            ibi.sprite = White();
            ibi.color = IconBg;
            ibi.raycastTarget = false;

            RectTransform ic = Node(ib, "ic", 1f, 1f, 24f, 24f);
            row.Icon = ic.gameObject.AddComponent<RawImage>();
            row.Icon.raycastTarget = false;
            row.Icon.enabled = false;

            // 四列：名称 18 px（最亮）、储量 17、处 16、库存 17。
            // 三列数字全部 MiddleRight + 同一条右边缘，数量级变化时小数点不会游移。
            row.Name = Label(rt, "nm", CNameX, 0f, CNameW, RowPitch, 18, TextMain,
                             TextAnchor.MiddleLeft);
            row.Amount = Label(rt, "amt", CAmtX, 0f, CAmtW, RowPitch, 17, TextValue,
                               TextAnchor.MiddleRight);
            row.Areas = Label(rt, "area", CAreaX, 0f, CAreaW, RowPitch, 16, TextFaint,
                              TextAnchor.MiddleRight);
            row.Stock = Label(rt, "st", CStockX, 0f, CStockW, RowPitch, 17, TextValue,
                              TextAnchor.MiddleRight);

            rt.gameObject.SetActive(false);
            return row;
        }

        // ==================== 数据绑定与刷新 ====================

        private void Bind(UIStationWindow sw, StationComponent sc)
        {
            _sw = sw;
            _stationId = sc.id;
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
        }

        /// <summary>
        /// 资源图标：从原版 <c>GameMain.iconSet</c>（2000×2000、25×25 格、带 mipchain）
        /// 按 <c>itemIconIndex[itemId]</c> 取一格画到 RawImage 上。
        ///
        /// 这是原版 UI 在小尺寸处的通用做法（`UIStorageGrid` / `UIItemPicker` 都是
        /// `iconImage.texture = GameMain.iconSet.texture` + 索引）。
        /// 直接用 `Image.sprite = item.iconSprite` 虽然 tooltip 里没问题，但那是一张
        /// **没有 mipmap 的 80×80** 图，缩到 20 多像素会严重走样 —— 实机就是一块色斑。
        /// </summary>
        private void SetOreIcon(OreRow row, ItemProto proto, int itemId)
        {
            IconSet set = GameMain.iconSet;
            if (set == null || set.texture == null || set.itemIconIndex == null ||
                itemId <= 0 || itemId >= set.itemIconIndex.Length)
            {
                row.Icon.enabled = false;
                row.IconItem = 0;
                return;
            }

            int idx = (int)set.itemIconIndex[itemId];
            if (idx <= 0)
            {
                // 这个物品没有图标（原版的自定义 proto 常见）：宁可不画，也不要留白框。
                row.Icon.enabled = false;
                row.IconItem = 0;
                return;
            }

            if (row.IconItem == itemId && row.Icon.enabled)
                return;
            row.IconItem = itemId;

            // 图集 25×25 格；常量直接写死，避免跨版本改 const 名后编译不过。
            const int axis = 25;
            const float cell = 1f / axis;
            int cx = idx % axis;
            int cy = idx / axis;

            row.Icon.texture = set.texture;
            row.Icon.uvRect = new Rect(cx * cell, cy * cell, cell, cell);
            row.Icon.enabled = true;

            if (!_iconLogged)
            {
                _iconLogged = true;
                Plugin.Log.LogInfo("资源图标: 改用原版 iconSet 图集（itemIconIndex + uvRect，" +
                                   "2000² 带 mip，缩小不再糊成色块） 示例 " +
                                   (proto != null ? proto.name : ("#" + itemId)) +
                                   " idx=" + idx + " 格=(" + cx + "," + cy + ")");
            }
        }

        private static void Show(bool on)
        {
            if (_inst == null)
                return;
            if (_inst.gameObject.activeSelf != on)
                _inst.gameObject.SetActive(on);
        }

        private void Step(UIStationWindow sw)
        {
            if (sw == null || sw.transport == null || sw.transport.stationPool == null)
            {
                Show(false);
                return;
            }
            int id = sw.stationId;
            if (id <= 0 || id >= sw.transport.stationPool.Length)
            {
                Show(false);
                return;
            }
            StationComponent sc = sw.transport.stationPool[id];
            if (sc == null || sc.id != id || !IsOurs(sw.factory, sc))
            {
                Show(false);
                return;
            }
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            // 原版电力行改造必须**每帧**做：`UIStationWindow._OnUpdate` 每帧都会把
            // powerText / stateText 重写成「断电 / 无法充电」，节流写就会看到 4 Hz 闪烁。
            PowerRow(sc);

            if (_stationId != id)
            {
                _stationId = id;
                Refresh(true);
                return;
            }
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.25f;
            Refresh(false);
        }

        /// <summary>
        /// 原版电力行（闪电图标 + 能量条 + 右侧两行字）就地改写成本站的供电读数。
        ///
        /// 为什么改这一行而不是另加一条：本站是**地热自持**的，没接电缆时原版这段代码
        /// 只会走进 else 分支写死「断电 / 无法充电」，玩家明明在采矿、能量条却标着
        /// 「无法充电」（0.9.7 用户反馈）。这一行的位置、宽度原版已经排好，就地改字
        /// 既不用赌版面，也不会压到别的东西。
        ///
        /// 0.9.25 起这两格的口径（玩家反馈「接上电网看不到当前用了多少电网功率」）：
        ///   接电网  → 第一格「电网 42MW」（= requiredEnergy×60×consumerRatio，原版口径），
        ///             第二格「地热 6.0MW」
        ///   地热自持 → 第一格「地热 6.00MW」，第二格「地热×3.00」
        ///   断电    → 「断电」/「地热×0.62 不足」
        /// </summary>
        private void PowerRow(StationComponent sc)
        {
            if (_sw == null || sc == null || !C.EnableDrill)
                return;
            Text pt = _sw.powerText;
            Text st = _sw.stateText;
            if (pt == null || st == null)
                return;
            if (_font == null)
                EnsureFont(_sw);           // 覆盖 Text 要用和右列同一套字体

            float geo = StationDrill.StrengthOf(sc.entityId);
            // 【0.9.30】以前这里在「地热强度为 0」时直接返回，保持原版「断电 / 无法充电」；
            // 现在第 10 格有氘棒时核电就是唯一电源，必须继续往下写，否则面板会谎报断电。
            if (geo <= 0.001f && !StationNuclear.HasFuel(sc))
                return;                       // 还没采样：保持原版

            if (!_powerStyled)
            {
                _powerStyled = true;

                RectTransform pr = pt.rectTransform;
                RectTransform sr = st.rectTransform;
                string grp = _sw.powerGroupRect != null
                    ? _sw.powerGroupRect.sizeDelta.x.ToString("0") + "×" + _sw.powerGroupRect.sizeDelta.y.ToString("0")
                    : "?";
                Plugin.Log.LogInfo("站体面板: 电力行 — 组 " + grp +
                                   " powerText " + pr.sizeDelta.x.ToString("0") + "×" + pr.sizeDelta.y.ToString("0") +
                                   " stateText " + sr.sizeDelta.x.ToString("0") + "×" + sr.sizeDelta.y.ToString("0"));
                Plugin.Log.LogInfo("站体面板: 电力行宿主 " + pt.GetType().FullName + " 字体 " +
                                   FontName(pt) + " / " + MatName(pt) + " ；" +
                                   st.GetType().FullName + " 字体 " + FontName(st) + " / " + MatName(st));

                // ---- 0.9.12：放弃改造原版 Text，改成「盖一层自己的」----
                // 0.9.10 关 Outline、0.9.11 连字体和材质一起换，实机都照旧发光：
                // 那两格的字形是**同色多遍绘制**出来的（伪粗体/发光描边写在渲染路径里，
                // 不在组件上），只要用亮色写进去就是一团光斑，改属性根本管不到。
                // 所以换个思路：原版两格清空并停用组件（它们只用来占位布局），
                // 在上面盖两个我们自己的 Text —— 普通 UI Text、Normal、无任何
                // BaseMeshEffect，画出来是什么样就是什么样。
                _geoMw = Overlay(pt, "hub-geo-mw");
                _geoMul = Overlay(st, "hub-geo-mul");
                if (_geoMw != null && _geoMul != null)
                {
                    pt.text = "";
                    st.text = "";
                    pt.enabled = false;
                    st.enabled = false;
                    _hostHidden = true;
                    Plugin.Log.LogInfo("站体面板: 电力行改用自建 Text 覆盖（原版两格已停用）");
                }
                else
                {
                    Plugin.Log.LogWarning("站体面板: 电力行自建 Text 失败，退回改写原版两格");
                }
            }

            bool on = StationDrill.Powered(sc.entityId);
            bool grid = StationDrill.GridOn(sc.entityId);
            float mw = StationDrill.GeoPowerMw(sc.entityId);
            float drawMw = StationDrill.GridDrawMw(_sw.factory, sc);

            // 【0.9.25】这两格现在回答两个不同的问题：
            //   第一格 = 「现在是谁在供、供了多少」 —— 接电网时是**电网实际取用**的功率
            //            （原版口径 requiredEnergy×60×consumerRatio），这才是玩家说的
            //            「当前使用电网功率」；没接电网时退回地热发电功率。
            //   第二格 = 另一路电源的读数（接电网时显示地热，地热自持时显示强度倍率）。
            // 第一格刻意取整数 MW：120 MW 上限下小数没有信息量，短一点也不会顶到
            // 左边那条「9.95GJ」能量读数上。
            string sMw, sMul;
            Color cMw, cMul;
            if (grid)
            {
                sMw = drawMw >= 0.05f ? "电网 " + drawMw.ToString("0") + "MW" : "电网 已充满";
                sMul = "地热 " + mw.ToString("0.0") + "MW";
                cMw = PowerGrid;
                cMul = PowerGeo;
            }
            else if (geo >= C.GeoSelfPowerMin)
            {
                sMw = "地热 " + mw.ToString("0.00") + "MW";
                sMul = "地热×" + geo.ToString("0.00");
                cMw = PowerValue;
                cMul = PowerGeo;
            }
            else if (on)
            {
                // 【0.9.30】没电网、地热也不够，但第 10 格有氘棒 → 核电在顶。
                sMw = "核电 " + C.FusionPowerMw.ToString("0.#") + "MW";
                sMul = StationNuclear.FuelName(sc) + " " + StationNuclear.FuelCount(sc);
                cMw = StNuke;
                cMul = TextDim;
            }
            else
            {
                sMw = "断电";
                sMul = geo > 0.001f ? "地热×" + geo.ToString("0.00") + " 不足" : "地热 —";
                cMw = StOff;
                cMul = StOff;
            }

            if (_hostHidden)
            {
                SetTxt(_geoMw, sMw);
                SetTxt(_geoMul, sMul);
                if (_geoMw != null) _geoMw.color = cMw;
                if (_geoMul != null) _geoMul.color = cMul;
                return;
            }

            pt.text = sMw;
            pt.color = cMw;
            st.text = sMul;
            st.color = cMul;
        }

        private void Refresh(bool force)
        {
            try
            {
                if (_sw == null || _rows == null)
                    return;
                int id = _sw.stationId;
                if (id <= 0 || _sw.transport == null || id >= _sw.transport.stationPool.Length)
                    return;
                StationComponent sc = _sw.transport.stationPool[id];
                if (sc == null || sc.id != id)
                    return;

                int key = sc.entityId;
                // 【0.9.28】面板自己拉一次科技等级。
                // 之前只在 StationMiner.Tick（要站通电且在钻孔）里刷新，于是站一断电、
                // 或玩家刚解锁科技还没钻孔时，面板就一直显示缓存的「未研究」——
                // 用户反馈的「科技已解锁，面板还写未研究」正是这个场景。
                // Refresh 内部 2 s 节流，成本可以忽略。
                MiningTech.Refresh(Time.unscaledTime);
                bool on = StationDrill.Powered(key);
                bool grid = StationDrill.GridOn(key);
                float geo = StationDrill.StrengthOf(key);
                float boost = StationDrill.MineBoost(key);
                float mw = StationDrill.GeoPowerMw(key);
                // 【0.9.25】实际从电网取用的功率。玩家反馈的「接上电网却看不到当前用多少电」
                // 就是这一条 —— 状态行直接把它写进去，电力行也同步显示（见 PowerRow）。
                float drawMw = StationDrill.GridDrawMw(_sw.factory, sc);
                // 【0.9.30】第 10 格核电：有燃料棒就能顶起整座站（FusionPowerMw 直充站内电池）。
                int fuel = StationNuclear.FuelCount(sc);
                bool nukeFuel = StationNuclear.HasFuel(sc);
                float nukeMw = StationNuclear.OutputMw(sc);

                // ---- 1. 供电：现在是谁在供 ----
                if (!on)
                {
                    _state.text = nukeFuel ? "○ 未通电 · 地热不足" : "○ 未通电 · 缺电";
                    _state.color = StOff;
                }
                else if (grid)
                {
                    _state.text = "● 电网供电";
                    _state.color = StGrid;
                }
                else if (geo >= C.GeoSelfPowerMin)
                {
                    _state.text = "● 地热自供";
                    _state.color = StGeo;
                }
                else
                {
                    // 没电网、地热也不够，但第 10 格有氘棒 → 核电在顶（0.9.30）。
                    _state.text = "● 核电供电";
                    _state.color = StNuke;
                }

                // 标题行右侧：核电读数。三种状态分别写清楚，玩家不用猜「第 10 格有没有在工作」。
                // 【0.9.32】第 10 格放哪种棒就写哪种（氘棒 / 反物质棒 / 奇异棒）。
                string rodName = StationNuclear.FuelName(sc);
                if (nukeMw > 0.01f)
                    SetTxt(_nuke, "核电 " + nukeMw.ToString("0.#") + "MW · " + rodName + " " + fuel);
                else if (nukeFuel)
                    SetTxt(_nuke, "核电待机·电池满 · " + rodName + " " + fuel);
                else
                    SetTxt(_nuke, "核电无燃料 · 第" + (C.FuelSlot + 1) + "格");
                _nuke.color = nukeMw > 0.01f ? StNuke : (nukeFuel ? TextDim : TextFaint);

                // ---- 2. 功率：构成 + 塔顶合计 ----
                // 一行回答两个问题：这股电从哪来（电网 / 地热 / 核电各多少），
                // 以及塔顶电浆炮真正能拿到多少（0.9.29 的反馈点，不能因为表格化丢掉）。
                if (!on)
                {
                    _power.text = "—";
                    _power.color = TextFaint;
                }
                else
                {
                    // 【0.9.37】列宽到 440 后可写宽度 370 px，三项并存（电网120.0 +
                    // 地热11.40 + 核电100 = 塔顶231MW）实测 321 px，终于有余量了。
                    // 电网仍用 0.# —— 120.0 这种整数不写小数更短，「45.2」也不会掉精度。
                    string comp = "";
                    if (grid && drawMw >= 0.05f)
                        comp += "电网" + drawMw.ToString("0.#") + "+";
                    if (mw > 0.005f)
                        comp += "地热" + mw.ToString("0.##") + "+";
                    if (nukeMw > 0.01f)
                        comp += "核电" + nukeMw.ToString("0.#") + "+";
                    if (comp.Length > 0)
                        comp = comp.Substring(0, comp.Length - 1);
                    if (comp.Length == 0)
                    {
                        // 电网/地热都在，但原版把 requiredEnergy 切成了 idle（电池满 + 全部待机），
                        // 这时没有任何「正在输入」的功率可写，直接说清楚状态。
                        _power.text = "电池已满 · 全部待机";
                        _power.color = TextDim;
                    }
                    else
                    {
                        float towerMw = mw + (grid ? drawMw : 0f) + nukeMw;
                        _power.text = comp + "=塔顶" + towerMw.ToString("0") + "MW";
                        _power.color = TextValue;
                    }
                }

                // 「矿物利用」每级 +10% 采矿速度 / -6% 消耗，见 MiningTech。
                // 0.9.14：这组数字从「钻孔」行的尾巴上挪下来单独成行（那行拼太长会顶出列宽）。
                float techSpd = MiningTech.SpeedMul;
                float techCost = MiningTech.CostMul;
                int techLv = MiningTech.Level;

                // ---- 3. 钻孔：节拍 + 地热（×）加成 ----
                // 【0.9.27】钻孔产出直接问 StationMiner.CycleCap —— 它内部按原版大型采矿机
                // （每矿脉 600000/minerPeriod 个/s × 12 条）算 base，再乘地热与矿物利用。
                // 面板不再自己乘 C.MinePerCycle，否则基准一改就显示与实际产出不一致。
                _drill.text = on
                    ? StationMiner.CycleCap(sc.entityId) + " 个 / " + C.MinePeriod.ToString("0.#") +
                      "s · ×" + boost.ToString("0.00")
                    : "暂停 · 需供电";
                _drill.color = on ? TextDim : TextFaint;

                // 【0.9.28】区分「没解锁」和「已解锁但还没升级」：
                // 0.9.27 只要算出来 Lv0 就写「未研究」，于是科技页明明已经解锁的玩家
                // 也会看到「未研究」（用户反馈）。现在按 MiningTech.Unlocked 分开写。
                if (techLv > 0)
                    SetTxt(_techLv, "矿物利用 Lv" + techLv);
                else if (MiningTech.Unlocked)
                    SetTxt(_techLv, "矿物利用 已解锁");
                else
                    SetTxt(_techLv, "矿物利用 未解锁");
                _techLv.color = techLv > 0 ? TechLv : (MiningTech.Unlocked ? TextDim : TextFaint);
                // 两个倍率来自存档累计值（见 MiningTech）：速度 ×1.60 / 耗矿 ×0.690
                // 耗矿是连乘 0.94^Lv，所以要 3 位小数才看得出差别（Lv6 = 69.0%，不是 64%）。
                // 0.9.37：右格从「速度 / 耗矿」两条变三条（加了地热转换倍率）。
                // 耗矿改成 2 位小数（0.94^Lv 的第三位对玩家没有决策意义），
                // 地热只留 1 位 —— 这样三条一起放进 231 px 的右格还不顶边（实测 226）。
                SetTxt(_techMul, (techLv > 0 || MiningTech.Unlocked)
                    ? "速度×" + techSpd.ToString("0.00") +
                      " 耗矿×" + techCost.ToString("0.00") +
                      " 地热×" + MiningTech.GeoMul.ToString("0.0")
                    : "");
                _techMul.color = techLv > 0 ? TextDim : TextFaint;

                // ---- 6. 武器：左格加成、右格伤害与弹耗 ----
                // 0.9.30 用户把电浆弹能耗提到 10 MJ/发，这个数字现在是最需要被看见的
                // （够不够电、要补几根棒都靠它）。0.9.31 拆成两格各自对齐 —— 之前一整串
                // 「电磁+10%　激光176　电浆990　弹10MJ」挤在 340 px 里，只能压到 13 px。
                //
                // 0.9.38：左格从「电磁+x%」改成「能量+x%」—— 伤害改绑原版科技
                // 「升级 → 能量武器伤害」（每级 +10%，见 MiningTech.EnergyWeaponMul），
                // 不再是「矿物利用每级 +5%」那套自造效果。倍率为 1.00（没升级）时整行变暗，
                // 和中文化「未解锁」的读感保持一致。
                int wpct = MiningTech.WeaponPct;
                // 0.9.45：面板必须和实际开火算的是同一个数 —— 实际是
                // `基础 × 能量武器倍率 × Plugin.DamageMul`（见 StationTurrets 的 dmg 计算），
                // 少乘一层就会出现「面板写 640、实打 1280」的对不上。
                int dmgLaser = Mathf.RoundToInt(C.TurretDamage * MiningTech.EnergyWeaponMul * Plugin.DamageMul);
                int dmgPlasma = Mathf.RoundToInt(C.TurretDamageSpace * MiningTech.EnergyWeaponMul * Plugin.DamageMul);
                SetTxt(_weapon, "能量+" + wpct + "%");
                // 0.9.46：基座激光也开始吃站内电量（15 MJ/发），所以「每发耗电」不再只有
                // 电浆弹一个数 —— 改成「伤害/耗电」成对写，两条线各自的账一眼能对上：
                //   激光640/15MJ · 电浆3600/20MJ
                // 宽度：右格 245 px，实测这一串约 210 px（13 px 字号），不顶边。
                SetTxt(_weaponR, "激光" + dmgLaser + "/" +
                                 (C.LaserShotEnergy / 1_000_000.0).ToString("0.#") + "MJ · 电浆" +
                                 dmgPlasma + "/" +
                                 (C.PlasmaShotEnergy / 1_000_000.0).ToString("0.#") + "MJ");
                _weapon.color = wpct > 0 ? TechLv : TextFaint;
                _weaponR.color = wpct > 0 ? TextDim : TextFaint;

                // 每格容量 = 2 万 + 3 万 × 矿物利用等级（封顶 50 万），
                // 和 StationDrill.SyncAutoCap 写进卡槽的 max 是同一个数（见 MiningTech.AutoSlotCap）。
                // 0.9.31：从表头挪到概况行右半（表头只留列名，四个字 15 px 才排得开）。
                int capExtra = GameMain.history != null ? GameMain.history.remoteStationExtraStorage : 0;

                // ---- 4. 电池（和原版能量条同一个池子：StationComponent.energy）----
                float batt = sc.energyMax > 0 ? Mathf.Clamp01((float)sc.energy / (float)sc.energyMax) : 0f;
                if (_battFill != null)
                {
                    Vector2 sd = _battFill.sizeDelta;
                    sd.x = (C.MineColW - Pad * 2f) * batt;
                    _battFill.sizeDelta = sd;
                }
                if (_battImg != null)
                    _battImg.color = batt <= 0.15f ? StOff : (on && !grid ? StGeo : TextValue);
                // 「+x MW」= 正在往池子里充的功率：电网供电时是电网实际给的（drawMw），
                // 地热自持时是地热发电，只有氘棒时是核电。0.9.31 前面补百分比，
                // 玩家不用自己拿 202.8 除 30.0 才知道还剩多少。
                float chargeMw = grid ? drawMw : mw + nukeMw;
                SetTxt(_batt, (batt * 100f).ToString("0") + "%　" + Energy(sc.energy) + " / " +
                             Energy(sc.energyMax) + (on ? "　+" + chargeMw.ToString("0.0") + "MW" : "　—"));

                StationDrill.VeinStats stats = StationDrill.PlanetVeinStats(_sw.factory);
                int kinds = stats != null ? stats.Items.Count : 0;
                int autoUsed = 0;
                if (sc.storage != null)
                    for (int i = C.AutoSlotStart; i < sc.storage.Length; i++)
                        if (sc.storage[i].itemId > 0)
                            autoUsed++;

                // ---- 7. 概况行：左「几种 / 总储量」、右「自动格占用 / 每格容量」----
                if (stats == null)
                    _sumL.text = "本星球数据不可用";
                else if (!stats.HasOre)
                    _sumL.text = "本星球无可采资源";
                else
                    _sumL.text = kinds + " 种 · 储量 " + StationDrill.Short(stats.Total) +
                                 (stats.OceanItem > 0 ? " ＋海洋" : "");
                _sumR.text = "自动" + autoUsed + "/" + (C.MaxItemKinds - C.AutoSlotStart) +
                             " · 每格" + StationDrill.Short(MiningTech.AutoSlotCap(capExtra));

                bool any = false;
                for (int i = 0; i < _rows.Length; i++)
                {
                    OreRow row = _rows[i];
                    if (stats == null || i >= stats.Items.Count)
                    {
                        if (row.Root.gameObject.activeSelf)
                            row.Root.gameObject.SetActive(false);
                        continue;
                    }
                    any = true;
                    if (!row.Root.gameObject.activeSelf)
                        row.Root.gameObject.SetActive(true);

                    int itemId = stats.Items[i];
                    ItemProto proto = LDB.items.Select(itemId);
                    if (row.Icon != null)
                        SetOreIcon(row, proto, itemId);
                    SetTxt(row.Name, proto != null ? proto.name : ("#" + itemId));

                    // 四列表格：储量 / 处 / 库存各自一格，数字全部右对齐。
                    // 之前是「名字 + 第二行说明」的两行式，10 行里 20 行文字挤在 340 px，
                    // 只能 13 px；改成单行四列之后每格都能给到 16~17 px。
                    bool inf = stats.IsInfinite(itemId);
                    if (inf)
                    {
                        SetTxt(row.Amount, "取之不尽");
                        row.Amount.color = TextDim;
                        SetTxt(row.Areas, "—");
                        row.Areas.color = TextFaint;
                    }
                    else
                    {
                        int areas = 0;
                        long amount = 0;
                        if (stats.Areas != null)
                            stats.Areas.TryGetValue(itemId, out areas);
                        if (stats.Amount != null)
                            stats.Amount.TryGetValue(itemId, out amount);
                        SetTxt(row.Amount, StationDrill.Short(amount));
                        row.Amount.color = TextValue;
                        SetTxt(row.Areas, areas > 0 ? areas.ToString() : "—");
                        row.Areas.color = areas > 0 ? TextDim : TextFaint;
                    }

                    long stock = StationDrill.StationStock(sc, itemId);
                    SetTxt(row.Stock, stock > 0 ? StationDrill.Short(stock) : "—");
                    row.Stock.color = stock > 0 ? TextValue : TextFaint;
                }

                bool showHint = !any;
                if (_hint.gameObject.activeSelf != showHint)
                    _hint.gameObject.SetActive(showHint);
                if (showHint)
                    _hint.text = on
                        ? "本星球没有可采资源，或已采空。\n卡槽可以手动配货，靠运输机转运。"
                        : "未通电。\n接入电网、或把站建在地热 ≥ " +
                          C.GeoSelfPowerMin.ToString("0.00") + " 的地方让它自己顶起来。";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("站体面板: 刷新数值失败 — " + e.Message);
            }
        }

        // ==================== uGUI 小工具 ====================

        /// <summary>
        /// 在 host 这个 Text 上盖一个我们自己的 Text（同位置、同对齐），用来接管显示。
        /// host 只保留它的 RectTransform 当作版位参考，组件本身由调用方停用。
        /// </summary>
        private Text Overlay(Text host, string name)
        {
            if (host == null || _font == null)
                return null;

            try
            {
                Transform old = host.transform.Find(name);
                GameObject go;
                if (old != null)
                {
                    go = old.gameObject;
                }
                else
                {
                    go = new GameObject(name, typeof(RectTransform));
                    go.transform.SetParent(host.transform, false);
                }

                RectTransform rt = go.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(1f, 0.5f);
                // 原版两格只有 64 / 72 px 宽，写「地热×3.00」会顶边，
                // 这里向左借 14 px（右边不动，MiddleRight 对齐所以不会压到窗口外）。
                rt.offsetMin = new Vector2(-14f, -3f);
                rt.offsetMax = new Vector2(0f, 3f);
                rt.localScale = Vector3.one;

                Text t = go.GetComponent<Text>();
                if (t == null)
                    t = go.AddComponent<Text>();
                t.font = _font;
                t.material = _font.material;      // 不显式给就会沿用挂上去时那套
                t.fontSize = 15;
                t.fontStyle = FontStyle.Normal;    // 绝不伪粗体
                t.alignment = TextAnchor.MiddleRight;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.supportRichText = false;
                t.raycastTarget = false;
                t.lineSpacing = 1f;

                // 只留一层纯黑投影：黑色不发光，只把亮色字从背后的行星环上剥出来。
                Shadow sh = go.GetComponent<Shadow>();
                if (sh == null)
                    sh = go.AddComponent<Shadow>();
                sh.effectColor = new Color(0f, 0f, 0f, 0.8f);
                sh.effectDistance = new Vector2(1f, -1f);
                sh.useGraphicAlpha = true;
                sh.enabled = true;

                t.enabled = true;
                go.SetActive(true);
                return t;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("站体面板: 覆盖 Text 创建失败 " + name + " — " + e.Message);
                return null;
            }
        }

        private static string FontName(Text t)
        {
            return t.font != null ? t.font.name : "null";
        }

        private static string MatName(Text t)
        {
            Material m = t.material;
            if (m == null)
                return "无材质";
            return m.name + "(" + (m.shader != null ? m.shader.name : "?") + ")";
        }

        private static void SetTxt(Text t, string s)
        {
            if (t != null && t.text != s)
                t.text = s;
        }

        /// <summary>能量格式化：1 GJ 以下用 MJ（与原版能量条同口径），以上用 GJ。</summary>
        private static string Energy(long j)
        {
            if (j >= 1_000_000_000L)
                return (j / 1e9).ToString("0.0") + "GJ";
            return (j / 1e6).ToString("0.#") + "MJ";
        }

        private static Sprite White()
        {
            if (_white != null)
                return _white;
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            _white = Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f, 0,
                                   SpriteMeshType.FullRect, new Vector4(1f, 1f, 1f, 1f));
            _white.name = "hub-white";
            return _white;
        }

        private static RectTransform Node(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            rt.localScale = Vector3.one;
            return rt;
        }

        private static Text Label(Transform parent, string name, float x, float y, float w, float h,
                                  int size, Color color, TextAnchor anchor)
        {
            RectTransform rt = Node(parent, name, x, y, w, h);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = false;

            // 一层 1 px 的黑色投影：这一列压在原版窗口的半透明底板上，
            // 不加投影时浅青字在亮色背景（例如行星环）上会糊掉。
            // 0.9.31：列底虽然已经提到 0.92 alpha，投影仍然保留 —— 它只作用在
            // 字形边缘，让 16~20 px 的字在「深底 + 斑马纹」上多一层分离感。
            var sh = rt.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.72f);
            sh.effectDistance = new Vector2(1f, -1f);
            sh.useGraphicAlpha = false;
            return t;
        }
    }
}
