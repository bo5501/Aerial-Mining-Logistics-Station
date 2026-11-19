using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 中央光柱 = 原版黑雾「中继站」自带的那段特效网格，直接搬过来用。
    ///
    /// 反编译结论（v0.5 之前走了弯路，这里记下来免得再猜）：
    ///   · <c>PrefabDesc.ReadPrefab</c> 里只要 prefab 上挂着 <c>DFRelayDesc</c> 就置
    ///     <c>isDFRelay = true</c>（DFRelayDesc 本身是个空 MonoBehaviour，纯标记）；
    ///   · 渲染走 <c>SectorModel</c> 的 <c>kRelayModelIndex = 288</c> →
    ///     <c>SSGPUInstancingManager</c>，和普通建筑一样是 DrawMeshInstanced；
    ///   · 光柱**没有任何专用代码**，就是 prefab 里的一段 mesh：
    ///     节点名 <c>space-unit-relay-eff-1</c>，网格 96 顶点 / 60 三角，
    ///     包围盒 y ∈ [-65.53, 2.32]（长 67.85 m，宽 5 m），材质
    ///     <c>space-unit-relay-eff</c>（shader: VF Shaders/Forward Spacecraft/
    ///     Unlit Alpha Relay Effect，_Multiplier 12 / _UVSpeed / _SideFade 0.46）。
    ///     所以它其实是一条「细能量带 + 几处爆闪」，不是圆柱 —— 自己拿棱柱去凑，
    ///     无论多细都不像。
    ///
    /// 这里做的事：运行时从模型 288 的 prefab 里把那条长条网格挑出来，
    /// 连同它自己的材质一起搬给站体根部的一个普通 MeshRenderer
    /// （和炮塔同一套渲染路径，避免与建筑的 instancing 打架）。
    ///
    /// 取不到时返回 false，StationTurrets 会退化成一条很细的发光棱柱（C.BeamRBot/Mid）。
    /// </summary>
    internal static class StationBeam
    {
        /// <summary>SectorModel.kRelayModelIndex —— 黑雾中继站的模型号。</summary>
        private const int RelayModelIndex = 288;

        /// <summary>挑长条网格的门槛：高 / 宽 至少这么大才算光柱。</summary>
        private const float MinAspect = 4f;

        private static bool _tried;
        private static bool _ok;

        /// <summary>true = 用的是原版中继站网格；false = 退化成内置细棱柱。</summary>
        public static bool UsingRelay { get { return _ok; } }
        /// <summary>光柱网格（无论哪种来源都保证可用，除非整个模块失败）。</summary>
        public static bool Ready { get { return BeamMesh != null && BeamMaterial != null; } }
        public static Mesh BeamMesh { get; private set; }
        public static Material BeamMaterial { get; private set; }
        /// <summary>挂点局部坐标：把源网格底端挪到 C.BeamY0、顶端刚好到 C.BeamY1。</summary>
        public static Vector3 LocalPos { get; private set; }
        public static float LocalScale { get; private set; }
        /// <summary>源网格包围盒的最低点（y）。信号带要按自己的高度重新落位，需要它。</summary>
        public static float SourceMinY { get; private set; }
        /// <summary>源网格高度（米）。主光柱与 4 条信号带共用同一段网格，只是缩放不同。</summary>
        public static float SourceHeight { get; private set; } = 1f;

        /// <summary>准备光柱资源。优先搬原版中继站网格，失败则现造一条细棱柱兜底。</summary>
        public static bool Ensure()
        {
            if (_tried)
                return Ready;
            _tried = true;

            try
            {
                PrefabDesc desc = FindRelayDesc();
                if (desc == null)
                    return Fallback("找不到黑雾中继站模型（模型 " + RelayModelIndex + "）");

                GameObject prefab = desc.prefab;
                if (prefab == null)
                    return Fallback("中继站 PrefabDesc.prefab 为空");

                MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
                var cands = new System.Collections.Generic.List<MeshFilter>(4);

                for (int i = 0; i < filters.Length; i++)
                {
                    MeshFilter f = filters[i];
                    if (f == null || f.sharedMesh == null)
                        continue;
                    Bounds b = f.sharedMesh.bounds;
                    float w = Mathf.Max(b.size.x, b.size.z);
                    if (b.size.y < 10f || w < 0.001f)
                        continue;
                    if (b.size.y / w < MinAspect)
                        continue;                       // 宽扁的是机体/残骸，不是光柱
                    cands.Add(f);
                }

                if (cands.Count == 0)
                    return Fallback("中继站 prefab 里没找到长条网格（共 " + filters.Length + " 个 MeshFilter）");

                // 同一段网格在 prefab 里挂了多份（蓝图预览那份用 Default-Diffuse 占位），
                // 必须挑带真材质的那个，否则光柱会变成一块灰板。
                cands.Sort((a, b) => b.sharedMesh.bounds.size.y.CompareTo(a.sharedMesh.bounds.size.y));
                MeshFilter pick = cands[0];
                for (int i = 0; i < cands.Count; i++)
                {
                    if (!IsPlaceholder(cands[i]))
                    {
                        pick = cands[i];
                        break;
                    }
                }

                Mesh best = pick.sharedMesh;
                MeshRenderer bestMr = pick.GetComponent<MeshRenderer>();

                Bounds bb = best.bounds;
                float srcH = Mathf.Max(0.001f, bb.size.y);
                float scale = (C.BeamY1 - C.BeamY0) / srcH;

                BeamMesh = best;
                LocalScale = scale;
                LocalPos = new Vector3(0f, C.BeamY0 - bb.min.y * scale, 0f);
                SourceMinY = bb.min.y;
                SourceHeight = srcH;
                BeamMaterial = BuildMaterial(bestMr);
                _ok = BeamMaterial != null;

                Plugin.Log.LogInfo("光柱: 已搬用原版中继站特效网格 " + best.name +
                                   "（顶点=" + best.vertexCount +
                                   " 源高=" + srcH.ToString("0.00") + "m" +
                                   " 源宽=" + Mathf.Max(bb.size.x, bb.size.z).ToString("0.00") + "m）" +
                                   " 缩放=" + scale.ToString("0.0000") +
                                   " 落位 y=" + LocalPos.y.ToString("0.00") +
                                   " → " + C.BeamY0 + "~" + C.BeamY1.ToString("0.00") + "m" +
                                   " 材质=" + (BeamMaterial != null ? BeamMaterial.name : "null") +
                                   " shader=" + (BeamMaterial != null && BeamMaterial.shader != null
                                       ? BeamMaterial.shader.name : "null"));
                return Ready;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("光柱: 取原版中继站网格异常 — " + e.Message);
                return Fallback("异常");
            }
        }

        /// <summary>取不到原版网格时现场造一条很细的发光棱柱，保证还有光柱可看。</summary>
        private static bool Fallback(string why)        {
            _ok = false;
            LocalPos = Vector3.zero;
            LocalScale = 1f;
            SourceMinY = 0f;
            SourceHeight = Mathf.Max(0.001f, C.BeamY1 - C.BeamY0);
            BeamMesh = BuildFallbackMesh();
            BeamMaterial = FallbackMaterial();
            Plugin.Log.LogWarning("光柱: " + why + " → 退回内置细棱柱（半径 " + C.BeamRMid +
                                  "m，长 " + (C.BeamY1 - C.BeamY0).ToString("0.0") + "m）");
            return Ready;
        }

        private static Mesh BuildFallbackMesh()
        {
            const int seg = 12;
            float y0 = C.BeamY0, y1 = C.BeamY1;
            var v = new Vector3[(seg + 1) * 2];
            var n = new Vector3[v.Length];
            for (int i = 0; i <= seg; i++)
            {
                float a = Mathf.PI * 2f * i / seg;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                v[i] = new Vector3(c * C.BeamRBot, y0, s * C.BeamRBot);
                v[seg + 1 + i] = new Vector3(c * C.BeamRMid, y1, s * C.BeamRMid);
                n[i] = new Vector3(c, 0f, s);
                n[seg + 1 + i] = n[i];
            }
            var tri = new int[seg * 6];
            for (int i = 0, t = 0; i < seg; i++, t += 6)
            {
                tri[t] = i;
                tri[t + 1] = seg + 1 + i;
                tri[t + 2] = seg + 2 + i;
                tri[t + 3] = i;
                tri[t + 4] = seg + 2 + i;
                tri[t + 5] = i + 1;
            }
            var m = new Mesh { name = "hub-beam-fallback" };
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetTriangles(tri, 0, false);
            m.RecalculateBounds();
            return m;
        }

        private static Material FallbackMaterial()
        {
            Shader sh = Shader.Find("VF Shaders/Forward/PBR Standard Glass") ??
                        Shader.Find("VF Shaders/Forward/PBR Standard") ??
                        Shader.Find("Standard");
            var m = new Material(sh != null ? sh : Shader.Find("Diffuse")) { name = "hub-beam-fallback" };
            if (m.HasProperty("_Color"))
                m.SetColor("_Color", new Color(0.34f, 0.90f, 1f, 0.35f));
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", new Color(0.40f, 0.92f, 1f) * 2f);
            if (m.HasProperty("_CullMode"))
                m.SetFloat("_CullMode", 0f);
            m.enableInstancing = false;
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = 3080;
            return m;
        }

        /// <summary>蓝图占位材质（Default-Diffuse / blueprint-anchor）不能拿来当光柱。</summary>
        private static bool IsPlaceholder(MeshFilter f)
        {
            MeshRenderer mr = f.GetComponent<MeshRenderer>();
            Material m = mr != null ? mr.sharedMaterial : null;
            if (m == null)
                return true;
            if (m.name.IndexOf("blueprint", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (m.shader == null)
                return true;
            return m.shader.name.IndexOf("Default", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>模型 288 的 PrefabDesc；编号偏了就在全表里找第一个 isDFRelay。</summary>
        private static PrefabDesc FindRelayDesc()
        {
            ModelProtoSet set = LDB.models;
            if (set == null)
                return null;

            ModelProto mp = set.Select(RelayModelIndex);
            if (mp != null && mp.prefabDesc != null && mp.prefabDesc.isDFRelay)
                return mp.prefabDesc;

            ModelProto[] arr = set.dataArray;
            if (arr == null)
                return null;
            for (int i = 0; i < arr.Length; i++)
            {
                ModelProto p = arr[i];
                if (p != null && p.prefabDesc != null && p.prefabDesc.isDFRelay)
                {
                    if (mp == null || mp.prefabDesc == null)
                        Plugin.Log.LogInfo("光柱: 模型 " + RelayModelIndex + " 不是中继站，改用它 " + p.ID);
                    return p.prefabDesc;
                }
            }
            return null;
        }

        /// <summary>
        /// 复制原版材质。必须关掉 instancing：站体走 BatchRenderer，
        /// 光柱是普通 MeshRenderer，两条路径共用同一份材质实例会互相干扰。
        /// 拿不到原版材质时退回青白自发光，至少不会黑。
        /// </summary>
        private static Material BuildMaterial(MeshRenderer src)
        {
            Material m = null;
            if (src != null && src.sharedMaterial != null)
                m = new Material(src.sharedMaterial);
            if (m == null)
            {
                Shader sh = Shader.Find("VF Shaders/Forward/PBR Standard Glass") ??
                            Shader.Find("VF Shaders/Forward/PBR Standard") ??
                            Shader.Find("Standard");
                m = new Material(sh != null ? sh : Shader.Find("Diffuse"));
                if (m.HasProperty("_Color"))
                    m.SetColor("_Color", new Color(0.34f, 0.90f, 1f, 0.55f));
                if (m.HasProperty("_EmissionColor"))
                    m.SetColor("_EmissionColor", new Color(0.40f, 0.92f, 1f) * 2f);
            }

            m.name = "hub-beam-relay";
            m.enableInstancing = false;
            if (m.HasProperty("_CullMode"))
                m.SetFloat("_CullMode", 0f);            // 双面：从任何角度看都是光
            m.SetOverrideTag("RenderType", "Transparent");
            if (m.renderQueue < 3000)
                m.renderQueue = 3080;
            return m;
        }

        // ==================== 侧向「信息 / 物资」传输带 ====================

        private static Material[] _signalMats;

        /// <summary>
        /// 4 条细传输带的材质：克隆中继站那条带子的材质（同一套 Unlit Alpha + 滚动 UV），
        /// 只把滚动速度按序号错开 —— 四条带子的频率不同，看起来才像「不同批次的数据/物资
        /// 在各自排队上行」，而不是同一根柱子的四份副本。
        /// 取不到原版材质时返回 null，StationTurrets 会直接复用主光柱材质。
        /// </summary>
        public static Material SignalMaterial(int index)
        {
            int n = Mathf.Max(1, C.SignalCount);
            if (_signalMats == null)
                _signalMats = new Material[n];
            int i = Mathf.Clamp(index, 0, n - 1);
            if (_signalMats[i] != null)
                return _signalMats[i];
            if (BeamMaterial == null)
                return null;

            Material m = new Material(BeamMaterial);
            m.name = "hub-signal-" + i;
            m.enableInstancing = false;
            if (m.HasProperty("_CullMode"))
                m.SetFloat("_CullMode", 0f);            // 双面：细带从侧面看才不消失
            m.SetOverrideTag("RenderType", "Transparent");
            if (m.renderQueue < 3000)
                m.renderQueue = 3080;

            // 滚动速度错开（0.72 / 0.94 / 1.16 / 1.38 倍）。属性名或类型对不上就跳过：
            // 各带的位置与相位本来就错开，缺这一项只影响「流动感」的差异度。
            float k = 0.72f + 0.22f * i;
            try
            {
                if (m.HasProperty("_UVSpeed"))
                    m.SetFloat("_UVSpeed", m.GetFloat("_UVSpeed") * k);
            }
            catch
            {
                // 该 shader 没有浮点型 _UVSpeed，忽略
            }
            _signalMats[i] = m;
            return m;
        }
    }
}
