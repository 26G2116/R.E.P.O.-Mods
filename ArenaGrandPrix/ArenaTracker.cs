using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ArenaGrandPrix
{
    /// <summary>
    /// アリーナでの試合の様子を見る(ホストの PC)。
    /// - 試合の始まりに参加者を決め、体力を満タンにする。決勝では、決勝に進まなかった人を倒して観戦にする
    /// - 参加者が倒れた時刻を覚えておき、試合の終わりに順位にする
    ///   (ゲームの勝者 = レースで最後まで残った人・バトルロワイヤルで王冠を取った人が 1 位、あとは倒れたのが遅い順)
    /// </summary>
    internal static class ArenaTracker
    {
        internal sealed class Placed
        {
            public string SteamID;
            public string Name;
            public int Placement;
        }

        private sealed class Participant
        {
            public PlayerAvatar Avatar;
            public string SteamID;
            public string Name;
            public float DeathTime = -1f;
        }

        private static Object sceneKey;
        private static readonly Dictionary<string, Participant> Participants = new Dictionary<string, Participant>();
        private static readonly List<PlayerAvatar> Spectators = new List<PlayerAvatar>();
        private static readonly HashSet<string> SpectatorIds = new HashSet<string>();
        private static float killAt;
        private static int killTries;

        internal static void Reset()
        {
            sceneKey = null;
            Participants.Clear();
            Spectators.Clear();
            SpectatorIds.Clear();
        }

        /// <summary>今の試合(決勝)で観戦になった人か(ArenaTweaks の残機で復活させないため)。</summary>
        internal static bool IsSpectator(PlayerAvatar player)
        {
            string id = Tournament.IdOf(player);
            return id != null && SpectatorIds.Contains(id) && SemiFunc.RunIsArena();
        }

        /// <summary>Runner から毎フレーム。</summary>
        internal static void Update()
        {
            if (Tournament.Running == null || !SemiFunc.IsMasterClientOrSingleplayer() || !SemiFunc.RunIsArena())
            {
                return;
            }
            Object key = ArenaRace.instance ? (Object)ArenaRace.instance : Arena.instance;
            if (!key || !LevelGenerator.Instance || !LevelGenerator.Instance.Generated)
            {
                return;
            }
            if (key != sceneKey)
            {
                if (!GameDirector.instance || GameDirector.instance.currentState != GameDirector.gameState.Main)
                {
                    return; // 全員がそろって出てくるまで待つ
                }
                Begin(key);
            }
            float now = Time.time;
            foreach (Participant p in Participants.Values)
            {
                if (p.DeathTime < 0f && p.Avatar && p.Avatar.isDisabled)
                {
                    p.DeathTime = now;
                    Plugin.Log.LogInfo($"{p.Name} が倒れました");
                }
                else if (p.DeathTime >= 0f && p.Avatar && !p.Avatar.isDisabled && !p.Avatar.deadSet)
                {
                    // 復活した(ArenaTweaks の残機など)。最後に倒れた時刻で順位をつける
                    p.DeathTime = -1f;
                    Plugin.Log.LogInfo($"{p.Name} が復活しました");
                }
            }
            if (Spectators.Count > 0 && now >= killAt)
            {
                KillSpectators();
            }
        }

        private static void Begin(Object key)
        {
            sceneKey = key;
            Participants.Clear();
            Spectators.Clear();
            SpectatorIds.Clear();
            Tournament.Stage stage = Tournament.Running.Value;
            var finalists = new HashSet<string>(Tournament.Finalists);
            List<PlayerAvatar> players = SemiFunc.PlayerGetAll();
            bool anyFinalist = players.Any(p => finalists.Contains(Tournament.IdOf(p)));
            if (stage == Tournament.Stage.Final && !anyFinalist)
            {
                Plugin.Log.LogWarning("決勝に進む人が部屋にいないので、全員で決勝をします");
            }
            foreach (PlayerAvatar player in players)
            {
                string id = Tournament.IdOf(player);
                if (id == null || Participants.ContainsKey(id))
                {
                    continue;
                }
                if (stage == Tournament.Stage.Final && anyFinalist && !finalists.Contains(id))
                {
                    Spectators.Add(player);
                    SpectatorIds.Add(id);
                    continue;
                }
                Participants[id] = new Participant { Avatar = player, SteamID = id, Name = SemiFunc.PlayerGetName(player) ?? id };
                if (Plugin.HealBeforeEvent.Value && player.playerHealth && !player.isDisabled)
                {
                    player.playerHealth.HealOther(player.playerHealth.maxHealth, false);
                }
            }
            killAt = Time.time + 1f;
            killTries = 0;
            Plugin.Log.LogInfo($"{Tournament.KindName(stage)}が始まります。参加 {Participants.Count} 人" +
                               (Spectators.Count > 0 ? $"、観戦 {Spectators.Count} 人" : ""));
        }

        /// <summary>決勝に進まなかった人を倒す(観戦になる)。倒れなければ少しあとにもう一度。</summary>
        private static void KillSpectators()
        {
            Spectators.RemoveAll(p => !p || p.isDisabled);
            foreach (PlayerAvatar player in Spectators)
            {
                Plugin.Log.LogInfo($"{SemiFunc.PlayerGetName(player)} は決勝に進まなかったので観戦にします");
                player.PlayerDeath(-1);
            }
            killAt = Time.time + 2f;
            if (++killTries >= 5)
            {
                Spectators.Clear();
            }
        }

        /// <summary>今の試合の順位(同着は同じ順位)。部屋を出た人は入れない。</summary>
        internal static List<Placed> Compute(Tournament.Stage stage)
        {
            PlayerAvatar winner = stage == Tournament.Stage.Race
                ? (ArenaRace.instance ? ArenaRace.instance.winnerPlayer : null)
                : (Arena.instance ? Arena.instance.winnerPlayer : null);
            string winnerId = Tournament.IdOf(winner);
            float now = Time.time;
            var present = Participants.Values.Where(p => p.Avatar).ToList();
            foreach (Participant p in present)
            {
                p.Name = SemiFunc.PlayerGetName(p.Avatar) ?? p.Name;
                if (p.DeathTime < 0f && p.Avatar.isDisabled)
                {
                    p.DeathTime = now;
                }
            }
            // 並べる: 勝者 → 倒れていない人 → 倒れたのが遅い人
            var ordered = present
                .OrderByDescending(p => p.SteamID == winnerId)
                .ThenByDescending(p => p.DeathTime < 0f)
                .ThenByDescending(p => p.DeathTime)
                .ToList();
            var placed = new List<Placed>();
            float tie = Plugin.TieSeconds.Value;
            for (int i = 0; i < ordered.Count; i++)
            {
                Participant p = ordered[i];
                int placement = i + 1;
                if (i > 0 && p.SteamID != winnerId)
                {
                    Participant prev = ordered[i - 1];
                    bool sameGroup = prev.SteamID != winnerId &&
                                     ((prev.DeathTime < 0f && p.DeathTime < 0f) ||
                                      (prev.DeathTime >= 0f && p.DeathTime >= 0f && prev.DeathTime - p.DeathTime <= tie));
                    if (sameGroup)
                    {
                        placement = placed[i - 1].Placement;
                    }
                }
                placed.Add(new Placed { SteamID = p.SteamID, Name = p.Name, Placement = placement });
            }
            Plugin.Log.LogInfo($"勝者: {(winner ? SemiFunc.PlayerGetName(winner) : "なし")}、" +
                               $"順位をつけた人 {placed.Count}(部屋を出た人 {Participants.Count - present.Count})");
            return placed;
        }
    }
}
