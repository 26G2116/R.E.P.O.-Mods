using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using UnityEngine;

namespace AIMonsters
{
    /// <summary>
    /// 味方のローブの会話(ホストだけ)。
    /// - 味方のローブから一定の距離のプレイヤーのチャットを読み、直近の会話(ローブの返事も含む)を覚えて AI に送る
    /// - AI は返事の文と、行動の指示(ついていく・待つ・敵を攻撃)を返す
    /// - しゃべるのは、ローブから一番近いプレイヤーのセミボット。頭に「Robe:」をつける(チャットと同じ仕組みなので MOD なしの人にも聞こえる)
    /// 発言者は既定で番号(P1 など)にして、名前は送らない(設定で変えられる)。
    /// </summary>
    internal static class AllyChat
    {
        internal const string Prefix = "Robe: ";
        private const string Section = "6. 味方の Robe の会話";

        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<float> HearRange;
        private static ConfigEntry<int> HistoryCount;
        private static ConfigEntry<bool> SendNames;
        private static ConfigEntry<float> Debounce;

        private sealed class Line
        {
            internal int Seq;
            /// <summary>発言した人(ローブの返事なら null)。名前・番号は送るときに決める(設定を途中で変えても反映される)。</summary>
            internal PlayerAvatar Player;
            internal string Text;
        }

        private static int nextSeq;

        private static readonly List<Line> History = new List<Line>();
        private static readonly Dictionary<PlayerAvatar, string> Labels = new Dictionary<PlayerAvatar, string>();
        private static readonly AzureChannel Channel = new AzureChannel("味方");
        private static bool pending;

