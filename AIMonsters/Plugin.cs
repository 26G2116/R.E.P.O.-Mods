using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AIMonsters
{
    /// <summary>
    /// Azure OpenAI のモデルに敵を動かさせる MOD。ホストだけが入れる(敵の AI も API の呼び出しもホストだけ)。
    /// - 敵の AI: 対象の敵がプレイヤーを見つけ始めたら、2 秒ごとに状況を送って方針を決めさせる(Director)
    /// - 味方の AI ローブ: あとで追加
    /// API キーはホストの PC の環境変数から読む(MOD・設定ファイルには入れない)。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shunm.AIMonsters";
        public const string PluginName = "AIMonsters";
        public const string PluginVersion = "0.7.0";

        private const string General = "0. 全体";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLog;
        internal static ConfigEntry<string> Deployment;
        internal static ConfigEntry<string> ReasoningEffort;
        internal static ConfigEntry<string> ApiVersion;
        internal static ConfigEntry<string> EndpointVariable;
        internal static ConfigEntry<string> KeyVariable;
        internal static ConfigEntry<float> Timeout;
        internal static ConfigEntry<int> MaxTokens;

        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind(General, "有効", true, "この MOD を使う(OFF にすると API を呼ばず、敵は普段どおりに動く)");
            VerboseLog = Config.Bind(General, "詳しいログ", false, "AI の判断や API の使用量をログに出す(動作確認用)");
            Deployment = Config.Bind(General, "デプロイ名", "gpt-6-luna", "Azure OpenAI のデプロイ名");
            ReasoningEffort = Config.Bind(General, "思考量", "none",
                new ConfigDescription("モデルの思考量。none が速い(low は 3〜4 秒かかり、2 秒ごとの判断に間に合わない)。空にすると送らない",
                    new AcceptableValueList<string>("none", "low", "medium", "high", "")));
            ApiVersion = Config.Bind(General, "API のバージョン", "2025-04-01-preview", "Azure OpenAI の api-version");
            EndpointVariable = Config.Bind(General, "エンドポイントの環境変数", "AZURE_OPENAI_ENDPOINT",
                "Azure OpenAI のエンドポイント(https://<リソース名>.openai.azure.com/)を入れた環境変数の名前");
            KeyVariable = Config.Bind(General, "API キーの環境変数", "AZURE_OPENAI_API_KEY",
                "API キーを入れた環境変数の名前(キーそのものはここに書かない)");
            Timeout = Config.Bind(General, "タイムアウト(秒)", 6f,
                new ConfigDescription("API の返事を待つ最長の時間。過ぎたらその回はあきらめる", new AcceptableValueRange<float>(1f, 30f)));
            MaxTokens = Config.Bind(General, "返事の最大トークン", 400,
                new ConfigDescription("1 回の返事の長さの上限", new AcceptableValueRange<int>(50, 4000)));

            Director.Bind(Config);
            LoomDriver.Bind(Config);
            RobeDriver.Bind(Config);
            CleanupCrewDriver.Bind(Config);
            AllyRobe.Bind(Config);
            AllyChat.Bind(Config);

            Runner.Create();
            harmony = new Harmony(PluginGuid);
            PatchEach();
            Log.LogInfo($"{PluginName} {PluginVersion} を読み込みました");
        }

        /// <summary>パッチをクラスごとに当てる。ゲームの更新で 1 つが壊れても、ほかは動くようにする。</summary>
        private void PatchEach()
        {
            var types = typeof(Plugin).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0);
            foreach (var type in types)
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    Log.LogError($"{type.Name} のパッチに失敗しました: {e.Message}");
                }
            }
        }

        internal static void Verbose(string message)
        {
            if (VerboseLog.Value)
            {
                Log.LogInfo(message);
            }
        }
    }

    /// <summary>コルーチンと毎フレームの処理を動かす、シーンをまたいで残るオブジェクト。</summary>
    internal sealed class Runner : MonoBehaviour
    {
        internal static Runner Instance { get; private set; }

        internal static void Create()
        {
            if (Instance)
            {
                return;
            }
            var go = new GameObject("AIMonsters.Runner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<Runner>();
        }

        private float nextErrorLog;

        private void Update()
        {
            try
            {
                Director.Tick();
                AllyRobe.Tick();
                AllyChat.Tick();
            }
            catch (Exception e)
            {
                // 毎フレーム同じエラーでログを埋めない
                if (Time.realtimeSinceStartup >= nextErrorLog)
                {
                    nextErrorLog = Time.realtimeSinceStartup + 5f;
                    Plugin.Log.LogError($"敵の AI の処理でエラー: {e}");
                }
            }
        }
    }
}
