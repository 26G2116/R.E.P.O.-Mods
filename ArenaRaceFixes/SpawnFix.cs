using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
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
    /// 既定では、スタートの箱の下のコースの上に、元の MOD の「横の人数」で並べる(箱には大人数が入らないため)。
    /// コースの上にいれば、カートはその足元に出て、スタートの合図まではゲームが運転を止めている。
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
                case SpawnLayout.Track:
                    if (!TrackSlot(index, out position, out rotation))
                    {
                        __result = true; // コースが見つからない: ゲームの開始位置のまま
                        return false;
                    }
                    break;
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

        // ---------------------------------------------------------------- コースの上に並べる

        private static int planFrame = -1;
        private static readonly List<Vector3> Slots = new List<Vector3>();
        private static Quaternion slotRotation = Quaternion.identity;

        /// <summary>index 番目の人の場所。元の MOD は全員を同じフレームで置くので、フレームごとに 1 回だけ並べ方を作る。</summary>
        private static bool TrackSlot(int index, out Vector3 position, out Quaternion rotation)
        {
            if (planFrame != Time.frameCount)
            {
                planFrame = Time.frameCount;
                try
                {
                    BuildTrackPlan();
                }
                catch (Exception e)
                {
                    Slots.Clear();
                    Plugin.Log.LogError($"並べ直し: コースの上に並べられませんでした: {e}");
                }
            }
            rotation = slotRotation;
            if (index < 0 || index >= Slots.Count)
            {
                position = default;
                return false;
            }
            position = Slots[index];
            return true;
        }

        private static void BuildTrackPlan()
        {
            Slots.Clear();
            ArenaRace race = ArenaRace.instance;
            if (!race || !race.trackSpawnOrigin)
            {
                Plugin.Log.LogWarning("並べ直し: レースのコースが見つからないので、ゲームの開始位置のままにします");
                return;
            }
            List<PlayerAvatar> players = SemiFunc.PlayerGetAll();
            int needed = players.Count;
            Vector3 forward = race.trackSpawnOrigin.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            slotRotation = Quaternion.LookRotation(forward);

            // 先頭の列: スタートの箱(今いる場所)の真下のコース。見つからなければ、コースの始まりの少し先
            Vector3 center = Vector3.zero;
            int count = 0;
            foreach (PlayerAvatar p in players)
            {
                if (p)
                {
                    center += p.transform.position;
                    count++;
                }
            }
            center = count > 0 ? center / count : race.trackSpawnOrigin.position;
            Vector3 anchor;
            if (!FirstTrackBelow(race, center + Vector3.up, 60f, out anchor) &&
                !FirstTrackBelow(race, race.trackSpawnOrigin.position + forward * 6f + Vector3.up * 10f, 30f, out anchor))
            {
                Plugin.Log.LogWarning("並べ直し: スタートの箱の下にコースが見つからないので、ゲームの開始位置のままにします");
                return;
            }

            int columns = Mathf.Clamp(SourceConfig("horizontalPlayersConfig", 10), 1, 20);
            float columnGap = Plugin.ColumnGap.Value;
            float rowGap = Plugin.RowGap.Value;
            // 列: 先頭(箱の真下)から、うしろ・前の順に試す。コースの床の上の場所だけ使う
            var found = new List<KeyValuePair<Vector2, Vector3>>(); // (列の前後, 横) → 床の点
            for (int k = 0; k < 41 && found.Count < needed; k++)
            {
                float z = k == 0 ? 0f : (k % 2 == 1 ? -1f : 1f) * rowGap * ((k + 1) / 2);
                for (int c = 0; c < columns; c++)
                {
                    float x = (c - (columns - 1) * 0.5f) * columnGap;
                    Vector3 origin = anchor + right * x + forward * z + Vector3.up * 3f;
                    if (TrackFloor(race, origin, 8f, out Vector3 point) && Footprint(race, origin, right, forward))
                    {
                        found.Add(new KeyValuePair<Vector2, Vector3>(new Vector2(z, x), point));
                    }
                }
            }
            // 前の列から、左から順に
            found.Sort((a, b) => a.Key.x != b.Key.x ? b.Key.x.CompareTo(a.Key.x) : a.Key.y.CompareTo(b.Key.y));
            foreach (var f in found)
            {
                Slots.Add(f.Value + Vector3.up * 1.6f); // カートはプレイヤーの 1.3 m 下に出る
            }
            Plugin.Log.LogInfo($"並べ直し: スタートの箱の下のコース {anchor} を先頭に、横 {columns} 人で {Slots.Count} か所を用意しました(参加 {needed} 人)");
            if (Slots.Count < needed)
            {
                Plugin.Log.LogWarning($"並べ直し: コースの上に {needed} 人分の場所がとれませんでした。残りの人はゲームの開始位置のままです");
            }
        }

        /// <summary>カートの四隅(横 0.6 m・前後 1 m)の下も、コースの床か(端でカートがはみ出して落ちないように)。</summary>
        private static bool Footprint(ArenaRace race, Vector3 origin, Vector3 right, Vector3 forward)
        {
            foreach (Vector3 corner in new[] { right * 0.6f + forward, right * 0.6f - forward, -right * 0.6f + forward, -right * 0.6f - forward })
            {
                if (!TrackFloor(race, origin + corner, 8f, out _))
                {
                    return false;
                }
            }
            return true;
        }

        private static int SourceConfig(string field, int fallback)
        {
            try
            {
                Type type = AccessTools.TypeByName(TargetType);
                if (type != null && AccessTools.Field(type, field)?.GetValue(null) is ConfigEntry<int> entry)
                {
                    return entry.Value;
                }
            }
            catch
            {
            }
            return fallback;
        }

        /// <summary>上から見て最初に当たるコースの床(箱やパイプなど、コースでないものは飛ばす)。</summary>
        private static bool FirstTrackBelow(ArenaRace race, Vector3 origin, float distance, out Vector3 point)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (hit.normal.y >= 0.7f && !IsMovable(hit.collider) && IsTrack(race, hit))
                {
                    point = hit.point;
                    return true;
                }
            }
            point = default;
            return false;
        }

        /// <summary>真下の最初の物がコースの床か(障害物や壁なら、その場所は使わない)。</summary>
        private static bool TrackFloor(ArenaRace race, Vector3 origin, float distance, out Vector3 point)
        {
            point = default;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (IsMovable(hit.collider))
                {
                    continue;
                }
                if (hit.normal.y < 0.7f || !IsTrack(race, hit))
                {
                    return false;
                }
                point = hit.point;
                return true;
            }
            return false;
        }

        private static bool IsMovable(Collider c) =>
            c.GetComponentInParent<PhysGrabObject>() || c.GetComponentInParent<PlayerAvatar>() || c.GetComponentInParent<EnemyParent>();

        /// <summary>当たったのがコースの部品か(部品の子、または部品の範囲の中)。</summary>
        private static bool IsTrack(ArenaRace race, RaycastHit hit)
        {
            if (hit.collider.GetComponentInParent<ArenaRaceTrackPiece>())
            {
                return true;
            }
            if (hit.collider.attachedRigidbody && !hit.collider.attachedRigidbody.isKinematic)
            {
                return false;
            }
            IEnumerable<ArenaRaceTrackPiece> pieces = race.spawnedTrackPieces != null && race.spawnedTrackPieces.Count > 0
                ? (IEnumerable<ArenaRaceTrackPiece>)race.spawnedTrackPieces
                : UnityEngine.Object.FindObjectsByType<ArenaRaceTrackPiece>(FindObjectsSortMode.None);
            foreach (ArenaRaceTrackPiece piece in pieces)
            {
                if (!piece)
                {
                    continue;
                }
                Bounds b = piece.CalculateBounds();
                b.Expand(0.5f);
                if (b.Contains(hit.point))
                {
                    return true;
                }
            }
            return false;
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
