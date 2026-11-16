using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace ArenaGrandPrix
{
    /// <summary>大会の進み具合(ホストの PC だけで持つ。シーンが切り替わっても残る)。</summary>
    internal static class Tournament
    {
        internal enum Stage
        {
            Race,
            Battle,
            Final,
        }

        internal sealed class Standing
        {
            public string SteamID;
            public string Name;
            public int Points;
        }

        internal sealed class Row
        {
            public string SteamID;
            public string Name;
            public int Placement;
            public int Gained;
        }

        internal sealed class EventResult
        {
            public string Title;
            public Stage Stage;
            public List<Row> Rows = new List<Row>();
        }

        /// <summary>大会の途中か(決勝が終わると false)。</summary>
        internal static bool Active;
        /// <summary>終わった試合の数(決勝を除く)。</summary>
        internal static int EventsDone;
        /// <summary>遊んでいる最中の試合(アリーナに送ったとき決まり、結果をつけると null)。</summary>
        internal static Stage? Running;
        internal static readonly Dictionary<string, Standing> Standings = new Dictionary<string, Standing>();
        internal static EventResult LastResult;
        internal static readonly List<string> Finalists = new List<string>();
        /// <summary>決勝の結果をトラックで見せる間 true(トラックを出ると false)。</summary>
        internal static bool ShowFinalResults;
        internal static string ChampionNames = "";

        /// <summary>自分で ChangeLevel を呼ぶときの行き先(呼んでいる間だけ)。</summary>
        private static Level pendingTarget;

        // ---------------------------------------------------------------- 試合の並び

        internal static EventKind KindOf(int index)
        {
            EventKind first = Plugin.FirstEvent.Value;
            return index % 2 == 0 ? first : (first == EventKind.Race ? EventKind.Battle : EventKind.Race);
        }

        internal static string KindName(Stage stage)
        {
            switch (stage)
            {
                case Stage.Race:
                    return "レース";
                case Stage.Battle:
                    return "バトルロワイヤル";
                default:
                    return "決勝";
            }
        }

        private static Stage ToStage(EventKind kind) => kind == EventKind.Race ? Stage.Race : Stage.Battle;

        internal static string EventTitle(int index) => $"第{index + 1}戦 {KindName(ToStage(KindOf(index)))}";

        /// <summary>次に遊ぶ試合(もうなければ null)。</summary>
        internal static Stage? NextStage()
        {
            if (!Active)
            {
                return null;
            }
            if (EventsDone < Plugin.EventCount.Value)
            {
                return ToStage(KindOf(EventsDone));
            }
            return Plugin.FinalEnabled.Value ? Stage.Final : (Stage?)null;
        }

        internal static string NextLabel()
        {
            if (ShowFinalResults || !Active)
            {
                return "大会終了！ おつかれさまでした";
            }
            Stage? next = NextStage();
            if (next == null)
            {
                return "大会終了";
            }
            if (next == Stage.Final)
            {
                var names = CurrentFinalists().Select(NameOf).ToList();
                return $"次: 決勝 バトルロワイヤル({string.Join("・", names)})";
            }
            return $"次: {EventTitle(EventsDone)}";
        }

        // ---------------------------------------------------------------- レベル

        internal static Level RaceLevel => FindArena(l => l.name.IndexOf("Arena Race", StringComparison.OrdinalIgnoreCase) >= 0);

        internal static Level BattleLevel => FindArena(l => l.name.IndexOf("Race", StringComparison.OrdinalIgnoreCase) < 0);

        private static Level FindArena(Predicate<Level> match)
        {
            var arenas = RunManager.instance ? RunManager.instance.levelArena : null;
            return arenas?.Find(l => l && match(l));
        }

        private static Level LevelOf(Stage stage) => stage == Stage.Race ? RaceLevel : BattleLevel;

        /// <summary>
        /// ゲームのレベル移動(ChangeLevel)の行き先を変える。アリーナを行き先にすると、ゲームはそれ以上行き先を変えない。
        /// トラックはゲームの決め方に合わせて「ショップの次」として行かせる(トラックを行き先にすると、ゲームは次のレベルへ進めてしまう)。
        /// </summary>
        private static bool Point(RunManager rm, Level target)
        {
            if (target == rm.levelLobby)
            {
                Level shop = rm.levelShop?.Find(l => l);
                if (!shop)
                {
                    Plugin.Log.LogError("ショップのレベルが見つからないので、トラックへ移れません");
                    return false;
                }
                rm.levelCurrent = shop;
                return true;
            }
            rm.levelCurrent = target;
            return true;
        }

        /// <summary>今すぐ行き先へ移る(ホスト)。</summary>
        private static bool GoTo(Level target)
        {
            var rm = RunManager.instance;
            if (!rm || !target)
            {
                return false;
            }
            if (rm.restarting)
            {
                Plugin.Log.LogWarning("いまレベルを切り替えている最中なので、移れません");
                return false;
            }
            pendingTarget = target;
            try
            {
                rm.ChangeLevel(false, false, RunManager.ChangeLevelType.Normal);
            }
            finally
            {
                pendingTarget = null;
            }
            return true;
        }

        [HarmonyPatch(typeof(RunManager), nameof(RunManager.ChangeLevel))]
        private static class ChangeLevelPatch
        {
            [HarmonyPriority(Priority.High)]
            private static void Prefix(RunManager __instance, ref bool _completedLevel, ref bool _levelFailed,
                                       ref RunManager.ChangeLevelType _changeLevelType)
            {
                if (__instance.restarting || !SemiFunc.IsMasterClientOrSingleplayer())
                {
                    return; // ゲームもこのときは何もしない
                }
                Level target;
                try
                {
                    target = Decide(__instance, _changeLevelType, _levelFailed);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"大会の行き先を決められませんでした: {e}");
                    return;
                }
                if (target == null || !Point(__instance, target))
                {
                    return;
                }
                _completedLevel = false;
                _levelFailed = false;
                _changeLevelType = RunManager.ChangeLevelType.Normal;
            }
        }

        private static Level Decide(RunManager rm, RunManager.ChangeLevelType type, bool failed)
        {
            if (pendingTarget)
            {
                return pendingTarget;
            }
            if (type != RunManager.ChangeLevelType.Normal)
            {
                if (Active && (type == RunManager.ChangeLevelType.LobbyMenu || type == RunManager.ChangeLevelType.MainMenu))
                {
                    Abort("メニューに戻ったので、大会をやめます");
                }
                return null;
            }
            if (SemiFunc.RunIsArena())
            {
                if (!Active || Running == null)
                {
                    return null; // 大会ではないアリーナ(全滅したときなど)はゲームのまま
                }
                FinishEvent();
                return rm.levelLobby;
            }
            if (SemiFunc.RunIsLobby())
            {
                ShowFinalResults = false;
                if (!Active)
                {
                    return null;
                }
                Stage? next = NextStage();
                if (next == null)
                {
                    Active = false;
                    return null;
                }
                return Prepare(next.Value);
            }
            return null;
        }

        /// <summary>次の試合のレベルを決め、遊んでいる試合にする。</summary>
        private static Level Prepare(Stage stage)
        {
            Level level = LevelOf(stage);
            if (!level)
            {
                Plugin.Log.LogError($"{KindName(stage)}のレベルが見つかりません(アリーナのレベル: " +
                                    $"{string.Join("、", RunManager.instance.levelArena.Where(l => l).Select(l => l.name))})");
                Abort("レベルが見つからないので、大会をやめます");
                return null;
            }
            if (stage == Stage.Final)
            {
                Finalists.Clear();
                Finalists.AddRange(CurrentFinalists());
                Plugin.Log.LogInfo($"決勝に進む人: {string.Join("、", Finalists.Select(NameOf))}");
            }
            Running = stage;
            ArenaTracker.Reset();
            Plugin.Log.LogInfo($"{(stage == Stage.Final ? "決勝" : EventTitle(EventsDone))} へ移ります({level.name})");
            return level;
        }

        // ---------------------------------------------------------------- コマンド

        internal static void Start(PlayerAvatar host)
        {
            if (Active)
            {
                Plugin.Log.LogInfo($"大会はもう始まっています({EventsDone} 試合終了、{NextLabel()})");
                return;
            }
            if (!(SemiFunc.RunIsLevel() || SemiFunc.RunIsShop() || SemiFunc.RunIsLobby()) || !LevelGenerator.Instance ||
                !LevelGenerator.Instance.Generated)
            {
                Plugin.Log.LogWarning("大会は、レベル・ショップ・トラックで、読み込みが終わってから始められます");
                return;
            }
            Active = true;
            EventsDone = 0;
            Running = null;
            Standings.Clear();
            LastResult = null;
            Finalists.Clear();
            ShowFinalResults = false;
            ChampionNames = "";
            foreach (PlayerAvatar player in SemiFunc.PlayerGetAll())
            {
                Register(player);
            }
            Level level = Prepare(NextStage() ?? Stage.Race);
            if (!level)
            {
                return;
            }
            Plugin.Log.LogInfo($"大会を始めます(試合 {Plugin.EventCount.Value} 回、決勝 {(Plugin.FinalEnabled.Value ? "あり" : "なし")}、" +
                               $"参加 {Standings.Count} 人)");
            Net.Speak(host, "アリーナ大会、スタート！");
            if (!GoTo(level))
            {
                Running = null;
                Active = false;
            }
        }

        internal static void Stop(PlayerAvatar host)
        {
            if (!Active && Running == null)
            {
                return;
            }
            bool inEvent = SemiFunc.RunIsArena() && Running != null;
            Abort("ホストが大会をやめました");
            Net.Speak(host, "大会はここまで！");
            if (inEvent)
            {
                GoTo(RunManager.instance.levelLobby);
            }
        }

        internal static void Skip(PlayerAvatar host)
        {
            if (!Active || Running == null || !SemiFunc.RunIsArena())
            {
                Plugin.Log.LogInfo("飛ばせる試合はありません(アリーナで試合をしている最中だけ使えます)");
                return;
            }
            Stage stage = Running.Value;
            Running = null;
            if (stage == Stage.Final)
            {
                Active = false;
                ShowFinalResults = true;
                ChampionNames = "なし(決勝を飛ばしました)";
            }
            else
            {
                LastResult = new EventResult { Title = EventTitle(EventsDone) + "(飛ばしました)", Stage = stage };
                EventsDone++;
            }
            Plugin.Log.LogInfo($"{KindName(stage)}をポイントなしで終わらせます");
            Net.Speak(host, "この試合はノーカウント！");
            GoTo(RunManager.instance.levelLobby);
        }

        private static void Abort(string reason)
        {
            Plugin.Log.LogInfo(reason);
            Active = false;
            Running = null;
            ShowFinalResults = false;
        }

        /// <summary>メニューに戻ったら大会をやめる(Runner から毎フレーム)。</summary>
        internal static void Tick()
        {
            if (!RunManager.instance || !RunManager.instance.levelCurrent)
            {
                return;
            }
            if ((Active || Running != null) && (SemiFunc.RunIsLobbyMenu() || SemiFunc.MenuLevel()))
            {
                Abort("メニューに戻ったので、大会をやめます");
            }
            if (ShowFinalResults && !SemiFunc.RunIsLobby() && !SemiFunc.RunIsArena() && LevelGenerator.Instance && LevelGenerator.Instance.Generated)
            {
                ShowFinalResults = false;
            }
        }

        // ---------------------------------------------------------------- 結果

        private static void Register(PlayerAvatar player)
        {
            string id = IdOf(player);
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            if (!Standings.TryGetValue(id, out Standing standing))
            {
                standing = new Standing { SteamID = id };
                Standings[id] = standing;
            }
            standing.Name = SemiFunc.PlayerGetName(player) ?? standing.Name ?? id;
        }

        internal static string IdOf(PlayerAvatar player)
        {
            if (!player)
            {
                return null;
            }
            return !string.IsNullOrEmpty(player.steamID) ? player.steamID : $"view{(player.photonView ? player.photonView.ViewID : 0)}";
        }

        internal static string NameOf(string id) => Standings.TryGetValue(id, out Standing s) && !string.IsNullOrEmpty(s.Name) ? s.Name : id;

        /// <summary>今この部屋にいる人のうち、ポイントがいちばん多い人たち。</summary>
        internal static List<string> CurrentFinalists()
        {
            var present = SemiFunc.PlayerGetAll().Select(IdOf).Where(id => id != null && Standings.ContainsKey(id)).Distinct().ToList();
            if (present.Count == 0)
            {
                return present;
            }
            int best = present.Max(id => Standings[id].Points);
            return present.Where(id => Standings[id].Points == best).ToList();
        }

        /// <summary>試合が終わったときに、順位とポイントをつける。</summary>
        private static void FinishEvent()
        {
            Stage stage = Running.Value;
            Running = null;
            List<ArenaTracker.Placed> placed = ArenaTracker.Compute(stage);
            var result = new EventResult
            {
                Title = stage == Stage.Final ? "決勝" : EventTitle(EventsDone),
                Stage = stage,
            };
            int[] table = ParseTable();
            foreach (ArenaTracker.Placed p in placed)
            {
                if (!Standings.TryGetValue(p.SteamID, out Standing standing))
                {
                    standing = new Standing { SteamID = p.SteamID };
                    Standings[p.SteamID] = standing;
                }
                standing.Name = p.Name;
                int gained = stage == Stage.Final ? 0 : PointsFor(p.Placement, placed.Count, table);
                standing.Points += gained;
                result.Rows.Add(new Row { SteamID = p.SteamID, Name = p.Name, Placement = p.Placement, Gained = gained });
            }
            LastResult = result;
            Plugin.Log.LogInfo($"{result.Title} の結果: " +
                               string.Join("、", result.Rows.Select(r => $"{r.Placement}位 {r.Name}(+{r.Gained})")));
            if (stage == Stage.Final)
            {
                var champions = result.Rows.Where(r => r.Placement == 1).Select(r => r.Name).ToList();
                ChampionNames = champions.Count > 0 ? string.Join("・", champions) : "なし";
                Active = false;
                ShowFinalResults = true;
                Plugin.Log.LogInfo($"大会の優勝: {ChampionNames}");
            }
            else
            {
                EventsDone++;
            }
        }

        private static int[] ParseTable()
        {
            string text = (Plugin.PointTable.Value ?? "").Trim();
            if (text.Length == 0)
            {
                return null;
            }
            var values = new List<int>();
            foreach (string part in text.Split(new[] { ',', '、', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Normalize(System.Text.NormalizationForm.FormKC), out int v))
                {
                    values.Add(v);
                }
            }
            return values.Count > 0 ? values.ToArray() : null;
        }

        private static int PointsFor(int placement, int participants, int[] table)
        {
            if (table == null)
            {
                return Mathf.Max(0, participants - placement + 1);
            }
            return placement >= 1 && placement <= table.Length ? table[placement - 1] : 0;
        }

        /// <summary>総合順位(ポイントの多い順、同点は同じ順位)。</summary>
        internal static List<KeyValuePair<int, Standing>> Ranking()
        {
            var sorted = Standings.Values.OrderByDescending(s => s.Points).ThenBy(s => s.Name).ToList();
            var ranked = new List<KeyValuePair<int, Standing>>();
            for (int i = 0; i < sorted.Count; i++)
            {
                int rank = i > 0 && sorted[i].Points == sorted[i - 1].Points ? ranked[i - 1].Key : i + 1;
                ranked.Add(new KeyValuePair<int, Standing>(rank, sorted[i]));
            }
            return ranked;
        }

        // ---------------------------------------------------------------- チャットのコマンド

        [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ChatMessageSend))]
        private static class CommandPatch
        {
            // 打ったチャットを送る処理(打った人の PC だけで動く)。ホストが打ったものだけを拾う。
            // 全員の PC で読み上げる処理(ChatMessageSpeak)で拾うと、ホストが倒れて観戦しているときに届かない
            // (倒れた人の PC は、チャットの読み上げを止める)
            private static bool Prefix(PlayerAvatar __instance, string _message, string localeCode)
            {
                if (!__instance || !__instance.isLocal || !SemiFunc.IsMasterClientOrSingleplayer() || string.IsNullOrEmpty(_message) ||
                    !string.IsNullOrEmpty(localeCode)) // localeCode があるのは、取りつかれてしゃべらされたとき
                {
                    return true;
                }
                string command = (Plugin.Command.Value ?? "").Trim();
                string text = _message.Trim();
                if (command.Length == 0 || !text.StartsWith(command, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (text.Length > command.Length && !char.IsWhiteSpace(text[command.Length]))
                {
                    return true; // 「/rgb」など別の言葉
                }
                string rest = text.Substring(command.Length).Trim().ToLowerInvariant();
                try
                {
                    switch (rest)
                    {
                        case "":
                        case "start":
                            Start(__instance);
                            break;
                        case "stop":
                            Stop(__instance);
                            break;
                        case "skip":
                            Skip(__instance);
                            break;
                        default:
                            return true;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"コマンド「{text}」でエラー: {e}");
                }
                return false; // コマンド自体は送らない(ほかの人にも読み上げない)
            }
        }
    }

    internal static class Net
    {
        /// <summary>プレイヤーにしゃべらせる(頭の上の吹き出しと読み上げ。ホストからなら全員に届く)。</summary>
        internal static void Speak(PlayerAvatar player, string text)
        {
            if (!player || string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                if (SemiFunc.IsMultiplayer())
                {
                    player.photonView.RPC("ChatMessageSendRPC", RpcTarget.All, text, false, "ja-JP");
                }
                else
                {
                    player.ChatMessageSpeak(text, false, "ja-JP");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"しゃべらせられませんでした: {e.Message}");
            }
        }
    }
}
