using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace LoadProgress
{
    /// <summary>
    /// 入った部屋の様子をログに残す(自分の PC のログだけ。外には送らない)。
    /// 他の人の部屋で、ホストがどんな MOD で何をしているかを、あとからログで調べるためのもの。
    /// - ステージが始まるたびに: ステージ名、ホスト、人数
    /// - 部屋とホストのカスタムプロパティ(MOD が書き込むことがある)。入ったときと、変わったとき
    /// - チャット(ホストの MOD がプレイヤーにしゃべらせた分も含む)と、トラックの画面に出た文字
    /// - ステージが終わるたびに: 処理できなかった通信(この PC にない MOD の通信)と、MOD のイベント番号の回数
    /// </summary>
    internal class RoomObserver : MonoBehaviour
    {
        internal static RoomObserver Instance;

        private const string Tag = "[部屋の記録]";
        private const int MaxValueLength = 80;
        private const int MaxSummaryRows = 15;
        private const float PropertyCheckInterval = 5f;

        // 「Received RPC "X" for viewID N but this PhotonView does not exist! ... By: #NN 'name'」
        private static readonly Regex MissingViewRegex =
            new Regex("^Received RPC \"([^\"]+)\" for viewID (\\d+) but this PhotonView does not exist!.*By: #\\d+ '(.*)'", RegexOptions.Compiled);
        // 「RPC method 'X(Args)' not found on object with PhotonView N」/「... found on object ... but has wrong parameters」
        private static readonly Regex MissingMethodRegex =
            new Regex("^RPC method '([^']+)' (not found|found) on object with PhotonView (\\d+)", RegexOptions.Compiled);

        private string roomName;
        private LevelGenerator observedLevel;
        private bool levelStarted;
        private float levelStartTime;
        private string levelLabel;
        private bool eventSubscribed;
        private float nextPropertyCheck;
        private readonly Dictionary<string, string> lastProps = new Dictionary<string, string>();
        private readonly Dictionary<string, int> rpcCounts = new Dictionary<string, int>();
        private readonly Dictionary<string, int> eventCounts = new Dictionary<string, int>();
        private HashSet<string> vanillaRpcNames;

        private void Awake()
        {
            Instance = this;
        }

        private void OnEnable()
        {
            Application.logMessageReceived += OnLog;
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= OnLog;
            if (eventSubscribed && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
            }
            eventSubscribed = false;
        }

        private void OnApplicationQuit()
        {
            EndLevel("ゲーム終了");
        }

        private static bool InMultiplayerRoom()
        {
            return PhotonNetwork.InRoom && GameManager.instance != null && SemiFunc.IsMultiplayer();
        }

        private void Update()
        {
            if (!eventSubscribed && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
                eventSubscribed = true;
            }

            if (!InMultiplayerRoom())
            {
                if (roomName != null)
                {
                    EndLevel("部屋を出た");
                    Plugin.Log.LogInfo($"{Tag} 部屋を出ました");
                    roomName = null;
                    lastProps.Clear();
                }
                return;
            }

            if (roomName != PhotonNetwork.CurrentRoom.Name)
            {
                EndLevel("部屋が変わった");
                roomName = PhotonNetwork.CurrentRoom.Name;
                lastProps.Clear();
                Plugin.Log.LogInfo($"{Tag} 部屋に入りました: ホスト {MasterName()}、{PhotonNetwork.CurrentRoom.PlayerCount} 人" +
                                   $"(自分がホスト: {(PhotonNetwork.IsMasterClient ? "はい" : "いいえ")})");
                LogPropertyChanges();
            }
            else if (Time.unscaledTime >= nextPropertyCheck)
            {
                LogPropertyChanges(); // ステージの途中で変わった分も残す
            }

            LevelGenerator lg = LevelGenerator.Instance;
            if (lg != observedLevel)
            {
                EndLevel("次のステージ");
                observedLevel = lg;
                levelStarted = false;
            }
            if (lg != null && !levelStarted && lg.Generated)
            {
                levelStarted = true;
                levelStartTime = Time.unscaledTime;
                levelLabel = LevelLabel(lg);
                Plugin.Log.LogInfo($"{Tag} ステージ開始: {levelLabel}、ホスト {MasterName()}、{PhotonNetwork.CurrentRoom.PlayerCount} 人");
                LogPropertyChanges();
            }
        }

        private static string LevelLabel(LevelGenerator lg)
        {
            Level level = lg.Level != null ? lg.Level : RunManager.instance != null ? RunManager.instance.levelCurrent : null;
            if (level == null)
            {
                return "(不明)";
            }
            string label = level.name;
            if (!string.IsNullOrEmpty(level.NarrativeName) && level.NarrativeName != level.name)
            {
                label += $" / {level.NarrativeName}";
            }
            if (RunManager.instance != null)
            {
                label += $"(クリアしたレベル数 {RunManager.instance.levelsCompleted})";
            }
            return label;
        }

        private static string MasterName()
        {
            var master = PhotonNetwork.MasterClient;
            return master == null ? "(不明)" : $"{master.NickName}(#{master.ActorNumber})";
        }

        // ---------------------------------------------------------------- カスタムプロパティ

        private void LogPropertyChanges()
        {
            nextPropertyCheck = Time.unscaledTime + PropertyCheckInterval;
            var current = new Dictionary<string, string>();
            Collect(current, "部屋", PhotonNetwork.CurrentRoom.CustomProperties);
            if (PhotonNetwork.MasterClient != null)
            {
                Collect(current, "ホスト", PhotonNetwork.MasterClient.CustomProperties);
            }
            var lines = new List<string>();
            foreach (var pair in current)
            {
                if (!lastProps.TryGetValue(pair.Key, out string old) || old != pair.Value)
                {
                    lines.Add($"  {pair.Key} = {pair.Value}");
                }
            }
            foreach (var key in lastProps.Keys)
            {
                if (!current.ContainsKey(key))
                {
                    lines.Add($"  {key}(消えた)");
                }
            }
            lastProps.Clear();
            foreach (var pair in current)
            {
                lastProps[pair.Key] = pair.Value;
            }
            if (lines.Count > 0)
            {
                Plugin.Log.LogInfo($"{Tag} カスタムプロパティ({lines.Count} 件の変化):\n" + string.Join("\n", lines));
            }
        }

        private static void Collect(Dictionary<string, string> into, string owner, Hashtable props)
        {
            if (props == null)
            {
                return;
            }
            foreach (var entry in props)
            {
                into[$"{owner}.{entry.Key}"] = Shorten(ValueText(entry.Value));
            }
        }

        private static string ValueText(object value)
        {
            if (value == null)
            {
                return "null";
            }
            if (value is string s)
            {
                return $"\"{s}\"";
            }
            if (value is System.Array array)
            {
                var parts = new List<string>();
                foreach (var item in array)
                {
                    parts.Add(item?.ToString() ?? "null");
                    if (parts.Count >= 8)
                    {
                        parts.Add("…");
                        break;
                    }
                }
                return $"{value.GetType().GetElementType()?.Name}[{array.Length}] {{{string.Join(", ", parts)}}}";
            }
            return $"{value}({value.GetType().Name})";
        }

        private static string Shorten(string text)
        {
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= MaxValueLength ? text : text.Substring(0, MaxValueLength) + "…";
        }

        // ---------------------------------------------------------------- チャットとトラックの画面

        private void LogChat(PlayerAvatar avatar, string message, PhotonMessageInfo info)
        {
            if (!InMultiplayerRoom() || avatar == null)
            {
                return;
            }
            string name = avatar.playerName;
            string via = "";
            var owner = avatar.photonView != null ? avatar.photonView.Owner : null;
            if (info.Sender != null && owner != null && info.Sender != owner)
            {
                via = $"(送ったのは {info.Sender.NickName})"; // ホストの MOD がしゃべらせたもの
            }
            Plugin.Log.LogInfo($"{Tag} チャット {name}{via}: {Shorten(message ?? "")}");
        }

        private void LogTruckMessage(string playerName, string message, PhotonMessageInfo info)
        {
            if (!InMultiplayerRoom())
            {
                return;
            }
            string from = info.Sender != null ? info.Sender.NickName : "(自分)";
            Plugin.Log.LogInfo($"{Tag} トラックの画面 {playerName}(送ったのは {from}): {Shorten(message ?? "")}");
        }

        [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ChatMessageSendRPC))]
        private static class ChatPatch
        {
            private static void Prefix(PlayerAvatar __instance, string _message, PhotonMessageInfo _info)
            {
                Instance?.LogChat(__instance, _message, _info);
            }
        }

        [HarmonyPatch(typeof(TruckScreenText), nameof(TruckScreenText.MessageSendCustomRPC))]
        private static class TruckMessagePatch
        {
            private static void Prefix(TruckScreenText __instance, string playerName, string message, PhotonMessageInfo _info)
            {
                if (__instance != null && __instance.isTyping)
                {
                    return; // 画面が文字を打っている間は、ゲームがこのメッセージを捨てる
                }
                Instance?.LogTruckMessage(playerName, message, _info);
            }
        }

        // ---------------------------------------------------------------- 処理できなかった通信とイベント

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (roomName == null || (type != LogType.Warning && type != LogType.Error) || string.IsNullOrEmpty(condition)
                || !condition.StartsWith("R"))
            {
                return;
            }
            Match m = MissingViewRegex.Match(condition);
            if (m.Success)
            {
                Count(rpcCounts, $"{m.Groups[1].Value}|窓口 {m.Groups[2].Value} がない|{m.Groups[3].Value}");
                return;
            }
            m = MissingMethodRegex.Match(condition);
            if (m.Success)
            {
                string reason = m.Groups[2].Value == "found" ? "引数が違う" : "受け取る処理がない";
                Count(rpcCounts, $"{m.Groups[1].Value}|{reason}|");
            }
        }

        private void OnEvent(EventData data)
        {
            // 200 以上は Photon 自身、123・124・199(キック)はゲーム本体、ProgressTracker.EventCode・MapReport.EventCode はこの MOD が使う番号
            if (roomName == null || data.Code >= 200 || data.Code == 123 || data.Code == 124 || data.Code == 199
                || data.Code == ProgressTracker.EventCode || data.Code == MapReport.EventCode)
            {
                return;
            }
            var player = PhotonNetwork.CurrentRoom?.GetPlayer(data.Sender);
            Count(eventCounts, $"{data.Code}|{(player != null ? player.NickName : $"#{data.Sender}")}");
        }

        private static void Count(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        private void EndLevel(string reason)
        {
            if (levelStarted || rpcCounts.Count > 0 || eventCounts.Count > 0)
            {
                var sb = new StringBuilder();
                string duration = levelStarted ? $"{Time.unscaledTime - levelStartTime:0}s" : "-";
                sb.Append($"{Tag} ステージ終了({reason}): {levelLabel ?? "(ステージ外)"}、遊んだ時間 {duration}");
                AppendRpcSummary(sb);
                AppendEventSummary(sb);
                Plugin.Log.LogInfo(sb.ToString());
            }
            rpcCounts.Clear();
            eventCounts.Clear();
            levelStarted = false;
            levelLabel = null;
        }

        private void AppendRpcSummary(StringBuilder sb)
        {
            if (rpcCounts.Count == 0)
            {
                return;
            }
            sb.Append("\n  処理できなかった通信(回数、名前、理由、送った人。[MOD] はゲーム本体にない名前):");
            foreach (var pair in rpcCounts.OrderByDescending(p => p.Value).Take(MaxSummaryRows))
            {
                string[] parts = pair.Key.Split('|');
                string name = parts[0];
                int paren = name.IndexOf('(');
                string bare = paren >= 0 ? name.Substring(0, paren) : name;
                string mod = IsVanillaRpc(bare) ? "" : " [MOD]";
                string sender = parts[2].Length > 0 ? $"、{parts[2]}" : "";
                sb.Append($"\n    {pair.Value,5} {name}{mod}、{parts[1]}{sender}");
            }
            if (rpcCounts.Count > MaxSummaryRows)
            {
                sb.Append($"\n    …ほか {rpcCounts.Count - MaxSummaryRows} 種類");
            }
        }

        private void AppendEventSummary(StringBuilder sb)
        {
            if (eventCounts.Count == 0)
            {
                return;
            }
            sb.Append("\n  MOD のイベント(回数、番号、送った人):");
            foreach (var pair in eventCounts.OrderByDescending(p => p.Value).Take(MaxSummaryRows))
            {
                string[] parts = pair.Key.Split('|');
                sb.Append($"\n    {pair.Value,5} {parts[0]}、{parts[1]}");
            }
        }

        /// <summary>ゲーム本体(Assembly-CSharp)に [PunRPC] として同じ名前のメソッドがあるか。</summary>
        private bool IsVanillaRpc(string name)
        {
            if (vanillaRpcNames == null)
            {
                vanillaRpcNames = new HashSet<string>();
                System.Type[] types;
                try
                {
                    types = typeof(PlayerAvatar).Assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray(); // 読めない型があっても、読めた分で判断する
                }
                foreach (var type in types)
                {
                    try
                    {
                        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (method.IsDefined(typeof(PunRPC), false))
                            {
                                vanillaRpcNames.Add(method.Name);
                            }
                        }
                    }
                    catch (System.Exception)
                    {
                        // この型は飛ばす
                    }
                }
            }
            return vanillaRpcNames.Contains(name);
        }
    }
}
