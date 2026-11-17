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
    ///
    /// ただし元の MOD の並べ方(ゲームの開始位置から 5 m 上・10 m 後ろ)は更新前のゲーム用で、今のゲームではスタートの部屋の外になる。
    /// そのため既定では動かさない(ゲームの開始位置のまま)。設定で「重なった人だけずらす」「元の並べ方」も選べる。
    /// </summary>
    internal static class SpawnFix
    {
        internal const string TargetGuid = "REPOJP.ArenaRaceMultiplayerSpawnFix";
        private const string TargetType = "REPOJP.ArenaRaceMultiplayerSpawnFix.ArenaRaceMultiplayerSpawnFixPlugin";

        private static MethodInfo spawnRpc;
        private static int applied;
        private static int spreadFrame = -1;
        private static readonly System.Collections.Generic.List<Vector3> Taken = new System.Collections.Generic.List<Vector3>();

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
            switch (Plugin.Layout.Value)
            {
                case SpawnLayout.Game:
                    __result = true; // 動かさない(ゲームの開始位置のまま)
                    if (applied++ < 200)
                    {
                        Plugin.Log.LogInfo($"並べ直し: {SemiFunc.PlayerGetName(player)} はゲームの開始位置のままにします");
                    }
                    return false;
                case SpawnLayout.Spread:
                    if (!SpreadPosition(player, out position))
                    {
                        __result = true; // 重なっていない
                        return false;
                    }
                    rotation = player.transform.rotation;
                    break;
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

        /// <summary>
        /// ゲームの開始位置で、先に置いた人と重なっていたら、コースの横向きにずらす(右・左・右 2 つ分…の順)。
        /// 元の MOD は全員を同じフレームで置くので、フレームが変わったら置いた位置を忘れる。
        /// </summary>
        private static bool SpreadPosition(PlayerAvatar player, out Vector3 position)
        {
            if (spreadFrame != Time.frameCount)
            {
                spreadFrame = Time.frameCount;
                Taken.Clear();
            }
            Vector3 start = player.transform.position;
            Vector3 forward = ArenaRace.instance && ArenaRace.instance.trackSpawnOrigin ? ArenaRace.instance.trackSpawnOrigin.forward : Vector3.forward;
            forward.y = 0f;
            Vector3 right = Vector3.Cross(Vector3.up, forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward).normalized;
            position = start;
            for (int k = 0; k < 20; k++)
            {
                float offset = k == 0 ? 0f : (k % 2 == 1 ? 1f : -1f) * 0.9f * ((k + 1) / 2);
                Vector3 candidate = start + right * offset;
                if (!Taken.Exists(t => Vector3.Distance(t, candidate) < 0.7f))
                {
                    position = candidate;
                    break;
                }
            }
            Taken.Add(position);
            return position != start;
        }
    }
}
