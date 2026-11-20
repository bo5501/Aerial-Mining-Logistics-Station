using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 全局常量。几何数值与 tools/station_model.py 严格对应（改动请两边同步，或只改 Python
    /// 后重新导出 StationGeometry.Generated.cs）。
    /// </summary>
    internal static class C
    {
        public const string Guid = "com.psd.logisticshub";
        public const string Name = "空中采矿机物流运输站";
        public const string Version = "0.9.43";

        // ---- id 分配（避开「星际能量枢纽塔」666/8000/300 与本体重载区间）----
        public const int ModelId = 673;
        public const int ItemId = 8100;
        public const int RecipeId = 320;
        public const int BuildIndex = 608;

        /// <summary>
        /// 建造栏可见分类页只有 1..9（UIBuildMenu._OnUpdate 循环 1..9，10/11 是拆除/升级）。
        /// 落到 10 页以后等于玩家永远点不进去。
        /// </summary>
        public const int BuildBarMaxPage = 9;

        /// <summary>
        /// 制造窗口 / 物品选择器的格子号：GridIndex = 类型×1000 + 行×100 + 列（行、列均从 1 起）。
        /// 原版是 8 行 × 14 列（UIItemPicker.RefreshIcons、UIReplicatorWindow.RefreshRecipeIcons
        /// 都判 row&lt;8 &amp;&amp; col&lt;14），越界直接丢弃 —— 之前 2115 的列号是 15，正好被扔掉。
        /// 这里只作为兜底初值，运行时 ResolveGridCell() 会按实际占位重新挑一个空格。
        /// </summary>
        public const int GridIndex = 2801;
        public const int GridType = 2;          // 2 = 建筑页
        public const int GridRows = 8;
        public const int GridCols = 14;

        // ---- 造型尺寸（米）----
        public const float BaseRadius = 8.15f;   // 基座裙边外接圆
        /// <summary>
        /// 建造碰撞盒半宽。必须比 BaseRadius 小一档：12 个接口按 30° 均布，
        /// 传送带起点 = 接口位置 + forward×1.1（BuildTool_Path），再按 0.28 m 半径做
        /// OverlapSphere 检查。r=9.70 在 30° 方向上的切向坐标是 cos30×9.70 = 8.40，
        /// 减去球半径后为 8.12 —— 若碰撞盒半宽仍取 8.15，这 4 个斜向接口的带子会被判 Occupied。
        /// 取 7.90 留 0.22 m 余量。
        /// </summary>
        public const float BuildHalf = 7.90f;
        public const float BaseTop = 4.05f;      // 二级平台顶面
        /// <summary>地面基座高度（建造占位盒/血条/框选用它，不含 264 m 以上的站体）。</summary>
        public const float Height = 4.05f;
        /// <summary>
        /// 站体抬升量 = 站体底面高度。对齐原版黑雾中继站：它的停驻位置是
        /// `planetData.realRadius + 70`（DFRelayComponent.SearchTargetPlaceProcess），
        /// 即本体中心离地 70 m；我们站体高 32.2 m，底面取 50 m → 中心 66.1 m、顶面 82.2 m。
        /// </summary>
        public const float BodyY = 50.00f;

        /// <summary>
        /// 塔顶（站体）整体缩放。基座一个像素都不动 —— 基座半径 8.15 是建造占位、
        /// 12 个传送带接口、OverlapSphere 判定一起咬合的，放大就会把带子挤成 Occupied。
        ///
        /// 0.9.13：用户实机反馈「塔顶再放大一些，50 艘星际货船还是挤」。原来泊位环
        /// 半径 25 m、25 个泊位一圈，相邻弧长 2π×25/25 = 6.28 m，而货船模型宽约 5.6 m，
        /// 停满就是贴脸。放到 1.18 倍后环半径 29.5 m、弧长 7.42 m，同时船、环、炮塔
        /// 全部按同一比例长，视觉上不会有「船飘在环外面」的错位。
        /// </summary>
        public const float BodyScale = 1.18f;

        /// <summary>
        /// 把站体局部坐标（塔顶部分）按 <see cref="BodyScale"/> 放大，以 BodyY 为基准。
        /// mesh 顶点、炮塔安装点、泊位环都必须走这一个函数，否则模型/炮塔/船会互相错开。
        /// 基座（y &lt; 25）原样返回。
        /// </summary>
        public static Vector3 ScaleBody(Vector3 v)
        {
            if (BodyScale == 1f || v.y <= 25f)
                return v;
            return new Vector3(v.x * BodyScale,
                               BodyY + (v.y - BodyY) * BodyScale,
                               v.z * BodyScale);
        }
        /// <summary>
        /// 网格实际总高。0.9.39 起塔顶多了一根天线（碟顶核心塔 31.4~34.96，桅杆顶 84.96 = 局部 34.96），
        /// 这里必须跟着改 —— 它同时喂给 <c>prefabDesc.cullingHeight</c>（+2）与
        /// <c>signHeight</c>（+6），写小了会出现「天线在屏幕里、整栋建筑被裁掉」。
        /// </summary>
        public const float TotalHeight = BodyY + 35.00f * BodyScale;   // ≈91.30

        // ---- 塔顶站体拾取盒 ----
        // RaycastLogic 只命中 usage==Build（isTrigger=true）的 ColliderData，所以要让
        // 264 m 高的碟形站体可点击/可拆除，必须单独放一个触发盒上去。盒子只做逻辑体积，
        // 不参与地面占位判定（占位用的是 collider-build 那个）。
        public const float PickCenterY = BodyY + 17.00f * BodyScale;   // 站体腰部
        public const float PickHeight = 40.00f * BodyScale;            // 包住吊环/主环/碟顶/核心环
        public const float PickHalf = 28.60f * BodyScale;              // 略大于主环外径
        /// <summary>
        /// 鼠标拾取距离（m）。
        ///
        /// 【0.9.6 修的就是这个】原版 <c>PrefabDesc.selectDistance</c> 默认 35 m，而
        /// <c>PlayerAction_Inspect.InspectSomething</c> 在给 UIGame 回调之前先做一次距离判定：
        /// <code>
        /// objectPos = entityPool[objId].pos            // 基座中心，贴地
        /// if ((player.position - objectPos).sqrMagnitude &gt; selectDistance²) return;   // 静默返回
        /// </code>
        /// 本站塔顶站体在 50~82 m 高（对齐黑雾中继站），站在地面点碟体时距离
        /// = √(水平² + 67²) ≥ 67 m → 直接 return，<b>既不报错也不打日志</b>，
        /// UIGame 收不到回调，站体窗口自然不弹 —— 表现就是「点了没反应 / 面板没弹出来」。
        ///
        /// 给到 90 m：站在基座旁边点碟体也能开窗；再远（90 m 外）仍旧点不到，
        /// 免得隔着半个厂区误点。
        /// </summary>
        public const float SelectDistance = 90f;
        public const int PortCount = 12;         // 传送带接口数（每条 12 边形边的中心 1 个）
        public const float PortPoseRadius = 8.60f;
        public const float PortPoseY = 0.2f;     // 传送带带面高度（与 BuildTool_Path 的地面基准一致）

        // ---- 空中采矿：钻孔取矿 ----
        public const bool EnableSurfaceMining = true;
        /// <summary>
        /// 优先吸附半径（米）。半径内的矿脉全部纳入，半径外按距离取最近的，
        /// 并保证每种矿物至少有一条可采 —— 站址常常在空地上，只按半径吸会「配好货但永远不出矿」。
        /// </summary>
        public const float MineRadius = 40f;
        public const float MinePeriod = 1.0f;    // 每 1 s 结算一个周期

        // 【0.9.27】采矿基准改成原版「大型采矿机」（高级采矿机，itemId 2302）。
        //
        // 原来写死 `MinePerCycle = 16 个/周期`，是拍出来的数；玩家要求「初始参数按原版
        // 大型采矿机算」。原版口径在 ItemProto 的描述里：
        //     产出/分/矿脉 = 60 × 600000 / prefabDesc.minerPeriod × miningSpeedScale
        // 也就是每矿脉每秒 = 600000 / minerPeriod（高级矿机的 periodf 由 prefab 决定）。
        // 本站底盘有 12 个吸气钟，等价于 12 条矿脉同时开采，所以
        //     base = 12 × 600000 / minerPeriod 个/周期（周期 1 s）
        // 之后再乘地热倍率与「矿物利用」的 miningSpeedScale，与原来一致。
        /// <summary>
        /// 原版采矿机的**回退**物品 id（基础「采矿机」）。
        ///
        /// 0.9.27 这里写的是 2302，实测那是「电弧熔炉」——`prefabDesc.minerPeriod = 0`，
        /// 于是基准退化成写死的 1 个/秒（日志：`原版电弧熔炉（itemId 2302）… minerPeriod=0`）。
        /// 0.9.28 起真实取值改为扫 `LDB.items.dataArray` 找 `minerPeriod > 0` 且最小的那台
        /// （= 效率最高的原版矿机，即大型/高级采矿机），这个常量只在扫不到时兜底。
        /// </summary>
        public const int AdvMinerItemId = 2301;
        /// <summary>本站钻孔等价覆盖的矿脉条数：12 个吸气钟，一个钟对一条矿脉。</summary>
        public const int MineBaseVeins = 12;
        /// <summary>
        /// cap 的硬上限（个/周期）= <c>MinePerCycle × 32</c>。0.9.27 起 MinePerCycle 不再是
        /// 基准产量，只作为「上限的单位」保留，避免地热 + 满级科技的极端组合把单周期撑爆。
        /// </summary>
        public const int MinePerCycle = 16;

        // ---- 泊位环：上下两层，各 25 个（合计必须 = MaxShips = 50）----
        // 单圈 50 个时相邻只隔 2π·25/50 = 3.14 m，货船长约 6 m 必然首尾重叠；
        // 拆成两层 25+25 后间距 2π·25/25 = 6.28 m，船体（长约 6 m、翼展约 4 m）
        // 首尾不再叠在一起。RingBerths 之和必须等于 MaxShips。
        public static readonly float[] RingY = { BodyY + 24.60f * BodyScale, BodyY + 17.60f * BodyScale };  // 环体中心高度（第二层=下层吊环）
        public static readonly float[] RingR = { 25.00f * BodyScale, 25.00f * BodyScale };   // 泊位半径（29.5）
        public static readonly float[] RingTube = { 3.20f * BodyScale, 0.30f * BodyScale };  // 环体径向半深（仅文档用）
        public static readonly int[] RingBerths = { 25, 25 };

        /// <summary>各层泊位所在高度（飞船停靠点）。上层=环顶甲板 +1.7，下层=吊环甲板 +1.3。</summary>
        public static readonly float[] ShipY = { BodyY + 30.60f * BodyScale, BodyY + 19.20f * BodyScale };

        public static float ShipYOn(int ring)
        {
            return ShipY[ring];
        }

        // ---- 主环造型（与 StationGeometry 对齐，供图纸/调试参考）----
        public const float RingDeckY = 28.90f;   // 环顶甲板面（发射座基面）
        public const float RingOuterR = 28.20f;  // 环外半径（外径 56.4 m）

        // ---- 中央光柱 ----
        // 光柱不再自己建模：直接借原版黑雾「中继站」自带的那段特效 mesh
        // （模型 288 的 space-unit-relay-eff-1，96 顶点 / 60 三角，长 67.85 m）。
        // 这里只保留「从哪到哪」和材质兜底用的半径，几何本身由 StationBeam 运行时取用。
        // 中继站的光柱本来就是「一根很细的能量带 + 几处爆闪」，靠 Unlit Alpha 材质
        // 滚动 UV 撑存在感，所以之前的实心棱柱怎么调都不像。
        public const float BeamRBot = 0.36f;      // 仅兜底棱柱用（取不到原版 mesh 时）
        public const float BeamRMid = 0.44f;      // 同上
        public const float BeamY0 = 6.40f;        // 光柱起点 = 地面发射筒喷口
        public const float BeamY1 = BodyY + 4.05f;// 54.05：插进站体底部接收锥
        public const float ReceiverR = 2.05f;     // 接收锥口半径

        // ---- 光柱流动特效（运行时对象，不烘进静态网格）----
        // 静态节环会把线撑成一根「带箍的杆子」，所以节环改成沿柱上移的实时对象。
        public const int BeamRingCount = 5;        // 同时存在的流动环数量
        public const float BeamRingGap = 7.60f;    // 环间距（m）
        public const float BeamRingSpeed = 13.0f;  // 上升速度（m/s）
        public const float BeamRingR = 0.86f;      // 环半径
        public const float BeamRingTube = 0.075f;  // 环管半径

        // ---- 钻孔（底座打洞：取地热 / 读矿脉 / 自动配货）----
        public const bool EnableDrill = true;
        public const float GeoPeriod = 2.0f;       // 地热强度采样周期（地形查询较贵，别每帧算）
        public const float GeoMinStrength = 0.25f; // 强度下限
        public const float GeoMaxStrength = 3.00f; // 强度上限（水星球分支会返回 3.0）
        /// <summary>
        /// 地热强度对采矿速度的加成上限：强度 3.0 时每周期多吸 3 倍。
        /// 地热只用来「加速钻孔」和面板读数，站点能量由电网 + 地热发电共同供给。
        /// </summary>
        public const float GeoMineBoostMax = 3.00f;

        // ---- 地热发电（0.9.4 重新接回，这次是「真的产出能量」）----
        // 0.6.0 曾把底座自发电彻底删掉，理由是「凭空 30 MW」。但那样一来
        // 面板上那个「地热 ×1.08」就只是个除法系数，玩家看不到任何能量收益 ——
        // 用户实测反馈的正是这一点（「打洞地热获取能量没有效果」）。
        // 现在按原版地热发电机同一套强度值换算功率：
        //   功率(W) = GeoPowerPerSec × 强度
        // 强度由 PowerSystem.CalculateGeothermalStrength 给出（基座周围 5 点地形采样，
        // 实测原版取值区间约 0.55 ~ 1.36，熔岩/低洼处更高），所以「选址」这件事终于有意义。
        /// <summary>强度 ×1.0 时的地热发电功率（W），约等于原版地热发电机的一半。</summary>
        public const float GeoPowerPerSec = 2_000_000f;
        // 【0.9.37】地热转换功率也跟「矿物利用」等级走（玩家要求「6 MW 也要同步绑定等级」）。
        // 口径与采矿速度同一套线性累加：功率 ×(1 + 0.15 × Lv)，Lv0 满强度 = 6 MW，
        // Lv6 = ×1.90 → 11.4 MW，Lv20 到顶 ×4 → 24 MW。
        // 为什么比采矿的 +10%/级 高 5 个点：地热在 100 MW 核电面前量级太小，
        // 若也按 10% 累加，玩家升到 Lv6 只多 3.6 MW，等于没感觉。
        /// <summary>「矿物利用」每级给地热转换功率的加成（0.15 = +15%/级）。</summary>
        public const float GeoTechPerLevel = 0.15f;
        /// <summary>地热倍率上限（×4，即 Lv20 到顶）。</summary>
        public const float GeoTechMulMax = 4.00f;
        /// <summary>
        /// 地热强度达到这个值，钻孔可以**不接电网**自持运转（自供电），
        /// 站点能量也一并由地热充上来。达不到就必须接电网 ——
        /// 这样「通电才工作」的设定还在，同时「打洞取能量」在好矿点上真的能顶用。
        /// </summary>
        public const float GeoSelfPowerMin = 1.05f;

        // ---- 底座 = 一台「地热发电机组」（0.9.5 新加，照原版地热发电机接进电网）----
        // 用户要的是「像黑雾中继站那样打洞取星球地热」，所以光把能量灌进站内电池不够，
        // 得让它在电网里真的算一台发电机（电网统计面板上看得见「+X MW」）。
        // 做法：给站点实体挂一个 PowerGeneratorComponent（geothermal 标记），
        // 强度由 PowerSystem.CalculateGeothermalStrength 现算 —— 和原版地热发电机同口径。
        public const bool EnableGeoGenerator = true;
        /// <summary>额定功率（W，强度 ×1.0 时）。原版地热发电机 3 MW，这里翻倍。</summary>
        public const float GeoGenPower = 6_000_000f;
        /// <summary>每 tick 的能量（游戏 60 tick/s）。真正写进 genPool.genEnergyPerTick 的值。</summary>
        public const long GeoGenPerTick = 100_000L;      // 6 MW / 60
        /// <summary>拉电缆的连线距离（m）。够到旁边一座输电塔就能并进同一张电网。</summary>
        public const float GeoGridConnect = 30f;
        /// <summary>
        /// 无线覆盖半径（m）。刻意给 0：本站不是输电塔，不该白送周围建筑无线供电。
        /// 「脱网自持」走的是另一条路 —— 地热强度 ≥ <see cref="GeoSelfPowerMin"/> 时
        /// 直接把地热充进站内能量池（StationDrill.ChargeGeo），不需要电网覆盖。
        /// </summary>
        public const float GeoGridCover = 0f;

        // ---- 海洋资源（水 / 硫酸）----
        // 原版抽水站是 EMinerType.Water：产物取 planet.waterItemId，与矿脉无关。
        // 底座是「打洞取本星球物资」，所以海洋也应该算一份 —— 之前只扫 veinPool，
        // 水星球上永远拿不到水。
        public const bool EnableOceanMining = true;

        // ---- 站内功能 ----
        public const int MaxShips = 50;          // 上下两层泊位各 25
        /// <summary>
        /// 运输机（小货运船）数量。原版物流塔上限 100，这里按用户要求给到 200；
        /// StationComponent 的 idleDroneCount 是 int（不像 idleShipIndices 是 ulong 位图），
        /// 200 不受位宽限制。
        /// </summary>
        public const int MaxDrones = 200;
        /// <summary>
        /// 卡槽数（20 = 两段语义不同）：
        ///   前段 <c>[0, ManualSlots)</c> —— 玩家自己配货（原版的「供应/需求」那一套）；
        ///   后段 <c>[AutoSlotStart, MaxItemKinds)</c> —— 通电后自动扫描本星球矿物填入，
        ///   专门给「采矿 + 存储」用，玩家不用管。
        ///
        /// 0.9.0~0.9.3 这 20 格被搬进了一个自建的独立窗口（Alt+H），结果玩家点开站体时
        /// 屏幕上是「原版窗口 + 独立窗口」两个面板，内容还重复。
        /// 0.9.4 起只有一个面板：**原版站体窗口本身**。原版窗口原来只造 6 个卡槽
        /// （<c>UIStationWindow._OnCreate</c> 里 `new UIStationStorage[6]`），
        /// 这里把它扩到 <see cref="ManualSlots"/> 个（补造 4 个克隆件），
        /// 行距压到 <see cref="SlotPitch"/>、卡槽等比缩到 <see cref="SlotScale"/>，
        /// 10 行才装得进 1080p；窗口右侧再挂一列只读的「当前采矿情况」。
        /// 后段那 10 格仍旧不铺卡槽 —— 它们本来就不可交互，铺出来只是把
        /// 「供应 / 0 / 50万」重复十遍。
        /// </summary>
        public const int MaxItemKinds = 20;
        /// <summary>前段（手动配货）格数，同时是原版窗口里要铺的卡槽个数。</summary>
        public const int ManualSlots = 10;
        /// <summary>后段（自动矿）起始索引，必须 = ManualSlots。</summary>
        public const int AutoSlotStart = 10;

        // ---- 站体窗口内卡槽排布（只对本站生效，原版站保持 76/1.0）----
        /// <summary>
        /// 卡槽行距。原版 76 px，10 行就是 760 px，窗口高度会到 1916 px、下半截配置面板
        /// 直接顶出屏幕；压到 54 px 后 10 行占 540 px，窗口 936 px（切到「优先级设置」页
        /// 是 1030 px），1080p 下整窗可见。
        /// </summary>
        public const float SlotPitch = 54f;
        /// <summary>
        /// 卡槽等比缩放。原版卡槽内部全是绝对定位（图标 34 / 文本 / 200 px 进度条 /
        /// 两颗供需按钮），单独压宽高会互相压住，只能整块 scale —— pivot 是 (0,1)，
        /// 缩放后左上角仍在原处，所以只剩「整体变小」这一种视觉后果。
        /// 0.70 时卡槽 520×70 → 364×49，正好落在 54 px 行距里。
        /// </summary>
        public const float SlotScale = 0.70f;
        /// <summary>
        /// 窗口右侧「当前采矿情况」列宽（只读，替代后段那 10 个卡槽）。
        /// 0.9.31：340 → 380。玩家反馈「供电 / 地热 / 钻孔 / 电池这些字太小」，
        /// 根因不是字号不够大，而是 340 px 里塞了三列（标签 + 读数 + 表头），
        /// 一放大就顶边，只能把字压在 13 px。改成表格后必须给右侧的
        /// 「储量 / 处 / 库存」三列留出对齐宽度，所以整列加宽 40 px。
        /// </summary>
        public const float MineColW = 440f;
        /// <summary>卡槽列与采矿列之间的间距。</summary>
        public const float MineColGap = 16f;
        public const int MaxItemCount = 500000;  // 每格容量（左列手动配货格始终是它）

        /// <summary>
        /// 自动采矿格（右列 <c>[AutoSlotStart, MaxItemKinds)</c>）的**每格**容量，跟原版
        /// 「矿物利用」（ID 3606）的等级绑定：
        ///   · 科技研究完成（Lv0）= 2 万；
        ///   · 之后每升 1 级 +3 万；
        ///   · Lv16 正好 20 000 + 30 000 × 16 = 500 000，封顶（= <see cref="MaxItemCount"/>）。
        ///
        /// 为什么按「每格」算而不是「整列合计」：右列 10 格一格一种矿，面板上玩家看到的就是
        /// 每格的「库存 / 容量」，门槛做在每格上才对得上眼睛看到的东西。
        /// 10 格合计：2 万 ×10 = 20 万 → 50 万 ×10 = 500 万。
        /// 若想改成「整列合计 50 万」，把 <see cref="AutoCapMax"/> 改成 50 000 即可，
        /// 曲线、封顶等级都不用动。
        /// </summary>
        public const int AutoCapBase = 20000;
        public const int AutoCapPerLevel = 30000;
        public const int AutoCapMax = 500000;
        /// <summary>
        /// 站内能量池上限。原版站点是 800 MJ，用户实测觉得太小（面板上「电池 701/800 MJ」
        /// 一格就满了，50 艘货船起两趟就空）—— 0.9.9 起提到 30 GJ。
        /// 注意它同时写进存档（StationComponent.energyMax），老存档要在运行时纠正一次。
        /// </summary>
        public const long MaxEnergyAcc = 30_000_000_000L;   // 30 GJ
        public const long WorkEnergyPerTick = 2_000_000L;
        public const long IdleEnergyPerTick = 100_000L;

        public const float ShipDockY = BodyY + 24.6f * BodyScale;   // stationShipPos（= 主环中心高度）
        public const float DroneDockY = BodyY + 6.70f * BodyScale;   // stationDronePos = 运输机甲板顶面

        // ---- 低空运输机甲板（小货船出口，16 条朝外发射道）----
        public const float DroneDeckY = BodyY + 6.30f * BodyScale;
        public const float DroneDeckR = 12.20f;
        public const int DroneBays = 16;

        // ---- 底座 ⇄ 塔顶的「信息 / 物资」传输带 ----
        // 主光柱（中继站 eff 网格）只负责能量。用户要的「几条细能量带互相传送信息、
        // 也包括待运物资」是另一组：4 条细带绕主轴 90° 均布，从底座肩台升到站体底面，
        // 每条都带小货箱光点往上走。分布半径要避让主光柱的宽度
        // （源宽 5.0 m × 0.7023 ≈ 3.51 m，半宽 1.75），3.80 m 留出 1.3 m 空隙，
        // 四条带之间也不会互相压住。
        public const int SignalCount = 4;
        public const float SignalR = 3.80f;        // 分布半径
        public const float SignalY0 = 4.30f;       // 起点：二级平台上方
        public const float SignalY1 = BodyY + 1.60f; // 51.60：插进站体底面
        public const float SignalWidthScale = 0.34f; // 相对主光柱网格的横向缩放（→ 宽约 1.19 m）
        public const float SignalSpeed = 9.0f;     // 物资光点上行速度（m/s）
        public const float CargoScale = 0.26f;     // 货箱光点半径（m）
        public const float CargoPerBand = 2;       // 每条带上的货箱数量

        // ---- 激光炮塔（塔顶 6 块武器平台 × 3 座 + 基座 4 座，绕 Y 轴 360° 旋转）----
        // 基座那 4 座挂在 45° + 90°k 的外伸炮座上，避开 12 个辊道口与 12 个吸气钟；
        // 与塔顶共用同一份转塔网格，所以数量必须等于 station_model.py 里 TURRETS 的长度。
        public const int TurretCount = 22;
        public const float TurretTurnSpeed = 55f;    // 度/秒，空闲扫掠与转向目标的最大角速度
        public const float TurretSweepSpeed = 9f;    // 度/秒，无敌可打时的巡逻扫掠

        // 炮塔的「亮度补偿」。塔顶那 18 座在碟体阴影里、基座那 4 座在裙边阴影里，
        // 而主建筑靠 BatchRenderer 由游戏统一喂光照；炮塔是普通 MeshRenderer，
        // 只吃场景光 —— 纯 PBR 反照率在阴影下就是一片黑。这里给炮塔专用材质
        // 铺一层很弱的冷白自发光，把轮廓「托」出来，物理上相当于微光漆。
        public const float TurretHullBoost = 1.06f;     // hull 反照率放大
        public const float TurretDarkBoost = 1.85f;     // dark 反照率放大（原来 0.26 太黑）
        public const float TurretWeaponBoost = 2.10f;   // weapon 反照率放大（新模型大量用它，原 1.45 仍偏黑）
        public const float TurretHullGlow = 0.62f;      // hull 自发光强度
        public const float TurretDarkGlow = 0.44f;      // dark 自发光强度
        public const float TurretWeaponGlow = 0.44f;    // weapon 自发光强度
        public const float TurretWarnGlow = 2.60f;      // 炮口琥珀镜片自发光

        // ---- 0.9.21：炮塔「在游戏里是黑的」的根治参数 ----
        // 0.8.0 只提亮了反照率、加了 0.30 的自发光，实机仍然是黑的，说明「黑」
        // 不是反照率不够，而是**光照这条路径本身没吃到东西**：
        //   · _MS_Tex 里 hull 那个取色点的金属度是满的（R=255）→ 纯金属面在
        //     没有反射探针的普通 MeshRenderer 上只剩镜面高光，漫反射项为 0；
        //   · 炮塔又是一直待在碟体/裙边的投影里，连高光都少；
        //   · 自发光 0.30 × 暗色贴图 ≈ 0.15，托不住。
        // 所以 0.9.21 改成「哑光 + 自发光托底 + 不接收阴影」三件套 ——
        // 金属度乘子归零（漫反射重新占主导），自发光翻倍，并且让炮塔不再接收
        // 站体的投影。这样无论太阳在哪个角度、玩家站在哪一侧，都不可能再是黑的。
        public const float TurretMetallicScale = 0.00f; // _MetallicMultiplier：0 = 纯漫反射
        public const float TurretSmoothScale = 0.55f;   // _SmoothMultiplier：留一点高光，别成塑料
        public const float TurretAlbedoScale = 1.10f;   // _AlbedoMultiplier 追加提亮

        // ---- 炮塔实战参数（0.9.13 / 0.9.24 分家）----
        // 基座 4 座照「高频激光塔」：对地，射程短、伤害中等、连发快，射线是连续激光；
        // 塔顶 18 座照「磁化电浆炮」：只打空中/太空目标，射程远、单发重、间隔长，弹丸是电浆炮。
        public const bool TurretCombat = true;
        // 【0.9.40】监控范围（玩家口中的「只有黑雾进圈才开打」就是这两条）：
        // 目标每 0.35 s 扫一次，超出对应圈的直接不进候选表 —— 基座激光塔不打圈外
        // 的地面黑雾，塔顶电浆炮不打射程外的空中/太空目标（原版磁化电浆炮
        // Search_Plasma 也是同款「先按 turretSpaceAttackRange 过滤」的口径）。
        public const float TurretRange = 140f;          // 基座激光塔监控范围（m，只打地面目标）
        // 【0.9.45】玩家选定「方案 B」：两处基础伤害同时翻倍。
        //   基座激光 160 → 320，间隔 0.22 → 0.18（4 座合计 DPS 5818 → 14222）。
        //   基座**不吃站内电量**（原版高频激光塔就是纯电武器，靠建筑自身供电），
        //   所以这一档是纯收益，不进「电浆弹能耗」那本账。
        //   实际单发仍要乘 history.energyDamageScale（能量武器伤害升级），
        //   当前存档 ×2.00，所以面板显示 640。
        public const int TurretDamage = 320;            // 对地单发伤害（基础值，再 × energyDamageScale）
        public const float TurretFireInterval = 0.18f;  // 对地射击间隔（s）
        /// <summary>
        /// 塔顶电浆炮的太空监控范围。**只作为读不到原版数据时的兜底**：
        /// 运行时由 <c>StationTurrets.SpaceRange()</c> 直接读原版磁化电浆炮 prefab 的
        /// <c>turretSpaceAttackRange</c>（0.10.35 = 6500 m），与原版
        /// <c>TurretComponent.Search_Plasma / CheckTargetValidBeforeShoot</c> 同一口径。
        ///
        /// 0.9.40 这里写的是 600 m —— 只有原版射程的 1/10。黑雾舰队在行星轨道上
        /// 1000~6000 m 就开始对地轰炸（<c>EnemyUnitComponent</c> 的轨道炮击），
        /// 600 m 的圈永远够不到，实机表现就是「塔顶武器完全不会攻击太空黑雾」。
        /// </summary>
        public const float TurretRangeSpace = 6500f;
        // 【0.9.45】方案 B：电浆炮基础单发 900 → 1800（当前存档 ×2.00 → 实发 3600）。
        //   射速（1.10 s）刻意**不动**：塔顶是「按功率降速」的，提高射速只会更快把
        //   站内电池抽干、实际 DPS 反而不涨；提单发才是这个模型下唯一有效的加强。
        //   代价见 C.PlasmaShotEnergy —— 每发能耗同步翻倍（10 → 20 MJ）。
        public const int TurretDamageSpace = 1800;      // 电浆炮单发伤害（基础值，再 × energyDamageScale）
        public const float TurretFireIntervalSpace = 1.10f;
        /// <summary>
        /// 【0.9.24】「打不动就放」：同一目标连续开火这么多秒还没击毁，判定为打不动的
        /// （无敌单位 / 护盾里 / 别的 mod 的怪），拉黑 <see cref="TurretGiveUpHold"/> 秒换目标。
        /// 上限：0.9.45 起基座单发 320 / 间隔 0.18 s，8 s 内一座能打 44 发 ≈ 14,222 伤害，
        /// 足够打死普通黑雾兵，又不会死磕（旧口径 8 s × 4 座 × 160 = 5120 已过时）。
        /// </summary>
        public const float TurretGiveUpSeconds = 8f;
        public const float TurretGiveUpHold = 6f;

        // ---- 0.9.46：基座激光也吃站内电量（15 MJ/发）----
        // 玩家要求：「基座激光每发能耗 15，需要吃站内电量」。
        // 之前基座是白打的（原版高频激光塔靠建筑自身供电，不占站内电池），
        // 0.9.45 伤害翻倍之后它成了纯收益，这次把电费补上，两条武器线才同一本账。
        //
        // 【代价必须先看清】4 座 × 1/0.18 s = 22.2 发/s × 15 MJ ≈ **333 MW**，
        // 和塔顶电浆炮的 327 MW 合起来满速要 **660 MW** —— 这已经是一整颗戴森球的量级。
        // 实机上（82 MW 的站）基座只能打到约 5.5 发/s（DPS 3,520），
        // 比 0.9.45「不吃电、14,222 DPS」明显更弱，这是玩家明确要的取舍。
        // 想兼顾就调小这个常数：5 MJ/发 → 111 MW（满速 DPS 降到 3,520 但供电轻松），
        // 3 MJ/发 → 67 MW。
        public const long LaserShotEnergy = 15_000_000L;

        // ---- 0.9.25：电浆炮「上子弹」= 塔顶电力 ----
        // 原版磁化电浆炮本来就不吃实体弹药（DSP 全系炮塔都是能量武器），所以塔顶这 18 座
        // 也不该让玩家去找弹链；但它必须**真的要能量**，否则「怎么上子弹」这个问题永远
        // 没有答案。这里按发扣站内电池（StationComponent.energy，和货船起飞同一个池子）。
        //
        // 【0.9.30】用户实测后要求「电浆弹能耗太低了，改成 10 MJ/发」：
        //   18 座满速 = 16.4 发/s × 10 MJ ≈ **164 MW**，单靠地热（6 MW）只能零星开火，
        //   必须接电网（120 MW 充电上限 + 电池缓冲）或靠第 10 格的核聚变发电机补足 ——
        //   这正是玩家想要的「电浆炮是重武器、烧电」的读感。
        //   面板武器行会直接写「弹10MJ」，日志「塔顶供电: …」会算出当前功率够几发/s。
        //
        // 【0.9.45】方案 B 的配套：单发伤害 900 → 1800（翻倍），每发能耗同步 10 → **20 MJ**，
        //   也就是「每 MJ 换多少伤害」这条比值保持不变 —— 强化的代价就是烧电。
        //   代价要说清楚：18 座满速从 163.6 MW 涨到 **327.3 MW**。
        //   好消息是**打母巢反而更划算**了：DamageObject 对母巢舰队是「每发固定减伤
        //   巢等级×50」，发数越少被扣的总额越少 ——
        //     旧：8.2 发/s × (1800−500) ≈ 10,660 DPS
        //     新：4.1 发/s × (3600−500) ≈ 12,710 DPS（同样 82 MW 供电，+19%）
        //   坏消息是打**小目标**会变慢：一发 3600 秒杀 800 血的小兵，剩下的 2800 是纯浪费，
        //   而发数只有原来一半。所以「想清小兵」请靠基座激光（0.9.45 已提到 320/0.18s），
        //   塔顶的定位就是重炮：打太空舰队、打母巢。
        public const long PlasmaShotEnergy = 20_000_000L;

        // ---- 0.9.30：第 10 格 = 核电燃料格（氘核燃料棒 → 发电）----
        // 用户要求：「左列最后一个（第 10 个）卡槽做成核电发电机，只需要请求氘核燃料棒来发电」。
        //
        // 做法：把第 10 格（storage[9]）固定成氘核燃料棒的**需求**槽 —— 别的站/运输机会
        // 自动把它送来（本地无人机 + 星际运输船都认），塔顶电浆炮与站内电池直接吃这份电。
        // 燃料热值取原版 ItemProto.HeatValue（氘核燃料棒 = 600 MJ），**能量守恒**：
        // 一根棒能发多少电是固定的，输出功率越高、烧得越快（时长为 热值 ÷ 功率）。
        //
        // 【0.9.36】输出从 15 MW 提到 **100 MW**（玩家先要 80、再改 100）。
        // 参考量级：塔顶 18 座电浆炮满速需 163.6 MW、单发电浆弹 10 MJ，
        // 15 MW 只够约 1.5 发/s；100 MW 能到约 10 发/s，单棒塔终于有「重武器」的读感。
        // 代价是烧得快：氘棒 6 s / 反物质棒 72 s / 奇异湮灭棒 720 s，一格 100 根
        // 在 100 MW 下分别撑 10 分钟 / 2 小时 / 20 小时 —— 靠物流自动补货。
        // 【0.9.45】单发能耗翻到 20 MJ 之后，100 MW 只够 5 发/s（18 座满速需 327 MW），
        // 单棒塔从「接近满速」退回「半速」—— 这是方案 B 的直接代价，要满速就得
        // 电网 120 MW 上限叠满 + 地热 + 核电三路一起上，或者多建几座塔分散。
        public const int FuelSlot = ManualSlots - 1;     // 9 = 面板左列第 10 格
        public const int FuelSlotMax = 100;              // 一格存 100 根（氘棒约 10 分钟满功率）
        public const float FusionPowerMw = 100f;         // 单塔核电输出（玩家指定 100 MW）
        /// <summary>读不到 ItemProto.HeatValue 时的兜底热值（氘核燃料棒 600 MJ）。</summary>
        public const long FallbackRodHeat = 600_000_000L;

        // ---- 0.9.38：炮塔伤害绑定原版「能量武器伤害」升级 ----
        // 【为什么不再自造绑定】0.9.25~0.9.37 是「每级矿物利用 +5% 电磁武器」——
        // 那是凭空加的第五条效果，科技页上并没有这条说明，玩家也无从对照。
        //
        // 玩家要求改绑「科技 → 升级 → 能量武器伤害（每级 +10%）」。这条是原版真科技：
        // 原版 TurretComponent 里三处开火都是这个口径 ——
        //   · Shoot_Laser       `100 × turretDamageScale × combatUpgradeData.energyDamageScale`
        //   · Shoot_Plasma      同上
        //   · Shoot_LocalPlasma 同上
        // 也就是「高频激光塔」和「磁化电浆炮」在**原版里就是同一档能量武器伤害**，
        // 所以本站的激光塔与电浆炮一起吃这个倍率，不需要再分家。
        //
        // 取值方式和 0.9.18 的 miningSpeedScale 完全一致：直接读存档累计值
        // `GameMain.history.energyDamageScale`（UnlockTechFunction case 62 每级 += 0.1），
        // 换存档 / 别的 mod 改等级都自动跟随，本站不再保存任何等级副本。
        /// <summary>能量武器倍率的安全上限（只为防 int 溢出，正常科技远到不了）。</summary>
        public const float WeaponMulMax = 100f;

        // ---- 0.9.24：塔顶 18 座换成原版「磁化电浆炮」的炮 ----
        // 模型不是自己拼的，而是运行时从 LDB 里按名字找原版预制体
        // （<see cref="PlasmaModelName"/>），只取它 lod0 的 5 块网格 +
        // 原版贴图（反照率 / 法线 / MS / 自发光掩码），shader 换成我们已经
        // 验证过的 PBR Standard —— 原版那份 `PBR Turret Plasma` 的俯仰/偏航
        // 是**顶点着色器按 _Pivot 转的**，直接拿来放在普通 MeshRenderer 上会
        // 读不到实例数据，所以必须脱掉那层。
        public const string PlasmaModelName = "turret-plasma";
        /// <summary>原版炮塔总高 8.32 m、底座直径 11.26 m，缩到 0.26 倍 = 高 2.16 m / 底 2.93 m。</summary>
        public const float PlasmaScale = 0.26f;
        /// <summary>模型原点比底座低 0.60 m，缩放后要抬起来才坐在平台上。</summary>
        public const float PlasmaLift = 0.156f;
        /// <summary>
        /// 俯仰枢轴，**模型原始坐标**（缩放挂在模板根节点上，所以子节点一律用模型单位）。
        /// 取自原版材质里的 `_Pivot = (0, 5.865, −0.850)` —— 原版 shader 就是绕这个点转炮身的。
        /// </summary>
        public const float PlasmaPivotY = 5.865f;
        public const float PlasmaPivotZ = -0.850f;
        /// <summary>
        /// 炮口相对俯仰枢轴的位置（模型单位）：炮口在 (0, 6.615, −5.634)，
        /// 减掉枢轴 = (0, 0.750, −4.784)。
        /// </summary>
        public const float PlasmaMuzzleY = 0.750f;
        public const float PlasmaMuzzleZ = -4.784f;
        /// <summary>塔顶前 18 个挂点是电浆炮，基座 4 座留给激光。</summary>
        public const int PlasmaTopCount = 18;
        /// <summary>
        /// 「空中目标」的高度兜底：高出地面多少米算天上的敌人。
        /// 主判据是原版的 <c>TurretComponent.IsAirEnemy()</c>（protoId 8129 / 8130，
        /// 黑雾的飞行单位）；这条只是给别的 mod 的飞行单位兜底 —— 取 95 m，
        /// 正好在站顶（约 75 m）之上，站在甲板上的东西不会被误判成空中。
        /// 行星敌人池里 `isSpace` 恒为 false（真正的星区单位在 SpaceSector 池里，
        /// 坐标是星区尺度，走的是太空战斗子系统，本 mod 不介入）。
        /// </summary>
        public const float PlasmaAirHeight = 95f;
        // ---- 0.9.23 射线改用原版高频激光塔 ----
        // 光束的网格 / 材质 / 流光动画 / 命中火花，全部由原版
        // `skillSystem.turretLaserContinuous` 负责（见 StationTurrets.LaserAim），
        // 所以这里不再有「束宽 / 三层颜色 / 持续时长 / 闪光缩放」这些参数 ——
        // 0.9.20~0.9.22 那套自制特效已整体删除。
        //
        // 炮口在转塔局部坐标系里的位置仍然要用：原版射线需要一段精确的起点，
        // 取 StationTurretGeometry 的中管口（主筒轴线 Y=0.95、中管抬高 +0.12、管口 z=3.30）。
        public const float TurretMuzzleY = 1.07f;
        public const float TurretMuzzleZ = 3.30f;
        /// <summary>
        /// 【0.9.25】三根炮管 → 三条射线。坐标是转塔局部单位（米），来自
        /// tools/station_model.py 的 `_barrel()` 三根管的终点：
        ///   · 中管：(0, Y+0.12, 3.30)（Y = 0.95，即上面的 1.07 / 3.30）；
        ///   · 左管：(-0.92, Y-0.44, 3.10)；
        ///   · 右管：(+0.92, Y-0.44, 3.10)。
        /// 管口还有 0.012 的薄框，这里取到 3.31 / 3.11，让射线起点稍微探出管口，
        /// 不会在炮口内部闪一条短线。
        /// </summary>
        public static readonly Vector3[] TurretMuzzles =
        {
            new Vector3(0f, TurretMuzzleY, TurretMuzzleZ),
            new Vector3(-0.92f, 0.51f, 3.112f),
            new Vector3(0.92f, 0.51f, 3.112f),
        };

        // ---- 中央护盾（原版行星护盾样式的半球能量罩）----
        // 塔顶放大到 1.18 后顶面 88.2 m，底圈在 BodyY=50，半径必须 ≥ 38.2 才包得住。
        // 0.9.39：碟顶核心塔加了一根天线，塔顶从局部 32.4 长到 34.96
        // （世界 50 + 34.96×1.18 = 91.3），40 m 的罩顶正好 90.0 —— 信标球会从罩顶戳出去。
        // 罩子是按「半球底圈在 BodyY、半径 R」算的，加 3 m 在 40 m 的球上根本看不出来，
        // 但能把天线整个包回罩内，所以 R 从 40 提到 43（罩顶 93.0）。
        public const float ShieldRadius = 43.00f;
        public const float ShieldBaseY = BodyY;      // 半球底圈高度 = 站体底部（不再是地面）
        public const int ShieldSeg = 48;
        public const int ShieldRings = 16;
        public const int ShieldLatLines = 9;
        public const float ShieldPulse = 0.55f;      // 罩面呼吸幅度

        // ---- 科技树 ----
        /// <summary>
        /// mod 科技 ID。**必须 &lt; 2000**：原版 <c>TechProto.page => ID >= 2000 ? 1 : 0</c>，
        /// 大于等于 2000 会被排到「升级」那一页（graphGroup1），主科技树上根本看不到，
        /// 也画不出到前置科技的那条连线 —— 0.9.13 用的 3996 就是这个毛病。
        /// 运行时 <see cref="Protos.RegisterTech"/> 会先挑一个空号（1900~1999），避让其它 mod。
        /// </summary>
        public static int TechId = 1997;
        /// <summary>可用的科技 ID 区间（含端点）。EnergyHub 用了 1999。</summary>
        public const int TechIdMin = 1900;
        public const int TechIdMax = 1999;
        /// <summary>
        /// 原版「矿物利用」升级科技 ID（TechProto.INFINITE_MINE_TECH_ID）。
        /// 我们的钻孔直接读存档里的三个累计值（miningSpeedScale / miningCostRate / enemyDropScale），
        /// 每级等于：采矿速度 +10%、每次作业消耗 -6%（连乘 0.94^Lv）。
        /// </summary>
        public const int MineralTechId = 3606;
        /// <summary>兜底公式用的每级系数（正常路径读存档，不经过这里）。</summary>
        public const float MineTechSpeedPer = 0.10f;
        public const float MineTechCostPer = 0.06f;
        /// <summary>
        /// 【已废弃，0.9.18 起不再使用】原版消耗倍率没有 5% 下限，
        /// 它一路连乘到 3.4E-11 以下才归零，所以本站也不再压下限。
        /// </summary>
        public const float MineTechCostMin = 0.05f;

        // ---- 本地化 key ----
        public const string LTechName = "LogisticsHub.Tech.Name";
        public const string LTechDesc = "LogisticsHub.Tech.Desc";
        public const string LTechConclusion = "LogisticsHub.Tech.Conclusion";
        public const string LItemName = "LogisticsHub.Item.Name";
        public const string LItemDesc = "LogisticsHub.Item.Desc";
        public const string LRecipeName = "LogisticsHub.Recipe.Name";
        public const string LRecipeDesc = "LogisticsHub.Recipe.Desc";

        // ---- 配方（按名字在 LDB 就绪后反查，避免硬编码 id）----
        // 注意：原版中文名是「星际物流运输站」，之前写成「星际物流站」导致反查失败、配方退化成占位材料
        public const string RecipeItemName1 = "星际物流运输站";
        public const int RecipeItemCount1 = 2;
        public const string RecipeItemName2 = "处理器";
        public const int RecipeItemCount2 = 20;
        public const string RecipeItemName3 = "钛合金";
        public const int RecipeItemCount3 = 40;
        public const string RecipeItemName4 = "空间翘曲器";
        public const int RecipeItemCount4 = 2;
        public const int RecipeTimeSpend = 600;  // 帧，10 秒
    }
}
