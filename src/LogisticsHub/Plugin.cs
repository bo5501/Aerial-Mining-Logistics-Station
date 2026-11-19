using System;
using BepInEx;
using BepInEx.Logging;
using CommonAPI;
using CommonAPI.Systems;
using HarmonyLib;
using xiaoye97;

namespace LogisticsHub
{
    [BepInPlugin(C.Guid, C.Name, C.Version)]
    [BepInDependency(CommonAPIPlugin.GUID)]
    [BepInDependency(LDBToolPlugin.MODGUID)]
    [CommonAPISubmoduleDependency(nameof(ProtoRegistry))]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        public static Plugin Instance;

        /// <summary>
        /// 是否允许 LDBTool 用 <c>config/LDBTool/LDBTool.CustomID.cfg</c> 覆盖我们填的 proto ID。
        /// 默认 false（跟 ProjectGenesis 的 UseLDBToolCache 默认一致）：一开就会把科技 ID
        /// 改回配置里的历史值，节点跑到升级页 + Preload2 找不到它 = 科技树 NRE。
        /// </summary>
        public static bool UseLdbToolIds;

        private static Harmony _harmony;
        private static bool _iconApplied;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Log.LogInfo(C.Name + " v" + C.Version + " 启动中…");

            try
            {
                UseLdbToolIds = Config.Bind("LDBTool", "UseLDBToolCustomID", false,
                    "允许 LDBTool 用 config/LDBTool/LDBTool.CustomID.cfg 覆盖本 mod 的 proto ID。\n" +
                    "默认关闭：开着的话，配置里残留的旧科技 ID（如 3996）会把科技节点顶到升级页，" +
                    "并导致科技树 NullReferenceException。").Value;
            }
            catch (Exception e)
            {
                Log.LogWarning("读取配置失败: " + e.Message);
            }

            try
            {
                _harmony = new Harmony(C.Guid);

                // 逐个补丁类落地：PatchAll 是一条链，中间任何一个类打不上（例如原版把方法改名了）
                // 异常会冒出来，排在它后面的补丁就全被丢掉。0.10.35 就踩过：UIStationWindow._Close
                // 改成了 _OnClose，于是 Patch_StationCloseDiag 抛异常，收尾的看门狗补丁一起失联。
                int ok = 0, skip = 0;
                foreach (Type type in typeof(Plugin).Assembly.GetTypes())
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                        continue;
                    try
                    {
                        _harmony.CreateClassProcessor(type).Patch();
                        ok++;
                    }
                    catch (Exception e)
                    {
                        skip++;
                        Log.LogWarning("补丁 " + type.Name + " 跳过（目标方法不存在？）: " + e.Message);
                    }
                }

                int n = 0;
                foreach (var _ in _harmony.GetPatchedMethods()) n++;
                Log.LogInfo("Harmony patch 完成: " + ok + " 个补丁类 / " + n + " 个目标" +
                            (skip > 0 ? "（跳过 " + skip + " 个）" : ""));
            }
            catch (Exception e)
            {
                Log.LogError("Harmony patch 失败: " + e);
            }

            try
            {
                // 字符串要早注册（CommonAPI 的字符串表在加载阶段就消费了），
                // proto 本身则等 LDBTool 的 PreAddDataAction（见下）。
                Protos.EnsureStrings();
            }
            catch (Exception e)
            {
                Log.LogError("字符串注册失败: " + e);
            }

            try
            {
                // 照 ProjectGenesis 的时机：注册推迟到 LDBTool 真正要追加 proto 的那一刻
                // （VFPreload.InvokeOnLoadWorkEnded 前缀里的 PreAddDataAction）。
                // 这时 LDB 已经反序列化完，FindGasGiantTechId / FindTechSpot 才能读到真实数据；
                // 原来在 Awake 里注册时 LDB.techs 还是空的，前置和位置全填了默认值，只能事后补。
                LDBTool.PreAddDataAction += Protos.RegisterAll;
                LDBTool.PostAddDataAction += Protos.PreloadAll;
            }
            catch (Exception e)
            {
                Log.LogError("proto 注册失败: " + e);
            }

            try
            {
                StationTurrets.Ensure();       // 炮塔 360° 旋转 + 中央护盾的运行时宿主
            }
            catch (Exception e)
            {
                Log.LogError("炮塔/护盾宿主创建失败: " + e);
            }
        }

        private void Update()
        {
            if (!_iconApplied && LDB.items?.dataArray != null && LDB.items.dataArray.Length > 0)
            {
                _iconApplied = true;
                Protos.EnsureIcon();
            }

            // 兜底：万一 LDBTool 的 PreAddDataAction 没跑到（插件晚加载 / 热重载），
            // 进游戏后补一次注册 + 预加载。
            if (!Protos.Registered && GameMain.data != null)
                Protos.RegisterAll();

            if (!Protos.IsPreloaded && GameMain.data != null)
                Protos.PreloadAll();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
