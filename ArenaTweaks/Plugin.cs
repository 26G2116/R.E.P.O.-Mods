using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ArenaTweaks
{
    /// <summary>
    /// アリーナレース(Arena Race)とバトルロワイヤル(Disposal Arena)の実験的な調整。ホストだけが入れれば動く。
    /// - レースのクラウン: コースに置かれる貴重品の代わりに、クラウン(敵)を 1 体だけ置く。足場が落ちたら、先のコースに置き直す
    /// - 残機: 倒れても、決められた回数だけ復活する(アリーナは中央、レースはトップの人の少し後ろ)
    /// - アリーナのアイテム: バトルロワイヤルに置かれるアイテムの数・確率と、中央に出る武器を変える
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("shunm.ArenaGrandPrix", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.ArenaTweaks";
        public const string PluginName = "ArenaTweaks";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static Runner Runner;

        // 残機
        internal static ConfigEntry<int> ArenaLives;
        internal static ConfigEntry<int> RaceLives;
        internal static ConfigEntry<float> ReviveSeconds;
        internal static ConfigEntry<Speaker> SpeakerKind;
        internal static ConfigEntry<float> ArenaReviveHeight;
        internal static ConfigEntry<float> RaceReviveBehind;
        internal static ConfigEntry<bool> RaceGroundedOnly;

        // レースのクラウン
        internal static ConfigEntry<bool> ClownEnabled;
        internal static ConfigEntry<string> ClownEnemyName;
        internal static ConfigEntry<int> ClownChance;
        internal static ConfigEntry<int> ClownFirstPiece;
        internal static ConfigEntry<float> ClownCooldown;

        // アリーナのアイテム
        internal static ConfigEntry<bool> ItemsEnabled;
        internal static ConfigEntry<int> UsableCount, UsableChance;
        internal static ConfigEntry<int> MeleeCount, MeleeChance;
        internal static ConfigEntry<int> GunCount, GunChance;
        internal static ConfigEntry<int> CartCount, CartChance;
        internal static ConfigEntry<int> DroneCount, DroneChance;
        internal static ConfigEntry<int> HealthCount, HealthChance;
        internal static ConfigEntry<float> MidInterval;
        internal static ConfigEntry<int> MidStartLevel;
        internal static ConfigEntry<MidKind> MidItems;

        private void Awake()
        {
            Log = Logger;

            const string lives = "残機";
            ArenaLives = Config.Bind(lives, "バトルロワイヤルの残機", 2,
                new ConfigDescription("バトルロワイヤル(Disposal Arena)で倒れても復活できる回数。0 で復活しない", new AcceptableValueRange<int>(0, 20)));
            RaceLives = Config.Bind(lives, "レースの残機", 2,
                new ConfigDescription("アリーナレースで倒れても復活できる回数。0 で復活しない", new AcceptableValueRange<int>(0, 20)));
            ReviveSeconds = Config.Bind(lives, "復活までの秒数", 5f,
                new ConfigDescription("倒れてから復活するまでの秒数(最後の 3 秒は数を読み上げる)", new AcceptableValueRange<float>(2f, 30f)));
            SpeakerKind = Config.Bind(lives, "読み上げる人", Speaker.Self,
                "残機とカウントダウンを読み上げる人(Self = 倒れた本人、Host = ホスト、None = 読み上げない)。" +
                "倒れている人の PC では読み上げが聞こえないので、本人には復活したときの「残り○機」だけが聞こえる");
            ArenaReviveHeight = Config.Bind(lives, "バトルロワイヤルで復活する高さ", 1.5f,
                new ConfigDescription("ステージの中央の床から、どれだけ上で復活するか(m)", new AcceptableValueRange<float>(0.5f, 10f)));
            RaceReviveBehind = Config.Bind(lives, "レースで復活する位置(後ろ)", 6f,
                new ConfigDescription("トップの人の何 m 後ろで復活するか", new AcceptableValueRange<float>(1f, 30f)));
            RaceGroundedOnly = Config.Bind(lives, "レースは走っている人の後ろだけ", true,
                "ON: トップから順に、カートが床に着いている人を探して、その後ろで復活する(ジャンプ中の人の後ろには出ない)");

            const string clown = "レースのクラウン";
            ClownEnabled = Config.Bind(clown, "クラウンを出す", true, "レースのコースに置かれる貴重品の代わりに、クラウンを置く(同時に 1 体まで)");
            ClownEnemyName = Config.Bind(clown, "敵の名前", "Beamer",
                "置く敵(ゲームの敵の設定の名前の一部。Beamer = クラウン)。見つからないときは、ログに選べる名前が出る");
            ClownChance = Config.Bind(clown, "置く確率(%)", 35,
                new ConfigDescription("クラウンがいない(または置き直す)とき、貴重品 1 つをクラウンに変える確率", new AcceptableValueRange<int>(1, 100)));
            ClownFirstPiece = Config.Bind(clown, "置き始めるコースの番号", 3,
                new ConfigDescription("スタートから何個目のコースから置くか", new AcceptableValueRange<int>(0, 50)));
            ClownCooldown = Config.Bind(clown, "置き直すまでの秒数", 4f,
                new ConfigDescription("足場が落ちてクラウンを隠してから、次に置けるようになるまでの秒数", new AcceptableValueRange<float>(0f, 60f)));

            const string items = "アリーナのアイテム";
            ItemsEnabled = Config.Bind(items, "調整する", true, "OFF にすると、ゲームのまま(下の設定を使わない)。既定の数と確率はゲームと同じ");
            BindItem(items, "使うアイテム", 1, 100, out UsableCount, out UsableChance);
            BindItem(items, "近接武器", 5, 100, out MeleeCount, out MeleeChance);
            BindItem(items, "銃", 3, 30, out GunCount, out GunChance);
            BindItem(items, "カート", 1, 30, out CartCount, out CartChance);
            BindItem(items, "ドローン・オーブ", 3, 30, out DroneCount, out DroneChance);
            BindItem(items, "回復", 3, 30, out HealthCount, out HealthChance);
            MidInterval = Config.Bind(items, "中央に武器を出す間隔(秒)", 5f,
                new ConfigDescription("ステージの中央に武器が出てくる間隔。0 で出さない", new AcceptableValueRange<float>(0f, 120f)));
            MidStartLevel = Config.Bind(items, "中央に武器を出し始める段階", 2,
                new ConfigDescription("床が何段階目になってから中央に武器を出すか(ゲームは 2 = 最初の床が落ちたあと)", new AcceptableValueRange<int>(0, 4)));
            MidItems = Config.Bind(items, "中央に出す種類", MidKind.MeleeAndGuns,
                "MeleeAndGuns = 近接武器と銃(ゲームと同じ)、Melee = 近接武器、Guns = 銃、Everything = 上の 6 種類すべて");

            try
            {
                new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony パッチの適用に失敗しました: {e}");
            }

            var runner = new GameObject("ArenaTweaks.Runner");
            runner.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(runner);
            Runner = runner.AddComponent<Runner>();

            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました" +
                        $"(残機: バトルロワイヤル {ArenaLives.Value}・レース {RaceLives.Value}、クラウン: {(ClownEnabled.Value ? "ON" : "OFF")}、" +
                        $"アイテムの調整: {(ItemsEnabled.Value ? "ON" : "OFF")})");
        }

        private void BindItem(string section, string name, int count, int chance, out ConfigEntry<int> countEntry, out ConfigEntry<int> chanceEntry)
        {
            countEntry = Config.Bind(section, $"{name}の数", count,
                new ConfigDescription($"バトルロワイヤルの始まりに置く{name}の数(1 つずつ、下の確率で置くか決める)", new AcceptableValueRange<int>(0, 20)));
            chanceEntry = Config.Bind(section, $"{name}の確率(%)", chance,
                new ConfigDescription($"{name}を 1 つ置く確率", new AcceptableValueRange<int>(0, 100)));
        }
    }

    internal enum Speaker
    {
        Self,
        Host,
        None
    }

    internal enum MidKind
    {
        MeleeAndGuns,
        Melee,
        Guns,
        Everything
    }
}
