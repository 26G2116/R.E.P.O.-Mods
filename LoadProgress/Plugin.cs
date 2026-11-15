using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace LoadProgress
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.LoadProgress";
        public const string PluginName = "LoadProgress";
        public const string PluginVersion = "0.4.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShareProgress;
        internal static ConfigEntry<float> PositionX;
        internal static ConfigEntry<float> PositionY;
        internal static ConfigEntry<float> Scale;
        internal static ConfigEntry<float> LingerSeconds;
        internal static ConfigEntry<bool> LogTransitions;
        internal static ConfigEntry<bool> LateJoinEnabled;
        internal static ConfigEntry<float> LateJoinWarnSeconds;
        internal static ConfigEntry<float> LateJoinStuckSeconds;
        internal static ConfigEntry<float> LateJoinHideSeconds;
        internal static ConfigEntry<bool> LateJoinMapResend;
        internal static ConfigEntry<float> LateJoinMapResendSeconds;
        internal static ConfigEntry<bool> ObserveEnabled;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "MOD の機能を有効にするか(変更はゲーム再起動後に反映)");
            ShareProgress = Config.Bind("Network", "ShareProgress", true,
                "自分の進捗(ホストなら全員の進捗)を、MOD を入れている他のプレイヤーに送るか");
            PositionX = Config.Bind("Display", "PositionX", 20f, "表示位置 X(画面左上からのピクセル)");
            PositionY = Config.Bind("Display", "PositionY", 20f, "表示位置 Y(画面左上からのピクセル)");
            Scale = Config.Bind("Display", "Scale", 1f, "表示の大きさ(1080p 基準の倍率)");
            LingerSeconds = Config.Bind("Display", "LingerSeconds", 2f, "ロード完了後に表示を残す秒数");
            LogTransitions = Config.Bind("Debug", "LogTransitions", true,
                "各プレイヤーのステップ進行をログ(LogOutput.log)に出す");
            LateJoinEnabled = Config.Bind("LateJoin", "Enabled", true,
                "レベルの途中で入ってきた人(途中参加 MOD)の読み込みの進み具合を表示する(参加者が MOD なしでも分かる)");
            LateJoinWarnSeconds = Config.Bind("LateJoin", "WarnSeconds", 20f,
                "途中参加の人が 1 つの段階でこの秒数止まったら、橙色にしてログに出す");
            LateJoinStuckSeconds = Config.Bind("LateJoin", "StuckSeconds", 45f,
                "途中参加の人が 1 つの段階でこの秒数止まったら、赤色にしてログに出す(読み込みが終わらない可能性)");
            LateJoinHideSeconds = Config.Bind("LateJoin", "HideSeconds", 5f,
                "途中参加の人の読み込みが終わった(または抜けた)あと、表示を残す秒数");
            LateJoinMapResend = Config.Bind("LateJoin", "MapResend", true,
                "(ホスト)途中参加の人が「マップ受信」で止まったら、足りないかもしれないマップの情報を送り直す。パーツの数をログに出す");
            LateJoinMapResendSeconds = Config.Bind("LateJoin", "MapResendSeconds", 8f,
                "(ホスト)参加からこの秒数たっても「マップ受信」が終わらなければ送り直す(10 秒ごとに最大 3 回)");
            ObserveEnabled = Config.Bind("Observe", "Enabled", true,
                "入った部屋の様子をログ(LogOutput.log)に残す: ステージ名、チャット、トラックの画面の文字、部屋のカスタムプロパティ、" +
                "この PC にない MOD の通信の回数。自分の PC のログに書くだけで、外には送らない(変更はゲーム再起動後に反映)");

            if (!Enabled.Value)
            {
                Log.LogInfo($"{PluginName} は設定で無効になっています");
                return;
            }

            // BepInEx の管理オブジェクトはシーン遷移で壊れることがあるので、専用オブジェクトを作る
            var go = new GameObject("LoadProgress");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<ProgressTracker>();
            go.AddComponent<LateJoinTracker>();
            if (ObserveEnabled.Value)
            {
                go.AddComponent<RoomObserver>();
            }
            new HarmonyLib.Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました");
        }
    }
}
