using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ArenaGrandPrix
{
    /// <summary>
    /// アリーナレース(Arena Race)とバトルロワイヤル(Disposal Arena)を交互に遊ぶ大会。
    /// ホストがチャットのコマンドで始め、試合ごとの順位でポイントをつけ、トラックのモニターに順位を出す。
    /// 決められた数の試合のあと、ポイントがいちばん多い人(同点なら全員)が決勝のバトルロワイヤルで王冠を取り合う。
    /// ホストだけが入れれば動く(レベルの移動・順位・モニターの文章は、ゲーム本来の通信で全員に届く)。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.ArenaGrandPrix";
        public const string PluginName = "ArenaGrandPrix";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> Command;
        internal static ConfigEntry<int> EventCount;
        internal static ConfigEntry<EventKind> FirstEvent;
        internal static ConfigEntry<bool> FinalEnabled;
        internal static ConfigEntry<string> PointTable;
        internal static ConfigEntry<float> TieSeconds;
        internal static ConfigEntry<bool> HealBeforeEvent;
        internal static ConfigEntry<float> PageSeconds;
        internal static ConfigEntry<int> MonitorTextSize;
        internal static ConfigEntry<int> MonitorLineHeight;

        private void Awake()
        {
            Log = Logger;
            const string rules = "大会";
            Command = Config.Bind(rules, "コマンド", "/rg",
                "ホストがチャットに打つコマンド。「/rg」で大会を始める、「/rg stop」でやめる、「/rg skip」で今の試合をポイントなしで終わらせる");
            EventCount = Config.Bind(rules, "試合の数", 5,
                new ConfigDescription("決勝の前に遊ぶ試合(レースとバトルロワイヤルを交互)の数", new AcceptableValueRange<int>(1, 20)));
            FirstEvent = Config.Bind(rules, "最初の試合", EventKind.Race, "最初に遊ぶ試合(Race = レース、Battle = バトルロワイヤル)。そのあとは交互");
            FinalEnabled = Config.Bind(rules, "決勝", true,
                "試合がすべて終わったら、ポイントがいちばん多い人(同点なら全員)だけでバトルロワイヤルをして王冠を取らせる(ほかの人はすぐ倒れて観戦)");
            PointTable = Config.Bind(rules, "ポイント表", "",
                "1 位から順にもらえるポイントをカンマ区切りで書く(例「10,8,6,5,4,3,2,1」、書いていない順位は 0)。空なら「参加人数 - 順位 + 1」(最下位が 1)");
            TieSeconds = Config.Bind(rules, "同着の秒数", 0.5f,
                new ConfigDescription("この秒数以内に続けて倒れた人は同じ順位にする", new AcceptableValueRange<float>(0f, 5f)));
            HealBeforeEvent = Config.Bind(rules, "試合の前に回復", true, "試合が始まる前に、全員の体力を満タンにする");

            const string monitor = "モニター";
            PageSeconds = Config.Bind(monitor, "ページを切り替える秒数", 10f,
                new ConfigDescription("トラックのモニターの、試合の結果と総合順位のページを切り替える間隔", new AcceptableValueRange<float>(3f, 60f)));
            MonitorTextSize = Config.Bind(monitor, "文字の大きさ(%)", 70,
                new ConfigDescription("モニターに出す順位の文字の大きさの上限(枠に収まらなければ、自動で小さくする)", new AcceptableValueRange<int>(30, 100)));
            MonitorLineHeight = Config.Bind(monitor, "行の高さ(%)", 85,
                new ConfigDescription("順位の 1 行の高さ(ふつうの大きさの文字の行を 100 とする)", new AcceptableValueRange<int>(40, 150)));

            try
            {
                new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError($"Harmony パッチの適用に失敗しました: {e}");
            }

            // BepInEx の管理オブジェクトはシーンの切り替えで壊れることがあるので、自分のオブジェクトで動かす
            var runner = new GameObject("ArenaGrandPrix.Runner");
            runner.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();

            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました(コマンド: {Command.Value})");
        }
    }

    internal enum EventKind
    {
        Race,
        Battle,
    }
}
