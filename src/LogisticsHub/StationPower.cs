using System.Collections.Generic;
using UnityEngine;

namespace LogisticsHub
{
    /// <summary>
    /// 站体接进电网（0.9.9）。
    ///
    /// 玩家反馈：卫星配电站 / 无线输电塔（无线输电）盖在旁边，站体就是没电。
    /// 原版这套逻辑在 <c>PowerSystem</c> 里只有两步：
    ///   ① 建消费者时把**建站那一刻**的 <c>entityPool[entityId].pos</c> 存成
    ///      <c>consumerPool[pcId].plugPos</c>（<see cref="PowerSystem.NewConsumerComponent"/>）；
    ///   ② 加节点时拿「节点的无线覆盖球」逐个比对 <c>plugPos</c>，命中才把这个消费者
    ///      挂进网络（<c>OnNodeAdded</c> / <c>OnConsumerAdded</c>）。
    ///
    /// 站体是我们自己造的 300 m 高塔，只要那个 plugPos 快照跟实体当前位置对不上
    /// （挪过的老存档、蓝图建造、别的 mod 改过实体坐标……），比对就会全落空 ——
    /// 表现就是「无线输电接不上，接上电以后又莫名其妙掉了」。
    ///
    /// 这里做两件事，都是只碰自己这个消费者，不改电网结构：
    ///   A. plugPos 和实体当前位置差 1 m 以上时纠正回来；
    ///   B. 仍然没挂进任何网络（networkId == 0）时，找一座覆盖半径够得着的节点，
    ///      按 <c>OnNodeAdded</c> 的做法把它加进 <c>PowerNetwork.consumers</c> 并写 networkId。
    ///
    /// 已经接好的站一个字节都不动；2 s 检查一次，失败/成功都只打一次日志。
    /// </summary>
    internal static class StationPower
    {
        /// <summary>覆盖半径余量（m）：基座直径约 19 m，玩家会把配电站放在基座外侧，
        /// 按「点对点」比距离容易刚好差几米，这里补一点。</summary>
        private const float CoverPad = 10f;

        private static readonly Dictionary<int, float> _next = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> _logged = new Dictionary<int, int>(); // 1=已接 2=没找到
        private static int _planetId = -1;