        private static bool Active =>
            Plugin.Enabled.Value && Enabled.Value && SemiFunc.IsMasterClientOrSingleplayer() && AllyRobe.Ally;
        private static float sendAt;
        private static int generation;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "味方のローブが近くのチャットを読んで、AI で返事をする(チャットの文は Azure に送られます)");
            HearRange = cfg.Bind(Section, "聞こえる距離(m)", 15f,
                new ConfigDescription("味方のローブからこの距離以内のプレイヤーのチャットを読む", new AcceptableValueRange<float>(1f, 200f)));
            HistoryCount = cfg.Bind(Section, "覚える発言の数", 10,
                new ConfigDescription("直近の発言(ローブの返事を含む)をこの数まで覚えて、毎回いっしょに送る。多いほど送るトークンが増える",
                    new AcceptableValueRange<int>(1, 50)));
            SendNames = cfg.Bind(Section, "名前を送る", false, "発言者の名前を AI に送る(OFF なら P1、P2 などの番号)");
            Debounce = cfg.Bind(Section, "返事までの待ち(秒)", 0.8f,
                new ConfigDescription("続けて発言されたときにまとめて返事するため、最後の発言からこの秒数待ってから送る", new AcceptableValueRange<float>(0f, 5f)));
        }

        // ================================================================
        // しゃべる
        // ================================================================

        /// <summary>決まった文をしゃべる(会話の履歴にも残す)。</summary>
        internal static void SayFixed(string text)
        {
            if (Say(text))
            {
                Remember(null, text, -1);
            }
        }

        private static bool Say(string text)
        {
            var robe = AllyRobe.Ally;
            if (!robe || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            var speaker = Nav.NearestPlayer(Director.EnemyPosition(robe.enemy));
            if (!speaker)
            {
                return false;
            }
            string message = Prefix + text.Trim();
            string locale = IsAscii(message) ? "en-US" : "ja-JP";
            try
            {
                if (SemiFunc.IsMultiplayer())
                {
                    speaker.photonView.RPC("ChatMessageSendRPC", RpcTarget.All, message, false, locale);
                }
                else
                {
                    speaker.ChatMessageSpeak(message, false, locale);
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"味方のローブ: しゃべらせられませんでした: {e.Message}");
                return false;
            }
        }

        internal static bool IsAscii(string text) => text.All(c => c <= 127);

        // ================================================================
        // 聞く
        // ================================================================

        /// <summary>
        /// チャットの発言。マルチプレイでは ChatMessageSendRPC から、シングルプレイでは直接呼ばれる
        /// (ホストには全員の発言が届く)。
        /// </summary>
        [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ChatMessageSpeak))]
        private static class ChatPatch
        {
            private static void Postfix(PlayerAvatar __instance, string _message)
            {
                try
                {
                    Hear(__instance, _message);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"味方のローブ: 発言を読めませんでした: {e.Message}");
                }
            }
        }

        private static void Hear(PlayerAvatar player, string message)
        {
            var robe = AllyRobe.Ally;
            if (!Active || !player || player.isDisabled
                || string.IsNullOrWhiteSpace(message) || !GameDirector.instance || GameDirector.instance.currentState != GameDirector.gameState.Main)
            {
                return;
            }
            message = message.Trim();
            // ローブ自身の発言(だれかのセミボットが代わりにしゃべった)と、コマンドは読まない
            if (message.StartsWith(Prefix.Trim(), StringComparison.OrdinalIgnoreCase) || message.StartsWith("/"))
            {
                return;
            }
            if (Vector3.Distance(player.transform.position, Director.EnemyPosition(robe.enemy)) > HearRange.Value)
            {
                return;
            }
            if (message.Length > 200)
            {
                message = message.Substring(0, 200);
            }
            Remember(player, message, -1);
            pending = true;
            sendAt = Time.time + Debounce.Value;
        }

        /// <summary>覚える。after が 0 以上なら、その番号の発言の直後に入れる(返事を待つ間に来た発言より前にする)。</summary>
        private static void Remember(PlayerAvatar player, string text, int after)
        {
            var line = new Line { Seq = nextSeq++, Player = player, Text = text };
            int index = after >= 0 ? History.FindIndex(l => l.Seq == after) : -1;
            if (index >= 0)
            {
                History.Insert(index + 1, line);
            }
            else
            {
                History.Add(line);
            }
            int max = Mathf.Max(1, HistoryCount.Value);
            if (History.Count > max)
            {
                History.RemoveRange(0, History.Count - max);
            }
        }

        private static string LabelOf(PlayerAvatar player)
        {
            if (SendNames.Value && !string.IsNullOrEmpty(player.playerName))
            {
                return player.playerName;
            }
            if (!Labels.TryGetValue(player, out var label))
            {
                label = $"P{Labels.Count + 1}";
                Labels[player] = label;
            }
            return label;
        }

        // ================================================================
        // AI に送る(Runner から毎フレーム)
        // ================================================================

        internal static void Tick()
        {
            if (!pending || Time.time < sendAt || Channel.Busy)
            {
                return;
            }
            if (!Active)
            {
                pending = false; // OFF にされた・ローブがいない: 待っていた発言は送らない
                return;
            }
            pending = false;
            int sentGeneration = generation;
            int lastSeq = History.Count > 0 ? History[History.Count - 1].Seq : -1;
            var enemyIds = new Dictionary<int, Enemy>();
            var playerIds = new Dictionary<string, PlayerAvatar>();
            string state = BuildState(enemyIds, playerIds);
            string system = BuildSystemPrompt();
            if (!Channel.Send(system, state, reply => OnReply(reply, sentGeneration, lastSeq, enemyIds, playerIds)))
            {
                return; // 環境変数がない(警告は 1 回出る)
            }
            Plugin.Verbose($"AI(味方)に送信: {state}");
        }

        private static string BuildSystemPrompt()
        {
            string persona = PromptFiles.Read("AllyRobe") ??
                "You are Robe, a monster from R.E.P.O. who has become the players' ally after they broke a big monster orb. " +
                "You are quiet, loyal and a little eerie, and you speak in short sentences. You follow the players, protect them and attack other monsters.";
            return persona + "\n" +
                   "Players talk to you in chat. Reply in the same language as the latest message, in at most 80 characters, " +
                   "or with an empty string if no reply is needed. Do not repeat yourself.\n" +
                   "You can also change what you do: \"follow\" (follow a player; set follow_player), \"wait\" (stay here), " +
                   "\"attack\" (attack a monster; set target_enemy to its id), or \"none\" (keep doing the same).\n" +
                   "Reply only with JSON: {\"say\":\"text or empty\",\"command\":\"none\",\"follow_player\":null,\"target_enemy\":null}";
        }

        private static string BuildState(Dictionary<int, Enemy> enemyIds, Dictionary<string, PlayerAvatar> playerIds)
        {
            var robe = AllyRobe.Ally;
            Vector3 position = Director.EnemyPosition(robe.enemy);
            var players = new JArray();
            foreach (var player in SemiFunc.PlayerGetList().Where(p => p && !p.isDisabled))
            {
                float distance = Vector3.Distance(position, player.transform.position);
                if (distance > Mathf.Max(HearRange.Value, 30f))
                {
                    continue;
                }
                string label = LabelOf(player);
                playerIds[label] = player;
                players.Add(new JObject
                {
                    ["id"] = label,
                    ["dist"] = Mathf.Round(distance * 10f) / 10f,
                    ["hp"] = player.playerHealth ? player.playerHealth.health : 0,
                    ["you_follow"] = player == AllyRobe.Leader,
                });
            }
            var enemies = new JArray();
            foreach (var enemy in AllyRobe.NearbyEnemies(30f))
            {
                int id = enemyIds.Count + 1;
                enemyIds[id] = enemy;
                enemies.Add(new JObject
                {
                    ["id"] = id,
                    ["type"] = AllyRobe.EnemyName(enemy),
                    ["dist"] = Mathf.Round(Vector3.Distance(position, Director.EnemyPosition(enemy)) * 10f) / 10f,
                });
            }
            var chat = new JArray(History.Select(line => new JObject
            {
                // Unity の == は、抜けたプレイヤー(破棄済み)も null 扱いにするので、ローブの返事かどうかは参照で見る
                ["from"] = (object)line.Player == null ? "Robe" : !line.Player ? "someone" : LabelOf(line.Player),
                ["text"] = line.Text,
            }));
            var root = new JObject
            {
                ["you"] = new JObject { ["mode"] = AllyRobe.CurrentMode.ToString().ToLowerInvariant() },
                ["players"] = players,
                ["monsters"] = enemies,
                ["chat"] = chat,
            };
            return root.ToString(Formatting.None);
        }

        private static void OnReply(string reply, int sentGeneration, int lastSeq, Dictionary<int, Enemy> enemyIds, Dictionary<string, PlayerAvatar> playerIds)
        {
            if (reply == null || sentGeneration != generation || !Active)
            {
                return;
            }
            JObject root;
            try
            {
                root = JObject.Parse(reply);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"AI(味方)の返事を読めませんでした: {e.Message}: {reply}");
                return;
            }
            string command = (root.Value<string>("command") ?? "none").Trim().ToLowerInvariant();
            switch (command)
            {
                case "follow":
                {
                    string label = root.Value<string>("follow_player");
                    if (label != null && playerIds.TryGetValue(label, out var player) && player && !player.isDisabled)
                    {
                        AllyRobe.Leader = player;
                    }
                    AllyRobe.CurrentMode = AllyRobe.Mode.Follow;
                    AllyRobe.OrderedTarget = null;
                    break;
                }
                case "wait":
                    AllyRobe.CurrentMode = AllyRobe.Mode.Wait;
                    AllyRobe.OrderedTarget = null;
                    break;
                case "attack":
                {
                    int? id = root["target_enemy"]?.Type == JTokenType.Integer ? root.Value<int?>("target_enemy") : null;
                    if (id != null && enemyIds.TryGetValue(id.Value, out var enemy) && enemy)
                    {
                        AllyRobe.OrderedTarget = enemy;
                        AllyRobe.CurrentMode = AllyRobe.Mode.Attack;
                    }
                    break;
                }
            }
            string say = root.Value<string>("say")?.Trim();
            if (!string.IsNullOrEmpty(say))
            {
                if (say.Length > 120)
                {
                    say = say.Substring(0, 120);
                }
                if (Say(say))
                {
                    Remember(null, say, lastSeq);
                }
            }
            Plugin.Verbose($"味方のローブ: 返事「{say}」、指示 {command}、今の動き {AllyRobe.CurrentMode}");
        }

        /// <summary>レベルが変わったとき。</summary>
        internal static void Reset()
        {
            History.Clear();
            Labels.Clear();
            pending = false;
            generation++;
        }
    }
}
