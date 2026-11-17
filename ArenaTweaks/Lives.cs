using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaTweaks
{
    /// <summary>
    /// 残機(ホストの PC)。
    /// - 倒れた人に残機があれば、カウントダウンのあと復活させる(バトルロワイヤルはステージの中央、レースはトップの人の少し後ろ)
    /// - 復活を待っている間は、ゲームの「生き残りの人数」の判定を止める(王冠の檻が開く・レースの勝者が決まる、を待たせる)
    /// - 復活はゲーム本来の「頭から復活」を使う: 先に頭を復活させる場所へ動かし、全員の PC に位置が届いてから復活させる
    /// </summary>
    internal static class Lives
    {
        private sealed class Life
        {
            public PlayerAvatar Avatar;
            public string Name;
            public int Left;
            public bool SeenAlive;
            public bool DeathHandled;
            public bool Pending;
            public bool Reviving;
            public float ReviveAt;
            public int LastCount;
            public PlayerDeathHead ProtectedHead;
            public bool HeadDestroyDisableBefore;
        }

        internal struct Spot
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public int PieceIndex;
        }

        private const string VehiclePath = "Items/Item Vehicle Semiscooter Small";

        private static Object sceneKey;
        private static bool isRace;
        private static Vector3? arenaCenter;
        private static bool arenaSpotLogged;
        private static readonly Dictionary<string, Life> All = new Dictionary<string, Life>();

        private static int MaxLives => isRace ? Plugin.RaceLives.Value : Plugin.ArenaLives.Value;

        /// <summary>今のアリーナで、復活を待っている人がいるか。</summary>
        internal static bool AnyPending
        {
            get
            {
                if (!EnsureScene())
                {
                    return false;
                }
                foreach (Life l in All.Values)
                {
                    if (l.Pending && l.Avatar)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>Runner から毎フレーム。</summary>
        internal static void Update()
        {
            if (!EnsureScene() || MaxLives <= 0)
            {
                return;
            }
            DetectDeaths();
            float now = Time.time;
            foreach (Life l in All.Values.ToList())
            {
                if (!l.Pending || l.Reviving)
                {
                    continue;
                }
                if (!l.Avatar)
                {
                    l.Pending = false;
                    continue;
                }
                if (!CanRevive())
                {
                    Cancel(l, "試合が終わったので、復活をやめました");
                    continue;
                }
                int count = Mathf.CeilToInt(l.ReviveAt - now);
                if (count >= 1 && count <= 3 && count < l.LastCount)
                {
                    l.LastCount = count;
                    Say(l, count.ToString());
                }
                if (now >= l.ReviveAt)
                {
                    l.Reviving = true;
                    Plugin.Runner.StartCoroutine(isRace ? ReviveInRace(l) : ReviveInArena(l));
                }
            }
        }

        /// <summary>倒れた人を見つける(ゲームの判定より先に呼ぶ。復活を待つ人を、脱落として数えさせないため)。</summary>
        internal static void DetectDeaths()
        {
            if (!EnsureScene() || MaxLives <= 0)
            {
                return;
            }
            foreach (PlayerAvatar p in SemiFunc.PlayerGetAll())
            {
                string id = p ? p.steamID : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                if (!All.TryGetValue(id, out Life l))
                {
                    l = new Life { Left = MaxLives };
                    All[id] = l;
                }
                l.Avatar = p;
                l.Name = SemiFunc.PlayerGetName(p) ?? id;
                bool dead = p.deadSet || p.isDisabled;
                if (!dead)
                {
                    l.SeenAlive = true;
                    l.DeathHandled = false;
                    continue;
                }
                if (!l.SeenAlive || l.DeathHandled || l.Pending)
                {
                    continue;
                }
                l.DeathHandled = true;
                OnDeath(l);
            }
        }

        /// <summary>レース: 復活を待つ人を、順位の表から一時的に外す(脱落の演出を出させない)。</summary>
        internal static void HideFromPlacements(ArenaRace race)
        {
            foreach (Life l in All.Values)
            {
                if (l.Pending && l.Avatar && race.playerVehicleMap != null && race.playerVehicleMap.ContainsKey(l.Avatar))
                {
                    race.eliminatedPlayers.Add(l.Avatar);
                }
            }
        }

        private static bool EnsureScene()
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer() || !SemiFunc.RunIsArena())
            {
                if (sceneKey != null)
                {
                    sceneKey = null;
                    All.Clear();
                }
                return false;
            }
            Object key = ArenaRace.instance ? (Object)ArenaRace.instance : Arena.instance;
            if (!key)
            {
                return false;
            }
            if (key != sceneKey)
            {
                sceneKey = key;
                isRace = key is ArenaRace;
                All.Clear();
                arenaCenter = null;
                arenaSpotLogged = false;
                missLogs = 0;
            }
            return true;
        }

        private static bool CanRevive()
        {
            if (isRace)
            {
                ArenaRace race = ArenaRace.instance;
                return race && race.currentState == ArenaRace.States.RaceActive && !race.winnerPlayer &&
                       !race.crownChasing && !race.crownExploded && !race.crownGoingToPieceEnd;
            }
            Arena arena = Arena.instance;
            return arena && arena.currentState != Arena.States.GameOver && !arena.winnerPlayer;
        }

        private static void OnDeath(Life l)
        {
            if (!CanRevive())
            {
                return;
            }
            if (Integration.IsSpectator(l.Avatar))
            {
                return; // ArenaGrandPrix の決勝で観戦になった人
            }
            if (l.Left <= 0)
            {
                Plugin.Log.LogInfo($"{l.Name} は残機がないので脱落です");
                Say(l, "残機なし！");
                return;
            }
            PlayerDeathHead head = l.Avatar.playerDeathHead;
            if (!head || !head.physGrabObject || !head.physGrabObject.impactDetector)
            {
                Plugin.Log.LogWarning($"{l.Name} の頭が見つからないので、復活させられません");
                return;
            }
            // アリーナでは頭が壊れる(壊れると復活できない)。復活するまで、奈落に落ちても消えないようにする
            l.ProtectedHead = head;
            l.HeadDestroyDisableBefore = head.physGrabObject.impactDetector.destroyDisable;
            head.physGrabObject.impactDetector.destroyDisable = true;
            l.Left--;
            l.Pending = true;
            l.Reviving = false;
            l.ReviveAt = Time.time + Plugin.ReviveSeconds.Value;
            l.LastCount = int.MaxValue;
            int seconds = Mathf.RoundToInt(Plugin.ReviveSeconds.Value);
            Plugin.Log.LogInfo($"{l.Name} が倒れました。{seconds} 秒後に復活します(残り {l.Left} 機)");
            Say(l, $"残り{l.Left}機。{seconds}秒後に復活！");
        }

        private static void Cancel(Life l, string reason)
        {
            if (l.Pending)
            {
                Plugin.Log.LogInfo($"{l.Name}: {reason}");
            }
            l.Pending = false;
            l.Reviving = false;
            RestoreHead(l);
        }

        /// <summary>頭を、壊れる・壊れないの元の状態に戻す(最後に倒れたときは、いつもどおり壊れるように)。</summary>
        private static void RestoreHead(Life l)
        {
            if (l.ProtectedHead && l.ProtectedHead.physGrabObject && l.ProtectedHead.physGrabObject.impactDetector)
            {
                l.ProtectedHead.physGrabObject.impactDetector.destroyDisable = l.HeadDestroyDisableBefore;
            }
            l.ProtectedHead = null;
        }

        /// <summary>
        /// 頭を復活させる場所へ動かして、少しの間止めておく(転がったり、持たれて動いたりしないように)。
        /// 頭の物理はホストが動かしているので、ホストで止めれば全員の PC で止まる。
        /// </summary>
        private static void PlaceHead(PlayerDeathHead head, Vector3 position, Quaternion rotation)
        {
            head.physGrabObject.Teleport(position, rotation);
            head.physGrabObject.OverrideKinematic(0.8f);
        }

        /// <summary>復活した人が、決めた場所から離れていたら(頭が動いてしまったなど)、その場所へ動かす。</summary>
        private static void Correct(Life l, Vector3 position, Quaternion rotation, float tolerance, string what)
        {
            if (!l.Avatar || l.Avatar.isDisabled || Vector3.Distance(l.Avatar.transform.position, position) <= tolerance)
            {
                return;
            }
            Plugin.Log.LogInfo($"{l.Name} が{what}から離れているので、動かします");
            if (SemiFunc.IsMultiplayer())
            {
                l.Avatar.photonView.RPC("SpawnRPC", RpcTarget.All, position, rotation, false);
            }
            else
            {
                l.Avatar.SpawnRPC(position, rotation, false, default);
            }
        }

        // ---------------------------------------------------------------- バトルロワイヤル

        private static IEnumerator ReviveInArena(Life l)
        {
            Arena arena = Arena.instance;
            float giveUp = Time.time + 10f;
            while (l.Avatar && !l.Avatar.isDisabled)
            {
                // 倒れる演出の途中(まだ頭が出ていない)。終わるまで待つ
                if (Time.time > giveUp || !CanRevive())
                {
                    Cancel(l, "倒れる演出が終わらないので、復活をやめました");
                    yield break;
                }
                yield return new WaitForSeconds(0.1f);
            }
            if (!l.Avatar || !CanRevive() || Arena.instance != arena)
            {
                Cancel(l, "復活をやめました");
                yield break;
            }
            Vector3 spot = ArenaSpot(arena);
            PlayerDeathHead head = l.Avatar.playerDeathHead;
            if (!head)
            {
                Cancel(l, "頭がなくなったので、復活させられません");
                yield break;
            }
            PlaceHead(head, spot, Quaternion.identity);
            yield return new WaitForSeconds(0.5f); // 頭の位置が全員の PC に届くのを待つ
            if (!l.Avatar || !l.Avatar.isDisabled || !CanRevive() || Arena.instance != arena)
            {
                Cancel(l, "復活をやめました");
                yield break;
            }
            l.Avatar.Revive(false);
            l.Pending = false;
            l.Reviving = false;
            RestoreHead(l);
            Plugin.Log.LogInfo($"{l.Name} が復活しました(残り {l.Left} 機)");
            yield return new WaitForSeconds(0.5f);
            Correct(l, spot, l.Avatar ? l.Avatar.transform.rotation : Quaternion.identity, 2.5f, "復活する場所");
            if (l.Avatar && !l.Avatar.isDisabled && l.Avatar.playerHealth)
            {
                l.Avatar.playerHealth.HealOther(l.Avatar.playerHealth.maxHealth, false);
            }
            SayRevived(l);
        }

        /// <summary>ステージの中央(床の上、少し高いところ)。中央は試合の初めに床の形から決めて、高さは毎回床を探す(床が落ちていくため)。</summary>
        private static Vector3 ArenaSpot(Arena arena)
        {
            if (arenaCenter == null)
            {
                var bounds = new Bounds();
                bool any = false;
                if (arena.platforms != null)
                {
                    foreach (ArenaPlatform platform in arena.platforms)
                    {
                        if (!platform)
                        {
                            continue;
                        }
                        foreach (Renderer r in platform.GetComponentsInChildren<Renderer>())
                        {
                            if (!any)
                            {
                                bounds = r.bounds;
                                any = true;
                            }
                            else
                            {
                                bounds.Encapsulate(r.bounds);
                            }
                        }
                    }
                }
                arenaCenter = any ? bounds.center : arena.crownTransformPosition != Vector3.zero ? arena.crownTransformPosition : arena.transform.position;
                Plugin.Log.LogInfo($"バトルロワイヤルの中央: {arenaCenter.Value}({(any ? "床の形から" : "床が見つからないので王冠の位置から")})");
            }
            Vector3 center = arenaCenter.Value;
            float height = Plugin.ArenaReviveHeight.Value;
            if (FindFloor(center + Vector3.up * 40f, 120f, out RaycastHit hit))
            {
                if (!arenaSpotLogged)
                {
                    arenaSpotLogged = true;
                    Plugin.Log.LogInfo($"バトルロワイヤルで復活する床: {hit.collider.name}(高さ {hit.point.y:F1})");
                }
                return hit.point + Vector3.up * height;
            }
            Plugin.Log.LogWarning("ステージの中央に床が見つかりませんでした。中央の高さで復活させます");
            return center + Vector3.up * height;
        }

        // ---------------------------------------------------------------- レース

        private static IEnumerator ReviveInRace(Life l)
        {
            ArenaRace race = ArenaRace.instance;
            float start = Time.time;
            Spot spot;
            while (true)
            {
                if (!l.Avatar || !CanRevive() || ArenaRace.instance != race)
                {
                    Cancel(l, "復活をやめました");
                    yield break;
                }
                // 走っている人がしばらく見つからなければ、ジャンプ中の人の後ろでもよいことにする
                bool groundedOnly = Plugin.RaceGroundedOnly.Value && Time.time - start < 6f;
                if (l.Avatar.isDisabled && TryFindRaceSpot(race, l.Avatar, groundedOnly, out spot))
                {
                    break;
                }
                if (Time.time - start > 20f)
                {
                    Cancel(l, "復活する場所が見つからないので、復活をやめました");
                    yield break;
                }
                yield return new WaitForSeconds(0.2f);
            }
            PlayerDeathHead head = l.Avatar.playerDeathHead;
            if (!head)
            {
                Cancel(l, "頭がなくなったので、復活させられません");
                yield break;
            }
            PlaceHead(head, spot.Position + Vector3.up * 1.2f, spot.Rotation);
            yield return new WaitForSeconds(0.5f); // 頭の位置が全員の PC に届くのを待つ
            if (!l.Avatar || !l.Avatar.isDisabled || !CanRevive() || ArenaRace.instance != race)
            {
                Cancel(l, "復活をやめました");
                yield break;
            }
            // カートを先に渡す(カートがないまま生き返ると、ゲームがすぐ倒してしまう)
            ItemVehicle vehicle = GiveVehicle(race, l.Avatar, spot);
            if (!vehicle)
            {
                Cancel(l, "カートを出せなかったので、復活をやめました");
                yield break;
            }
            race.eliminatedPlayers.Remove(l.Avatar);
            l.Avatar.Revive(false);
            l.Pending = false;
            l.Reviving = false;
            RestoreHead(l);
            Plugin.Log.LogInfo($"{l.Name} がコース {spot.PieceIndex} で復活しました(残り {l.Left} 機)");
            yield return new WaitForSeconds(0.6f);
            // カートに乗れていない(遠くにいる)ときは、カートのところへ動かす
            if (vehicle)
            {
                Correct(l, vehicle.transform.position + Vector3.up * 1f, spot.Rotation, 5f, "カート");
            }
            SayRevived(l);
        }

        private static ItemVehicle GiveVehicle(ArenaRace race, PlayerAvatar player, Spot spot)
        {
            Vector3 pos = spot.Position + Vector3.up * 0.6f;
            try
            {
                if (SemiFunc.IsMultiplayer())
                {
                    GameObject obj = PhotonNetwork.InstantiateRoomObject(VehiclePath, pos, spot.Rotation, 0, null);
                    ItemVehicle vehicle = obj ? obj.GetComponent<ItemVehicle>() : null;
                    PhotonView view = obj ? obj.GetComponent<PhotonView>() : null;
                    if (!vehicle || !view)
                    {
                        return null;
                    }
                    // どのコースにいるかを先に決めておく(決めないとスタートからの扱いになり、通ったコースを一気に落としてしまう)
                    race.vehicleCurrentPiece[vehicle] = spot.PieceIndex;
                    race.photonView.RPC("SyncVehiclePieceRPC", RpcTarget.Others, view.ViewID, spot.PieceIndex);
                    race.photonView.RPC("AssignVehiclesRPC", RpcTarget.All, new[] { view.ViewID }, new[] { player.photonView.ViewID });
                    return vehicle;
                }
                GameObject prefab = Resources.Load<GameObject>(VehiclePath);
                GameObject local = prefab ? Object.Instantiate(prefab, pos, spot.Rotation) : null;
                ItemVehicle single = local ? local.GetComponent<ItemVehicle>() : null;
                if (!single)
                {
                    return null;
                }
                race.vehicles.Add(single);
                race.vehicleCurrentPiece[single] = spot.PieceIndex;
                race.playerVehicleMap[player] = single;
                return single;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"カートを出せませんでした: {e}");
                return null;
            }
        }

        /// <summary>トップから順に、カートで走っている人の少し後ろのコースの床を探す。</summary>
        private static bool TryFindRaceSpot(ArenaRace race, PlayerAvatar dead, bool groundedOnly, out Spot spot)
        {
            spot = default;
            var order = new List<PlayerAvatar>(race.placementOrder);
            foreach (PlayerAvatar p in race.playerVehicleMap.Keys)
            {
                if (!order.Contains(p))
                {
                    order.Add(p);
                }
            }
            bool anyRunner = false;
            float behind = Plugin.RaceReviveBehind.Value;
            foreach (PlayerAvatar p in order)
            {
                if (!p || p == dead || p.isDisabled || p.deadSet)
                {
                    continue;
                }
                if (!race.playerVehicleMap.TryGetValue(p, out ItemVehicle vehicle) || !vehicle || vehicle.isFullyDestroyed || !vehicle.rb)
                {
                    continue;
                }
                anyRunner = true;
                if (groundedOnly && !vehicle.isAnyWheelGrounded)
                {
                    continue;
                }
                Vector3 forward = TrackForward(race, vehicle);
                foreach (float scale in new[] { 1f, 0.6f, 1.5f, 0.3f })
                {
                    Vector3 origin = vehicle.rb.position - forward * (behind * scale) + Vector3.up * 4f;
                    if (FindTrack(race, origin, out Vector3 point, out ArenaRaceTrackPiece piece) && PieceSafe(piece, 2f))
                    {
                        spot = new Spot { Position = point, Rotation = Quaternion.LookRotation(forward), PieceIndex = piece.pieceIndex };
                        return true;
                    }
                }
            }
            if (anyRunner)
            {
                return false; // 走っている人はいる。床に着くまで待つ
            }
            // 走っている人がいない: まだ崩れ始めていない、いちばん手前のコースの、入口から少し進んだところ
            ArenaRaceTrackPiece first = race.spawnedTrackPieces
                .Where(x => x && !x.countdownStarted && !x.plunging)
                .OrderBy(x => x.pieceIndex)
                .FirstOrDefault();
            if (!first)
            {
                return false;
            }
            Bounds b = first.CalculateBounds();
            Vector3 fwd = race.cachedPieceEndForwards.TryGetValue(first.pieceIndex, out Vector3 f) ? Flat(f) : Flat(first.transform.forward);
            var candidates = new List<Vector3>();
            if (race.cachedPieceEndPositions.TryGetValue(first.pieceIndex - 1, out Vector3 entrance))
            {
                foreach (float d in new[] { 4f, 7f, 10f, 2f, 13f })
                {
                    candidates.Add(entrance + fwd * d);
                }
            }
            candidates.Add(b.center);
            candidates.Add(first.transform.position + fwd * 4f);
            float top = b.max.y + 4f;
            foreach (Vector3 c in candidates)
            {
                if (FindTrack(race, new Vector3(c.x, Mathf.Max(top, c.y + 4f), c.z), out Vector3 p2, out ArenaRaceTrackPiece piece2, b.size.y + 20f) && PieceSafe(piece2, 2f))
                {
                    spot = new Spot { Position = p2, Rotation = Quaternion.LookRotation(fwd), PieceIndex = piece2.pieceIndex };
                    return true;
                }
            }
            return false;
        }

        private static Vector3 TrackForward(ArenaRace race, ItemVehicle vehicle)
        {
            if (race.vehicleCurrentPiece.TryGetValue(vehicle, out int index) && race.cachedPieceEndForwards.TryGetValue(index, out Vector3 f))
            {
                return Flat(f);
            }
            return Flat(vehicle.transform.forward);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.01f ? v.normalized : Vector3.forward;
        }

        private static bool PieceSafe(ArenaRaceTrackPiece piece, float minSeconds) =>
            piece && !piece.plunging && (!piece.countdownStarted || piece.countdownTimer >= minSeconds);

        private static int missLogs;

        /// <summary>
        /// 真下にあるコースの床(物やプレイヤーは飛ばす。ほかの物に当たったら失敗)。
        /// 床の当たり判定がコースの部品の子でないこともあるので、当たった点がコースの部品の範囲にあれば、その部品の床とみなす。
        /// </summary>
        private static bool FindTrack(ArenaRace race, Vector3 origin, out Vector3 point, out ArenaRaceTrackPiece piece, float distance = 14f)
        {
            point = default;
            piece = null;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (IsMovable(hit.collider))
                {
                    continue;
                }
                if (hit.normal.y < 0.7f)
                {
                    // 壁や障害物の横に当たった(床は、ほぼ水平な面だけ)
                    if (missLogs++ < 10)
                    {
                        Plugin.Log.LogInfo($"復活する場所を探しています: {hit.collider.name} は床ではありません(傾きすぎ)");
                    }
                    return false;
                }
                piece = hit.collider.GetComponentInParent<ArenaRaceTrackPiece>();
                if (!piece && hit.collider.attachedRigidbody && !hit.collider.attachedRigidbody.isKinematic)
                {
                    // 動く物(コースの部品の外のもの)は床にしない
                    if (missLogs++ < 10)
                    {
                        Plugin.Log.LogInfo($"復活する場所を探しています: {hit.collider.name} は動く物なので床にしません");
                    }
                    return false;
                }
                if (!piece)
                {
                    foreach (ArenaRaceTrackPiece p in race.spawnedTrackPieces)
                    {
                        if (!p)
                        {
                            continue;
                        }
                        Bounds b = p.CalculateBounds();
                        b.Expand(1f);
                        if (b.Contains(hit.point))
                        {
                            piece = p;
                            break;
                        }
                    }
                }
                if (!piece)
                {
                    if (missLogs++ < 10)
                    {
                        Plugin.Log.LogInfo($"復活する場所を探しています: {hit.collider.name}(レイヤー {LayerMask.LayerToName(hit.collider.gameObject.layer)})はコースの床ではありません");
                    }
                    return false;
                }
                point = hit.point;
                return true;
            }
            if (missLogs++ < 10)
            {
                Plugin.Log.LogInfo($"復活する場所を探しています: {origin} の下に床がありません");
            }
            return false;
        }

        private static bool FindFloor(Vector3 origin, float distance, out RaycastHit floor)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (!IsMovable(hit.collider))
                {
                    floor = hit;
                    return true;
                }
            }
            floor = default;
            return false;
        }

        private static bool IsMovable(Collider c) =>
            c.GetComponentInParent<PhysGrabObject>() || c.GetComponentInParent<PlayerAvatar>() || c.GetComponentInParent<EnemyParent>();

        // ---------------------------------------------------------------- 読み上げ

        private static void Say(Life l, string text)
        {
            switch (Plugin.SpeakerKind.Value)
            {
                case Speaker.Self:
                    Speak(l.Avatar, text);
                    break;
                case Speaker.Host:
                    Speak(SemiFunc.PlayerAvatarLocal(), $"{l.Name}、{text}");
                    break;
            }
        }

        private static void SayRevived(Life l)
        {
            if (Plugin.SpeakerKind.Value == Speaker.None)
            {
                return;
            }
            // 本人にも聞こえるように、復活した本人が言う(倒れている間は、本人の PC では読み上げが鳴らない)
            Speak(l.Avatar, l.Left > 0 ? $"復活！残り{l.Left}機" : "復活！もう残機なし");
        }

        private static void Speak(PlayerAvatar player, string text)
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
                Plugin.Log.LogWarning($"読み上げられませんでした: {e.Message}");
            }
        }
    }

    /// <summary>ほかの MOD との連携(入っていなければ何もしない)。</summary>
    internal static class Integration
    {
        private static bool looked;
        private static Func<PlayerAvatar, bool> spectator;

        /// <summary>ArenaGrandPrix の決勝で、観戦になった人か(その人は復活させない)。</summary>
        internal static bool IsSpectator(PlayerAvatar player)
        {
            if (!looked)
            {
                looked = true;
                var method = AccessTools.Method("ArenaGrandPrix.Api:IsSpectator", new[] { typeof(PlayerAvatar) });
                if (method != null)
                {
                    spectator = AccessTools.MethodDelegate<Func<PlayerAvatar, bool>>(method);
                    Plugin.Log.LogInfo("ArenaGrandPrix と連携します(決勝で観戦になった人は復活させません)");
                }
            }
            try
            {
                return spectator != null && spectator(player);
            }
            catch
            {
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- ゲームの判定を待たせる

    /// <summary>
    /// レース: 復活を待つ人がいる間は、生き残りの人数を数えない(勝者を決めない・全員脱落にしない)。
    /// カートが壊れた人を倒す判定は、ゲームが毎フレーム別に(VehicleDestroyedDeathCheck)しているので、止めても残る。
    /// </summary>
    [HarmonyPatch(typeof(ArenaRace), nameof(ArenaRace.PlayerAliveCheck))]
    internal static class RaceAliveCheckPatch
    {
        private static bool Prefix()
        {
            Lives.DetectDeaths();
            return !Lives.AnyPending;
        }
    }

    /// <summary>レース: 復活を待つ人を、脱落として順位の表から消さない(脱落の演出を出さない)。</summary>
    [HarmonyPatch(typeof(ArenaRace), nameof(ArenaRace.UpdatePlacements))]
    internal static class RacePlacementsPatch
    {
        private static void Prefix(ArenaRace __instance)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer())
            {
                return;
            }
            Lives.DetectDeaths();
            Lives.HideFromPlacements(__instance);
        }
    }

    /// <summary>バトルロワイヤル: 復活を待つ人がいる間は、生き残りの人数を数えない(王冠の檻を開けない・終わりにしない)。</summary>
    [HarmonyPatch(typeof(Arena), nameof(Arena.CrownLogic))]
    internal static class ArenaCrownLogicPatch
    {
        private static bool Prefix()
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer())
            {
                return true;
            }
            Lives.DetectDeaths();
            return !Lives.AnyPending;
        }
    }
}
