using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LogisticsHub
{
    /// <summary>
    /// 运行时构造物流站 prefab 与 PrefabDesc（不走 AssetBundle）。
    ///
    /// 物流站的全部行为都由 PrefabDesc 上的 station* 字段驱动（见 StationComponent.Init）：
    ///   stationMaxShipCount  → workShipDatas / shipDiskPos / shipRenderers 的长度，也就是泊位数
    ///   stationMaxItemKinds  → storage 槽数（原版 needs 只喂 0..4，所以保持 5）
    ///   stationMaxItemCount  → 每格容量（+ 科技附加仓储）
    ///   stationMaxEnergyAcc  → 站内能量池上限（每艘船起飞 6,000,000，50 艘要留足池子）
    ///   stationDronePos      → 物流机停靠点
    ///   stationShipPos       → 飞船盘中心（多层泊位由此展开）
    ///   isStellarStation     → 星际站：才允许装翘曲器、才参与跨星系运输
    ///   portPoses.Length     → 传送带接口数（= StationComponent.slots.Length）
    /// </summary>
    internal static class StationPrefab
    {
        private static GameObject _prefab;
        private static PrefabDesc _desc;

        /// <summary>材质模板兜底 prefab（原版能量枢纽，材质语义最接近「带自发光的机械体」）。</summary>
        public const string FallbackPrefabPath = "Entities/Prefabs/energy-exchanger";

        public static PrefabDesc GetDesc()
        {
            if (_desc != null)
            {
                if (!StationMesh.HasTemplate)
                    TryRetryMaterials();
                return _desc;
            }

            Mesh mesh = StationMesh.Get();
            Material[] mats = StationMesh.BuildMaterials();

            var root = new GameObject("logistics-hub");
            root.SetActive(false);

            var mf = root.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = root.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            BuildColliders(root);

            _prefab = root;
            _desc = new PrefabDesc(C.ModelId, root);

            // ---- 渲染数据（运行时 prefab 读不到 LODModelDesc，手工补齐）----
            _desc.lodCount = 1;
            _desc.lodMeshes = new Mesh[1] { mesh };
            _desc.lodMaterials = new Material[1][] { mats };
            _desc.lodBlueprintMaterials = new Material[1][] { mats };
            _desc.lodSubmeshIgnores = new bool[1][] { new bool[mats.Length] };
            _desc.lodVertas = new VertaBuffer[1];
            // 必须给满 4 个：ObjectRenderer/DynamicRenderer 无条件读 [0..3]
            _desc.lodDistances = new float[4] { 5000f, 5000f, 5000f, 5000f };
            // cullingHeight 必须覆盖到塔顶（300 m），否则离远了整站会被剔除
            _desc.cullingHeight = C.TotalHeight + 2f;
            _desc.startInstCapacity = 16;
            _desc.batchCapacity = 2048;
            _desc.castShadow = 99;
            _desc.recvShadow = 99;

            if (_desc.mesh == null) _desc.mesh = mesh;
            if (_desc.materials == null || _desc.materials.Length == 0) _desc.materials = mats;

            // ---- 物流站本体 ----
            _desc.isStation = true;
            _desc.isStellarStation = true;          // 星际级：允许翘曲器、跨星系运输
            _desc.isCollectStation = false;
            _desc.isVeinCollector = false;
            // 照黑雾中继站的写法打标记：PrefabDesc.ReadPrefab 只要看到 prefab 上挂着
            // DFRelayDesc 就会置 isDFRelay = true（见 PrefabDesc.cs:1225）。这里直接置位，
            // 与中继站同一个标记位。行星建筑不会走 SpaceSector.CreateEnemyLogicComponents，
            // 所以它只是个标记，不会给自己找个巢穴宿主。
            // 光柱本体由 StationBeam 从模型 288（SectorModel.kRelayModelIndex）里直接搬网格。
            _desc.isDFRelay = true;
            _desc.stationMaxItemCount = C.MaxItemCount;
            _desc.stationMaxItemKinds = C.MaxItemKinds;
            _desc.stationMaxDroneCount = C.MaxDrones;
            _desc.stationMaxShipCount = C.MaxShips; // 50 艘 —— 上限受 idleShipIndices(ulong) 约束，最大 64
            _desc.stationMaxEnergyAcc = C.MaxEnergyAcc;
            _desc.stationDronePos = new Vector3(0f, C.DroneDockY, 0f);
            _desc.stationShipPos = new Vector3(0f, C.ShipDockY, 0f);

            // ---- 电网：耗电组件必须有，否则 StationComponent.pcId=0，站内能量永远是 0 ----
            _desc.isPowerConsumer = true;
            _desc.workEnergyPerTick = C.WorkEnergyPerTick;
            _desc.idleEnergyPerTick = C.IdleEnergyPerTick;
            _desc.isPowerNode = false;              // 不做输电节点，只作为电网负载

            // ---- 建造占地 ----
            // roughRadius/roughHeight 会被 PrefabDesc 构造函数按碰撞盒重算覆盖，这里只是兜底。
            _desc.roughRadius = C.BaseRadius;
            _desc.roughHeight = C.Height;
            _desc.roughWidth = C.BaseRadius * 2f;
            _desc.dragBuild = false;
            _desc.blueprintBoxSize = new Vector2(C.BaseRadius * 2f, C.BaseRadius * 2f);
            _desc.buildingGizmoCenter = new Vector3(0f, C.Height * 0.5f, 0f);
            _desc.buildingGizmoExt = new Vector3(C.BaseRadius, C.Height * 0.5f, C.BaseRadius);
            _desc.barWidth = C.BaseRadius * 2f;
            _desc.barHeight = C.Height;
            // 鼠标拾取距离。PrefabDesc 的默认值是 35 m（`selectDistance = 35f`），
            // 而它是从 prefab 上的 SlotConfig 读的 —— 我们的 prefab 没有 SlotConfig，
            // 所以一值都是默认 35。塔顶站体在 67 m 高，35 m 判定下永远点不到：
            // PlayerAction_Inspect 会静默 return，窗口不弹。见 C.SelectDistance 的注释。
            _desc.selectDistance = C.SelectDistance;
            // 站名/状态图标必须顶到塔顶，否则 hover 提示会飘在地面基座上
            _desc.signHeight = C.TotalHeight + 6f;
            _desc.signSize = 2f;

            // ---- 12 个传送带接口 ----
            _desc.portPoses = BuildPortPoses(C.PortCount);

            FillNullArrays(_desc);

            Plugin.Log.LogInfo("物流站 prefab 完成: 接口=" + _desc.portPoses.Length +
                               " 泊位=" + _desc.stationMaxShipCount +
                               " 槽位=" + _desc.stationMaxItemKinds + "×" + _desc.stationMaxItemCount +
                               " 能量池=" + _desc.stationMaxEnergyAcc +
                               " 拾取盒 y=" + C.PickCenterY.ToString("F1") + "±" + (C.PickHeight * 0.5f).ToString("F1") +
                               "m 半宽=" + C.PickHalf.ToString("F1") + "m" +
                               " buildCollider=" + (_desc.hasBuildCollider ? "有" : "无") +
                               "(半宽 " + _desc.buildCollider.ext.x.ToString("F2") + "m 高 " + (_desc.buildCollider.ext.y * 2f).ToString("F2") + "m)" +
                               " buildColliders=" + (_desc.buildColliders == null ? 0 : _desc.buildColliders.Length));
            return _desc;
        }

        /// <summary>
        /// 12 个接口：12 边形基座的每条边中心向外伸出 1 个。
        /// 索引 = 边序号 = portPoses 下标 = StationComponent.slots 下标，
        /// 实体连接池每个实体 16 格（entityConnPool[objId*16 + slot]），12 个安全。
        ///
        /// forward 必须朝建筑外：BuildTool_Path 里传送带起点 = 接口位置 + forward × 1.1，
        /// 朝内会让起点落进碰撞体，带子接不上。
        /// </summary>
        private static Pose[] BuildPortPoses(int count)
        {
            var poses = new Pose[count];
            for (int i = 0; i < count; i++)
            {
                float ang = 360f / count * i * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                poses[i] = new Pose(dir * C.PortPoseRadius + new Vector3(0f, C.PortPoseY, 0f),
                                    Quaternion.LookRotation(dir, Vector3.up));
            }
            return poses;
        }

        /// <summary>PrefabDesc 里所有数组字段若为 null 填空数组，防止游戏侧 .Length NRE。</summary>
        private static void FillNullArrays(PrefabDesc desc)
        {
            try
            {
                var fields = typeof(PrefabDesc).GetFields(System.Reflection.BindingFlags.Public |
                                                          System.Reflection.BindingFlags.Instance);
                for (int i = 0; i < fields.Length; i++)
                {
                    Type t = fields[i].FieldType;
                    if (!t.IsArray || fields[i].GetValue(desc) != null)
                        continue;
                    fields[i].SetValue(desc, Array.CreateInstance(t.GetElementType(), 0));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("填充 PrefabDesc 空数组失败: " + e.Message);
            }
        }

        private static void TryRetryMaterials()
        {
            try
            {
                Material[] mats = StationMesh.BuildMaterials();
                if (!StationMesh.HasTemplate || mats == null || mats.Length == 0)
                    return;
                if (_desc.lodMaterials != null && _desc.lodMaterials.Length > 0) _desc.lodMaterials[0] = mats;
                if (_desc.lodBlueprintMaterials != null && _desc.lodBlueprintMaterials.Length > 0)
                    _desc.lodBlueprintMaterials[0] = mats;
                _desc.materials = mats;
                if (_prefab != null)
                {
                    var mr = _prefab.GetComponent<MeshRenderer>();
                    if (mr != null) mr.sharedMaterials = mats;
                }
                Plugin.Log.LogInfo("已补上原版建筑材质（instancing 可用）");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("补材质失败: " + e.Message);
            }
        }

        private static void BuildColliders(GameObject root)
        {
            // ---- ① 塔顶站体拾取盒 ----
            // RaycastLogic.GameTick 只测 usage == Build（isTrigger=true 且非 independent）的
            // ColliderData，非触发碰撞体会被 `isForBuild` 过滤掉 —— 所以想让鼠标点得到
            // 264 m 高的碟形站体，必须给一个触发盒，且位置在塔顶。
            //
            // 顺序很关键：PrefabDesc.ReadPrefab 里 buildCollider（单数）= 遍历到的**最后一个**
            // build 盒，它决定建造预览的占地框/落位判定。所以站体盒要排在占地盒前面。
            var saucerGo = new GameObject("collider-saucer");
            saucerGo.transform.SetParent(root.transform, false);
            var sc = saucerGo.AddComponent<BoxCollider>();
            sc.isTrigger = true;                       // => ColliderData.usage = Build => 可拾取
            sc.center = new Vector3(0f, C.PickCenterY, 0f);
            sc.size = new Vector3(C.PickHalf * 2f, C.PickHeight, C.PickHalf * 2f);

            // ---- ② 建造占地框（isTrigger + 同一 prefab => ColliderData.usage = Build）----
            // 半宽用 BuildHalf 而非 BaseRadius：12 个接口按 30° 均布，斜向那 4 个接口的
            // 传送带起点（r=9.70）切向坐标只有 8.40，盒子取 8.15 会把它们判成 Occupied。
            var buildGo = new GameObject("collider-build");
            buildGo.transform.SetParent(root.transform, false);
            var bc = buildGo.AddComponent<BoxCollider>();
            bc.isTrigger = true;
            bc.center = new Vector3(0f, C.Height * 0.5f, 0f);
            bc.size = new Vector3(C.BuildHalf * 2f, C.Height, C.BuildHalf * 2f);

            // 物理/拾取（非 Trigger => usage = Physics）
            // 只覆盖基座与塔身，不覆盖 50 m 宽的停靠环：否则周围一大片都造不了东西。
            var physGo = new GameObject("collider-physics");
            physGo.transform.SetParent(root.transform, false);
            var pc = physGo.AddComponent<BoxCollider>();
            pc.isTrigger = false;
            pc.center = new Vector3(0f, 11f, 0f);
            pc.size = new Vector3(C.BaseRadius * 1.8f, 22f, C.BaseRadius * 1.8f);
        }
    }
}
