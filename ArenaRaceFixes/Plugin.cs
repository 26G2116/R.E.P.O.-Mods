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
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> FixSpawnFix;
        internal static ConfigEntry<bool> FixCosmeticBoxTrap;
        internal static ConfigEntry<SpawnLayout> Layout;
        internal static ConfigEntry<float> ColumnGap;
        internal static ConfigEntry<float> RowGap;

        private void Awake()
        {
            Log = Logger;
            FixSpawnFix = Config.Bind("修正", "ArenaRaceMultiplayerSpawnFix を直す", true,
                "ArenaRaceMultiplayerSpawnFix の並べ直しを、今のゲームの通信の形で送る(変更はゲームの再起動後)");
            Layout = Config.Bind("レースの開始位置", "並べ方", SpawnLayout.Track,
                "Track = スタートの箱の下のコースの上に、ArenaRaceMultiplayerSpawnFix の「横の人数」で並べる(おすすめ。大人数でも入る)、" +
                "Game = ゲームの開始位置(箱の中)のまま、Spread = 箱の中で重なった人だけ横にずらす、Original = 元の MOD の並べ方(今のゲームでは箱の外になる)");
            ColumnGap = Config.Bind("レースの開始位置", "横の間隔", 1.6f,
                new ConfigDescription("Track のとき、横に並ぶ人の間隔(m)。コースからはみ出す場所には置かず、その分の列を増やす", new AcceptableValueRange<float>(1f, 5f)));
            RowGap = Config.Bind("レースの開始位置", "縦の間隔", 3f,
                new ConfigDescription("Track のとき、列と列の間隔(m)", new AcceptableValueRange<float>(2f, 8f)));
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
        Track,
        Game,
        Spread,
        Original
    }
}
