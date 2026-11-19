using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 底座「打洞取地热」的**电网侧回收**模块。
    ///
    /// 0.9.5/0.9.6 曾经在这里做了一件看着很合理、实际會毁掉整个站体 UI 的事：
    /// 给站点实体补 <c>PowerGeneratorComponent</c> + <c>PowerNodeComponent</c>，
    /// 让它像原版地热发电机一样并网发电。
    ///
    /// 后果是原版 <c>UIGame._OnUpdate</c> 会把本站当成**发电机建筑**：
    ///
    /// <code>
    /// num14 = entityPool[objId].powerNodeId;   // 我们写过 → 1
    /// num15 = entityPool[objId].powerGenId;    // 我们写过 → 1
    /// if (num13 &gt; 0) num14 = 0;
    /// if (num15 &gt; 0) num14 = 0;                // 有发电机 → 不认节点
    /// ...
    /// if (this.inspectGeneratorId == 0 &amp;&amp; num15 &gt; 0)
    /// {
    ///     this.ShutAllFunctionWindow();        // ← 里面会 ShutStationWindow()
    ///     this.OpenGeneratorWindow();          // ← 弹出「地热发电站」窗口
    /// }
    /// </code>
    ///
    /// 而 <c>ShutGeneratorWindow()</c> 会把 <c>inspectGeneratorId</c> 复位成 0，
    /// 于是**每一帧**都会重来一次：关掉站体窗口 → 开地热发电站窗口 → 站体窗口又被
    /// 下一个分支重开 …… 玩家看到的就是「点站体，面板一直闪 / 根本弹不出来 / 弹出来的是
    /// 别的窗口」。这就是 0.9.5 之后「UI 面板没有弹出来」的真正来源。
    ///
    /// 所以这里反过来做：**把老存档里已经挂上去的发电机组 / 电力节点回收掉**，
    /// 把 <c>EntityData.powerGenId / powerNodeId</c> 清 0，让站点回到「单纯一座物流站」。
    ///
    /// 地热收益并没有丢 —— 它走 <see cref="StationDrill.ChargeGeo"/>：
    /// 功率 = <see cref="C.GeoPowerPerSec"/> × 地热强度，直接充进
    /// <c>StationComponent.energy</c>（站内电池），面板上就是这个数。
    /// 强度 ≥ <see cref="C.GeoSelfPowerMin"/> 时还能脱网自持。
    /// </summary>
    internal static class StationGeo
    {
        private static readonly System.Collections.Generic.HashSet<int> _cleaned =
            new System.Collections.Generic.HashSet<int>();
        private static int _planetId = -1;
        private static bool _failed;

        /// <summary>本站是否还残留着 0.9.5 的发电机组（面板/日志用）。</summary>
        public static bool Active(PlanetFactory factory, int entityId)
        {
            PowerSystem ps = factory != null ? factory.powerSystem : null;
            if (ps == null || ps.genPool == null || factory.entityPool == null)
                return false;
            if (entityId <= 0 || entityId >= factory.entityPool.Length)
                return false;
            int gid = factory.entityPool[entityId].powerGenId;
            return gid > 0 && gid < ps.genPool.Length && ps.genPool[gid].id == gid;
        }

        /// <summary>
        /// 残留机组的功率（MW）。回收干净后恒为 0，保留接口是为了面板不必改类型。
        /// </summary>
        public static float PowerMw(PlanetFactory factory, int entityId)
        {
            PowerSystem ps = factory != null ? factory.powerSystem : null;
            if (ps == null || ps.genPool == null || factory.entityPool == null)
                return 0f;
            if (entityId <= 0 || entityId >= factory.entityPool.Length)
                return 0f;
            int gid = factory.entityPool[entityId].powerGenId;
            if (gid <= 0 || gid >= ps.genPool.Length || ps.genPool[gid].id != gid)
                return 0f;
            PowerGeneratorComponent g = ps.genPool[gid];
            return (float)(g.genEnergyPerTick * 60.0 * g.gthStrength * g.gthAffectStrength * g.warmup) / 1e6f;
        }

        /// <summary>每帧调用（<see cref="StationDrill.Tick"/> 里）。每个站只做一次。</summary>
        public static void Ensure(PlanetFactory factory, StationComponent sc)
        {
            if (_failed || !C.EnableDrill || factory == null || sc == null || sc.entityId <= 0)
                return;
            if (factory.entityPool == null || sc.entityId >= factory.entityPool.Length)
                return;

            int pid = factory.planet != null ? factory.planet.id : 0;
            if (pid != _planetId)
            {
                _planetId = pid;
                _cleaned.Clear();
            }
            if (_cleaned.Contains(sc.entityId))
                return;

            PowerSystem ps = factory.powerSystem;
            if (ps == null)
                return;

            try
            {
                RemoveLegacy(factory, ps, sc.entityId);
                _cleaned.Add(sc.entityId);
            }
            catch (System.Exception e)
            {
                // 回收失败只影响「点击弹出哪个窗口」，不该拖累采矿/发电
                _cleaned.Add(sc.entityId);
                Plugin.Log.LogWarning("回收残留发电机组失败（不影响采矿）: " + e.Message);
            }
        }

        private static void RemoveLegacy(PlanetFactory factory, PowerSystem ps, int eid)
        {
            // ---- ① 电力节点：必须排掉，否则 entity.powerNodeId > 0 会让原版去开「电力节点」窗口
            int node = factory.entityPool[eid].powerNodeId;
            if (node > 0 && ps.nodePool != null && node < ps.nodePool.Length &&
                ps.nodePool[node].id == node && ps.nodePool[node].entityId == eid)
            {
                ps.RemoveNodeComponent(node);
                factory.entityPool[eid].powerNodeId = 0;
                Plugin.Log.LogInfo("已回收站点上的电力节点 " + node + "（原来会让点击弹出「电力节点」窗口）");
            }

            // ---- ② 发电机组：同样排掉，否则原版每帧开关一次「地热发电站」窗口
            int gen = factory.entityPool[eid].powerGenId;
            if (gen > 0 && ps.genPool != null && gen < ps.genPool.Length &&
                ps.genPool[gen].id == gen && ps.genPool[gen].entityId == eid)
            {
                bool wasGeo = ps.genPool[gen].geothermal;
                ps.RemoveGeneratorComponent(gen);
                factory.entityPool[eid].powerGenId = 0;
                Plugin.Log.LogInfo("已回收站点上的" + (wasGeo ? "地热" : "") + "发电机组 " + gen +
                                   "（地热收益改由站内电池直充，见 StationDrill.ChargeGeo）");
            }
        }
    }
}
