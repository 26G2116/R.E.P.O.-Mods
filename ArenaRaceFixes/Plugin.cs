using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ArenaRaceFixes
{
    /// <summary>
    /// 2026 年 11 月 5 日のゲームの更新で動かなくなった、REPO_JP さんのアリーナレース用 MOD の修正。
    /// 元の MOD はそのまま入れておき、壊れた処理だけを差し替える(元の MOD が入っていなければ何もしない)。
    /// - ArenaRaceMultiplayerSpawnFix: 並べ直しの通信(SpawnRPC)を今のゲームの形で送る
    /// - ArenaRaceCosmeticBoxTrap: コスメボックスに触れたときの爆発を今のゲームの処理で起こす
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(SpawnFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(CosmeticBoxTrap.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.ArenaRaceFixes";
        public const string PluginName = "ArenaRaceFixes";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> FixSpawnFix;
        internal static ConfigEntry<bool> FixCosmeticBoxTrap;
        internal static ConfigEntry<SpawnLayout> Layout;

        private void Awake()
        {
            Log = Logger;
            FixSpawnFix = Config.Bind("修正", "ArenaRaceMultiplayerSpawnFix を直す", true,
                "ArenaRaceMultiplayerSpawnFix の並べ直しを、今のゲームの通信の形で送る(変更はゲームの再起動後)");
            Layout = Config.Bind("修正", "レースの開始位置", SpawnLayout.Game,
                "ArenaRaceMultiplayerSpawnFix の並べ直しをどうするか。Game = ゲームの開始位置のまま(おすすめ)、" +
                "Spread = ゲームの開始位置で、重なった人だけ横にずらす、Original = 元の MOD の並べ方(今のゲームでは、スタートの部屋の外になる)");
            FixCosmeticBoxTrap = Config.Bind("修正", "ArenaRaceCosmeticBoxTrap を直す", true,
                "ArenaRaceCosmeticBoxTrap のコスメボックスが、触れたときに爆発するようにする(変更はゲームの再起動後)");

            var harmony = new Harmony(PluginGuid);
            if (FixSpawnFix.Value)
            {
                Apply(harmony, "ArenaRaceMultiplayerSpawnFix", SpawnFix.Patch);
            }
            if (FixCosmeticBoxTrap.Value)
            {
                Apply(harmony, "ArenaRaceCosmeticBoxTrap", CosmeticBoxTrap.Patch);
            }
            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました");
        }

        private static void Apply(Harmony harmony, string name, Func<Harmony, bool> patch)
        {
            try
            {
                if (patch(harmony))
                {
                    Log.LogInfo($"{name} を今のゲームに合わせて直しました");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"{name} を直せませんでした: {e}");
            }
        }
    }

    internal enum SpawnLayout
    {
        Game,
        Spread,
        Original
    }
}
