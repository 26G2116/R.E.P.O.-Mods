using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace AIMonsters
{
    /// <summary>
    /// レベルの途中で入ってくる人(途中参加 MOD)への対応。ホストだけで動く。
    ///
    /// 2026-11-14 の記録では、味方のローブがいるレベルへの途中参加が 4 回とも失敗した(参加者の読み込みが終わらない)。
    /// 味方のローブがいないレベルでは成功している。参加者側で何が止まるかはまだ分かっていないので、
    /// 味方のローブと目印のオーブが、参加者の読み込みに一切関わらないようにする:
    /// - 生成の記録を部屋から消す(AllyRobe.Uncache)。あとから入る人は、読み込みのときに受け取らない
    /// - 入ってきたら、読み込みが終わるまで味方のローブを退場させ、目印のオーブを消す(通信も止まる)
    /// - 読み込みが終わったら、その人にだけ生成を送ってから、ついていく人の後ろに出し直す
    ///
    /// 読み込みが終わった合図: 参加者は自分の読み込み(LevelGenerator.Generated)が終わってから、
    /// 見た目(PlayerCosmetics)を全員に送る。それが届いたら終わったとみなす。
    /// </summary>
    internal static class AllyLateJoin
    {
        /// <summary>この秒数を過ぎても読み込みが終わらない人は待たない(味方のローブを出し直す)。</summary>
        private const float LoadTimeout = 60f;
        /// <summary>読み込みが終わった合図から、生成を送るまでの秒数(トラックの演出が落ち着くのを待つ)。</summary>
        private const float SettleTime = 3f;
        /// <summary>生成を送ってから、出し直すまでの秒数(準備完了の同期が届くのを待つ)。</summary>
        private const float ShowTime = 1.5f;

        private sealed class Joiner
        {
            public Player Player;
            public string Name;
            public float JoinedAt;
            public float ReadyAt = -1f;
            public float ShownAt = -1f;
            public bool TimedOut;
        }

        private static readonly Dictionary<int, Joiner> Joiners = new Dictionary<int, Joiner>();
        private static LevelGenerator level;

        private static bool Active =>
            Plugin.Enabled.Value && AllyRobe.Enabled.Value && AllyRobe.HideWhileJoining.Value
            && SemiFunc.IsMultiplayer() && PhotonNetwork.IsMasterClient;

        /// <summary>読み込み中の人がいる(この間は味方のローブを出さない)。</summary>
        internal static bool Holding
        {
            get
            {
                foreach (var joiner in Joiners.Values)
                {
                    if (!joiner.TimedOut || joiner.ShownAt >= 0f)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        // ================================================================
        // 部屋の出入り(ゲームの NetworkManager が Photon から受け取ったあとに呼ばれる)
        // ================================================================

        [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnPlayerEnteredRoom))]
        private static class EnteredPatch
        {
            private static void Postfix(Player newPlayer)
            {
                // Photon の受け取りの中なので、エラーを外へ出さない(ほかの MOD やゲームの処理を止めないため)
                try
                {
                    PlayerEntered(newPlayer);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"途中参加への対応でエラー: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnPlayerLeftRoom))]
        private static class LeftPatch
        {
            private static void Postfix(Player otherPlayer)
            {
                if (otherPlayer != null && Joiners.Remove(otherPlayer.ActorNumber))
                {
                    Plugin.Log.LogInfo($"途中参加: {otherPlayer.NickName} は読み込みが終わる前に抜けました");
                }
            }
        }

        private static void PlayerEntered(Player player)
        {
            if (player == null || !Active || !SemiFunc.RunIsLevel())
            {
                return;
            }
            Joiners[player.ActorNumber] = new Joiner
            {
                Player = player,
                Name = player.NickName,
                JoinedAt = Time.unscaledTime,
            };
            bool hidden = AllyRobe.HideForJoin();
            Plugin.Log.LogInfo($"途中参加: {player.NickName} が入ってきました。読み込みが終わるまで味方のローブを出しません"
                + (hidden ? "(退場させました)" : "(今は出ていません)"));
        }

        /// <summary>見た目の同期が届いた = その人の読み込みが終わった。</summary>
        private static void CosmeticsFrom(Player sender)
        {
            if (sender != null && Joiners.TryGetValue(sender.ActorNumber, out var joiner))
            {
                MarkReady(joiner, "見た目の同期が届いた");
            }
        }

        private static void MarkReady(Joiner joiner, string reason)
        {
            if (joiner.ReadyAt >= 0f)
            {
                return;
            }
            joiner.ReadyAt = Time.unscaledTime;
            Plugin.Log.LogInfo($"途中参加: {joiner.Name} の読み込みが終わりました({joiner.ReadyAt - joiner.JoinedAt:0.0} 秒、{reason})");
        }

        /// <summary>動き始めた = 読み込みが終わっている(見た目の同期が届かなかったときの保険)。</summary>
        private static bool Moving(Joiner joiner)
        {
            var avatar = SemiFunc.PlayerAvatarGetFromPhotonPlayer(joiner.Player);
            return avatar && (avatar.isMoving || avatar.InputDirectionRaw.sqrMagnitude > 0.001f);
        }

        [HarmonyPatch(typeof(PlayerCosmetics), nameof(PlayerCosmetics.SetupColorsRPC))]
        private static class ColorsPatch
        {
            private static void Postfix(PhotonMessageInfo _info) => CosmeticsFrom(_info.Sender);
        }

        [HarmonyPatch(typeof(PlayerCosmetics), nameof(PlayerCosmetics.SetupColorsAllRPC))]
        private static class ColorsAllPatch
        {
            private static void Postfix(PhotonMessageInfo _info) => CosmeticsFrom(_info.Sender);
        }

        [HarmonyPatch(typeof(PlayerCosmetics), nameof(PlayerCosmetics.SetupCosmeticsRPC))]
        private static class CosmeticsPatch
        {
            private static void Postfix(PhotonMessageInfo _info) => CosmeticsFrom(_info.Sender);
        }

        // ================================================================
        // 毎フレーム(Runner から。AllyRobe.Tick より先)
        // ================================================================

        internal static void Tick()
        {
            if (level != LevelGenerator.Instance)
            {
                // レベルが変わった: 途中参加の人も、ほかの人と同じようにレベルの読み込みから入り直す
                level = LevelGenerator.Instance;
                Joiners.Clear();
            }
            if (Joiners.Count == 0)
            {
                return;
            }
            if (!Active)
            {
                Joiners.Clear();
                return;
            }
            float now = Time.unscaledTime;
            var room = PhotonNetwork.CurrentRoom;
            foreach (var joiner in Joiners.Values.ToList())
            {
                if (room == null || !room.Players.ContainsKey(joiner.Player.ActorNumber))
                {
                    Joiners.Remove(joiner.Player.ActorNumber); // 抜けた合図を受け取れなかったときの保険
                    continue;
                }
                if (joiner.ReadyAt < 0f && Moving(joiner))
                {
                    MarkReady(joiner, "動き始めた");
                }
                if (joiner.ShownAt >= 0f)
                {
                    if (now - joiner.ShownAt >= ShowTime)
                    {
                        Joiners.Remove(joiner.Player.ActorNumber); // これで Holding が外れ、味方のローブが出直す
                    }
                }
                else if (joiner.ReadyAt >= 0f)
                {
                    if (now - joiner.ReadyAt >= SettleTime)
                    {
                        joiner.ShownAt = now;
                        if (AllyRobe.ShowTo(joiner.Player, joiner.JoinedAt))
                        {
                            Plugin.Log.LogInfo($"途中参加: {joiner.Name} に味方のローブを見せました");
                        }
                    }
                }
                else if (!joiner.TimedOut && now - joiner.JoinedAt >= LoadTimeout)
                {
                    // 読み込みが終わらない人を待ち続けない。あとで終わったら、そのとき見せる(記録は残す)
                    joiner.TimedOut = true;
                    Plugin.Log.LogWarning($"途中参加: {joiner.Name} の読み込みが {LoadTimeout:0} 秒たっても終わりません。味方のローブを出し直します");
                }
            }
        }

        // ================================================================
        // Photon の操作
        // ================================================================

        /// <summary>
        /// 生成の記録を部屋から消す(今いる人には何も起きない。あとから入る人が、読み込みのときに受け取らなくなる)。
        /// PhotonNetwork.Destroy が内部でしていることと同じ操作。
        /// </summary>
        internal static void Uncache(GameObject obj)
        {
            if (!obj || !SemiFunc.IsMultiplayer() || !PhotonNetwork.InRoom || !AllyRobe.HideWhileJoining.Value)
            {
                return;
            }
            var view = obj.GetComponent<PhotonView>();
            if (!view || view.InstantiationId <= 0)
            {
                return;
            }
            var filter = new Hashtable { [(byte)7] = view.InstantiationId };
            var options = new RaiseEventOptions { CachingOption = EventCaching.RemoveFromRoomCache };
            PhotonNetwork.NetworkingClient.OpRaiseEvent(202, filter, options, SendOptions.SendReliable);
        }

        /// <summary>
        /// 1 人にだけ生成を送る(PhotonNetwork.SendInstantiate と同じ中身)。
        /// 記録を消した物は、あとから入った人の PC に存在しないので、読み込みが終わってから送る。
        /// </summary>
        internal static bool SendInstantiateTo(Player target, string prefabPath, Vector3 position, int[] viewIds, int levelPrefix)
        {
            if (target == null || string.IsNullOrEmpty(prefabPath) || viewIds == null || viewIds.Length == 0
                || !SemiFunc.IsMultiplayer() || !PhotonNetwork.InRoom)
            {
                return false;
            }
            var data = new Hashtable { [(byte)0] = prefabPath };
            if (position != Vector3.zero)
            {
                data[(byte)1] = position;
            }
            if (viewIds.Length > 1)
            {
                data[(byte)4] = viewIds;
            }
            data[(byte)6] = PhotonNetwork.ServerTimestamp;
            data[(byte)7] = viewIds[0];
            if (levelPrefix > 0)
            {
                data[(byte)8] = (byte)levelPrefix;
            }
            data[(byte)9] = false;
            var options = new RaiseEventOptions { TargetActors = new[] { target.ActorNumber } };
            return PhotonNetwork.NetworkingClient.OpRaiseEvent(202, data, options, SendOptions.SendReliable);
        }
    }
}
