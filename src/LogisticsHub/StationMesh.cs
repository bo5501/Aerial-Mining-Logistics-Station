using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace LogisticsHub
{
    /// <summary>
    /// 深空物流枢纽站的程序化网格。
    ///
    /// 几何本体不写在这里：tools/station_model.py 是唯一真源，tools/gen_cs_geometry.py
    /// 把它导出成 <c>StationGeometry.Generated.cs</c>（1800 条原语），本文件只提供
    /// 「原语 → 顶点」的实现 + 材质管线。这样预览图（station_views.png）和游戏内的
    /// 网格逐三角一致，改造型只改 Python 再重新导出。
    ///
    /// 材质管线沿用已验证的做法：从原版建筑偷 instancing 材质 → 换 PBR Standard shader →
    /// 关掉所有「跟着工作状态调制自发光」的开关 → 只改 _Color 与自发光三件套。
    /// </summary>
    /// <summary>一个待提交的几何缓冲（同一材质槽）。</summary>
    internal class Part
    {
        public readonly List<Vector3> V = new List<Vector3>(1024);
        public readonly List<Vector3> N = new List<Vector3>(1024);
        public readonly List<int> T = new List<int>(2048);
    }

    internal static class StationMesh
    {
        // 材质槽（= StationGeometry 里 P 数组的下标）
        public const int MatHull = 0;
        public const int MatDark = 1;
        public const int MatEmissive = 2;
        public const int MatWarn = 3;
        public const int MatGlass = 4;
        public const int MatWeapon = 5;  // 武器平台基板：深蓝紫装甲
        /// <summary>
        /// 光柱外晕：玻璃基材 + 半透明青，套在发光内芯外面。
        /// 中继站的光柱是「实心发光 + 一圈更淡的外溢」，单靠 PBR 自发光只能做出实心棒，
        /// 必须再叠一层半透明壳才有「光在空气里散开」的读感。
        /// </summary>
        public const int MatBeam = 6;
        public const int MatCount = 7;

        private static Mesh _mesh;

        /// <summary>submesh 索引 -> 材质槽位。</summary>
        public static int[] SubmeshMaterial = new int[0];

        public static int SubmaterialCount => SubmeshMaterial.Length;

        /// <summary>每个材质槽在原版贴图上的常量 UV（取平坦色块，避免把细节/法线拉伸）。</summary>
        private static readonly Vector2[] Swatches =
        {
            new Vector2(0.2532f, 0.9480f), // hull  中灰
            new Vector2(0.9270f, 0.9968f), // dark  亮白（压暗 + 冷蓝染色）
            new Vector2(0.7161f, 0.8914f), // emis  暗底
            new Vector2(0.9270f, 0.9968f), // warn  亮白（琥珀染色）
            new Vector2(0.9270f, 0.9968f), // glass（未使用）
            new Vector2(0.9270f, 0.9968f),
            new Vector2(0.2532f, 0.9480f),
            new Vector2(0.7161f, 0.8914f), // beam 用暗底，靠自发光/alpha 出亮
        };

        /// <summary>反照率染色：冷白装甲 / 深蓝灰结构 / 深青底 / 琥珀 / 光柱青。</summary>
        private static readonly Color[] Tints =
        {
            new Color(0.88f, 0.91f, 0.96f), // hull 冷白
            new Color(0.26f, 0.32f, 0.42f), // dark 深蓝灰
            new Color(0.06f, 0.18f, 0.26f), // emis 深青底
            new Color(0.42f, 0.26f, 0.10f), // warn 琥珀底
            new Color(0.55f, 0.62f, 0.95f, 0.19f), // glass
            new Color(0.15f, 0.20f, 0.34f),         // weapon 深蓝紫装甲
            new Color(0.30f, 0.86f, 1.00f, 0.16f),  // beam 光柱外晕（低 alpha）
        };

        /// <summary>自发光颜色（写进 _EmissionTex）。</summary>
        private static readonly Color[] Glows =
        {
            Color.black,
            Color.black,
            new Color(0.26f, 0.80f, 1.00f), // 青
            new Color(1.00f, 0.60f, 0.24f), // 琥珀
            new Color(0.62f, 0.50f, 1.00f),
            new Color(0.04f, 0.10f, 0.20f),         // weapon 极弱冷光
            new Color(0.40f, 0.92f, 1.00f),         // beam 外晕青
        };

        private static readonly float[] GlowMul = { 0f, 0f, 1.7f, 1.5f, 0.55f, 0.35f, 1.1f };

        private static Vector2 Swatch(int matSlot)
        {
            return Swatches[matSlot >= 0 && matSlot < Swatches.Length ? matSlot : 0];
        }

        // ==================== 可动件：激光炮塔（360° 旋转） ====================

        private static Mesh _turretMesh;
        private static int[] _turretSlots;

        /// <summary>
        /// 单座转塔的局部网格（18 座共用一份）：原点 = 回转轴心，+Z = 炮口方向。
        /// 运行时由 StationTurrets 按安装点摆放并绕 Y 轴旋转。
        /// </summary>
        public static Mesh GetTurret()
        {
            if (_turretMesh != null)
                return _turretMesh;

            var verts = new List<Vector3>(4096);
            var norms = new List<Vector3>(4096);
            var uvs = new List<Vector2>(4096);
            var tris = new List<int[]>(4);
            var sub = new List<int>(4);

            var P = new Part[MatCount];
            for (int i = 0; i < P.Length; i++)
                P[i] = new Part();

            StationTurretGeometry.Build(P);
            for (int i = 0; i < MatCount; i++)
                Commit(P[i], i, verts, norms, uvs, tris, sub);

            var mesh = new Mesh { name = "hub-turret" };
            mesh.indexFormat = IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = tris.Count;
            for (int i = 0; i < tris.Count; i++)
                mesh.SetTriangles(tris[i], i, false);
            mesh.RecalculateBounds();

            _turretMesh = mesh;
            _turretSlots = sub.ToArray();
            Plugin.Log.LogInfo("炮塔网格: 安装点=" + StationTurretGeometry.Slots.Length +
                               " submesh=" + tris.Count + " 顶点=" + verts.Count +
                               " 三角=" + (verts.Count > 0 ? CountTris(tris) : 0));
            return _turretMesh;
        }

        private static int CountTris(List<int[]> tris)
        {
            int n = 0;
            for (int i = 0; i < tris.Count; i++)
                n += tris[i].Length / 3;
            return n;
        }

        private static Material[] _turretMats;

        /// <summary>
        /// 炮塔每个 submesh 对应的材质（按材质槽从主建筑配色里取，保证同色系）。
        ///
        /// 必须克隆一份并关掉 enableInstancing：主建筑走 BatchRenderer 的
        /// DrawMeshInstancedIndirect，矩阵来自 <c>_InstBuffer</c>；炮塔是普通 MeshRenderer，
        /// 走 <c>unity_ObjectToWorld</c>。直接共用同一份材质实例，两条渲染路径会互相干扰
        /// （典型症状是炮塔全部叠在世界原点，或干脆不显示）。
        ///
        /// 【0.8.0 修「激光炮看起来全黑」】
        /// 实机表现是模型位置/朝向都对，但整体发黑。原因不是配色表，而是**光照来源**：
        /// 主建筑由 <c>BatchRenderer</c> 统一喂光照与实例参数，PBR Standard 在那种路径下
        /// 能拿到正确的环境项；炮塔是独立 MeshRenderer，之前又把 <c>lightProbeUsage</c>/
        /// <c>reflectionProbeUsage</c> 全关成 Off，于是只剩直射光。塔顶那 18 座正好在
        /// 碟体阴影里、基座 4 座在裙边阴影里 → 反照率再白也渲成一片黑。
        ///
        /// 两条修法叠加：
        ///  1) 反照率提亮（dark 槽从 0.26 拉到 ~0.48，hull 微调）+ 给材质铺一层很弱的
        ///     冷白自发光当「微光漆」，阴影里也能读出轮廓与倒角；
        ///  2) StationTurrets 侧把 lightProbe / reflectionProbe 改回 BlendProbes。
        /// 自发光强度刻意压得很低（hull 0.30 / dark 0.18），否则炮塔会变成一根白棍。
        /// </summary>
        public static Material[] TurretMaterials()
        {
            if (_turretMats != null && _turretMats.Length > 0)
                return _turretMats;

            GetTurret();
            var list = new List<Material>(_turretSlots.Length);
            string shaderName = "null";

            for (int i = 0; i < _turretSlots.Length; i++)
            {
                int slot = _turretSlots[i];
                Material src = MaterialForSlot(slot);
                Material m;
                if (src != null)
                {
                    m = new Material(src);
                }
                else
                {
                    Shader sh = Shader.Find("VF Shaders/Forward/PBR Standard") ??
                                Shader.Find("Standard");
                    m = new Material(sh != null ? sh : Shader.Find("Diffuse"));
                    if (m.HasProperty("_Color"))
                        m.SetColor("_Color", Tints[Mathf.Clamp(slot, 0, Tints.Length - 1)]);
                }

                m.name = "hub-turret-mat-" + i;
                m.enableInstancing = false;

                // 若拿到的是 "(Instanced)" 变体，换回非实例化的同名 shader。
                // Instanced 变体会去 `_InstBuffer` 里按 SV_InstanceID 取矩阵与颜色，
                // 普通 MeshRenderer 下 instanceID 恒 0 → 取到别人的数据（表现为变黑/错位）。
                Shader cur = m.shader;
                if (cur != null && cur.name.Contains("Instanced"))
                {
                    Shader plain = Shader.Find("VF Shaders/Forward/PBR Standard");
                    if (plain != null)
                        m.shader = plain;
                }
                if (m.shader != null)
                    shaderName = m.shader.name;

                BrightenForTurret(m, slot);
                list.Add(m);
            }

            _turretMats = list.ToArray();
            Plugin.Log.LogInfo("炮塔材质: " + _turretMats.Length + " 个，shader=" + shaderName +
                               "（普通 MeshRenderer，已关闭 instancing；" +
                               "0.9.21：金属度归零 + 自发光托底 + 不接收阴影）");

            // 逐槽把最终值打出来：实机如果还偏黑，一眼就能看出是「反照率没生效」
            // 还是「自发光被别的东西盖掉了」。0.9.21 追加金属度/光滑度/反照率倍率。
            var sb = new StringBuilder("[炮塔材质]");
            for (int i = 0; i < _turretMats.Length; i++)
            {
                Material m = _turretMats[i];
                int slot = _turretSlots[i];
                sb.Append(" #").Append(i).Append("(slot").Append(slot).Append(") 反照率=")
                  .Append(m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "n/a")
                  .Append(" 自发光=")
                  .Append(m.HasProperty("_EmissionMultiplier")
                      ? m.GetFloat("_EmissionMultiplier").ToString("0.00") : "n/a")
                  .Append(" 金属=")
                  .Append(m.HasProperty("_MetallicMultiplier")
                      ? m.GetFloat("_MetallicMultiplier").ToString("0.00") : "n/a")
                  .Append(" 光滑=")
                  .Append(m.HasProperty("_SmoothMultiplier")
                      ? m.GetFloat("_SmoothMultiplier").ToString("0.00") : "n/a")
                  .Append(" 反照率×")
                  .Append(m.HasProperty("_AlbedoMultiplier")
                      ? m.GetFloat("_AlbedoMultiplier").ToString("0.00") : "n/a")
                  .Append(" 不接收阴影");
            }
            Plugin.Log.LogInfo(sb.ToString());
            return _turretMats;
        }

        /// <summary>
        /// 炮塔专用材质调校（0.9.21 重做）。
        ///
        /// 0.8.0 认为「黑」是反照率太低，于是只提了 _Color、铺了 0.30 的自发光；
        /// 0.9.20 换了新模型之后实机仍然是黑的 —— 说明反照率不是根因。
        /// 真正的根因是**这条渲染路径上的光照项基本为 0**：
        ///   1) 主建筑材质共用一张 _MS_Tex，hull 取色点的金属度是满的（R=255）。
        ///      纯金属面的漫反射项 = 0，剩下的镜面反射要有反射探针才出东西；
        ///      炮塔是普通 MeshRenderer，场景里没有探针 → 反射到的是黑。
        ///   2) 炮塔一直待在碟体 / 裙边的投影里，连太阳高光都被剪掉。
        ///   3) 0.30 的自发光乘上暗色贴图只剩 ~0.15，托不住轮廓。
        ///
        /// 所以这里不再「调色」，而是换口径：
        ///   · _MetallicMultiplier = 0 → 漫反射重新占主导，金属度不再是 1；
        ///   · _SmoothMultiplier 压到 0.55 → 留一点高光，但不至于变成塑料；
        ///   · 自发光按槽位抬到 0.44~0.62（炮口镜片仍 2.60），阴影里也能读出倒角；
        ///   · _ToggleVerta / _ToggleAnim / _AlphaClip 显式归零 —— 本模型没有
        ///     VertaBuffer 与动画缓冲，开着这些分支 shader 会去读没绑定的数据。
        /// </summary>
        private static void BrightenForTurret(Material m, int slot)
        {
            float boost = slot == MatDark ? C.TurretDarkBoost
                        : slot == MatHull ? C.TurretHullBoost
                        : slot == MatWeapon ? C.TurretWeaponBoost
                        : 1f;

            if (m.HasProperty("_Color"))
            {
                Color c = m.GetColor("_Color");
                c.r = Mathf.Clamp01(c.r * boost);
                c.g = Mathf.Clamp01(c.g * boost);
                c.b = Mathf.Clamp01(c.b * boost);
                m.SetColor("_Color", c);
            }

            // ---- 关键三件：金属度归零 / 光滑度收敛 / 反照率再抬一点 ----
            if (m.HasProperty("_AlbedoMultiplier"))
            {
                float a = m.GetFloat("_AlbedoMultiplier");
                if (a <= 0f) a = 1f;
                m.SetFloat("_AlbedoMultiplier", a * C.TurretAlbedoScale);
            }
            if (m.HasProperty("_MetallicMultiplier"))
                m.SetFloat("_MetallicMultiplier", C.TurretMetallicScale);
            if (m.HasProperty("_SmoothMultiplier"))
                m.SetFloat("_SmoothMultiplier", C.TurretSmoothScale);

            // ---- 本模型没有顶点动画 / Verta 缓冲，相关分支必须关掉 ----
            if (m.HasProperty("_ToggleVerta")) m.SetFloat("_ToggleVerta", 0f);
            if (m.HasProperty("_ToggleAnim")) m.SetFloat("_ToggleAnim", 0f);
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 0f);
            if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", 2f);   // 背面剔除：封闭体，省一半片元

            float glowMul = slot == MatWarn ? C.TurretWarnGlow
                          : slot == MatHull ? C.TurretHullGlow
                          : slot == MatDark ? C.TurretDarkGlow
                          : slot == MatWeapon ? C.TurretWeaponGlow
                          : 0f;
            if (glowMul <= 0f)
                return;

            // 自发光贴图色 = 反照率色的淡化版，避免「白棍」
            Color glow = slot == MatWarn
                ? new Color(1.00f, 0.62f, 0.26f)
                : slot == MatDark
                    ? new Color(0.34f, 0.40f, 0.52f)
                    : slot == MatWeapon
                        ? new Color(0.30f, 0.36f, 0.56f)
                        : new Color(0.62f, 0.70f, 0.86f);

            if (m.HasProperty("_EmissionTex"))
                m.SetTexture("_EmissionTex", SolidTex(glow));
            if (m.HasProperty("_EmissionMask"))
                m.SetColor("_EmissionMask", Color.white);
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", Color.white);
            if (m.HasProperty("_EmissionSwitch"))
                m.SetFloat("_EmissionSwitch", 1f);
            if (m.HasProperty("_EmissionMultiplier"))
                m.SetFloat("_EmissionMultiplier", glowMul);
            if (m.HasProperty("_EmissionUsePower"))
                m.SetFloat("_EmissionUsePower", 0f);
            if (m.HasProperty("_EmissionJitter"))
                m.SetFloat("_EmissionJitter", 0f);
            if (m.HasProperty("_EmissionMipBias"))
                m.SetFloat("_EmissionMipBias", 0f);
            m.EnableKeyword("_EMISSION");
        }

        // ==================== 光柱流动环（运行时特效） ====================

        private static Mesh _beamRingMesh;
        private static Material[] _beamRingMats;

        /// <summary>
        /// 一个水平圆环（torus），原点 = 环心。运行时由 StationTurrets 沿光柱向上平移，
        /// 做出「能量往上走」的读感 —— 这才叫特效；烘进静态网格只能得到一根带箍的杆子。
        /// </summary>
        public static Mesh BuildBeamRing(float R, float tube, int major, int minor)
        {
            if (_beamRingMesh != null)
                return _beamRingMesh;

            major = Mathf.Max(6, major);
            minor = Mathf.Max(3, minor);

            var verts = new List<Vector3>(major * minor);
            var norms = new List<Vector3>(major * minor);
            var uvs = new List<Vector2>(major * minor);
            var tris = new List<int>(major * minor * 6);

            for (int i = 0; i < major; i++)
            {
                float u = Mathf.PI * 2f * i / major;
                Vector3 dir = new Vector3(Mathf.Cos(u), 0f, Mathf.Sin(u));
                for (int j = 0; j < minor; j++)
                {
                    float v = Mathf.PI * 2f * j / minor;
                    Vector3 n = dir * Mathf.Cos(v) + Vector3.up * Mathf.Sin(v);
                    verts.Add(dir * R + n * tube);
                    norms.Add(n);
                    uvs.Add(new Vector2((float)i / major, (float)j / minor));
                }
            }

            for (int i = 0; i < major; i++)
            {
                int i1 = (i + 1) % major;
                for (int j = 0; j < minor; j++)
                {
                    int j1 = (j + 1) % minor;
                    int a = i * minor + j;
                    int b = i1 * minor + j;
                    int c = i1 * minor + j1;
                    int d = i * minor + j1;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(a); tris.Add(c); tris.Add(d);
                }
            }

            var mesh = new Mesh { name = "hub-beam-ring" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0, false);
            mesh.RecalculateBounds();
            _beamRingMesh = mesh;
            Plugin.Log.LogInfo("光柱流光环网格: 半径=" + R + "m 管径=" + tube +
                               " 顶点=" + verts.Count + " 三角=" + (tris.Count / 3));
            return _beamRingMesh;
        }

        /// <summary>流光环材质：自发光青，双面（环内外都要看得见）。</summary>
        public static Material[] BeamRingMaterials()
        {
            if (_beamRingMats != null && _beamRingMats.Length > 0)
                return _beamRingMats;

            Material src = MaterialForSlot(MatEmissive) ?? MaterialForSlot(MatHull);
            Material m;
            if (src != null)
            {
                m = new Material(src);
            }
            else
            {
                Shader sh = Shader.Find("VF Shaders/Forward/PBR Standard") ?? Shader.Find("Standard");
                m = new Material(sh != null ? sh : Shader.Find("Diffuse"));
            }
            m.name = "hub-beam-ring-mat";
            m.enableInstancing = false;
            if (m.HasProperty("_Color"))
                m.SetColor("_Color", new Color(0.55f, 0.94f, 1.00f, 1f));
            if (m.HasProperty("_CullMode"))
                m.SetFloat("_CullMode", 0f);
            m.renderQueue = 3050;
            _beamRingMats = new[] { m };
            return _beamRingMats;
        }

        // ==================== 传输带上的「物资」光点 ====================

        private static Mesh _cargoMesh;
        private static Material[] _cargoMats;

        /// <summary>一个小立方体（边长 2×C.CargoScale），代表正沿传输带上行的货箱。</summary>
        public static Mesh GetCargoMesh()
        {
            if (_cargoMesh != null)
                return _cargoMesh;

            float h = Mathf.Max(0.05f, C.CargoScale);
            var verts = new List<Vector3>(24);
            var norms = new List<Vector3>(24);
            var uvs = new List<Vector2>(24);
            var tris = new List<int>(36);

            // 6 个面 × 4 顶点，法线硬边（货箱要有棱有角，和光柱的柔和形成对比）
            for (int f = 0; f < 6; f++)
            {
                Vector3 n;
                Vector3 u, v;
                switch (f)
                {
                    case 0: n = Vector3.right; u = Vector3.forward; v = Vector3.up; break;
                    case 1: n = Vector3.left; u = Vector3.forward; v = Vector3.up; break;
                    case 2: n = Vector3.up; u = Vector3.right; v = Vector3.forward; break;
                    case 3: n = Vector3.down; u = Vector3.right; v = Vector3.forward; break;
                    case 4: n = Vector3.forward; u = Vector3.right; v = Vector3.up; break;
                    default: n = Vector3.back; u = Vector3.right; v = Vector3.up; break;
                }
                Vector3 c = n * h;
                int i0 = verts.Count;
                verts.Add(c - u * h - v * h);
                verts.Add(c + u * h - v * h);
                verts.Add(c + u * h + v * h);
                verts.Add(c - u * h + v * h);
                for (int k = 0; k < 4; k++)
                {
                    norms.Add(n);
                    uvs.Add(Vector2.zero);
                }
                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
            }

            var mesh = new Mesh { name = "hub-cargo" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0, false);
            mesh.RecalculateBounds();
            _cargoMesh = mesh;
            return _cargoMesh;
        }

        /// <summary>货箱材质：强自发光琥珀，不投影（它是个光点，不该在地面留影子）。</summary>
        public static Material[] CargoMaterials()
        {
            if (_cargoMats != null && _cargoMats.Length > 0)
                return _cargoMats;

            var sh = Shader.Find("VF Shaders/Forward/PBR Standard");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh != null ? sh : Shader.Find("Diffuse"));
            m.name = "hub-cargo-mat";
            m.enableInstancing = false;
            if (m.HasProperty("_Color"))
                m.SetColor("_Color", new Color(0.98f, 0.78f, 0.42f));
            // 0.9.21：货箱是「物资光点」，必须自己亮，不能靠场景光。
            // 金属度归零 + 光滑度压平，否则没有 _MS_Tex 时默认取样是「满金属满光滑」，
            // 在普通 MeshRenderer 上会反射到黑，光点变成黑方块。
            if (m.HasProperty("_AlbedoMultiplier")) m.SetFloat("_AlbedoMultiplier", 1.25f);
            if (m.HasProperty("_MetallicMultiplier")) m.SetFloat("_MetallicMultiplier", 0f);
            if (m.HasProperty("_SmoothMultiplier")) m.SetFloat("_SmoothMultiplier", 0.25f);
            if (m.HasProperty("_EmissionMask")) m.SetColor("_EmissionMask", Color.white);
            if (m.HasProperty("_EmissionTex"))
                m.SetTexture("_EmissionTex", SolidTex(new Color(1.00f, 0.74f, 0.30f)));
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", Color.white);
            if (m.HasProperty("_EmissionSwitch"))
                m.SetFloat("_EmissionSwitch", 1f);
            if (m.HasProperty("_EmissionMultiplier"))
                m.SetFloat("_EmissionMultiplier", 3.20f);
            if (m.HasProperty("_EmissionUsePower"))
                m.SetFloat("_EmissionUsePower", 0f);
            if (m.HasProperty("_EmissionJitter"))
                m.SetFloat("_EmissionJitter", 0f);
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 0f);
            if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", 2f);
            if (m.HasProperty("_ToggleVerta"))
                m.SetFloat("_ToggleVerta", 0f);
            if (m.HasProperty("_ToggleAnim"))
                m.SetFloat("_ToggleAnim", 0f);
            // 0.9.22：货箱网格的 UV 也是 (0,0)，`new Material` 的 _MainTex 未绑定时
            // 采样结果不保证是白 —— 显式挂一张 1×1 纯白，反照率只由 _Color 决定。
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", SolidTex(Color.white));
            m.EnableKeyword("_EMISSION");
            _cargoMats = new[] { m };
            return _cargoMats;
        }

        // ==================== 中央护盾：半球能量罩 ====================

        /// <summary>
        /// 半球能量罩网格。submesh 0 = 罩面（双面），submesh 1 = 经纬网格线。
        /// 球心在地面原点、半径覆盖整个站体，与 C.ShieldRadius 一致。
        /// </summary>
        public static Mesh BuildShield(float radius, int seg, int rings, float baseY)
        {
            var verts = new List<Vector3>(8192);
            var norms = new List<Vector3>(8192);
            var uvs = new List<Vector2>(8192);
            var shell = new List<int>(seg * rings * 6);
            var grid = new List<int>(seg * rings * 6);

            // ---- 罩面：经纬网格，正面 + 反面各一遍 ----
            for (int j = 0; j < rings; j++)
            {
                float v0 = Mathf.PI * 0.5f * j / rings;
                float v1 = Mathf.PI * 0.5f * (j + 1) / rings;
                float y0 = Mathf.Sin(v0) * radius, r0 = Mathf.Cos(v0) * radius;
                float y1 = Mathf.Sin(v1) * radius, r1 = Mathf.Cos(v1) * radius;
                for (int i = 0; i < seg; i++)
                {
                    float u0 = Mathf.PI * 2f * i / seg;
                    float u1 = Mathf.PI * 2f * (i + 1) / seg;
                    Vector3 a = new Vector3(Mathf.Cos(u0) * r0, y0, Mathf.Sin(u0) * r0);
                    Vector3 b = new Vector3(Mathf.Cos(u0) * r1, y1, Mathf.Sin(u0) * r1);
                    Vector3 c = new Vector3(Mathf.Cos(u1) * r1, y1, Mathf.Sin(u1) * r1);
                    Vector3 d = new Vector3(Mathf.Cos(u1) * r0, y0, Mathf.Sin(u1) * r0);
                    AddShieldQuad(verts, norms, uvs, shell, a, b, c, d);
                }
            }

            // ---- 网格线：若干条纬线 + 经线，做成细带 ----
            const float W = 0.20f;
            int lonCount = 16;
            for (int k = 2; k <= 8; k++)          // 跳过贴地的赤道环，免得和基座糊在一起
            {
                float v = Mathf.PI * 0.5f * k / 9f;
                float y = Mathf.Sin(v) * radius, rr = Mathf.Cos(v) * radius;
                for (int i = 0; i < seg; i++)
                {
                    float u0 = Mathf.PI * 2f * i / seg;
                    float u1 = Mathf.PI * 2f * (i + 1) / seg;
                    Vector3 a = new Vector3(Mathf.Cos(u0) * rr, y + W * 0.5f, Mathf.Sin(u0) * rr);
                    Vector3 b = new Vector3(Mathf.Cos(u1) * rr, y + W * 0.5f, Mathf.Sin(u1) * rr);
                    Vector3 c = new Vector3(Mathf.Cos(u1) * rr, y - W * 0.5f, Mathf.Sin(u1) * rr);
                    Vector3 d = new Vector3(Mathf.Cos(u0) * rr, y - W * 0.5f, Mathf.Sin(u0) * rr);
                    AddShieldQuad(verts, norms, uvs, grid, a, b, c, d);
                }
            }
            for (int k = 0; k < lonCount; k++)
            {
                float u = Mathf.PI * 2f * k / lonCount;
                float cu = Mathf.Cos(u), su = Mathf.Sin(u);
                for (int j = 0; j < rings; j++)
                {
                    float v0 = Mathf.PI * 0.5f * j / rings;
                    float v1 = Mathf.PI * 0.5f * (j + 1) / rings;
                    float y0 = Mathf.Sin(v0) * radius, r0 = Mathf.Cos(v0) * radius;
                    float y1 = Mathf.Sin(v1) * radius, r1 = Mathf.Cos(v1) * radius;
                    // 带子沿方位角方向偏移
                    Vector3 a = new Vector3(cu * r0, y0, su * r0);
                    Vector3 b = new Vector3(cu * r1, y1, su * r1);
                    Vector3 c = new Vector3(cu * (r1 - W), y1, su * (r1 - W));
                    Vector3 d = new Vector3(cu * (r0 - W), y0, su * (r0 - W));
                    AddShieldQuad(verts, norms, uvs, grid, a, b, c, d);
                }
            }

            // 站体已抬到塔顶：把半球整体搬到 baseY（法线是相对球心算好的，平移不影响）
            if (Mathf.Abs(baseY) > 0.001f)
            {
                Vector3 up = new Vector3(0f, baseY, 0f);
                for (int i = 0; i < verts.Count; i++)
                    verts[i] = verts[i] + up;
            }

            var mesh = new Mesh { name = "hub-shield" };
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(shell, 0, false);
            mesh.SetTriangles(grid, 1, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>把四边形按「正反两面」写进同一个 submesh（护盾不需要背面剔除）。</summary>
        private static void AddShieldQuad(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs,
            List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i0 = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int i = 0; i < 4; i++)
            {
                norms.Add(verts[i0 + i].normalized);
                uvs.Add(new Vector2(0.5f, 0.5f));
            }
            tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
            // 反面
            int i1 = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int i = 0; i < 4; i++)
            {
                norms.Add(-verts[i1 + i].normalized);
                uvs.Add(new Vector2(0.5f, 0.5f));
            }
            tris.Add(i1); tris.Add(i1 + 2); tris.Add(i1 + 1);
            tris.Add(i1); tris.Add(i1 + 3); tris.Add(i1 + 2);
        }

        private static Material _shieldShell, _shieldGrid;

        /// <summary>护盾材质：罩面极淡 + 网格线较亮，都用玻璃（透明队列）shader。</summary>
        public static Material[] ShieldMaterials()
        {
            if (_shieldShell != null && _shieldGrid != null)
                return new[] { _shieldShell, _shieldGrid };

            Material src = MaterialForSlot(MatEmissive) ?? MaterialForSlot(MatHull);
            _shieldShell = MakeShieldMat(src, false);
            _shieldGrid = MakeShieldMat(src, true);
            return new[] { _shieldShell, _shieldGrid };
        }

        private static Material MakeShieldMat(Material src, bool grid)
        {
            var m = src != null ? new Material(src) : new Material(Shader.Find("Standard"));
            m.name = grid ? "hub-shield-grid" : "hub-shield-shell";

            Shader glass = Shader.Find("VF Shaders/Forward/PBR Standard Glass");
            if (glass != null)
                m.shader = glass;                       // 自带 Transparent 队列 + 菲涅尔边缘光

            m.enableInstancing = false;
            Color c = grid
                ? new Color(0.32f, 0.86f, 1.00f, 0.17f)
                : new Color(0.14f, 0.48f, 0.92f, 0.045f);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_RimColor")) m.SetColor("_RimColor",
                grid ? new Color(0.55f, 0.96f, 1.00f, 1f) : new Color(0.28f, 0.66f, 1.00f, 1f));
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", new Color(0.16f, 0.52f, 0.95f) * (grid ? 1.10f : 0.45f));
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 0f);
            if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", 0f);   // 双面
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = grid ? 3060 : 3050;

            if (glass == null)
            {
                // 兜底：手工切到 Unity 标准透明混合
                if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 3f);
                if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            return m;
        }

        public static Mesh Get()
        {
            if (_mesh != null)
                return _mesh;

            var verts = new List<Vector3>(32768);
            var norms = new List<Vector3>(32768);
            var uvs = new List<Vector2>(32768);
            var tris = new List<int[]>(8);
            var sub = new List<int>(8);

            var P = new Part[MatCount];
            for (int i = 0; i < P.Length; i++)
                P[i] = new Part();

            StationGeometry.Build(P);

            int triCount = 0;
            for (int i = 0; i < MatCount; i++)
            {
                Commit(P[i], i, verts, norms, uvs, tris, sub);
                triCount += P[i].T.Count / 3;
            }

            // 塔顶整体放大：只缩放 y > 25 的顶点（基座/裙边/接口一个像素都不动）。
            if (C.BodyScale != 1f)
            {
                for (int i = 0; i < verts.Count; i++)
                    verts[i] = C.ScaleBody(verts[i]);
            }

            var mesh = new Mesh { name = "logistics-hub" };
            mesh.indexFormat = verts.Count > 65000
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = tris.Count;
            for (int i = 0; i < tris.Count; i++)
                mesh.SetTriangles(tris[i], i, false);
            mesh.RecalculateBounds();
            SubmeshMaterial = sub.ToArray();

            _mesh = mesh;

            // 统计日志：环数组已改为单环（长度 1），不能再写死索引，否则这里抛
            // IndexOutOfRange 会让整个 Get() 失败、prefabDesc 挂不上，建筑直接不可见。
            int lastRing = C.RingR.Length - 1;
            float maxDia = (C.RingR[lastRing] + C.RingTube[lastRing]) * 2f;
            Plugin.Log.LogInfo("枢纽站网格已构建: submesh=" + tris.Count +
                               " 顶点=" + verts.Count + " 三角=" + triCount +
                               " 基座高=" + C.Height + "m 站体底=" + C.BodyY +
                               "m 塔顶=" + C.TotalHeight +
                               "m 最大环径=" + maxDia + "m 泊位=" + C.MaxShips);
            return _mesh;
        }

        // ================= 几何原语（签名与 StationGeometry.Generated.cs 一一对应）=================

        /// <summary>多边形棱台（侧面 + 可选上下端盖）。</summary>
        internal static void Prism(Part[] P, int mat, float y0, float y1, float r0, float r1,
            int seg, float rotDeg, bool capBottom, bool capTop)
        {
            Part p = P[mat];
            float rot = rotDeg * Mathf.Deg2Rad;
            float dr = (r1 - r0) / Mathf.Max(0.0001f, y1 - y0);
            for (int i = 0; i < seg; i++)
            {
                float t0 = rot + Mathf.PI * 2f * i / seg;
                float t1 = rot + Mathf.PI * 2f * (i + 1) / seg;
                float c0 = Mathf.Cos(t0), s0 = Mathf.Sin(t0);
                float c1 = Mathf.Cos(t1), s1 = Mathf.Sin(t1);
                Vector3 a = new Vector3(c0 * r0, y0, s0 * r0);
                Vector3 b = new Vector3(c0 * r1, y1, s0 * r1);
                Vector3 c = new Vector3(c1 * r1, y1, s1 * r1);
                Vector3 d = new Vector3(c1 * r0, y0, s1 * r0);
                Vector3 n0 = new Vector3(c0, -dr, s0).normalized;
                Vector3 n1 = new Vector3(c1, -dr, s1).normalized;
                AddQuad(p, a, b, c, d, n0, n0, n1, n1, (n0 + n1) * 0.5f);
            }
            if (capBottom) Cap(p, Vector3.zero, y0, r0, seg, -1f);
            if (capTop) Cap(p, Vector3.zero, y1, r1, seg, 1f);
        }

        /// <summary>水平环板（上下两面 + 内外壁）。</summary>
        internal static void Annulus(Part[] P, int mat, float y, float rIn, float rOut,
            float thick, int seg, bool walls)
        {
            Part p = P[mat];
            var up = new Vector3(0f, 1f, 0f);
            var dn = new Vector3(0f, -1f, 0f);
            float yt = y + thick * 0.5f, yb = y - thick * 0.5f;
            for (int i = 0; i < seg; i++)
            {
                float t0 = Mathf.PI * 2f * i / seg;
                float t1 = Mathf.PI * 2f * (i + 1) / seg;
                float c0 = Mathf.Cos(t0), s0 = Mathf.Sin(t0);
                float c1 = Mathf.Cos(t1), s1 = Mathf.Sin(t1);
                AddQuad(p,
                    new Vector3(c0 * rIn, yt, s0 * rIn), new Vector3(c0 * rOut, yt, s0 * rOut),
                    new Vector3(c1 * rOut, yt, s1 * rOut), new Vector3(c1 * rIn, yt, s1 * rIn),
                    up, up, up, up, up);
                AddQuad(p,
                    new Vector3(c0 * rIn, yb, s0 * rIn), new Vector3(c0 * rOut, yb, s0 * rOut),
                    new Vector3(c1 * rOut, yb, s1 * rOut), new Vector3(c1 * rIn, yb, s1 * rIn),
                    dn, dn, dn, dn, dn);
                if (!walls)
                    continue;
                var n0 = new Vector3(c0, 0f, s0);
                var n1 = new Vector3(c1, 0f, s1);
                AddQuad(p,
                    new Vector3(c0 * rOut, yb, s0 * rOut), new Vector3(c0 * rOut, yt, s0 * rOut),
                    new Vector3(c1 * rOut, yt, s1 * rOut), new Vector3(c1 * rOut, yb, s1 * rOut),
                    n0, n0, n1, n1, (n0 + n1) * 0.5f);
                AddQuad(p,
                    new Vector3(c0 * rIn, yb, s0 * rIn), new Vector3(c0 * rIn, yt, s0 * rIn),
                    new Vector3(c1 * rIn, yt, s1 * rIn), new Vector3(c1 * rIn, yb, s1 * rIn),
                    -n0, -n0, -n1, -n1, -(n0 + n1) * 0.5f);
            }
        }

        /// <summary>环面（torus），停靠环的主体。</summary>
        internal static void Torus(Part[] P, int mat, float cy, float R, float r, int major, int minor)
        {
            Part p = P[mat];
            for (int i = 0; i < major; i++)
            {
                float t0 = Mathf.PI * 2f * i / major;
                float t1 = Mathf.PI * 2f * (i + 1) / major;
                float ct0 = Mathf.Cos(t0), st0 = Mathf.Sin(t0);
                float ct1 = Mathf.Cos(t1), st1 = Mathf.Sin(t1);
                for (int j = 0; j < minor; j++)
                {
                    float ph0 = Mathf.PI * 2f * j / minor;
                    float ph1 = Mathf.PI * 2f * (j + 1) / minor;
                    Vector3 n0 = new Vector3(ct0 * Mathf.Cos(ph0), Mathf.Sin(ph0), st0 * Mathf.Cos(ph0));
                    Vector3 n1 = new Vector3(ct0 * Mathf.Cos(ph1), Mathf.Sin(ph1), st0 * Mathf.Cos(ph1));
                    Vector3 n2 = new Vector3(ct1 * Mathf.Cos(ph1), Mathf.Sin(ph1), st1 * Mathf.Cos(ph1));
                    Vector3 n3 = new Vector3(ct1 * Mathf.Cos(ph0), Mathf.Sin(ph0), st1 * Mathf.Cos(ph0));
                    Vector3 a = new Vector3(ct0 * (R + r * Mathf.Cos(ph0)), cy + r * Mathf.Sin(ph0), st0 * (R + r * Mathf.Cos(ph0)));
                    Vector3 b = new Vector3(ct0 * (R + r * Mathf.Cos(ph1)), cy + r * Mathf.Sin(ph1), st0 * (R + r * Mathf.Cos(ph1)));
                    Vector3 c = new Vector3(ct1 * (R + r * Mathf.Cos(ph1)), cy + r * Mathf.Sin(ph1), st1 * (R + r * Mathf.Cos(ph1)));
                    Vector3 d = new Vector3(ct1 * (R + r * Mathf.Cos(ph0)), cy + r * Mathf.Sin(ph0), st1 * (R + r * Mathf.Cos(ph0)));
                    AddQuad(p, a, b, c, d, n0, n1, n2, n3, (n0 + n1 + n2 + n3) * 0.25f);
                }
            }
        }

        /// <summary>以 (cx,cy,cz) 为中心的水平圆盘环（泊位托盘）。</summary>
        internal static void Disc(Part[] P, int mat, float cx, float cy, float cz,
            float rIn, float rOut, float thick, int seg)
        {
            Part p = P[mat];
            var up = new Vector3(0f, 1f, 0f);
            var dn = new Vector3(0f, -1f, 0f);
            float yt = cy + thick * 0.5f, yb = cy - thick * 0.5f;
            for (int i = 0; i < seg; i++)
            {
                float t0 = Mathf.PI * 2f * i / seg;
                float t1 = Mathf.PI * 2f * (i + 1) / seg;
                float c0 = Mathf.Cos(t0), s0 = Mathf.Sin(t0);
                float c1 = Mathf.Cos(t1), s1 = Mathf.Sin(t1);
                AddQuad(p,
                    new Vector3(cx + c0 * rIn, yt, cz + s0 * rIn), new Vector3(cx + c0 * rOut, yt, cz + s0 * rOut),
                    new Vector3(cx + c1 * rOut, yt, cz + s1 * rOut), new Vector3(cx + c1 * rIn, yt, cz + s1 * rIn),
                    up, up, up, up, up);
                AddQuad(p,
                    new Vector3(cx + c0 * rIn, yb, cz + s0 * rIn), new Vector3(cx + c0 * rOut, yb, cz + s0 * rOut),
                    new Vector3(cx + c1 * rOut, yb, cz + s1 * rOut), new Vector3(cx + c1 * rIn, yb, cz + s1 * rIn),
                    dn, dn, dn, dn, dn);
                var n0 = new Vector3(c0, 0f, s0);
                var n1 = new Vector3(c1, 0f, s1);
                AddQuad(p,
                    new Vector3(cx + c0 * rOut, yb, cz + s0 * rOut), new Vector3(cx + c0 * rOut, yt, cz + s0 * rOut),
                    new Vector3(cx + c1 * rOut, yt, cz + s1 * rOut), new Vector3(cx + c1 * rOut, yb, cz + s1 * rOut),
                    n0, n0, n1, n1, (n0 + n1) * 0.5f);
                if (rIn > 1e-4f)   // 内壁：环形盘必须封闭，否则会透出下层
                    AddQuad(p,
                        new Vector3(cx + c0 * rIn, yb, cz + s0 * rIn), new Vector3(cx + c0 * rIn, yt, cz + s0 * rIn),
                        new Vector3(cx + c1 * rIn, yt, cz + s1 * rIn), new Vector3(cx + c1 * rIn, yb, cz + s1 * rIn),
                        -n0, -n0, -n1, -n1, -(n0 + n1) * 0.5f);
            }
        }

        /// <summary>
        /// 朝 ±Z 的圆环面（炮口透镜）。Disc 的轴是 +Y，prism 的 rot 也只绕 Y，
        /// 都做不出正对炮口方向的面，所以单列一个轴沿 Z 的版本（与 Python 的 disc_z 对齐）。
        /// </summary>
        internal static void DiscZ(Part[] P, int mat, float cx, float cy, float cz,
            float rIn, float rOut, float thick, int seg)
        {
            Part p = P[mat];
            var fw = new Vector3(0f, 0f, 1f);
            var bk = new Vector3(0f, 0f, -1f);
            float zf = cz + thick * 0.5f, zb = cz - thick * 0.5f;
            for (int i = 0; i < seg; i++)
            {
                float t0 = Mathf.PI * 2f * i / seg;
                float t1 = Mathf.PI * 2f * (i + 1) / seg;
                float c0 = Mathf.Cos(t0), s0 = Mathf.Sin(t0);
                float c1 = Mathf.Cos(t1), s1 = Mathf.Sin(t1);
                AddQuad(p,
                    new Vector3(cx + c0 * rIn, cy + s0 * rIn, zf), new Vector3(cx + c0 * rOut, cy + s0 * rOut, zf),
                    new Vector3(cx + c1 * rOut, cy + s1 * rOut, zf), new Vector3(cx + c1 * rIn, cy + s1 * rIn, zf),
                    fw, fw, fw, fw, fw);
                AddQuad(p,
                    new Vector3(cx + c0 * rIn, cy + s0 * rIn, zb), new Vector3(cx + c0 * rOut, cy + s0 * rOut, zb),
                    new Vector3(cx + c1 * rOut, cy + s1 * rOut, zb), new Vector3(cx + c1 * rIn, cy + s1 * rIn, zb),
                    bk, bk, bk, bk, bk);
                var n0 = new Vector3(c0, s0, 0f);
                var n1 = new Vector3(c1, s1, 0f);
                AddQuad(p,
                    new Vector3(cx + c0 * rOut, cy + s0 * rOut, zb), new Vector3(cx + c0 * rOut, cy + s0 * rOut, zf),
                    new Vector3(cx + c1 * rOut, cy + s1 * rOut, zf), new Vector3(cx + c1 * rOut, cy + s1 * rOut, zb),
                    n0, n0, n1, n1, (n0 + n1) * 0.5f);
                if (rIn > 1e-4f)
                    AddQuad(p,
                        new Vector3(cx + c0 * rIn, cy + s0 * rIn, zb), new Vector3(cx + c0 * rIn, cy + s0 * rIn, zf),
                        new Vector3(cx + c1 * rIn, cy + s1 * rIn, zf), new Vector3(cx + c1 * rIn, cy + s1 * rIn, zb),
                        -n0, -n0, -n1, -n1, -(n0 + n1) * 0.5f);
            }
        }

        internal static void Ball(Part[] P, int mat, float cx, float cy, float cz,
            float r, int rings, int seg)
        {
            Part p = P[mat];
            Vector3 ctr = new Vector3(cx, cy, cz);
            for (int i = 0; i < rings; i++)
            {
                float p0 = Mathf.PI * i / rings;
                float p1 = Mathf.PI * (i + 1) / rings;
                float y0 = Mathf.Cos(p0), rr0 = Mathf.Sin(p0);
                float y1 = Mathf.Cos(p1), rr1 = Mathf.Sin(p1);
                for (int j = 0; j < seg; j++)
                {
                    float t0 = Mathf.PI * 2f * j / seg;
                    float t1 = Mathf.PI * 2f * (j + 1) / seg;
                    Vector3 a = new Vector3(Mathf.Cos(t0) * rr0, y0, Mathf.Sin(t0) * rr0);
                    Vector3 b = new Vector3(Mathf.Cos(t0) * rr1, y1, Mathf.Sin(t0) * rr1);
                    Vector3 c = new Vector3(Mathf.Cos(t1) * rr1, y1, Mathf.Sin(t1) * rr1);
                    Vector3 d = new Vector3(Mathf.Cos(t1) * rr0, y0, Mathf.Sin(t1) * rr0);
                    AddQuad(p, ctr + a * r, ctr + b * r, ctr + c * r, ctr + d * r,
                        a, b, c, d, (a + b + c + d) * 0.25f);
                }
            }
        }

        /// <summary>两点之间的圆管（桁架/拉索）。</summary>
        internal static void Strut(Part[] P, int mat, float ax, float ay, float az,
            float bx, float by, float bz, float r, int seg)
        {
            Part p = P[mat];
            Vector3 a = new Vector3(ax, ay, az);
            Vector3 b = new Vector3(bx, by, bz);
            Vector3 dir = b - a;
            if (dir.sqrMagnitude < 1e-8f)
                return;
            dir = dir.normalized;
            Vector3 up = Mathf.Abs(dir.y) > 0.95f ? Vector3.right : Vector3.up;
            Vector3 u = Vector3.Cross(dir, up).normalized;
            Vector3 v = Vector3.Cross(dir, u);
            for (int i = 0; i < seg; i++)
            {
                float t0 = Mathf.PI * 2f * i / seg + Mathf.PI / seg;
                float t1 = Mathf.PI * 2f * (i + 1) / seg + Mathf.PI / seg;
                Vector3 o0 = u * Mathf.Cos(t0) + v * Mathf.Sin(t0);
                Vector3 o1 = u * Mathf.Cos(t1) + v * Mathf.Sin(t1);
                AddQuad(p, a + o0 * r, b + o0 * r, b + o1 * r, a + o1 * r,
                    o0, o0, o1, o1, (o0 + o1) * 0.5f);
            }
        }

        /// <summary>
        /// 绕 Y 轴的螺旋带（中央辊道塔外挂的上升辊道）：上表面 + 外侧壁 + 下表面。
        /// 与 tools/station_model.py 的 helix() 逐三角一致：turns 圈 × 每圈 seg 段。
        /// </summary>
        internal static void Helix(Part[] P, int mat, float y0, float y1, float rIn,
            float width, float thick, int turns, int seg)
        {
            Part p = P[mat];
            int n = Mathf.Max(1, turns * seg);
            float rise = y1 - y0;
            float rOut = rIn + width;
            float h = thick * 0.5f;
            var up = new Vector3(0f, 1f, 0f);
            var dn = new Vector3(0f, -1f, 0f);

            for (int i = 0; i < n; i++)
            {
                float u0 = (float)i / n, u1 = (float)(i + 1) / n;
                float a0 = Mathf.PI * 2f * u0, a1 = Mathf.PI * 2f * u1;
                float ya = y0 + rise * u0, yb = y0 + rise * u1;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0);
                float c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);

                Vector3 i0 = new Vector3(c0 * rIn, ya + h, s0 * rIn);
                Vector3 o0 = new Vector3(c0 * rOut, ya + h, s0 * rOut);
                Vector3 i1 = new Vector3(c1 * rIn, yb + h, s1 * rIn);
                Vector3 o1 = new Vector3(c1 * rOut, yb + h, s1 * rOut);
                AddQuad(p, i0, o0, o1, i1, up, up, up, up, up);      // 上表面

                Vector3 b0 = new Vector3(c0 * rOut, ya - h, s0 * rOut);
                Vector3 b1 = new Vector3(c1 * rOut, yb - h, s1 * rOut);
                Vector3 n0 = new Vector3(c0, 0f, s0);
                Vector3 n1 = new Vector3(c1, 0f, s1);
                AddQuad(p, b0, o0, o1, b1, n0, n0, n1, n1, (n0 + n1) * 0.5f);   // 外侧壁

                Vector3 d0 = new Vector3(c0 * rIn, ya - h, s0 * rIn);
                Vector3 d1 = new Vector3(c1 * rIn, yb - h, s1 * rIn);
                AddQuad(p, d0, b0, b1, d1, dn, dn, dn, dn, dn);      // 下表面
            }
        }

        /// <summary>径向对齐的长方体：yaw 决定朝外方向（局部 +Z），+X 切向。</summary>
        internal static void Box(Part[] P, int mat, float cx, float cy, float cz,
            float hx, float hy, float hz, float yawDeg)
        {
            Part p = P[mat];
            float a = yawDeg * Mathf.Deg2Rad;
            Vector3 f = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            Vector3 rt = new Vector3(Mathf.Sin(a), 0f, -Mathf.Cos(a));
            Vector3 u = new Vector3(0f, 1f, 0f);
            Vector3 c = new Vector3(cx, cy, cz);

            Vector3 Corner(float sx, float sy, float sz)
            {
                return c + rt * (sx * hx) + u * (sy * hy) + f * (sz * hz);
            }

            Vector3 nf = f, nb = -f, nr = rt, nl = -rt, nu = u, nd = -u;
            AddQuad(p, Corner(-1, -1, -1), Corner(-1, -1, 1), Corner(-1, 1, 1), Corner(-1, 1, -1),
                nl, nl, nl, nl, nl);
            AddQuad(p, Corner(1, -1, -1), Corner(1, -1, 1), Corner(1, 1, 1), Corner(1, 1, -1),
                nr, nr, nr, nr, nr);
            AddQuad(p, Corner(-1, -1, -1), Corner(1, -1, -1), Corner(1, -1, 1), Corner(-1, -1, 1),
                nd, nd, nd, nd, nd);
            AddQuad(p, Corner(-1, 1, -1), Corner(1, 1, -1), Corner(1, 1, 1), Corner(-1, 1, 1),
                nu, nu, nu, nu, nu);
            AddQuad(p, Corner(-1, -1, -1), Corner(1, -1, -1), Corner(1, 1, -1), Corner(-1, 1, -1),
                nb, nb, nb, nb, nb);
            AddQuad(p, Corner(-1, -1, 1), Corner(1, -1, 1), Corner(1, 1, 1), Corner(-1, 1, 1),
                nf, nf, nf, nf, nf);
        }

        private static void Cap(Part p, Vector3 off, float y, float r, int seg, float sign)
        {
            Vector3 ctr = off + new Vector3(0f, y, 0f);
            var nrm = new Vector3(0f, sign, 0f);
            for (int i = 0; i < seg; i++)
            {
                float t0 = Mathf.PI * 2f * i / seg;
                float t1 = Mathf.PI * 2f * (i + 1) / seg;
                Vector3 a = off + new Vector3(Mathf.Cos(t0) * r, y, Mathf.Sin(t0) * r);
                Vector3 b = off + new Vector3(Mathf.Cos(t1) * r, y, Mathf.Sin(t1) * r);
                AddTri(p, ctr, a, b, nrm, nrm, nrm, nrm);
            }
        }

        /// <summary>加四边形，绕序按期望法线自动纠正。</summary>
        private static void AddQuad(Part p, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd, Vector3 want)
        {
            int i0 = p.V.Count;
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), want) < 0f)
            {
                p.V.Add(a); p.V.Add(d); p.V.Add(c); p.V.Add(b);
                p.N.Add(na); p.N.Add(nd); p.N.Add(nc); p.N.Add(nb);
            }
            else
            {
                p.V.Add(a); p.V.Add(b); p.V.Add(c); p.V.Add(d);
                p.N.Add(na); p.N.Add(nb); p.N.Add(nc); p.N.Add(nd);
            }
            p.T.Add(i0); p.T.Add(i0 + 1); p.T.Add(i0 + 2);
            p.T.Add(i0); p.T.Add(i0 + 2); p.T.Add(i0 + 3);
        }

        private static void AddTri(Part p, Vector3 a, Vector3 b, Vector3 c,
            Vector3 na, Vector3 nb, Vector3 nc, Vector3 want)
        {
            int i0 = p.V.Count;
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), want) < 0f)
            {
                p.V.Add(a); p.V.Add(c); p.V.Add(b);
                p.N.Add(na); p.N.Add(nc); p.N.Add(nb);
            }
            else
            {
                p.V.Add(a); p.V.Add(b); p.V.Add(c);
                p.N.Add(na); p.N.Add(nb); p.N.Add(nc);
            }
            p.T.Add(i0); p.T.Add(i0 + 1); p.T.Add(i0 + 2);
        }

        private static void Commit(Part p, int matSlot, List<Vector3> verts, List<Vector3> norms,
            List<Vector2> uvs, List<int[]> tris, List<int> subMat)
        {
            if (p.V.Count == 0)
                return;
            int baseIndex = verts.Count;
            verts.AddRange(p.V);
            norms.AddRange(p.N);
            Vector2 uv = Swatch(matSlot);
            for (int i = 0; i < p.V.Count; i++)
                uvs.Add(uv);

            int slot = -1;
            for (int i = 0; i < subMat.Count; i++)
                if (subMat[i] == matSlot) { slot = i; break; }

            int[] dst;
            if (slot >= 0)
            {
                int[] old = tris[slot];
                dst = new int[old.Length + p.T.Count];
                Array.Copy(old, dst, old.Length);
                for (int i = 0; i < p.T.Count; i++) dst[old.Length + i] = p.T[i] + baseIndex;
            }
            else
            {
                slot = tris.Count;
                dst = new int[p.T.Count];
                for (int i = 0; i < p.T.Count; i++) dst[i] = p.T[i] + baseIndex;
                subMat.Add(matSlot);
                tris.Add(null);
            }
            tris[slot] = dst;
        }

        // ---------------- 材质 ----------------

        /// <summary>返回与 submesh 数量等长的材质数组。</summary>
        public static Material[] BuildMaterials()
        {
            Get();
            var src = TemplateMaterials();
            HasTemplate = src.Length > 0;
            if (!HasTemplate)
                Plugin.Log.LogError("找不到可用的原版建筑材质模板，将退化为无实例化材质（建筑可能不显示）");

            Material baseMat = PickBase(src);
            Material glassMat = PickGlass(src);
            if (baseMat != null)
                Plugin.Log.LogInfo("材质模板: " + baseMat.name +
                                   " shader=" + (baseMat.shader != null ? baseMat.shader.name : "null") +
                                   " / 玻璃模板: " + (glassMat != null ? glassMat.name : "未找到，按 shader 兜底"));

            int n = SubmeshMaterial.Length;
            var mats = new Material[n];
            for (int i = 0; i < n; i++)
            {
                int slot = SubmeshMaterial[i];
                bool wantGlass = slot == MatGlass || slot == MatBeam;
                mats[i] = CreateFor(wantGlass && glassMat != null ? glassMat : baseMat, slot);
            }

            var sb = new StringBuilder("[配色] 参考近程电浆塔(turret-plasma-ground):");
            for (int i = 0; i < n; i++)
            {
                int slot = SubmeshMaterial[i];
                sb.Append(" #").Append(i).Append("(slot").Append(slot).Append(") 染色=")
                  .Append(Tints[Mathf.Clamp(slot, 0, Tints.Length - 1)]).Append(" 自发光=")
                  .Append(Glows[Mathf.Clamp(slot, 0, Glows.Length - 1)]).Append("×")
                  .Append(GlowMul[Mathf.Clamp(slot, 0, GlowMul.Length - 1)]);
            }
            Plugin.Log.LogInfo(sb.ToString());
            _mats = mats;
            return mats;
        }

        private static Material[] _mats;

        /// <summary>已构建的材质数组（按 submesh 顺序，槽位见 SubmeshMaterial）。</summary>
        public static Material[] GetMaterials()
        {
            if (_mats != null && _mats.Length > 0)
                return _mats;
            return BuildMaterials();
        }

        /// <summary>按材质槽（MatHull/MatDark/…）取材质，炮塔与护盾共用主建筑的配色。</summary>
        public static Material MaterialForSlot(int slot)
        {
            Material[] mats = GetMaterials();
            for (int i = 0; i < mats.Length && i < SubmeshMaterial.Length; i++)
                if (SubmeshMaterial[i] == slot)
                    return mats[i];
            return mats.Length > 0 ? mats[0] : null;
        }

        /// <summary>上一次构建材质时是否成功取到原版建筑材质（false 表示要重试）。</summary>
        public static bool HasTemplate { get; private set; }

        /// <summary>
        /// 从游戏现有建筑偷材质。必须用原版建筑材质：DSP 的建筑 shader 会从
        /// GPU 实例缓冲 _InstBuffer 里读每栋建筑的位移/旋转，用自己的 shader 会全部堆在原点。
        /// </summary>
        private static Material[] TemplateMaterials()
        {
            // 1) 优先用原版能量枢纽（同样走 PowerExchanger 通路，材质语义最接近）
            Material[] fromProto = ScanProtoMaterials(true);
            if (fromProto.Length > 0)
                return fromProto;

            // 2) 任意原版建筑
            fromProto = ScanProtoMaterials(false);
            if (fromProto.Length > 0)
                return fromProto;

            // 3) 兜底：直接加载原版枢纽 prefab，读它的 LODModelDesc
            return MaterialsFromPrefab();
        }

        private static Material[] ScanProtoMaterials(bool onlyExchanger)
        {
            if (LDB.items == null || LDB.items.dataArray == null)
                return new Material[0];
            foreach (var p in LDB.items.dataArray)
            {
                if (p == null || p.ID == C.ItemId || p.prefabDesc == null)
                    continue;
                if (onlyExchanger && !p.prefabDesc.isPowerExchanger)
                    continue;
                var lm = p.prefabDesc.lodMaterials;
                if (lm != null && lm.Length > 0 && lm[0] != null && lm[0].Length > 0)
                    return lm[0];
            }
            return new Material[0];
        }

        /// <summary>
        /// 直接 Resources.Load 原版枢纽 prefab 取材质。
        /// 走这条路不依赖 VFPreload 的顺序——我们的 prefab 可能在原版模型 Preload 之前就要建好。
        /// </summary>
        private static Material[] MaterialsFromPrefab()
        {
            try
            {
                var go = Resources.Load<GameObject>(StationPrefab.FallbackPrefabPath);
                if (go == null)
                    return new Material[0];
                var lmd = go.GetComponentInChildren<LODModelDesc>(true);
                if (lmd == null || lmd.lodMeshDatas == null || lmd.lodMeshDatas.Length == 0)
                    return new Material[0];
                Material[] mats = lmd.lodMeshDatas[0].materials;
                if (mats == null || mats.Length == 0)
                    return new Material[0];
                Plugin.Log.LogInfo("材质模板取自 prefab: " + StationPrefab.FallbackPrefabPath + "，共 " + mats.Length + " 个");
                return mats;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("从 prefab 取材质失败: " + e.Message);
                return new Material[0];
            }
        }

        /// <summary>
        /// 在模板材质里挑「主装甲」那一支。
        /// 原版枢纽的 LOD0 材质表里还混着 glass / effect / black / ruins 变体，
        /// 拿错就会得到透明或全功率发光的材质。
        /// </summary>
        /// <summary>
        /// 从原版建筑的材质列表里挑玻璃材质（名字带 glass）。
        /// 目的是拿到 shader = <c>PBR Standard Glass</c> 的那份基材：
        /// 它已经配好了 Transparent 队列、SrcAlpha 混合、ZWrite=0 和 _RimColor 边缘光。
        /// 挑不到就返回 null，CreateFor 里会按 shader 名兜底新建。
        /// </summary>
        private static Material PickGlass(Material[] src)
        {
            if (src == null)
                return null;
            for (int i = 0; i < src.Length; i++)
            {
                Material m = src[i];
                if (m == null)
                    continue;
                string n = (m.name ?? string.Empty).ToLowerInvariant();
                if (n.Contains("glass") && !n.Contains("black"))
                    return m;
            }
            return null;
        }

        private static Material PickBase(Material[] src)
        {
            if (src == null || src.Length == 0)
                return null;
            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < src.Length; i++)
                {
                    Material m = src[i];
                    if (m == null)
                        continue;
                    string n = (m.name ?? string.Empty).ToLowerInvariant();
                    bool bad = n.Contains("glass") || n.Contains("effect") || n.Contains("black") ||
                               n.Contains("ruins") || n.Contains("wreck") || n.Contains("mcl");
                    if (pass == 0)
                    {
                        if (!bad && n.Contains("exchanger"))
                            return m;
                    }
                    else if (pass == 1)
                    {
                        if (!bad)
                            return m;
                    }
                    else
                    {
                        return m;
                    }
                }
            }
            return null;
        }

        private static Material CreateFor(Material baseMat, int matSlot)
        {
            int slot = Mathf.Clamp(matSlot, 0, Tints.Length - 1);
            Material m;
            if (baseMat != null)
            {
                m = new Material(baseMat);
            }
            else
            {
                var sh = Shader.Find("VF Shaders/Forward/PBR Standard");
                if (sh == null) sh = Shader.Find("Standard");
                m = new Material(sh != null ? sh : Shader.Find("Diffuse"));
            }
            m.name = "logistics-hub-mat-" + matSlot;
            m.enableInstancing = true;

            // 1.2.5 关键修复：原版能量交换站用的是专用 shader 变体
            // 「VF Shaders/Forward/PBR Energy Exchanger」，它会从 GPU 实例缓冲里读
            // 本建筑的「工作状态」来调制自发光（原版交换站的能量窗就是这么亮的）。
            // 对我们的建筑后果就是：待机时自发光被压成黑色（顶端圆球发黑）、
            // 工作状态变化时颜色忽明忽暗（外观颜色不稳定）。
            // 换成所有普通原版建筑共用的「VF Shaders/Forward/PBR Standard」，
            // 自发光 = _EmissionTex × _EmissionColor × _EmissionMultiplier 恒定不变。
            Shader cur = m.shader;
            bool translucent = slot == MatGlass || slot == MatBeam;
            if (!translucent && cur != null && cur.name.Contains("Energy Exchanger"))
            {
                Shader std = Shader.Find("VF Shaders/Forward/PBR Standard") ??
                             Shader.Find("VF Shaders/Forward/PBR Standard (Instanced)");
                if (std != null)
                {
                    m.shader = std;
                    if (!_shaderSwapped)
                    {
                        _shaderSwapped = true;
                        Plugin.Log.LogInfo("材质 shader 已从 " + cur.name + " 切换为 " + std.name +
                                           "（去掉按工作状态调制的自发光，配色恒定）");
                    }
                }
            }

            // 1.5.0 玻璃槽：必须用 PBR Standard Glass（自带 Transparent 队列 + 边缘光）。
            // 基材没取到原版 glass 材质时，这里兜底把 shader 换过去。
            // v0.4.1：MatBeam（光柱外晕）复用同一套玻璃通路，只是 alpha 更低、双面。
            if (translucent && (m.shader == null || !m.shader.name.Contains("Glass")))
            {
                Shader gl = Shader.Find("VF Shaders/Forward/PBR Standard Glass");
                if (gl != null) m.shader = gl;
            }
            if (translucent)
            {
                // 边缘光：玻璃棱镜的「棱」就是靠菲涅尔亮边读出来的
                if (m.HasProperty("_RimColor"))
                    m.SetColor("_RimColor", slot == MatBeam
                        ? new Color(0.42f, 0.90f, 1.00f, 1f)
                        : new Color(0.74f, 0.64f, 1.00f, 1f));
                if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 0f);
                // 光晕要双面（内外两壁都透出光），玻璃棱镜保持背面剔除
                if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", slot == MatBeam ? 0f : 2f);
                if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 3f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = slot == MatBeam ? 3100 : 3000;
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword("_ALPHABLEND_ON");
            }

            // 唯一的改动：本建筑没有 VertaBuffer（顶点动画缓冲），
            // 关掉顶点动画分支，避免 shader 去读一个没绑定的动画缓冲。
            // 其余贴图（_MainTex/_MS_Tex/_NormalTex）全部保持原版——
            // 反照率的 alpha 通道就是光滑度，动它就会变成镜面白。
            if (m.HasProperty("_ToggleVerta"))
                m.SetFloat("_ToggleVerta", 0f);

            // 1.3.9 关键修复：上面那些只是「已知」的调制开关，原版 energy-exchanger 材质
            // 还有别的自发光调制项（名字不一定叫 _EmissionUsePower）。它们会跟着建筑的
            // 工作状态（animPool.state，卡槽里有料、开始传能后就会 0/100 来回跳）调制自发光，
            // 表现就是「放入消耗品之后建筑模型一闪一闪」。
            // 这里把所有 _Emission* 的浮点属性统一清零，只留下我们自己要设的三个，
            // 自发光就变成纯静态 = 配色恒定。
            ClearEmissionModulators(m, slot);

            // 配色：只改 _Color（反照率染色）与自发光三件套。
            // 自发光贴图换成纯色贴图（不是纯白！），遮罩外的区域不会发光。
            if (m.HasProperty("_Color"))
                m.SetColor("_Color", Tints[slot]);
            float mul = GlowMul[slot];
            if (m.HasProperty("_EmissionSwitch"))
                m.SetFloat("_EmissionSwitch", mul > 0f ? 1f : 0f);
            if (m.HasProperty("_EmissionMultiplier"))
                m.SetFloat("_EmissionMultiplier", mul > 0f ? mul : 0f);
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", Color.white);
            // 1.2.7：PBR Standard 这类 Unity 系 shader 需要 _EMISSION 关键字才会渲染自发光
            // （原版交换站专用 shader 不需要）。不打开关键字，自发光完全不生效，
            // 球顶只剩深紫反照率 ≈ 黑色——这就是「待机时顶端圆球发黑」的根因。
            if (m.HasProperty("_EmissionTex"))
            {
                m.SetTexture("_EmissionTex", SolidTex(Glows[slot]));
                if (mul > 0f) m.EnableKeyword("_EMISSION");
                else m.DisableKeyword("_EMISSION");
            }
            // 1.2.5：功率调制与随机抖动全部显式关闭——
            // _EmissionUsePower=1 时自发光跟随建筑工作状态（待机发黑），_EmissionJitter 是随机闪烁。
            // 用户要的是「颜色稳定」，这两项都必须为 0。
            if (m.HasProperty("_EmissionUsePower"))
                m.SetFloat("_EmissionUsePower", 0f);
            if (m.HasProperty("_EmissionJitter"))
                m.SetFloat("_EmissionJitter", 0f);

            if (!_matLogged)
            {
                _matLogged = true;
                DumpMaterial(m);
            }
            return m;
        }

        private static bool _matLogged;
        private static bool _shaderSwapped;

        private static readonly Dictionary<int, Texture2D> _solidTex = new Dictionary<int, Texture2D>();

        /// <summary>
        /// 4x4 纯色贴图（当 _EmissionTex 用）。注意不能用 1x1 + 纯白：
        /// 原版 shader 的自发光是「贴图 × _EmissionColor × _EmissionMultiplier」，
        /// 贴图给纯白、倍率再拉满，整栋建筑就会过曝成白块。
        /// 这里贴图直接带色，倍率只做亮度微调。
        /// </summary>
        private static Texture2D SolidTex(Color c)
        {
            int key = ((int)(Mathf.Clamp01(c.r) * 255f) << 16) |
                      ((int)(Mathf.Clamp01(c.g) * 255f) << 8) |
                       (int)(Mathf.Clamp01(c.b) * 255f);
            Texture2D t;
            if (_solidTex.TryGetValue(key, out t) && t != null)
                return t;
            t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color32[16];
            var v = new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f),
                (byte)(Mathf.Clamp01(c.b) * 255f), 255);
            for (int i = 0; i < px.Length; i++) px[i] = v;
            t.SetPixels32(px);
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.name = "station-glow-" + key.ToString("X6");
            _solidTex[key] = t;
            return t;
        }

        /// <summary>
        /// 把材质上所有「自发光调制」浮点属性清零。
        ///
        /// 为什么要这么做：原版 energy-exchanger 用的是它专用的 PBR 变体，材质里带着一整套
        /// 「按建筑工作状态调制自发光」的开关。我们虽然显式关了 _EmissionUsePower / _EmissionJitter，
        /// 但原版还存在别的同类开关（例如按 animPool.state 做的呼吸、按 power 做的明暗），
        /// 一旦卡槽里放进消耗品、建筑开始传能，animPool.state 就在 0/100 之间来回跳，
        /// 自发光跟着跳 —— 视觉上就是「建筑模型一闪一闪」。
        ///
        /// 这里不猜属性名：直接枚举材质全部浮点属性，凡是以 _Emission 开头的
        /// （除了 _EmissionMultiplier / _EmissionSwitch 这两个留给下面显式设置）一律置 0。
        /// 效果：自发光 = 静态贴图 × 固定倍率，与工作状态完全解耦。
        /// </summary>
        /// <summary>
        /// 属性名里出现这些词，就认为它是「跟着建筑工作状态变化的调制开关」。
        /// 全部小写比较（属性名大小写不统一，原版用的就是 _EmissionUsePower 这种驼峰）。
        /// </summary>
        private static readonly string[] ModulatorKeys =
        {
            "use", "jitter", "anim", "state", "speed", "flow",
            "scroll", "pulse", "time", "power", "breath", "blink", "flicker",
        };

        private static void ClearEmissionModulators(Material m, int slot)
        {
            if (m == null || m.shader == null)
                return;
            // 用 Shader.GetProperty* 而不是 Material.GetPropertyNames：
            // DSP 引用的 Unity 程序集里没有 MaterialPropertyType（读取的是 shader 的属性表，
            // 结果等价，但兼容性更好）。
            Shader sh = m.shader;
            int propCount;
            try
            {
                propCount = sh.GetPropertyCount();
            }
            catch
            {
                return;
            }

            StringBuilder killed = null;
            for (int i = 0; i < propCount; i++)
            {
                string n;
                try
                {
                    if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Float)
                        continue;
                    n = sh.GetPropertyName(i);
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(n))
                    continue;
                string lower = n.ToLowerInvariant();
                if (lower.IndexOf("_emission", StringComparison.Ordinal) != 0)
                    continue;
                if (n == "_EmissionMultiplier" || n == "_EmissionSwitch")
                    continue;   // 这两个下面会按配色显式赋值
                // 只清「调制类」开关：名字里带 use / jitter / state / time / power / flow …
                // 的才会跟着工作状态变化。强度类（_EmissionIntensity 之类）必须留着，
                // 清掉会让自发光整体消失（球变黑）。
                bool isModulator = false;
                for (int k = 0; k < ModulatorKeys.Length && !isModulator; k++)
                    isModulator = lower.IndexOf(ModulatorKeys[k], StringComparison.Ordinal) >= 0;
                if (!isModulator)
                    continue;
                float v;
                try { v = m.GetFloat(n); }
                catch { continue; }
                if (Mathf.Approximately(v, 0f))
                    continue;
                m.SetFloat(n, 0f);
                if (killed == null) killed = new StringBuilder();
                if (killed.Length > 0) killed.Append(", ");
                killed.Append(n).Append('=').Append(v.ToString("0.###"));
            }

            if (killed != null)
                Plugin.Log.LogInfo("材质#" + slot + " 已关闭自发光调制开关（会跟着工作状态闪）: " + killed);
        }

        private static void DumpMaterial(Material m)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("材质自检(原版参数): name=").Append(m.name)
                  .Append(" shader=").Append(m.shader != null ? m.shader.name : "null");
                sb.Append(" tex=[").Append(string.Join(",", m.GetTexturePropertyNames())).Append(']');
                string[] cols = { "_Color", "_TintColor", "_EmissionColor", "_EmissionMask", "_SpecularColor" };
                for (int i = 0; i < cols.Length; i++)
                    if (m.HasProperty(cols[i])) sb.Append(' ').Append(cols[i]).Append('=').Append(m.GetColor(cols[i]));
                string[] flts =
                {
                    "_AlbedoMultiplier", "_EmissionMultiplier", "_EmissionSwitch", "_EmissionUsePower",
                    "_EmissionJitter", "_Metallic", "_Glossiness", "_SmoothMultiplier", "_ToggleVerta"
                };
                for (int i = 0; i < flts.Length; i++)
                    if (m.HasProperty(flts[i])) sb.Append(' ').Append(flts[i]).Append('=').Append(m.GetFloat(flts[i]));
                for (int i = 0; i < 5; i++)
                {
                    string p = i == 0 ? "_MainTex" : i == 1 ? "_EmissionTex" : i == 2 ? "_MS_Tex" :
                               i == 3 ? "_NormalTex" : "_EmissionJitterTex";
                    if (!m.HasProperty(p)) continue;
                    var t = m.GetTexture(p) as Texture2D;
                    sb.Append(' ').Append(p).Append('=').Append(t != null ? t.name + "(" + t.width + ")" : "null");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("材质自检失败: " + e.Message);
            }
        }
    }
}