        /// <summary>每帧由 <see cref="StationDrill.Tick"/> 调（内部 2 s 节流）。</summary>
        internal static void Ensure(PlanetFactory factory, StationComponent sc)
        {
            if (factory == null || sc == null || sc.pcId <= 0 || sc.entityId <= 0)
                return;
            PowerSystem ps = factory.powerSystem;
            if (ps == null || ps.consumerPool == null || ps.netPool == null || factory.entityPool == null)
                return;

            int pid = factory.planet != null ? factory.planet.id : 0;
            if (pid != _planetId)
            {
                _planetId = pid;
                _next.Clear();
                _logged.Clear();
            }

            int key = sc.entityId;
            float now = Time.unscaledTime;
            float at;
            _next.TryGetValue(key, out at);
            if (now < at)
                return;
            _next[key] = now + 2f;

            if (sc.pcId >= ps.consumerPool.Length)
                return;
            PowerConsumerComponent c = ps.consumerPool[sc.pcId];
            if (c.id != sc.pcId || c.entityId != sc.entityId)
                return;                                  // 组件被回收过，等下一轮重建

            // ---- A. 纠正 plug 点 ----
            Vector3 pos = factory.entityPool[sc.entityId].pos;
            if (pos.sqrMagnitude > 1f && (c.plugPos - pos).sqrMagnitude > 1f)
            {
                ps.consumerPool[sc.pcId].plugPos = pos;
                ps.consumerPool[sc.pcId].plugAlt = pos.magnitude;
                c = ps.consumerPool[sc.pcId];
                Plugin.Log.LogInfo("站体电力: 站 id=" + sc.id + " 的 plug 点与实体位置不符，已纠正（" +
                                   pos.magnitude.ToString("0.0") + " m）");
            }

            // ---- 已经挂进某个网络：不动 ----
            if (InNetwork(ps, c.networkId))
            {
                if (!_logged.ContainsKey(key))
                {
                    _logged[key] = 1;
                    Plugin.Log.LogInfo("站体电力: 站 id=" + sc.id + " 已接入电网 net=" + c.networkId +
                                       "（plugAlt " + c.plugAlt.ToString("0.0") + " m）");
                }
                return;
            }

            // ---- B. 无线覆盖兜底 ----
            int foundNet = 0;
            float foundR = 0f, foundD = 0f;
            try
            {
                float num1 = factory.planet != null ? factory.planet.realRadius + 0.2f : 200.2f;
                float alt = c.plugAlt > 0.1f ? c.plugAlt : pos.magnitude;
                if (alt < 0.1f)
                    return;
                // 归一化坐标：和 PowerSystem.OnNodeAdded 里比对节点的坐标系一致。
                Vector3 cp = c.plugPos / (alt / num1);

                for (int n = 1; n < ps.netPool.Length; n++)
                {
                    PowerNetwork pn = ps.netPool[n];
                    if (pn == null || pn.id != n || pn.nodes == null)
                        continue;
                    for (int i = 0; i < pn.nodes.Count; i++)
                    {
                        PowerNetworkStructures.Node node = pn.nodes[i];
                        if (node == null)
                            continue;
                        float r = Mathf.Sqrt(node.coverRadius2);
                        if (r <= 0.5f)
                            continue;                    // 不是无线节点（普通输电塔覆盖 0）
                        var np = new Vector3(node.x, node.y, node.z);
                        float d = Vector3.Distance(cp, np);
                        if (d > r + CoverPad)
                            continue;
                        if (foundNet == 0 || r > foundR)
                        {
                            foundNet = n;
                            foundR = r;
                            foundD = d;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("站体电力: 扫描无线覆盖失败 — " + e.Message);
                return;
            }

            if (foundNet <= 0)
            {
                int st;
                if (!_logged.TryGetValue(key, out st) || st != 2)
                {
                    _logged[key] = 2;
                    Plugin.Log.LogInfo("站体电力: 站 id=" + sc.id + " 不在任何无线覆盖内" +
                                       "（pcId=" + sc.pcId + " networkId=" + c.networkId +
                                       " plugAlt=" + c.plugAlt.ToString("0.0") + " m）");
                }
                return;
            }

            try
            {
                PowerNetwork target = ps.netPool[foundNet];
                // 0 号网络是「还没接进任何电网」的候车室，挂进正式网络后必须从这里摘掉，
                // 否则下次有人加节点时原版还会把它再挂一次，同一个消费者会出现在两个网络里。
                if (ps.netPool[0] != null)
                    ps.netPool[0].consumers.Remove(sc.pcId);
                int idx = target.consumers.BinarySearch(sc.pcId);
                if (idx < 0)
                    target.consumers.Insert(~idx, sc.pcId);   // 和 list_sorted_add 一样保持有序
                ps.consumerPool[sc.pcId].networkId = foundNet;
                _logged[key] = 1;
                Plugin.Log.LogInfo("站体电力: 站 id=" + sc.id + " 接入无线覆盖 net=" + foundNet +
                                   "（覆盖 " + foundR.ToString("0") + " m，距离 " + foundD.ToString("0.0") + " m）");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("站体电力: 接入电网失败 — " + e.Message);
            }
        }

        private static bool InNetwork(PowerSystem ps, int net)
        {
            return net > 0 && net < ps.netPool.Length &&
                   ps.netPool[net] != null && ps.netPool[net].id == net;
        }

        /// <summary>换星球 / 拆站时清缓存。</summary>
        internal static void Forget(int entityId)
        {
            _next.Remove(entityId);
            _logged.Remove(entityId);
        }
    }
}
