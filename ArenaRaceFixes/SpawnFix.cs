using System;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace ArenaRaceFixes
{
    /// <summary>
    /// ArenaRaceMultiplayerSpawnFix 4.0.2 の修正。
    /// 11 月 5 日の更新で、プレイヤーの位置を決める通信 SpawnRPC の受け取り側に「デバッグ用のスポーンか」(bool)が増えた。
    /// 元の MOD は古い形(位置・向きの 2 つ)で送るので、全員の PC で「引数が違う」エラーになり、並べ直しが効かない。
    /// 元の MOD の送る処理(TryApplySpawnPositionByRpc)を、今の形(位置・向き・false)で送る処理に差し替える。
    /// </summary>
    internal static class SpawnFix
    {
        internal const string TargetGuid = "REPOJP.ArenaRaceMultiplayerSpawnFix";
        private const string TargetType = "REPOJP.ArenaRaceMultiplayerSpawnFix.ArenaRaceMultiplayerSpawnFixPlugin";

        private static MethodInfo spawnRpc;
        private static int applied;

        internal static bool Patch(Harmony harmony)
        {
            Type type = AccessTools.TypeByName(TargetType);
            if (type == null)
            {
                return false; // 入っていない
            }
            MethodInfo target = AccessTools.Method(type, "TryApplySpawnPositionByRpc",
                new[] { typeof(PlayerAvatar), typeof(Vector3), typeof(Quaternion), typeof(int) });
            if (target == null || target.ReturnType != typeof(bool))
            {
                Plugin.Log.LogWarning("ArenaRaceMultiplayerSpawnFix の版が想定と違うので直しません(並べ直しの処理が見つかりません)");
                return false;
            }
            spawnRpc = AccessTools.Method(typeof(PlayerAvatar), "SpawnRPC",
                new[] { typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(PhotonMessageInfo) });
            if (spawnRpc == null)
            {
                Plugin.Log.LogWarning("ゲームの SpawnRPC の形がまた変わっているので、ArenaRaceMultiplayerSpawnFix は直しません");
                return false;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(SpawnFix), nameof(Prefix)));
            Plugin.Log.LogInfo("ArenaRaceMultiplayerSpawnFix の起動時の「SpawnRPC method was not found」の警告は、この修正で問題なくなります");
            return true;
        }

        // 引数の名前は元の処理と同じにする(Harmony が名前で結びつける)
        private static bool Prefix(PlayerAvatar player, Vector3 position, Quaternion rotation, int index, ref bool __result)
        {
            __result = false;
            if (!player)
            {
                return false;
            }
            try
            {
                if (!SemiFunc.IsMultiplayer())
                {
                    spawnRpc.Invoke(player, new object[] { position, rotation, false, default(PhotonMessageInfo) });
                }
                else
                {
                    if (!player.photonView)
                    {
                        player.photonView = player.GetComponent<PhotonView>();
                    }
                    if (!player.photonView)
                    {
                        Plugin.Log.LogWarning($"並べ直し: {index} 番目のプレイヤーに PhotonView がありません");
                        return false;
                    }
                    player.photonView.RPC("SpawnRPC", RpcTarget.All, position, rotation, false);
                }
                __result = true;
                if (applied++ < 200)
                {
                    Plugin.Log.LogInfo($"並べ直し: {SemiFunc.PlayerGetName(player)} を {position} に置きました(今の形の SpawnRPC で送信)");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"並べ直し: {index} 番目のプレイヤーを置けませんでした: {e}");
            }
            return false; // 元の(古い形で送る)処理は動かさない
        }
    }
}
