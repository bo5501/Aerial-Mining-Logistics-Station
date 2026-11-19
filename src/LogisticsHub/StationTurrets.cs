using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LogisticsHub
{
    /// <summary>
    /// 深空物流枢纽站的两个「活」部件：
    ///   · 22 座激光炮塔 —— 塔顶 18 座（6 块武器平台 × 3）+ 基座 4 座（45° 外伸炮座），
    ///     每座绕自己的 Y 轴 360° 连续扫掠（转速各不相同，避免整齐划一）
    ///   · 中央护盾 —— 天线顶部的核心展开半径 43 m 的半球能量罩，覆盖整个站体
    ///
    /// 为什么用普通 GameObject 而不是 Graphics.DrawMesh：
    /// DSP 的建筑走 GPU instancing（BatchRenderer + DrawMeshInstancedIndirect），shader 从实例
    /// 缓冲读每栋建筑的矩阵；单独 DrawMesh 时 SV_InstanceID 恒为 0，模型会叠到别人身上。
    /// 这里改成普通 MeshRenderer（layer 0，与建筑同层），由 Unity 正常走 transform 渲染，
    /// 材质仍然复用主建筑偷来的 VF PBR 材质，配色与站体一致。
    ///
    /// 炮塔几何来自 StationTurretGeometry（tools/station_model.py 导出的局部模板），
    /// 所以游戏内与预览图逐三角一致。
    /// </summary>
    internal class StationTurrets : MonoBehaviour
    {
        private class Rig
        {
            public GameObject Root;
            public MeshRenderer Shield;
            public GameObject Beam;        // 原版中继站光柱网格（取不到时光柱退回内置棱柱）
            public Transform[] Turret;
            public Transform[] BeamRing;   // 光柱上流动的能量环
            public Transform[] Signal;     // 底座 ⇄ 塔顶的 4 条细传输带（信息 + 物资）
            public Transform[] Cargo;      // 沿传输带上行的货箱光点
            public float[] Angle;      // 相对初始朝向的累计转角（度）
            public float[] Speed;      // 每座自己的扫掠速度（度/秒）
            public float Age;
            public bool Seen;
            public bool Powered;

            // ---- 0.9.23 武器系统：直接借用原版高频激光塔的连续射线 ----
            // LaserId[t*3+b] = 工厂 skillSystem.turretLaserContinuous 池里的句柄，0 表示当前没有射线。
            // 【0.9.25】改成每座塔三根炮管各一条（b = 0/1/2，见 C.TurretMuzzles）：
            // 模型本来就是三叉三管，只画中间一条，实机看上去像「三根管子只有一根在工作」。
            // 光束的网格 / 材质 / 流动动画 / 命中火花全由原版系统负责，我们只喂三个起点 + 一个终点。
            public int[] LaserId;
            public float[] FireCd;
            public int[] TargetEnemy;      // 当前锁定的 enemyPool 索引，-1 为空
            public Tgt[] Candidates;       // 每 0.35 s 扫一次的结果
            public int CandCount;
            public float ScanTimer;
            /// <summary>0.9.25：电浆炮因电量不足停火（每台一个，用于只打一次日志）。</summary>
            public bool[] NoAmmo;

            // ---- 0.9.24：塔顶 18 座是原版「磁化电浆炮」----
            // Gun[t] = 俯仰节点。电浆炮的模型分「静止底座 + 偏航围壳 + 可俯仰炮身」，
            // 只能转枪身；激光塔没有这个节点（null → 直接俯仰整座塔）。
            public Transform[] Gun;
            // Space[t] = true → 只打空中目标（电浆炮），false → 只打地面（激光塔）。
            public bool[] Space;
            // 每帧把候选目标按「空中 / 地面」分到两个下标表里，塔按 t % N 轮着打，
            // 免得 22 座全扑同一个目标（实机上就是一圈光柱聚成一个点）。
            public int[] GIdx;
            public int[] AIdx;
            public int GN;
            public int AN;
            // 「打不动就放」：同一目标连续开火超过 C.TurretGiveUpSeconds 还没死，
            // 拉黑一段时间，免得死锁在一个打不掉的目标上一直输出（实机上就是
            // 「敌人没了/打不动，炮塔还在持续开火」）。
            public float[] Lock;
            public int[] Ignore;
            public float[] IgnoreT;
        }

        /// <summary>一个可打的目标：enemyPool 索引 + 世界坐标 + 相对站体底座的高度。</summary>
        private struct Tgt
        {
            public int Id;
            public Vector3 Pos;
            public bool Air;
            public float Height;
            public float Dist2;
        }

        private static StationTurrets _inst;

        private readonly Dictionary<int, Rig> _rigs = new Dictionary<int, Rig>();
        private Mesh _turretMesh;
        private GameObject _plasmaTpl;      // 原版磁化电浆炮模板（5 块网格 + 1 个俯仰节点）
        private Material _plasmaMat;        // 电浆炮专用材质（原版贴图 + PBR Standard）
        private Mesh _shieldMesh;
        private Mesh _beamRingMesh;
        private Material[] _turretMats;
        private Material[] _shieldMats;
        private Material[] _beamRingMats;
        private bool _combatFailed;
        /// <summary>最近一次战斗用到的技能系统（原版连续激光池挂在它上面）。</summary>
        private SkillSystem _ss;
        private bool _laserLogged;
        private bool _laserErr;
        private bool _shellLogged;
        private bool _giveUpLogged;
        private bool _shellErr;
        /// <summary>0.9.25：电浆炮电量不足停火，只打一次日志。</summary>
        private bool _noAmmoLogged;
        /// <summary>0.9.26：上一次记过日志的能量武器倍率（电浆炮伤害变化时打一行）。</summary>
        private float _plasmaMul = -1f;
        /// <summary>0.9.29：上一次记过日志的塔顶供电状态（断电位 ×10000 + 总功率 MW）。</summary>
        private float _towerKey = -1f;
        /// <summary>0.9.29：电浆炮材质当前的自发光倍率（通电 1.10 / 断电 0.28）。</summary>
        private float _plasmaGlow = 0.28f;
        private Color _shellBase = new Color(0.14f, 0.48f, 0.92f, 0.075f);
        private float _time;
        private int _planetId = -1;
        private bool _failed;

        public static void Ensure()
        {
            if (_inst != null)
                return;
            var go = new GameObject("logistics-hub-turrets");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<StationTurrets>();
        }

        private void Prepare()
        {
            try
            {
                int rigTurretCount = Mathf.Min(C.TurretCount, StationTurretGeometry.Slots.Length);
                _turretMesh = StationMesh.GetTurret();
                _turretMats = StationMesh.TurretMaterials();
                BuildPlasmaTemplate();
                _shieldMesh = StationMesh.BuildShield(C.ShieldRadius, C.ShieldSeg, C.ShieldRings, C.ShieldBaseY);
                _shieldMats = StationMesh.ShieldMaterials();
                _beamRingMesh = StationMesh.BuildBeamRing(C.BeamRingR, C.BeamRingTube, 32, 6);
                _beamRingMats = StationMesh.BeamRingMaterials();
                if (_shieldMats != null && _shieldMats.Length > 0 && _shieldMats[0] != null &&
                    _shieldMats[0].HasProperty("_Color"))
                    _shellBase = _shieldMats[0].GetColor("_Color");
                Plugin.Log.LogInfo("炮塔 / 护盾运行时已就绪: 炮塔=" +
                                   (rigTurretCount) + " 座（塔顶 " + Mathf.Min(C.PlasmaTopCount, rigTurretCount) +
                                   " 座 = 原版磁化电浆炮，只打空中；基座剩下的 = 高频激光塔，只打地面） 材质=" +
                                   (_turretMats != null ? _turretMats.Length : 0) +
                                   (_plasmaTpl != null ? " + 电浆炮原版材质" : "（电浆炮模型缺失，塔顶沿用激光塔）") +
                                   " 护盾半径=" + C.ShieldRadius + "m");
            }
            catch (System.Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("炮塔/护盾初始化失败: " + e);
            }
        }

        private void Update()
        {
            if (_failed)
                return;

            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f)
                return;                                  // 游戏暂停时 Time.deltaTime = 0
            _time += dt;

            PlanetData planet = GameMain.localPlanet;
            PlanetFactory factory = planet == null ? null : planet.factory;
            if (factory == null || factory.transport == null || factory.entityPool == null)
            {
                ClearAll();
                return;
            }

            if (_planetId != planet.id)
            {
                ClearAll();
                _planetId = planet.id;
            }

            if (_turretMesh == null)
            {
                Prepare();
                if (_turretMesh == null)
                    return;
            }

            foreach (var kv in _rigs)
                kv.Value.Seen = false;

            StationComponent[] pool = factory.transport.stationPool;
            int cursor = Mathf.Min(factory.transport.stationCursor, pool.Length);
            int live = 0;

            for (int i = 1; i < cursor; i++)
            {
                StationComponent sc = pool[i];
                // 【0.9.6】pool 里空位很多（拆掉的站会留 null）。原来这里直接 sc.id，
                // 一旦撞上空位就抛 NullReferenceException，整个 Update 从这一行中断：
                // 后面的站不再处理（炮塔/护盾/光柱全不出现），StationDrill / StationMiner
                // 也一起停摆，日志还误报「本星球暂时没有枢纽站建筑」。
                if (sc == null || sc.id != i || sc.entityId <= 0 || sc.entityId >= factory.entityPool.Length)
                    continue;

                EntityData ed = factory.entityPool[sc.entityId];
                if (ed.protoId != C.ItemId && ed.modelIndex != C.ModelId)
                    continue;

                Rig rig;
                if (!_rigs.TryGetValue(sc.entityId, out rig))
                {
                    rig = CreateRig();
                    if (rig == null)
                        continue;
                    _rigs[sc.entityId] = rig;
                }

                rig.Seen = true;
                rig.Age += dt;
                live++;

                // 底座钻孔：先算出「能不能工作（电网 / 地热自供电）/ 地热多强」，
                // 采矿与护盾都依赖它；地热同时会往站点能量池里充电。
                StationDrill.Tick(factory, sc, ed.pos, ed.rot, dt);
                // 底部 12 个吸气钟：把脚下矿脉吸进站点仓储
                StationMiner.Tick(factory, sc, ed.pos, dt);
                rig.Powered = StationDrill.Powered(sc.entityId);

                Transform root = rig.Root.transform;
                root.SetPositionAndRotation(ed.pos, ed.rot);

                if (C.TurretCombat && !_combatFailed)
                    Combat(factory, sc, rig, dt);
                else
                    SweepAll(rig, dt);

                // 光柱流光：环沿柱上移，越往上收得越小（跟光柱本身的收口一致）。
                // 这是「特效」而不是模型 —— 静态节环只会把线撑成一根带箍的杆子。
                // 0.6.0：流光 = 在运送能量，所以没通电就不转（整组隐藏）。
                if (rig.BeamRing != null && rig.BeamRing.Length > 0)
                {
                    float span = Mathf.Max(1f, C.BeamY1 - C.BeamY0);
                    for (int b = 0; b < rig.BeamRing.Length; b++)
                    {
                        if (rig.BeamRing[b] == null)
                            continue;
                        GameObject rg = rig.BeamRing[b].gameObject;
                        if (rg.activeSelf != rig.Powered)
                            rg.SetActive(rig.Powered);
                        if (!rig.Powered)
                            continue;
                        float y = Mathf.Repeat(_time * C.BeamRingSpeed + b * C.BeamRingGap, span);
                        rig.BeamRing[b].localPosition = new Vector3(0f, C.BeamY0 + y, 0f);
                        float k = y / span;
                        float s = Mathf.Lerp(1.25f, 0.55f, k);
                        rig.BeamRing[b].localScale = new Vector3(s, 1f, s);
                    }
                }

                // 传输带：断电时整组隐藏（没电就没有信息/物资在跑）。
                // 货箱沿各自带子上行，到顶回绕；每条的初相位与速度都错开，
                // 看起来才是「四个通道各自在送东西」，而不是四份同步副本。
                if (rig.Signal != null)
                {
                    for (int bi = 0; bi < rig.Signal.Length; bi++)
                    {
                        Transform st = rig.Signal[bi];
                        if (st != null && st.gameObject.activeSelf != rig.Powered)
                            st.gameObject.SetActive(rig.Powered);
                    }
                }
                if (rig.Cargo != null && rig.Cargo.Length > 0)
                {
                    if (rig.Powered)
                    {
                        float span = Mathf.Max(1f, C.SignalY1 - C.SignalY0);
                        int per = Mathf.Max(1, Mathf.RoundToInt(C.CargoPerBand));
                        int bands = Mathf.Max(1, C.SignalCount);
                        for (int k = 0; k < rig.Cargo.Length; k++)
                        {
                            Transform cg = rig.Cargo[k];
                            if (cg == null)
                                continue;
                            if (!cg.gameObject.activeSelf)
                                cg.gameObject.SetActive(true);

                            int band = k / per;
                            float a = Mathf.PI * 2f * band / bands;
                            float spd = C.SignalSpeed * (1f + 0.12f * band);
                            // 同一条带上「一格上行、一格下行」：上行是待运物资，
                            // 下行是回执/指令 —— 用户说的「相互传送信息」就是这个双向感。
                            int idx = k % per;
                            bool up = (idx % 2) == 0;
                            float y = Mathf.Repeat(_time * spd + (idx * span) / per, span);
                            if (!up)
                                y = span - y;
                            cg.localPosition = new Vector3(Mathf.Cos(a) * C.SignalR,
                                                           C.SignalY0 + y,
                                                           Mathf.Sin(a) * C.SignalR);
                            cg.localRotation = Quaternion.Euler(y * 24f, y * 37f, y * 11f);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < rig.Cargo.Length; k++)
                        {
                            Transform cg = rig.Cargo[k];
                            if (cg != null && cg.gameObject.activeSelf)
                                cg.gameObject.SetActive(false);
                        }
                    }
                }

                // 护盾：通电才启动。展开（0.9 s）→ 常驻；罩面做缓慢呼吸。
                if (rig.Shield != null)
                {
                    if (rig.Shield.enabled != rig.Powered)
                    {
                        rig.Shield.enabled = rig.Powered;
                        if (rig.Powered)
                            rig.Age = 0f;                 // 每次上电都重新展开
                    }
                    if (rig.Powered)
                    {
                        float grow = Mathf.Clamp01(rig.Age / 0.9f);
                        float s = Mathf.SmoothStep(0.06f, 1f, grow);
                        rig.Shield.transform.localScale = new Vector3(s, s, s);
                    }
                }

                // 【0.9.29】塔顶「通电」的视觉反馈：底座把电送上来之后，炮身自发光抬到 1.10，
                // 断电熄回 0.28。用户反馈「底座在打洞发电，塔顶却看不出通电」——
                // 面板上给数字（「塔顶 xMW」），模型上给亮度，两条一起才说得清。
                if (_plasmaMat != null)
                {
                    float em = rig.Powered ? 1.10f : 0.28f;
                    if (!Mathf.Approximately(em, _plasmaGlow))
                    {
                        _plasmaGlow = em;
                        _plasmaMat.SetFloat("_EmissionMultiplier", em);
                    }
                }
            }

            // 护盾呼吸（材质共享，一次设置对所有站生效）
            if (_shieldMats != null && _shieldMats.Length > 0 && _shieldMats[0] != null)
            {
                float a = 1f + C.ShieldPulse * 0.4f * Mathf.Sin(_time * 1.35f);
                Color c = _shellBase;
                c.a = _shellBase.a * a;
                _shieldMats[0].SetColor("_Color", c);
            }

            CleanupUnseen();

            if (live == 0 && _rigs.Count == 0 && _warned == false)
            {
                _warned = true;
                Plugin.Log.LogInfo("炮塔/护盾：本星球暂时没有枢纽站建筑，等待建造…");
            }
            if (live > 0)
                _warned = false;
        }

        private bool _warned;

        // ================= 原版「磁化电浆炮」模型 =================

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindDeep(root.GetChild(i), name);
                if (r != null)
                    return r;
            }
            return null;
        }

        /// <summary>
        /// 电浆炮材质：**贴图全用原版的，shader 换成我们验证过的 PBR Standard**。
        ///
        /// 原版那份 `VF Shaders/Forward/PBR Turret Plasma` 的俯仰/偏航不是靠 Transform，
        /// 而是顶点着色器按材质里的 `_Pivot`（0, 5.865, −0.850）转网格 —— 它读的是
        /// BatchRenderer 喂的实例缓冲，套在普通 MeshRenderer 上根本拿不到，姿态会乱。
        /// 所以把 shader 换掉，姿态交回 Transform（见 <see cref="CreateRig"/> 的 gun 节点）。
        ///
        /// 另外两处必须改口径：
        /// · 反照率贴图的 alpha 均值只有 17/255（原版 shader 压根不读 alpha），
        ///   在 PBR Standard 上必须 `_AlphaClip = 0`，否则整座炮被裁掉一半；
        /// · 原版 `_EmissionTex`（turret-plasma-lod0-e）是**分区掩码**而不是亮度图，
        ///   均值接近纯白，直接拿来当自发光会把整座炮烧成白棍 —— 换成用反照率贴图
        ///   做极弱的「微光漆」，和塔顶激光塔的自发光托底同一个路子。
        /// </summary>
        private static Material MakePlasmaMaterial(Material src)
        {
            Shader sh = Shader.Find("VF Shaders/Forward/PBR Standard");
            if (sh == null)
                sh = Shader.Find("Standard");

            Material m = src != null ? new Material(src) : new Material(sh);
            if (sh != null)
                m.shader = sh;
            m.name = "hub-plasma-hull";
            m.enableInstancing = false;

            if (m.HasProperty("_ToggleVerta")) m.SetFloat("_ToggleVerta", 0f);
            if (m.HasProperty("_ToggleAnim")) m.SetFloat("_ToggleAnim", 0f);
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 0f);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0f);
            if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", 2f);
            // MS 贴图金属度均值 147/255、光滑度 252/255：满金属 + 全光滑在没有反射探针的
            // 普通 MeshRenderer 上就是一片黑（0.9.21 踩过的同一个坑），压下来。
            if (m.HasProperty("_MetallicMultiplier")) m.SetFloat("_MetallicMultiplier", 0.15f);
            if (m.HasProperty("_SmoothMultiplier")) m.SetFloat("_SmoothMultiplier", 0.45f);
            if (m.HasProperty("_AlbedoMultiplier")) m.SetFloat("_AlbedoMultiplier", 2.20f);
            if (m.HasProperty("_Color"))
            {
                Color c = m.GetColor("_Color");
                c.a = 1f;
                m.SetColor("_Color", c);
            }

            Texture albedo = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            if (m.HasProperty("_EmissionTex") && albedo != null)
                m.SetTexture("_EmissionTex", albedo);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.white);
            if (m.HasProperty("_EmissionSwitch")) m.SetFloat("_EmissionSwitch", 1f);
            if (m.HasProperty("_EmissionMultiplier")) m.SetFloat("_EmissionMultiplier", 0.28f);
            if (m.HasProperty("_EmissionUsePower")) m.SetFloat("_EmissionUsePower", 0f);
            if (m.HasProperty("_EmissionJitter")) m.SetFloat("_EmissionJitter", 0f);
            m.EnableKeyword("_EMISSION");
            return m;
        }

        /// <summary>
        /// 做一份「电浆炮」模板：从 LDB 里按名字找原版预制体，取它 lod0 的 5 块网格，
        /// 摆成和原版一致的三段结构 —— 底座 / 围壳 / 活塞静止，炮身（p3·p4）挂在
        /// `gun` 节点下，绕原版 `_Pivot` 俯仰。
        ///
        /// 子节点一律用**模型原始坐标**，缩放（<see cref="C.PlasmaScale"/>）与 180° 转向
        /// 都挂在模板根节点上：模型炮口朝 −Z，转 180° 后才对得上我们「+Z 为正向」的约定，
        /// 而炮口世界坐标由 <c>gun.TransformPoint</c> 自动带上根节点的缩放，不用手乘。
        /// </summary>
        private void BuildPlasmaTemplate()
        {
            try
            {
                ModelProtoSet set = LDB.models;
                if (set == null || set.modelArray == null)
                {
                    Plugin.Log.LogWarning("电浆炮: LDB.models 还没就绪，塔顶暂时沿用激光塔");
                    return;
                }
                ModelProto[] models = set.modelArray;
                GameObject prefab = null;
                for (int i = 1; i < models.Length; i++)
                {
                    ModelProto mp = models[i];
                    if (mp == null || mp.prefabDesc == null)
                        continue;
                    GameObject pf = mp.prefabDesc.prefab;
                    if (pf == null || pf.name != C.PlasmaModelName)
                        continue;
                    prefab = pf;
                    break;
                }
                if (prefab == null)
                {
                    Plugin.Log.LogWarning("电浆炮: LDB 里没找到模型 " + C.PlasmaModelName +
                                          "，塔顶 18 座暂时沿用激光塔");
                    return;
                }

                var src = new Transform[5];
                for (int i = 0; i < 5; i++)
                {
                    src[i] = FindDeep(prefab.transform, C.PlasmaModelName + "-lod0-" + i);
                    if (src[i] == null)
                    {
                        Plugin.Log.LogWarning("电浆炮: 预制体缺少部件 " + C.PlasmaModelName + "-lod0-" + i);
                        return;
                    }
                }

                MeshRenderer srcMr = src[0].GetComponent<MeshRenderer>();
                _plasmaMat = MakePlasmaMaterial(srcMr != null ? srcMr.sharedMaterial : null);

                var tpl = new GameObject("plasma-tpl");
                tpl.layer = 0;
                tpl.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(tpl);
                Transform rt = tpl.transform;

                Transform AddPart(Transform parent, int idx, Vector3 pos)
                {
                    var go = new GameObject("p" + idx);
                    go.layer = 0;
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = pos;
                    MeshFilter mf = src[idx].GetComponent<MeshFilter>();
                    go.AddComponent<MeshFilter>().sharedMesh = mf != null ? mf.sharedMesh : null;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = _plasmaMat;
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    // 和激光塔一个口径：不接收阴影，否则在碟体投影里整片发黑。
                    mr.receiveShadows = false;
                    mr.lightProbeUsage = LightProbeUsage.BlendProbes;
                    mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    return go.transform;
                }

                AddPart(rt, 0, Vector3.zero);                       // 蝶形底座
                AddPart(rt, 1, Vector3.zero);                       // 偏航围壳
                AddPart(rt, 2, src[2].localPosition);               // 液压活塞

                var gun = new GameObject("gun");
                gun.layer = 0;
                gun.transform.SetParent(rt, false);
                gun.transform.localPosition = new Vector3(0f, C.PlasmaPivotY, C.PlasmaPivotZ);
                Vector3 pivot = gun.transform.localPosition;
                AddPart(gun.transform, 3, src[3].localPosition - pivot);
                AddPart(gun.transform, 4, src[4].localPosition - pivot);

                _plasmaTpl = tpl;
                Plugin.Log.LogInfo("电浆炮模型: 原版 " + C.PlasmaModelName + " lod0 五件（底座/围壳/活塞/炮身×2），" +
                                   "缩放 " + C.PlasmaScale.ToString("0.00") + "（高 " +
                                   (8.32f * C.PlasmaScale).ToString("0.00") + "m、底径 " +
                                   (11.26f * C.PlasmaScale).ToString("0.00") + "m），" +
                                   "俯仰枢轴=(" + C.PlasmaPivotY + ", " + C.PlasmaPivotZ + ")，" +
                                   "贴图=原版 turret-plasma-lod0-a/n/s，shader=PBR Standard");
            }
            catch (System.Exception e)
            {
                _plasmaTpl = null;
                Plugin.Log.LogError("电浆炮模型构建失败（塔顶沿用激光塔）: " + e);
            }
        }

        private Rig CreateRig()
        {
            try
            {
                var root = new GameObject("hub-rig");
                root.layer = 0;

                var rig = new Rig { Root = root };
                int n = Mathf.Min(C.TurretCount, StationTurretGeometry.Slots.Length);
                rig.Turret = new Transform[n];
                rig.Angle = new float[n];
                rig.Speed = new float[n];
                rig.LaserId = new int[n * C.TurretMuzzles.Length];
                rig.FireCd = new float[n];
                rig.TargetEnemy = new int[n];
                rig.NoAmmo = new bool[n];
                rig.Gun = new Transform[n];
                rig.Space = new bool[n];
                rig.Candidates = new Tgt[C.TurretCount + 4];
                rig.GIdx = new int[C.TurretCount + 4];
                rig.AIdx = new int[C.TurretCount + 4];
                rig.Lock = new float[n];
                rig.Ignore = new int[8];
                rig.IgnoreT = new float[8];
                rig.ScanTimer = 0f;

                // 模板可能因为换星球 / 场景重载被销毁，这里补一次。
                if (_plasmaTpl == null)
                    BuildPlasmaTemplate();

                int plasmaCount = 0;
                for (int i = 0; i < n; i++)
                {
                    TurretSlot slot = StationTurretGeometry.Slots[i];
                    var go = new GameObject("turret-" + i);
                    go.layer = 0;
                    go.transform.SetParent(root.transform, false);
                    // 塔顶跟着 BodyScale 一起长；基座 4 座原样（ScaleBody 内部按 y 判断）。
                    go.transform.localPosition = C.ScaleBody(slot.Pivot);
                    go.transform.localRotation = Quaternion.Euler(0f, slot.Yaw, 0f);

                    // 【0.9.24】塔顶 18 座 = 原版磁化电浆炮（只打空中），
                    // 基座 4 座 = 原来的激光塔（只打地面）。
                    bool plasma = i < C.PlasmaTopCount && _plasmaTpl != null;
                    rig.Gun[i] = null;
                    rig.Space[i] = plasma;

                    if (plasma)
                    {
                        GameObject inst = Instantiate(_plasmaTpl, go.transform, false);
                        inst.name = "plasma";
                        inst.transform.localPosition = new Vector3(0f, C.PlasmaLift, 0f);
                        // 模型炮口朝 −Z，转 180° 让 +Z 成为正向，跟瞄准公式对齐。
                        inst.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                        inst.transform.localScale = Vector3.one * C.PlasmaScale;
                        inst.SetActive(true);
                        rig.Gun[i] = inst.transform.Find("gun");
                        plasmaCount++;
                    }
                    else
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = _turretMesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterials = _turretMats;
                        mr.shadowCastingMode = ShadowCastingMode.On;
                        // 【0.9.21】不接收阴影。塔顶 18 座正好在碟体的投影里、基座 4 座在裙边
                        // 投影里，一接收阴影就只剩环境光 —— 而环境光在没有反射探针的场景里
                        // 几乎为 0（原版建筑走 BatchRenderer，是游戏统一喂光照，不吃这一套）。
                        // 炮塔不接收阴影后永远按「太阳直射」着色，配合材质侧金属度归零
                        // （见 StationMesh.BrightenForTurret），实机不会再出现整片发黑。
                        mr.receiveShadows = false;
                        // 探针仍开：有探针时能拿到一点环境色，没有就自动退回环境光，不会报错。
                        mr.lightProbeUsage = LightProbeUsage.BlendProbes;
                        mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    }

                    rig.Turret[i] = go.transform;
                    rig.FireCd[i] = 0f;
                    rig.TargetEnemy[i] = -1;
                    // 每座转速略有差异：0.62 ~ 0.90 倍基准
                    rig.Speed[i] = C.TurretSweepSpeed * (0.62f + 0.28f * (((i * 37) % 11) / 11f));
                    rig.Angle[i] = 0f;
                }
                if (plasmaCount > 0)
                    Plugin.Log.LogInfo("塔顶武器: " + plasmaCount + " 座换成原版磁化电浆炮（只打空中目标），" +
                                       "基座 " + (n - plasmaCount) + " 座仍是高频激光塔（只打地面）");

                // 光柱本体：原版黑雾中继站自带的那段特效网格（StationBeam 负责搬过来）。
                // 不用 Graphics.DrawMesh，也不塞进建筑 prefab —— 普通 MeshRenderer ，
                // 和炮塔同一条渲染路径，材质用中继站自己的 Unlit Alpha（关掉 instancing）。
                if (StationBeam.Ensure())
                {
                    var bg = new GameObject("beam");
                    bg.layer = 0;
                    bg.transform.SetParent(root.transform, false);
                    bg.transform.localPosition = StationBeam.LocalPos;
                    bg.transform.localScale = Vector3.one * StationBeam.LocalScale;
                    bg.AddComponent<MeshFilter>().sharedMesh = StationBeam.BeamMesh;
                    var bmr = bg.AddComponent<MeshRenderer>();
                    bmr.sharedMaterial = StationBeam.BeamMaterial;
                    bmr.shadowCastingMode = ShadowCastingMode.Off;
                    bmr.receiveShadows = false;
                    bmr.lightProbeUsage = LightProbeUsage.Off;
                    bmr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    rig.Beam = bg;
                }

                // 光柱流光环：只做上下平移，挂在站根节点下（localPosition 相对地面基座）
                int nb = Mathf.Max(1, C.BeamRingCount);
                rig.BeamRing = new Transform[nb];
                for (int b = 0; b < nb; b++)
                {
                    var rg = new GameObject("beam-ring-" + b);
                    rg.layer = 0;
                    rg.transform.SetParent(root.transform, false);
                    rg.AddComponent<MeshFilter>().sharedMesh = _beamRingMesh;
                    var rmr = rg.AddComponent<MeshRenderer>();
                    rmr.sharedMaterials = _beamRingMats;
                    rmr.shadowCastingMode = ShadowCastingMode.Off;
                    rmr.receiveShadows = false;
                    rmr.lightProbeUsage = LightProbeUsage.Off;
                    rmr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    rig.BeamRing[b] = rg.transform;
                }

                // 4 条细传输带：底座 ⇄ 塔顶，除能量外还传「信息与待运物资」。
                // 网格与主光柱是同一段（中继站 eff），只是横向压窄、纵向按
                // SignalY0..SignalY1 重新落位 —— 这才是中继站那种「几条细带」的读感，
                // 自己拿棱柱拼出来的永远是一根柱子。
                if (StationBeam.Ready)
                {
                    int ns = Mathf.Max(1, C.SignalCount);
                    int per = Mathf.Max(1, Mathf.RoundToInt(C.CargoPerBand));
                    rig.Signal = new Transform[ns];
                    rig.Cargo = new Transform[ns * per];

                    float srcH = Mathf.Max(0.001f, StationBeam.SourceHeight);
                    float sy = (C.SignalY1 - C.SignalY0) / srcH;
                    float sxz = StationBeam.LocalScale * C.SignalWidthScale;
                    // 源网格底端在 SourceMinY，缩放后为 SourceMinY·sy；要落到 SignalY0 就补这个差
                    float baseY = C.SignalY0 - StationBeam.SourceMinY * sy;

                    for (int i = 0; i < ns; i++)
                    {
                        float a = Mathf.PI * 2f * i / ns;
                        var sg = new GameObject("signal-band-" + i);
                        sg.layer = 0;
                        sg.transform.SetParent(root.transform, false);
                        sg.transform.localPosition =
                            new Vector3(Mathf.Cos(a) * C.SignalR, baseY, Mathf.Sin(a) * C.SignalR);
                        sg.transform.localRotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);

                        var inner = new GameObject("mesh");
                        inner.layer = 0;
                        inner.transform.SetParent(sg.transform, false);
                        inner.transform.localScale = new Vector3(sxz, sy, sxz);
                        inner.AddComponent<MeshFilter>().sharedMesh = StationBeam.BeamMesh;
                        var bmr = inner.AddComponent<MeshRenderer>();
                        Material sm = StationBeam.SignalMaterial(i);
                        bmr.sharedMaterial = sm != null ? sm : StationBeam.BeamMaterial;
                        bmr.shadowCastingMode = ShadowCastingMode.Off;
                        bmr.receiveShadows = false;
                        bmr.lightProbeUsage = LightProbeUsage.Off;
                        bmr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                        rig.Signal[i] = sg.transform;
                    }

                    // 货箱光点：每条带 per 个，沿带子往上走 —— 这就是「包含物资」的那一项
                    for (int k = 0; k < rig.Cargo.Length; k++)
                    {
                        var cg = new GameObject("cargo-" + k);
                        cg.layer = 0;
                        cg.transform.SetParent(root.transform, false);
                        cg.AddComponent<MeshFilter>().sharedMesh = StationMesh.GetCargoMesh();
                        var cmr = cg.AddComponent<MeshRenderer>();
                        cmr.sharedMaterials = StationMesh.CargoMaterials();
                        cmr.shadowCastingMode = ShadowCastingMode.Off;
                        cmr.receiveShadows = false;
                        cmr.lightProbeUsage = LightProbeUsage.Off;
                        cmr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                        rig.Cargo[k] = cg.transform;
                    }

                    Plugin.Log.LogInfo("传输带: " + ns + " 条（半径 " + C.SignalR + "m，" +
                                       C.SignalY0 + "~" + C.SignalY1.ToString("0.0") + "m，" +
                                       "每条宽约 " + (5.0f * sxz).ToString("0.00") + "m）+ 货箱光点 " +
                                       rig.Cargo.Length + " 个");
                }

                var sh = new GameObject("shield");
                sh.layer = 0;
                sh.transform.SetParent(root.transform, false);
                sh.AddComponent<MeshFilter>().sharedMesh = _shieldMesh;
                var smr = sh.AddComponent<MeshRenderer>();
                smr.sharedMaterials = _shieldMats;
                smr.shadowCastingMode = ShadowCastingMode.Off;
                smr.receiveShadows = false;
                smr.lightProbeUsage = LightProbeUsage.Off;
                smr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                rig.Shield = smr;

                return rig;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("创建炮塔挂点失败: " + e);
                return null;
            }
        }

        // ================= 炮塔实战 =================

        /// <summary>空闲扫掠（原行为）：绕自身 Y 轴慢慢转。</summary>
        private static void SweepOne(Rig rig, int t, float dt)
        {
            rig.Angle[t] += rig.Speed[t] * dt;
            float yaw = StationTurretGeometry.Slots[t].Yaw + rig.Angle[t];
            rig.Turret[t].localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        /// <summary>
        /// 目标扫描：每 0.35 s 扫一遍 <c>factory.enemyPool</c>，只留最近的 N 个。
        ///
        /// 【0.9.24】射程按「相对高度」分成两档，和武器分家对齐：
        ///   · 高出站体底座 <see cref="C.PlasmaAirHeight"/> 的算空中目标，归塔顶电浆炮，
        ///     射程 <see cref="C.TurretRangeSpace"/>；
        ///   · 贴地的归基座激光塔，射程 <see cref="C.TurretRange"/>。
        ///
        /// 为什么必须节流：enemyPool 在中等规模的黑雾战里就有上千个元素，
        /// 22 座塔每帧各扫一遍 = 每帧两万次距离计算，帧率会掉。
        ///
        /// 【0.9.24】同时补上原版炮塔的「目标合法性」判定：
        /// <c>TurretComponent</c> 选目标时要求 <c>id == index &amp;&amp; !isInvincible</c>（见
        /// TurretComponent.cs 的 Search 分支），我们之前只看 id，于是**无敌目标**
        /// （黑雾正在孵化/生长的单位、剧情战里的单位）会被一直锁着打 ——
        /// 打不动、又永远不消失，实机上就是「敌人早没了，炮塔还在持续输出」。
        /// </summary>
        private static int Scan(PlanetFactory factory, Vector3 origin, Tgt[] buf)
        {
            if (buf == null || factory.enemyPool == null)
                return 0;

            EnemyData[] pool = factory.enemyPool;
            int n = 0;
            int cursor = Mathf.Min(pool.Length, factory.enemyCursor);
            float rg = C.TurretRange;
            float rgs = C.TurretRangeSpace;

            for (int i = 1; i < cursor; i++)
            {
                EnemyData e = pool[i];
                if (e.id != i || e.id <= 0 || e.isInvincible)
                    continue;

                Vector3 p = (Vector3)e.pos;
                // 星区坐标/异常坐标直接排除（这里只处理行星本地目标）
                if (Mathf.Abs(p.x) > 6000f || Mathf.Abs(p.z) > 6000f || Mathf.Abs(p.y) > 6000f)
                    continue;

                float height = p.y - origin.y;
                // 「空中目标」的判定和原版炮塔同一口径：TurretComponent.IsAirEnemy()
                // 就是 protoId == 8129 || 8130（黑雾的飞行单位）；另外补一条高度兜底，
                // 免得别的 mod 的飞行单位漏掉。
                bool air = e.protoId == 8129 || e.protoId == 8130 || height >= C.PlasmaAirHeight;
                float r = air ? rgs : rg;
                float d2 = (p - origin).sqrMagnitude;
                if (d2 > r * r)
                    continue;

                var t = new Tgt { Id = i, Pos = p, Air = air, Height = height, Dist2 = d2 };

                if (n < buf.Length)
                {
                    buf[n++] = t;
                }
                else
                {
                    // 满了就顶掉最远的一个
                    int worst = 0;
                    for (int k = 1; k < n; k++)
                        if (buf[k].Dist2 > buf[worst].Dist2)
                            worst = k;
                    if (d2 < buf[worst].Dist2)
                        buf[worst] = t;
                }
            }
            return n;
        }

        /// <summary>目标是否还活着（每帧验一次，不用等下一次 Scan）。</summary>
        private static bool Alive(PlanetFactory factory, int id)
        {
            if (factory == null || factory.enemyPool == null || id <= 0)
                return false;
            EnemyData[] pool = factory.enemyPool;
            if (id >= pool.Length)
                return false;
            return pool[id].id == id && !pool[id].isInvincible;
        }

        /// <summary>把一个「打不死」的目标记进黑名单（环形 8 槽，到点自动失效）。</summary>
        private static void IgnoreAdd(Rig rig, int id)
        {
            if (id <= 0 || rig.Ignore == null)
                return;
            int slot = id % rig.Ignore.Length;
            rig.Ignore[slot] = id;
            rig.IgnoreT[slot] = Time.time + C.TurretGiveUpHold;
        }

        private static bool Ignored(Rig rig, int id)
        {
            if (rig.Ignore == null || id <= 0)
                return false;
            int slot = id % rig.Ignore.Length;
            return rig.Ignore[slot] == id && Time.time < rig.IgnoreT[slot];
        }

        /// <summary>炮口世界坐标：电浆炮取枪身上的炮口点，激光塔取中管管口。</summary>
        private static Vector3 Muzzle(Rig rig, int t)
        {
            Transform gun = rig.Gun != null ? rig.Gun[t] : null;
            if (gun != null)
                return gun.TransformPoint(new Vector3(0f, C.PlasmaMuzzleY, C.PlasmaMuzzleZ));
            return rig.Turret[t].TransformPoint(new Vector3(0f, C.TurretMuzzleY, C.TurretMuzzleZ));
        }

        private void Combat(PlanetFactory factory, StationComponent sc, Rig rig, float dt)
        {
            if (factory == null || factory.enemyPool == null || factory.skillSystem == null ||
                rig.Turret == null || rig.LaserId == null)
            {
                SweepAll(rig, dt);
                return;
            }

            _ss = factory.skillSystem;

            // 【0.9.29】塔顶靠底座送电：没电就不打。
            // 之前 Combat 完全不看 Powered —— 断电时炮塔照样开火、还会把站内电池抽干，
            // 玩家看到的是「底座明明在打洞发电，塔顶却在打空包弹」，和「没通电」的直觉冲突。
            // 现在断电只做扫掠（待机转塔），射线一并收掉。
            // 判定走 Working：钻孔模块被关掉时会退回「只看电网」，与采矿/护盾同一个口。
            if (!StationDrill.Working(factory, sc))
            {
                SweepAll(rig, dt);
                LogTowerPower(factory, sc);
                return;
            }
            LogTowerPower(factory, sc);

            rig.ScanTimer -= dt;
            if (rig.ScanTimer <= 0f)
            {
                rig.ScanTimer = 0.35f;
                rig.CandCount = Scan(factory, rig.Root.transform.position, rig.Candidates);
                // 按「空中 / 地面」拆成两张下标表，各打各的，互不抢目标；
                // 顺手把「打不动被拉黑」的目标滤掉。
                rig.GN = 0;
                rig.AN = 0;
                for (int k = 0; k < rig.CandCount; k++)
                {
                    if (Ignored(rig, rig.Candidates[k].Id))
                        continue;
                    if (rig.Candidates[k].Air)
                        rig.AIdx[rig.AN++] = k;
                    else
                        rig.GIdx[rig.GN++] = k;
                }
            }

            for (int t = 0; t < rig.Turret.Length; t++)
            {
                bool air = rig.Space[t];
                int count = air ? rig.AN : rig.GN;
                if (count <= 0)
                {
                    LaserStop(rig, t);
                    SweepOne(rig, t, dt);
                    rig.TargetEnemy[t] = -1;
                    continue;
                }

                // 轮着打：22 座塔不会全扑同一个目标（实机上那样就是一圈光柱聚成一个点）。
                int ci = air ? rig.AIdx[t % count] : rig.GIdx[t % count];
                Tgt tg = rig.Candidates[ci];

                // 【0.9.24】每帧验一次目标：死了 / 变无敌 / 没了，立刻收射线去扫掠，
                // 不再等下一次 Scan（0.35 s）—— 实机上那 0.35 s 就是「敌人没了还在打」。
                if (!Alive(factory, tg.Id))
                {
                    rig.TargetEnemy[t] = -1;
                    LaserStop(rig, t);           // 目标已经出池，射线也要收（Stop 会让池自己淡出回收）
                    SweepOne(rig, t, dt);
                    rig.ScanTimer = 0f;          // 下一帧立刻重扫
                    continue;
                }

                if (rig.TargetEnemy[t] != tg.Id)
                {
                    rig.TargetEnemy[t] = tg.Id;
                    rig.Lock[t] = 0f;            // 换目标就重新计时
                }
                else
                {
                    // 同一目标连续锁定这么久还没被击毁 → 大概率打不动（无敌/护盾/别的 mod 的怪），
                    // 拉黑 C.TurretGiveUpHold 秒再挑别的，别死磕。
                    rig.Lock[t] += dt;
                    if (rig.Lock[t] > C.TurretGiveUpSeconds)
                    {
                        rig.Lock[t] = 0f;
                        IgnoreAdd(rig, tg.Id);
                        LaserStop(rig, t);
                        rig.TargetEnemy[t] = -1;
                        rig.ScanTimer = 0f;
                        SweepOne(rig, t, dt);
                        if (!_giveUpLogged)
                        {
                            _giveUpLogged = true;
                            Plugin.Log.LogInfo("炮塔目标放弃: enemyId=" + tg.Id +
                                               " 连续锁定 " + C.TurretGiveUpSeconds.ToString("0") +
                                               "s 仍未击毁（判定为打不动的目标），拉黑 " +
                                               C.TurretGiveUpHold.ToString("0") + "s 后重新选目标");
                        }
                        continue;
                    }
                }

                Vector3 lp = rig.Root.transform.InverseTransformPoint(tg.Pos);
                Vector3 d = lp - rig.Turret[t].localPosition;
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
                float pitch = -Mathf.Atan2(d.y, Mathf.Max(0.001f, flat)) * Mathf.Rad2Deg;
                Quaternion want = Quaternion.Euler(pitch, yaw, 0f);

                float turn = C.TurretTurnSpeed * 4f * dt;
                rig.Turret[t].localRotation = Quaternion.RotateTowards(
                    rig.Turret[t].localRotation, want, turn);

                // 电浆炮只有枪身俯仰（底座/围壳不动），而且模型炮口朝 −Z、
                // 外层又转了 180° → 俯仰角取反才是「抬高」。
                Transform gun = rig.Gun[t];
                Quaternion gunWant = Quaternion.Euler(-pitch, 0f, 0f);
                if (gun != null)
                    gun.localRotation = Quaternion.RotateTowards(gun.localRotation, gunWant, turn);

                bool aimed = Quaternion.Angle(rig.Turret[t].localRotation, want) <= 9f &&
                             (gun == null || Quaternion.Angle(gun.localRotation, gunWant) <= 14f);
                if (!aimed)
                {
                    LaserStop(rig, t);              // 还没转到位，射线先收掉
                    continue;
                }

                // 【0.9.23】激光塔：射线直接写进原版高频激光塔的连续激光池。
                // 炮口在转塔局部坐标里固定（三根炮管各自的管口），端点每帧跟着目标走，
                // 原版系统自己做网格朝向、流光动画与命中火花。
                // 【0.9.24】电浆炮不走这条路 —— 它开的是弹丸（见 PlasmaShot）。
                // 【0.9.25】三根炮管 → 三条射线（C.TurretMuzzles 里的三个管口点）。
                if (gun == null)
                {
                    try
                    {
                        for (int b = 0; b < C.TurretMuzzles.Length; b++)
                            LaserAim(factory, rig, t, b, tg.Pos);
                    }
                    catch (Exception e)
                    {
                        // 原版池的接口是 internal 实现，跨版本可能改；出错只停射线，
                        // 不影响扣血与扫掠（否则一发异常会把整座站的战斗打断）。
                        if (!_laserErr)
                        {
                            _laserErr = true;
                            Plugin.Log.LogError("原版激光射线更新失败（射线停用，伤害照常）: " + e);
                        }
                    }
                }

                rig.FireCd[t] -= dt;
                if (rig.FireCd[t] > 0f)
                    continue;

                rig.FireCd[t] = air ? C.TurretFireIntervalSpace : C.TurretFireInterval;
                // 【0.9.38】伤害倍率 = 原版「能量武器伤害」升级（每级 +10%）。
                // 口径与 TurretComponent.Shoot_Laser / Shoot_Plasma 完全一致：
                // 原版就是 `基础伤害 × history.energyDamageScale`，所以激光塔和电浆炮
                // 在原版里本来就同吃这一条，本站照抄（见 MiningTech.EnergyWeaponMul）。
                int dmg = Mathf.RoundToInt(
                    (air ? C.TurretDamageSpace : C.TurretDamage) * MiningTech.EnergyWeaponMul);

                if (air)
                {
                    // 电浆炮要有「弹药」才打得出去：每发扣 C.PlasmaShotEnergy 的站内电量。
                    // 电量不够就这一发不打（连伤害一起跳过），面板的电池读数就是玩家要的弹仓。
                    if (!PlasmaShot(factory, sc, rig, t, tg.Pos))
                        continue;
                    LogPlasmaDamage(dmg);
                }
                Fire(factory, tg.Id, dmg);
            }
        }

        private void SweepAll(Rig rig, float dt)
        {
            if (rig.Turret == null)
                return;
            for (int t = 0; t < rig.Turret.Length; t++)
            {
                LaserStop(rig, t);            // 停火时把原版射线收掉
                SweepOne(rig, t, dt);
            }
        }

        /// <summary>
        /// 实际扣血。走原版 <c>SkillSystem.DamageGroundObjectByLocalCaster</c> ——
        /// 这是原版高频激光塔打地面敌人用的同一个入口：内部处理黑雾基地等级减伤、
        /// combatStats 血量、lastCaster 仇恨，死亡由战斗系统下一 tick 收尾。
        /// 自己动 CombatStat.hp 会漏掉死亡流程，尸体会留在场上。
        /// </summary>
        private void Fire(PlanetFactory factory, int enemyId, int dmg)
        {
            try
            {
                SkillTargetLocal target = new SkillTargetLocal();
                target.type = ETargetType.Enemy;
                target.id = enemyId;

                SkillTargetLocal caster = new SkillTargetLocal();
                caster.type = ETargetType.Entity;
                caster.id = 0;

                factory.skillSystem.DamageGroundObjectByLocalCaster(factory, dmg, 1, ref target, ref caster);
            }
            catch (Exception e)
            {
                _combatFailed = true;
                Plugin.Log.LogError("炮塔开火失败（已停用实战逻辑，炮塔恢复扫掠）: " + e);
            }
        }

        // ================= 塔顶供电 =================

        /// <summary>
        /// 【0.9.29】把「底座 → 塔顶」这条电力链路打到日志里，只在功率变化超过 0.1 MW 时输出。
        ///
        /// 玩家看到的是「底座在打洞发电，塔顶却像没通电」，而面板原先只写地热/电网，
        /// 没有任何一行说明「这些电塔顶到底用不用得上」。这里把三个数放在一起：
        /// 地热发多少、电网给多少、满速开火需要多少 —— 缺多少一眼就能算出来。
        /// </summary>
        private void LogTowerPower(PlanetFactory factory, StationComponent sc)
        {
            if (sc == null)
                return;

            bool on = StationDrill.Powered(sc.entityId);
            bool grid = StationDrill.GridOn(sc.entityId);
            float geoMw = StationDrill.GeoPowerMw(sc.entityId);
            float gridMw = grid ? StationDrill.GridDrawMw(factory, sc) : 0f;
            // 【0.9.30】第 10 格核电也是「送到塔顶的电」的一部分 —— 不写进来，
            // 玩家会以为核电白烧了（面板和这里必须是同一份账）。
            float nukeMw = StationNuclear.OutputMw(sc);
            float total = geoMw + gridMw + nukeMw;

            // 状态位也进比较：断电 / 恢复供电要打，不只看功率数字。
            float key = (on ? 1f : 0f) * 10000f + total;
            if (Mathf.Abs(key - _towerKey) < 0.1f)
                return;
            _towerKey = key;

            float need = C.PlasmaTopCount / Mathf.Max(0.05f, C.TurretFireIntervalSpace) *
                         C.PlasmaShotEnergy / 1e6f;      // 满速需要多少 MW
            string state = !on
                ? "断电（塔顶停火）"
                : (grid ? "电网+地热" : (nukeMw > 0.01f ? "核电" : "地热自供电"));
            Plugin.Log.LogInfo("塔顶供电: " + state +
                               " · 地热 " + geoMw.ToString("0.00") + "MW" +
                               (grid ? " + 电网 " + gridMw.ToString("0.0") + "MW" : "") +
                               (nukeMw > 0.01f ? " + 核电 " + nukeMw.ToString("0.0") + "MW" : "") +
                               " = " + total.ToString("0.0") + "MW" +
                               " · 电浆弹 " + (C.PlasmaShotEnergy / 1e6f).ToString("0.##") +
                               "MJ/发，18 座满速需 " + need.ToString("0.0") + "MW" +
                               (on
                                   ? (total >= need
                                       ? "（够用，满火力）"
                                       : "（不足，按功率降速 → 约 " +
                                         (total * 1e6f / C.PlasmaShotEnergy).ToString("0.0") + " 发/s）")
                                   : ""));
        }

        // ================= 原版「磁化电浆炮」的弹丸 =================

        /// <summary>
        /// 【0.9.26 / 0.9.38】电浆炮的伤害倍率 = 原版「能量武器伤害」升级（每级 +10%），
        /// 倍率一变就打一行日志。
        ///
        /// 数值上它和激光塔走的是同一个 <see cref="MiningTech.EnergyWeaponMul"/>（见 Combat
        /// 里的 dmg 计算），打日志只是让玩家能在 BepInEx 里核对「升了一级，电浆确实涨了」，
        /// 面板那行 `电浆1170` 也是同一个数。
        /// </summary>
        private void LogPlasmaDamage(int dmg)
        {
            float m = MiningTech.EnergyWeaponMul;
            if (Mathf.Abs(m - _plasmaMul) < 0.001f)
                return;
            _plasmaMul = m;
            Plugin.Log.LogInfo("电浆炮: 能量武器 ×" + m.ToString("0.00") +
                               " → 塔顶单发 " + dmg + " 伤害（与激光塔同吃原版「能量武器伤害」升级，每级 +10%）");
        }

        /// <summary>
        /// 塔顶电浆炮开火：先扣站内电量（这就是它的「弹药」），再往
        /// <c>skillSystem.turretCannonades</c> 池里挂一发原版电浆弹，
        /// 弹丸从炮口飞到目标点、命中时炸出原版粒子（`hitEffects[10]` + 次级 `[11]`）。
        ///
        /// 【0.9.25】「怎么上子弹」的答案就是这里：原版磁化电浆炮本来就不吃实体弹药
        /// （DSP 全部炮塔都是能量武器），所以本站也不让玩家去找弹链，而是**真的吃电** ——
        /// 每发从 <c>StationComponent.energy</c>（和货船起飞同一个电池）里扣
        /// <see cref="C.PlasmaShotEnergy"/>。电量来源只有两条：接着电网充电、或建在地热上。
        /// 电量不足时这一发直接不打（返回 false，调用方连伤害一起跳过），
        /// 实机上就是「没子弹了」的表现。
        ///
        /// 弹丸本身只借表现：<c>damage = 0</c>、<c>caster.id = 0</c>。
        /// 为什么：这个池在弹丸落点会遍历半径内的敌人逐个
        /// <c>DamageGroundObjectByLocalCaster</c>；如果给真伤害就是**双重扣血**
        /// （我们的 <see cref="Fire"/> 已经结算过一次），给 caster 就是每发都把
        /// lastCaster 抢一遍（无限仇恨）。清空之后它只剩爆炸特效。
        /// </summary>
        private bool PlasmaShot(PlanetFactory factory, StationComponent sc, Rig rig, int t, Vector3 world)
        {
            SkillSystem ss = factory.skillSystem;
            if (ss == null || sc == null)
                return false;
            DataPool<LocalCannonade> pool = ss.turretCannonades;
            if (pool == null || pool.buffer == null)
                return false;

            // ---- 弹药 = 站内电量 ----
            if (sc.energy < C.PlasmaShotEnergy)
            {
                if (rig.NoAmmo != null && t < rig.NoAmmo.Length && !rig.NoAmmo[t])
                {
                    rig.NoAmmo[t] = true;
                    if (!_noAmmoLogged)
                    {
                        _noAmmoLogged = true;
                        Plugin.Log.LogInfo("电浆炮停火: 站 id=" + sc.id + " 电量不足（" + sc.energy +
                                           " J < 每发 " + C.PlasmaShotEnergy + " J）——" +
                                           "接电网 / 建在地热上 / 往左列第 " + (C.FuelSlot + 1) +
                                           " 格补氘核燃料棒（核电 " +
                                           C.FusionPowerMw.ToString("0.#") + " MW）即可");
                    }
                }
                return false;
            }
            if (rig.NoAmmo != null && t < rig.NoAmmo.Length)
                rig.NoAmmo[t] = false;
            sc.energy -= C.PlasmaShotEnergy;
            if (sc.energy < 0)
                sc.energy = 0;

            try
            {
                Vector3 from = Muzzle(rig, t);
                int id;
                using (pool.Add(out id))
                {
                    ref LocalCannonade c = ref pool.buffer[id];
                    c.astroId = factory.planet.astroId;
                    c.hitIndex = 10;          // 原版电浆弹的命中火花组
                    c.minorHitIndex = 11;
                    c.speed = 4f;
                    c.targetPos = world;
                    c.distance = Vector3.Distance(from, world);
                    c.damage = 0;             // 只做表现（伤害在 Fire() 里）
                    // 【关键】半径必须给 0：`LocalCannonade.TickSkillLogic` 在落点会遍历
                    // 半径内的敌人逐个 DamageGroundObjectByLocalCaster，而那个函数对
                    // 「挂在黑雾基地下的敌人」有一条 `damage = Max(damage, 100/slice)`
                    // 的下限保护 —— 也就是说 damage=0 传进去会被抬成 **100 真伤害**，
                    // 等于我们打一发扣两发。半径 0 之后整个循环一次都不进，
                    // 命中特效（hitIndex）在它之前就已经播了，画面不受影响。
                    c.blastRadius0 = 0f;
                    c.blastRadius1 = 0f;
                    c.blastFalloff = 1f;
                    c.mask = ETargetTypeMask.Enemy;
                    c.caster.id = 0;
                    c.caster.type = ETargetType.None;
                    c.life = 3;
                }
                if (!_shellLogged)
                {
                    _shellLogged = true;
                    Plugin.Log.LogInfo("电浆炮弹丸: 已接入原版 turretCannonades" +
                                       "（网格/材质/命中爆炸全部由 SkillSystem 驱动，damage=0 只做表现；" +
                                       "弹药=站内电量，每发 " + (C.PlasmaShotEnergy / 1_000_000) + " MJ）");
                }
                return true;
            }
            catch (Exception e)
            {
                if (!_shellErr)
                {
                    _shellErr = true;
                    Plugin.Log.LogError("电浆炮弹丸发射失败（弹丸停用，伤害照常）: " + e);
                }
                return false;
            }
        }

        // ================= 原版高频激光塔的连续射线 =================

        /// <summary>
        /// 让<b>某座炮塔</b>对着 world 射出原版高频激光塔那条射线。
        ///
        /// 做法完全照抄原版 <c>TurretComponent</c> 的激光分支：往工厂的
        /// <c>skillSystem.turretLaserContinuous</c> 池里挂一条 <see cref="LocalLaserContinuous"/>，
        /// 之后每帧只更新 <c>beginPos</c> / <c>endPos</c>。光束的网格、材质、
        /// 沿光束流动的流光、命中点的火花粒子，全部由原版 <c>SkillSystem.TickSkillLogic</c>
        /// 驱动 —— 连材质的实例（`turret-laser-continuous`）都是游戏自己 Instantiate 的，
        /// 所以外观与真正的高频激光塔 100% 一致，不存在「自己拼特效拼出黑块」的可能。
        ///
        /// 为什么 <c>target.id</c> 留 0：这个池每帧会拿 target 去
        /// <c>DamageGroundObjectByLocalCaster</c> 结算伤害。我们的伤害仍然走
        /// <see cref="Fire"/>（160 / 0.22 s，数值在 C.cs 里可调），
        /// 这里只借它的表现，所以把 target 清空让结算直接返回 —— 否则会双重扣血、
        /// 而且每帧都会给敌人刷 lastCaster。
        /// </summary>
        private void LaserAim(PlanetFactory factory, Rig rig, int t, int barrel, Vector3 world)
        {
            SkillSystem ss = factory.skillSystem;
            if (ss == null)
                return;
            DataPoolRenderer<LocalLaserContinuous> pool = ss.turretLaserContinuous;
            if (pool == null || pool.buffer == null)
                return;

            // 【0.9.25】三根炮管各自的管口（C.TurretMuzzles，模型原始单位）：
            // 中管最高、左右两管略低且外撇 —— 三条射线在远处汇聚到同一个目标点，
            // 近处看得出是「三管齐射」，这正是玩家要的那三条激活线。
            int idx = t * C.TurretMuzzles.Length + barrel;
            if (rig.LaserId == null || idx < 0 || idx >= rig.LaserId.Length)
                return;
            Vector3 muzzle = C.TurretMuzzles[barrel];
            Vector3 from = rig.Turret[t].TransformPoint(muzzle);

            int id = rig.LaserId[idx];
            // 句柄有效性只在锁外粗判一次（id 落在池内 + 槽位没被别人回收）。
            // 注意：这里 **不能** 在持锁时再 pool.Add —— 同一个 LockScopeHandle
            // 上再进一次 upgradeable-read 会和手里的 read 锁打架。
            if (id <= 0 || id >= pool.cursor || pool.buffer[id].id != id)
            {
                LaserStopId(id);
                rig.LaserId[idx] = 0;
                id = LaserAdd(pool, factory, from, world);
                rig.LaserId[idx] = id;
                if (id > 0 && !_laserLogged)
                {
                    _laserLogged = true;
                    Plugin.Log.LogInfo("激光射线: 已改用原版高频激光塔的连续激光" +
                                       "（skillSystem.turretLaserContinuous，" +
                                       "每座塔三根炮管各一条，共 " + C.TurretMuzzles.Length +
                                       " 条/座；网格/材质/流光/命中火花全部由原版驱动）");
                }
                return;
            }

            using (pool.GetScopeLock())
            {
                ref LocalLaserContinuous l = ref pool.buffer[id];
                if (l.id != id)
                {
                    // 刚好被系统回收了，下一帧重挂
                    rig.LaserId[idx] = 0;
                    return;
                }
                l.astroId = factory.planet.astroId;
                l.hitIndex = 4;                  // 原版炮塔激光用的命中火花组
                l.beginPos = from;
                l.endPos = world;
            }
        }

        /// <summary>新挂一条连续激光，返回池句柄（0 = 失败）。</summary>
        private static int LaserAdd(DataPoolRenderer<LocalLaserContinuous> pool,
                                    PlanetFactory factory, Vector3 from, Vector3 to)
        {
            try
            {
                int id;
                using (pool.Add(out id))
                {
                    ref LocalLaserContinuous l = ref pool.buffer[id];
                    l.Start();                   // fade = 0.01，开始淡入
                    l.astroId = factory.planet.astroId;
                    l.hitIndex = 4;
                    l.beginPos = from;
                    l.endPos = to;
                    l.finalPos = to;
                    l.damage = 0;                // 只做表现，伤害在 Fire() 里
                    l.damageScale = 1f;
                    l.mask = ETargetTypeMask.Enemy;
                    l.target.id = 0;
                    l.target.type = ETargetType.None;
                    l.caster.id = 0;
                    l.caster.type = ETargetType.None;
                }
                return id;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("原版激光射线挂载失败: " + e);
                return 0;
            }
        }

        /// <summary>
        /// 收掉某座炮塔的射线（0.9.25 起一座塔有三条，一起收）。<c>Stop</c> 只是把 fade 置 -1，
        /// 池自己会在下一帧按 fade 归零回收 —— 千万别手动 Remove，
        /// 会和系统的回收撞在一起。
        /// </summary>
        private void LaserStop(Rig rig, int t)
        {
            if (rig == null || rig.LaserId == null || t < 0)
                return;
            int m = C.TurretMuzzles.Length;
            for (int b = 0; b < m; b++)
            {
                int idx = t * m + b;
                if (idx < 0 || idx >= rig.LaserId.Length)
                    continue;
                int id = rig.LaserId[idx];
                rig.LaserId[idx] = 0;
                LaserStopId(id);
            }
        }

        /// <summary>按句柄收掉一条射线（站被拆 / 换星球时用，没有 rig 可查）。</summary>
        private void LaserStopId(int id)
        {
            SkillSystem ss = _ss;
            if (id <= 0 || ss == null)
                return;
            DataPoolRenderer<LocalLaserContinuous> pool = ss.turretLaserContinuous;
            if (pool == null || pool.buffer == null || id >= pool.cursor)
                return;
            using (pool.GetScopeLock())
            {
                ref LocalLaserContinuous l = ref pool.buffer[id];
                if (l.id == id)
                    l.Stop(ss);
            }
        }


        private void CleanupUnseen()
        {
            List<int> dead = null;
            foreach (var kv in _rigs)
            {
                if (kv.Value.Seen)
                    continue;
                if (dead == null)
                    dead = new List<int>();
                dead.Add(kv.Key);
            }
            if (dead == null)
                return;
            for (int i = 0; i < dead.Count; i++)
            {
                Rig r = _rigs[dead[i]];
                // 站被拆了：先把还挂着的原版射线收掉，否则光束会留在原地不消失
                // （池里那条的 fade 一直停在 1，永远不回收）。
                LaserStopAll(r);
                if (r.Root != null)
                    Destroy(r.Root);
                StationMiner.Forget(dead[i]);
                StationDrill.Forget(dead[i]);
                _rigs.Remove(dead[i]);
            }
        }

        private void ClearAll()
        {
            foreach (var kv in _rigs)
            {
                LaserStopAll(kv.Value);
                if (kv.Value.Root != null)
                    Destroy(kv.Value.Root);
            }
            _rigs.Clear();
        }

        private void LaserStopAll(Rig rig)
        {
            if (rig == null || rig.LaserId == null)
                return;
            for (int t = 0; t < rig.LaserId.Length; t++)
                LaserStop(rig, t);
        }

        private void OnDestroy()
        {
            ClearAll();
        }
    }
}
