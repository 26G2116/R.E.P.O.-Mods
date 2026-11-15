using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AIMonsters
{
    /// <summary>
    /// 味方のローブ(ホストだけで動く)。
    /// - 召喚: ティア 3 の敵のオーブ(大、AssetManager.enemyValuableBig)が、プレイヤーにつかまれたまま壊れたら(DestroyObject)、
    ///   その場に味方のローブを出す。レベル中に 1 回だけ。出荷(DestroyPhysGrabObject)は別の処理なので数えない
    /// - 動き: プレイヤーについて歩き、近くの敵を攻撃する(ゲームの攻撃の動きで見せ、ダメージはホストが直接与える)。プレイヤーは狙わない
    /// - 見分け方: 頭の上にヒーラーオーブを浮かべる(ゲーム本来の物なので MOD なしの人にも見える)。出たときに一言しゃべる
    /// - 敵の AI(Director)の対象にはしない
    /// 会話・指示は AllyChat が行う。
    /// </summary>
    internal static class AllyRobe
    {
        private const string Section = "5. 味方の Robe";

        internal static ConfigEntry<bool> Enabled;
        private static ConfigEntry<float> AttackRange;
        private static ConfigEntry<float> LeashRange;
        private static ConfigEntry<int> Damage;
        private static ConfigEntry<float> DamageTaken;
        private static ConfigEntry<bool> MarkerOrb;
        private static ConfigEntry<float> RespawnDelay;
        internal static ConfigEntry<bool> HideWhileJoining;

        internal enum Mode
        {
            Follow,
            Wait,
            Attack,
            /// <summary>案内: 先に歩いて、プレイヤーを目的地(トラック・ほかのプレイヤー)へ連れていく。</summary>
            Guide,
        }

        /// <summary>自分からは敵を攻撃しない(「敵を攻撃しないで」)。攻撃の指示・「戦って」で戻る。</summary>
        internal static bool Peaceful;

        /// <summary>案内の目的地がトラックか(false ならプレイヤー GuideTarget)。</summary>
        internal static bool GuideToTruck { get; private set; }
        internal static PlayerAvatar GuideTarget { get; private set; }
        /// <summary>案内される人(この人が離れたら待つ。着いたらこの人についていく)。</summary>
        private static PlayerAvatar guideFor;
        private static float guideUntil;
        private static bool guideJapanese;
        /// <summary>案内される人を迎えに行っている(遠くにいる人をまずそばまで迎えに行く)。</summary>
        private static bool guideGathering;

        private const float GuideTimeout = 180f;
        /// <summary>案内される人がこれより離れたら止まって待つ。</summary>
        private const float GuideWaitDistance = 8f;
        /// <summary>案内される人がこれより離れたら迎えに戻る。迎えに行ったら GuideGatherDistance まで近づく。</summary>
        private const float GuideLostDistance = 15f;
        private const float GuideGatherDistance = 4f;
        private const float GuideArriveDistance = 3.5f;
        /// <summary>着いたとみなす、案内される人と目的地の距離。</summary>
        private const float GuidePlayerArriveDistance = 6f;
        private const float GuideSpeed = 4.5f;

        /// <summary>今のレベルの味方ローブ(いなければ null)。</summary>
        internal static EnemyRobe Ally { get; private set; }

        /// <summary>ついていく相手(召喚した人。倒れたら一番近い人)。</summary>
        internal static PlayerAvatar Leader;

        internal static Mode CurrentMode = Mode.Follow;

        /// <summary>指示で狙う敵(Attack のとき)。</summary>
        internal static Enemy OrderedTarget;

        private static EnemyParent allyParent;
        private static bool pendingSpawn;
        private static bool respawnTimerSet;
        private static Vector3 pendingSpot;
        private static bool used;
        private static LevelGenerator level;
        private static PrefabRef robeRef;
        private static bool robeSearched;
        private static Enemy attackTarget;
        private static Enemy swingTarget;
        private static float swingAt;
        private static float nextAttack;
        private static PhysGrabObject marker;
        private static string markerName;
        private static ItemAttributes markerAttributes;
        private static HashSet<string> namesBeforeMarker;

        // 途中参加の人への対応(AllyLateJoin)
        /// <summary>途中参加の人の読み込みを待って、退場させたままにしている。</summary>
        private static bool heldForJoin;
        /// <summary>途中参加の人の読み込み中にオーブが割られた: 読み込みが終わってから呼び出す。</summary>
        private static bool summonQueued;
        private static Vector3 queuedSpot;
        private static PlayerAvatar queuedSummoner;
        /// <summary>生成したときの情報(途中参加の人にだけ、あとから生成を送るのに使う)。</summary>
        private static string allyPath;
        private static int[] allyViewIds;
        private static int allyLevelPrefix;
        /// <summary>生成した時刻(Time.unscaledTime)。これより前から部屋にいる人は、生成をもう受け取っている。</summary>
        private static float allyCreatedAt;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true,
                "ティア 3 の敵のオーブ(大)を、つかんだまま割ると味方のローブが出る(レベル中に 1 回)");
            AttackRange = cfg.Bind(Section, "敵を攻撃しに行く距離(m)", 10f,
                new ConfigDescription("味方のローブからこの距離以内の敵を攻撃しに行く", new AcceptableValueRange<float>(0f, 40f)));
            LeashRange = cfg.Bind(Section, "ついていく相手から離れる最大の距離(m)", 15f,
                new ConfigDescription("敵を追っても、ついていく相手からこの距離より離れない", new AcceptableValueRange<float>(5f, 60f)));
            Damage = cfg.Bind(Section, "敵へのダメージ", 60,
                new ConfigDescription("1 回の攻撃で敵に与えるダメージ", new AcceptableValueRange<int>(1, 1000)));
            DamageTaken = cfg.Bind(Section, "受けるダメージの倍率", 0f,
                new ConfigDescription("味方のローブが受けるダメージの倍率(0 で倒されない。プレイヤーの武器の巻き添え対策)", new AcceptableValueRange<float>(0f, 2f)));
            MarkerOrb = cfg.Bind(Section, "頭の上にオーブを浮かべる", true,
                "敵のローブと見分けられるように、頭の上にヒーラーオーブを浮かべる(MOD なしの人にも見える)");
            RespawnDelay = cfg.Bind(Section, "消えたあと出直すまでの秒数", 1f,
                new ConfigDescription("何度も詰まったときなど、ゲームの仕組みで消えたあと、ついていく人の後ろに出直すまでの秒数",
                    new AcceptableValueRange<float>(0f, 300f)));
            HideWhileJoining = cfg.Bind(Section, "途中参加の人の読み込み中は隠す", true,
                "途中参加 MOD で人が入ってくるとき、その人の読み込みが終わるまで味方のローブを退場させる(味方のローブがいると途中参加に失敗するため)。"
                + "読み込みが終わると、ついていく人の後ろに出直す");
        }

        internal static bool IsAlly(Enemy enemy) => enemy && Ally && Ally.enemy == enemy;

        private static bool IsHost => SemiFunc.IsMasterClientOrSingleplayer();

        // ================================================================
        // 召喚
        // ================================================================

        [HarmonyPatch(typeof(PhysGrabObjectImpactDetector), nameof(PhysGrabObjectImpactDetector.DestroyObject))]
        private static class OrbBreakPatch
        {
            private static void Prefix(PhysGrabObjectImpactDetector __instance, bool forced)
            {
                if (!Plugin.Enabled.Value || !Enabled.Value || used || !IsHost || !SemiFunc.RunIsLevel())
                {
                    return;
                }
                var grab = __instance.physGrabObject;
                if ((__instance.destroyDisable && !forced) || !grab || grab.dead || !__instance.GetComponent<EnemyValuable>() || !IsBigOrb(__instance.gameObject))
                {
                    return;
                }
                var summoner = grab.playerGrabbing?.Where(g => g && g.playerAvatar && !g.playerAvatar.isDisabled)
                    .Select(g => g.playerAvatar).FirstOrDefault();
                if (!summoner)
                {
                    return; // つかまれずに壊れた(投げた・落とした・数の上限で消えた)
                }
                used = true;
                if (AllyLateJoin.Holding)
                {
                    // 途中参加の人が読み込み中: 今生成すると、その人の読み込みに混ざる。終わってから呼び出す(Tick)
                    summonQueued = true;
                    queuedSpot = grab.centerPoint;
                    queuedSummoner = summoner;
                    Plugin.Log.LogInfo("味方のローブ: 途中参加の人の読み込みが終わってから呼び出します");
                    return;
                }
                Summon(grab.centerPoint, summoner);
            }
        }

        private static bool IsBigOrb(GameObject obj)
        {
            var big = AssetManager.instance ? AssetManager.instance.enemyValuableBig : null;
            return big && obj.name.StartsWith(big.name);
        }

        private static void Summon(Vector3 position, PlayerAvatar summoner)
        {
            var prefab = RobePrefab();
            if (prefab == null || !LevelGenerator.Instance)
            {
                Plugin.Log.LogWarning("味方のローブ: ローブの敵が見つからないため、呼び出せません");
                return;
            }
            Vector3 spot = Nav.Sample(position, out var sampled) ? sampled : position;
            GameObject obj = SemiFunc.IsMultiplayer()
                ? PhotonNetwork.InstantiateRoomObject(prefab.ResourcePath, spot, Quaternion.identity, 0, null)
                : (prefab.Prefab ? Object.Instantiate(prefab.Prefab, spot, Quaternion.identity) : null);
            var parent = obj ? obj.GetComponent<EnemyParent>() : null;
            var robe = obj ? obj.GetComponentInChildren<EnemyRobe>(true) : null;
            if (!parent || !robe)
            {
                Plugin.Log.LogWarning("味方のローブ: 生成に失敗しました");
                return;
            }
            // LevelGenerator.EnemySpawnObject と同じ準備(EnemiesSpawnTarget は増やさない)
            parent.SetupDone = true;
            if (robe.enemy)
            {
                robe.enemy.EnemyTeleported(spot);
            }
            allyParent = parent;
            Ally = robe;
            // 途中参加の人の読み込みに混ざらないように、生成の記録を部屋から消す。
            // あとから入った人には、読み込みが終わってから ShowTo で生成を送る(そのための情報を覚えておく)
            var views = obj.GetComponentsInChildren<PhotonView>(true); // Photon が ID を割り当てたときと同じ順番
            allyPath = prefab.ResourcePath;
            allyViewIds = SemiFunc.IsMultiplayer() && views.Length > 0 && views.All(v => v.ViewID > 0)
                ? views.Select(v => v.ViewID).ToArray()
                : null;
            allyLevelPrefix = views.Length > 0 ? views[0].Prefix : 0;
            allyCreatedAt = Time.unscaledTime;
            AllyLateJoin.Uncache(obj);
            // ほかの MOD(MonsterTweaks)がこのローブの行動を変えないように、AI が動かしている目印をつける
            if (robe.enemy && !robe.enemy.GetComponent<AIMonstersControlled>())
            {
                robe.enemy.gameObject.AddComponent<AIMonstersControlled>();
            }
            if (robe.enemy && !robe.enemy.GetComponent<AIMonstersAlly>())
            {
                robe.enemy.gameObject.AddComponent<AIMonstersAlly>();
            }
            Leader = summoner;
            CurrentMode = Mode.Follow;
            OrderedTarget = null;
            pendingSpawn = true;
            pendingSpot = spot;
            Plugin.Log.LogInfo($"味方のローブを呼び出しました({(summoner ? summoner.playerName : "?")} がオーブを割りました)");
            Runner.Instance.StartCoroutine(Greet());
        }

        private static System.Collections.IEnumerator Greet()
        {
            yield return new WaitForSeconds(2.5f);
            if (Ally)
            {
                AllyChat.SayFixed("I'm on your side now. Lead the way.");
            }
        }

        private static PrefabRef RobePrefab()
        {
            if (robeRef != null || robeSearched)
            {
                return robeRef;
            }
            var director = EnemyDirector.instance;
            if (!director)
            {
                return null;
            }
            robeSearched = true;
            // ほかの敵のプレハブを読み込まないように、まず名前で絞り込む。見つからなければ全部を調べる
            for (int pass = 0; pass < 2 && robeRef == null; pass++)
            {
                foreach (var list in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
                {
                    if (list == null)
                    {
                        continue;
                    }
                    foreach (var setup in list)
                    {
                        if (!setup || setup.spawnObjects == null)
                        {
                            continue;
                        }
                        foreach (var spawn in setup.spawnObjects)
                        {
                            if (spawn == null || (pass == 0 && (spawn.ResourcePath == null
                                || spawn.ResourcePath.IndexOf("robe", System.StringComparison.OrdinalIgnoreCase) < 0)))
                            {
                                continue;
                            }
                            var p = spawn.Prefab;
                            if (p && p.GetComponentInChildren<EnemyRobe>(true))
                            {
                                robeRef = spawn;
                                return robeRef;
                            }
                        }
                    }
                }
            }
            return robeRef;
        }

        /// <summary>呼び出したローブをすぐ出現させ、出現している間は時間切れで帰らないようにする。</summary>
        [HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.Update))]
        private static class ParentUpdatePatch
        {
            private static void Postfix(EnemyParent __instance)
            {
                if (!allyParent || __instance != allyParent || !IsHost)
                {
                    return;
                }
                if (!__instance.Spawned)
                {
                    if (AllyLateJoin.Holding)
                    {
                        // 途中参加の人が読み込み中: 退場させたままにする
                        heldForJoin = true;
                        __instance.DespawnedTimer = Mathf.Max(__instance.DespawnedTimer, 1f);
                    }
                    else if (heldForJoin)
                    {
                        // 読み込みが終わった: すぐ出直す(最初ならオーブが割れた場所、2 回目からはついていく人の後ろ)
                        heldForJoin = false;
                        respawnTimerSet = true;
                        __instance.DespawnedTimer = Mathf.Min(__instance.DespawnedTimer, 0.3f);
                    }
                    else if (pendingSpawn)
                    {
                        // 呼び出した直後: すぐ出す
                        __instance.DespawnedTimer = Mathf.Min(__instance.DespawnedTimer, 0.3f);
                    }
                    else if (!respawnTimerSet)
                    {
                        // 消えた: 出直すまでの時間を設定の秒数にする(消えるたびに 1 回だけ)
                        respawnTimerSet = true;
                        __instance.DespawnedTimer = RespawnDelay.Value;
                    }
                }
                else
                {
                    respawnTimerSet = false;
                    __instance.SpawnedTimer = Mathf.Max(__instance.SpawnedTimer, 60f);
                }
            }
        }

        // ================================================================
        // 途中参加の人への対応(AllyLateJoin から呼ばれる)
        // ================================================================

        /// <summary>
        /// 途中参加の人が入ってきた: 味方のローブを退場させ(ゲームの「帰る」と同じ仕組み)、目印のオーブを消す。
        /// 読み込みが終わるまで出さない(ParentUpdatePatch)。退場させたら true。
        /// </summary>
        internal static bool HideForJoin()
        {
            if (!IsHost || !Ally || !allyParent)
            {
                return false;
            }
            CancelAttack();
            bool despawned = false;
            if (allyParent.Spawned)
            {
                allyParent.Despawn();
                despawned = true;
            }
            if (marker || (object)markerAttributes != null)
            {
                CleanupMarker();
            }
            markerName = null; // 出直したときに、目印のオーブも作り直す
            return despawned;
        }

        /// <summary>
        /// 読み込みが終わった途中参加の人に、味方のローブの生成を送る(生成の記録を消してあるので、その人の PC にはまだいない)。
        /// 送ったら true。joinedAt はその人が部屋に入った時刻(Time.unscaledTime)。
        /// </summary>
        internal static bool ShowTo(Photon.Realtime.Player target, float joinedAt)
        {
            if (!IsHost || !Ally || !allyParent || allyViewIds == null)
            {
                return false;
            }
            if (allyCreatedAt >= joinedAt - 1f)
            {
                // その人が部屋に入ったあとで生成した: 生成はもう届いている(もう一度送ると、同じ ID の物が 2 つできる)。
                // 入る直前(1 秒以内)の生成は、通信の遅れでどちらか分からないので送らない(その人には見えないだけで済む)
                return false;
            }
            var parent = allyParent;
            if (!AllyLateJoin.SendInstantiateTo(target, allyPath, parent.transform.position, allyViewIds, allyLevelPrefix))
            {
                Plugin.Log.LogWarning("味方のローブ: 途中参加の人に生成を送れませんでした");
                return false;
            }
            if (!parent.Spawned)
            {
                // 生成した直後は出現している状態なので、ホストと同じ「退場中」にそろえる
                parent.photonView.RPC("DespawnRPC", target);
            }
            Runner.Instance.StartCoroutine(ResendSetupDone(parent));
            return true;
        }

        /// <summary>
        /// 「準備完了」(EnemyParent.SetupDone)は値が変わったときだけ同期されるので、いったん戻してから立て直して送り直す。
        /// (届かないと、途中参加の人の PC で、このローブが敵の一覧に入らない)
        /// </summary>
        private static System.Collections.IEnumerator ResendSetupDone(EnemyParent parent)
        {
            parent.SetupDone = false;
            yield return new WaitForSecondsRealtime(0.5f);
            if (parent)
            {
                parent.SetupDone = true;
            }
        }

        /// <summary>
        /// 出現場所: 最初はオーブが割れた場所、2 回目からはついていく人の後ろ。
        /// (ゲームの「何度も詰まったら消える」などで消えたとき、遠くの出現地点に出ないようにする)
        /// </summary>
        [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.EnemySpawn))]
        private static class EnemySpawnPatch
        {
            private static bool Prefix(Enemy enemy, ref bool __result)
            {
                if (!enemy || !allyParent || enemy.EnemyParent != allyParent || !IsHost)
                {
                    return true;
                }
                Vector3 spot;
                if (pendingSpawn)
                {
                    spot = pendingSpot;
                }
                else if (!RespawnSpot(out spot))
                {
                    return true; // ついていく人がいない: ゲームの出現地点に任せる
                }
                else
                {
                    Plugin.Log.LogInfo("味方のローブ: 消えたので、ついていく人の後ろに出し直しました");
                }
                pendingSpawn = false;
                enemy.EnemyTeleported(spot);
                enemy.EnemyParent.firstSpawnPointUsed = true;
                if (enemy.Health)
                {
                    enemy.Health.spawnValuable = false; // 倒されてもオーブを落とさない(また召喚できてしまうため)
                }
                // ホストの PC では、攻撃の当たり判定がプレイヤーに当たらないようにする。
                // (当たり判定は各自の PC でも動くので、MOD なしの人が攻撃の目の前に立つと当たることはある)
                foreach (var hurt in enemy.EnemyParent.GetComponentsInChildren<HurtCollider>(true))
                {
                    hurt.playerLogic = false;
                }
                __result = true;
                return false;
            }
        }

        /// <summary>出し直す場所(ついていく人の 2m 後ろ。倒れていれば一番近い人)。</summary>
        private static bool RespawnSpot(out Vector3 spot)
        {
            spot = default;
            var leader = Leader && !Leader.isDisabled ? Leader : null;
            if (!leader)
            {
                foreach (var player in SemiFunc.PlayerGetList())
                {
                    if (player && !player.isDisabled)
                    {
                        leader = player;
                        break;
                    }
                }
            }
            if (!leader)
            {
                return false;
            }
            Vector3 position = leader.transform.position;
            var back = -Vector3.ProjectOnPlane(leader.transform.forward, Vector3.up).normalized;
            spot = Nav.Sample(position + back * 2f, out var point) ? point : Nav.Sample(position, out point) ? point : position;
            return true;
        }

        /// <summary>受けるダメージを減らす(既定は 0 = 倒されない)。</summary>
        [HarmonyPatch(typeof(EnemyHealth), nameof(EnemyHealth.Hurt))]
        private static class HurtPatch
        {
            private static void Prefix(EnemyHealth __instance, ref int _damage)
            {
                if (Ally && __instance.enemy == Ally.enemy && IsHost)
                {
                    _damage = Mathf.RoundToInt(_damage * DamageTaken.Value);
                }
            }
        }

        // ================================================================
        // 動き
        // ================================================================

        /// <summary>プレイヤーを見つけても突進しない。</summary>
        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.OnVision))]
        private static class VisionPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(EnemyRobe __instance) => !(Ally && __instance == Ally);
        }

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.Update))]
        private static class UpdatePatch
        {
            private static void Prefix(EnemyRobe __instance)
            {
                if (Ally && __instance == Ally && IsHost)
                {
                    Drive(__instance);
                }
            }
        }

        private static void Drive(EnemyRobe robe)
        {
            var enemy = robe.enemy;
            if (!enemy || enemy.IsStunned() || enemy.CurrentState == EnemyState.Despawn || !allyParent || !allyParent.Spawned)
            {
                return;
            }
            if (!Leader || Leader.isDisabled)
            {
                Leader = Nav.NearestPlayer(Director.EnemyPosition(enemy));
            }
            var state = robe.currentState;
            // ダメージ: 攻撃の動きの途中で、狙った敵がまだ近くにいれば与える
            if (swingTarget && Time.time >= swingAt)
            {
                if (state == EnemyRobe.State.Attack && Alive(swingTarget)
                    && Vector3.Distance(Director.EnemyPosition(enemy), Director.EnemyPosition(swingTarget)) <= 3.5f)
                {
                    var direction = (Director.EnemyPosition(swingTarget) - Director.EnemyPosition(enemy)).normalized;
                    swingTarget.Health.Hurt(Damage.Value, direction);
                    Plugin.Verbose($"味方のローブ: {Name(swingTarget)} に {Damage.Value} ダメージ");
                }
                swingTarget = null;
            }
            switch (state)
            {
                case EnemyRobe.State.Spawn:
                case EnemyRobe.State.Stun:
                case EnemyRobe.State.Attack:
                case EnemyRobe.State.StuckAttack:
                case EnemyRobe.State.Despawn:
                    return;
                case EnemyRobe.State.SeekPlayer:
                    break;
                default:
                    // 突進・のぞき込み・歩き回りなどには入らない。目的地を決められる SeekPlayer で動かす
                    robe.chaseTimer = 0f;
                    robe.UpdateState(EnemyRobe.State.SeekPlayer);
                    return;
            }
            if (robe.stateImpulse)
            {
                return;
            }
            UpdateGuide(Director.EnemyPosition(enemy));
            attackTarget = ChooseTarget(enemy);
            if (attackTarget && Time.time >= nextAttack)
            {
                Vector3 target = Director.EnemyPosition(attackTarget);
                // 攻撃の当たり判定は MOD なしの人の PC でも動き、そばのプレイヤーにも当たるので、近くに人がいるときは振らない
                if (Nav.Flat(Director.EnemyPosition(enemy), target) <= 2f && !PlayerNear(Director.EnemyPosition(enemy), target))
                {
                    nextAttack = Time.time + 2.5f;
                    robe.stuckAttackTarget = target;
                    robe.chaseTimer = 0f;
                    robe.targetPlayer = null;
                    robe.UpdateState(EnemyRobe.State.StuckAttack); // 敵のほうを向いてから攻撃の動き(Attack)に入る
                    swingTarget = attackTarget;
                    swingAt = Time.time + 1.5f + 0.4f;
                }
            }
        }

        /// <summary>攻撃の当たり判定が届きそうな所(ローブと狙う敵のまわり)にプレイヤーがいるか。</summary>
        private static bool PlayerNear(Vector3 self, Vector3 target)
        {
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled)
                {
                    continue;
                }
                Vector3 p = player.transform.position;
                if (Vector3.Distance(p, self) <= 3.5f || Vector3.Distance(p, target) <= 3f)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Alive(Enemy enemy) =>
            enemy && enemy.EnemyParent && enemy.EnemyParent.Spawned && enemy.CurrentState != EnemyState.Despawn
            && enemy.HasHealth && enemy.Health && enemy.Health.healthCurrent > 0 && Director.EnemyPosition(enemy).y < 1000f;

        private static string Name(Enemy enemy) => enemy.EnemyParent ? enemy.EnemyParent.enemyName : enemy.name;

        // ================================================================
        // 案内
        // ================================================================

        /// <summary>
        /// 案内を始める。toTruck なら トラックへ、そうでなければ target のところへ。
        /// forPlayer(案内される人)が離れたら待ち、着いたらその人についていく。
        /// </summary>
        internal static bool StartGuide(bool toTruck, PlayerAvatar target, PlayerAvatar forPlayer, bool japanese)
        {
            if (toTruck ? !TruckPoint(out _) : (!target || target.isDisabled))
            {
                return false;
            }
            GuideToTruck = toTruck;
            GuideTarget = toTruck ? null : target;
            guideFor = forPlayer && !forPlayer.isDisabled ? forPlayer : Leader;
            guideUntil = Time.time + GuideTimeout;
            guideJapanese = japanese;
            guideGathering = guideFor && Ally
                && Vector3.Distance(Director.EnemyPosition(Ally.enemy), guideFor.transform.position) > GuideWaitDistance;
            CurrentMode = Mode.Guide;
            OrderedTarget = null;
            return true;
        }

        /// <summary>自分からの攻撃をやめる(振りかぶっている攻撃のダメージも与えない)。</summary>
        internal static void CancelAttack()
        {
            swingTarget = null;
            attackTarget = null;
        }

        private static void EndGuide(bool arrived)
        {
            if (guideFor && !guideFor.isDisabled)
            {
                Leader = guideFor;
            }
            CurrentMode = Mode.Follow;
            GuideTarget = null;
            guideFor = null;
            if (arrived)
            {
                AllyChat.SayFixed(guideJapanese ? "着いた。" : "We're here.");
            }
            Plugin.Verbose(arrived ? "味方のローブ: 案内の目的地に着きました" : "味方のローブ: 案内をやめました(目的地がない・時間切れ)");
        }

        /// <summary>トラックの位置(ナビメッシュ上)。</summary>
        private static bool TruckPoint(out Vector3 point)
        {
            point = default;
            Transform truck = LevelGenerator.Instance && LevelGenerator.Instance.LevelPathTruck
                ? LevelGenerator.Instance.LevelPathTruck.transform
                : TruckSafetySpawnPoint.instance ? TruckSafetySpawnPoint.instance.transform : null;
            if (!truck)
            {
                return false;
            }
            point = Nav.Sample(truck.position, out var sampled) ? sampled : truck.position;
            return true;
        }

        private static bool GuideGoal(out Vector3 goal)
        {
            if (GuideToTruck)
            {
                return TruckPoint(out goal);
            }
            goal = default;
            if (!GuideTarget || GuideTarget.isDisabled)
            {
                return false;
            }
            goal = GuideTarget.transform.position;
            return true;
        }

        /// <summary>案内中: 着いた・目的地がなくなった・時間切れなら、案内を終えてついていく動きに戻る。</summary>
        private static void UpdateGuide(Vector3 position)
        {
            if (CurrentMode != Mode.Guide)
            {
                return;
            }
            if (!GuideGoal(out var goal) || Time.time > guideUntil)
            {
                EndGuide(false);
                return;
            }
            bool hasFollower = guideFor && !guideFor.isDisabled;
            float followerDistance = hasFollower ? Vector3.Distance(position, guideFor.transform.position) : 0f;
            // 迎えに行く: 遠くにいる(見失った)ら戻り、そばまで来たら案内を再開する
            if (hasFollower && followerDistance > GuideLostDistance)
            {
                guideGathering = true;
            }
            else if (!hasFollower || followerDistance <= GuideGatherDistance)
            {
                guideGathering = false;
            }
            // 着いた: ローブが目的地にいて、案内される人も目的地の近くまで来た
            if (!guideGathering && Vector3.Distance(position, goal) <= GuideArriveDistance
                && (!hasFollower || Vector3.Distance(guideFor.transform.position, goal) <= GuidePlayerArriveDistance))
            {
                EndGuide(true);
            }
        }

        /// <summary>案内中に向かう場所(迎えに行く・目的地へ先に歩く。案内される人が少し離れたら null = 止まって待つ)。</summary>
        private static Vector3? GuideDestination(Vector3 position)
        {
            bool hasFollower = guideFor && !guideFor.isDisabled;
            if (hasFollower && guideGathering)
            {
                return guideFor.transform.position;
            }
            if (hasFollower && Vector3.Distance(position, guideFor.transform.position) > GuideWaitDistance)
            {
                return null;
            }
            return GuideGoal(out var goal) ? goal : (Vector3?)null;
        }

        /// <summary>攻撃しに行く敵(指示で決めた敵、なければ近くの敵)。待機中・案内中・攻撃しないと言われたときは自分から攻撃しに行かない。</summary>
        private static Enemy ChooseTarget(Enemy self)
        {
            if (CurrentMode == Mode.Wait || CurrentMode == Mode.Guide)
            {
                return null;
            }
            Vector3 position = Director.EnemyPosition(self);
            Vector3 anchor = Leader ? Leader.transform.position : position;
            if (CurrentMode == Mode.Attack && Alive(OrderedTarget))
            {
                return OrderedTarget;
            }
            if (CurrentMode == Mode.Attack)
            {
                CurrentMode = Mode.Follow; // 狙った敵がいなくなった
                OrderedTarget = null;
            }
            var director = EnemyDirector.instance;
            if (Peaceful || !director || AttackRange.Value <= 0f)
            {
                return null;
            }
            Enemy best = null;
            float bestDistance = AttackRange.Value;
            foreach (var parent in director.enemiesSpawned)
            {
                var enemy = parent ? parent.Enemy : null;
                if (!enemy || enemy == self || !Alive(enemy))
                {
                    continue;
                }
                Vector3 point = Director.EnemyPosition(enemy);
                float distance = Vector3.Distance(position, point);
                if (distance < bestDistance && Vector3.Distance(anchor, point) <= LeashRange.Value)
                {
                    bestDistance = distance;
                    best = enemy;
                }
            }
            return best;
        }

        /// <summary>近くの敵(会話で AI に伝える)。</summary>
        internal static IEnumerable<Enemy> NearbyEnemies(float range)
        {
            var director = EnemyDirector.instance;
            if (!Ally || !director)
            {
                yield break;
            }
            Vector3 position = Director.EnemyPosition(Ally.enemy);
            foreach (var parent in director.enemiesSpawned)
            {
                var enemy = parent ? parent.Enemy : null;
                if (enemy && enemy != Ally.enemy && Alive(enemy) && Vector3.Distance(position, Director.EnemyPosition(enemy)) <= range)
                {
                    yield return enemy;
                }
            }
        }

        internal static string EnemyName(Enemy enemy) => Name(enemy);

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.StateSeekPlayer))]
        private static class SeekPatch
        {
            private static void Postfix(EnemyRobe __instance)
            {
                if (!Ally || __instance != Ally || !IsHost || __instance.currentState != EnemyRobe.State.SeekPlayer)
                {
                    return;
                }
                var enemy = __instance.enemy;
                var agent = enemy.NavMeshAgent;
                Vector3 position = Director.EnemyPosition(enemy);
                __instance.stateTimer = Mathf.Max(__instance.stateTimer, 5f);
                Vector3? destination = null;
                float? speed = null;
                if (attackTarget)
                {
                    destination = Director.EnemyPosition(attackTarget);
                }
                else if (CurrentMode == Mode.Guide)
                {
                    // 案内: 目的地へ先に歩く。案内される人が離れたら止まって待ち、見失ったら迎えに戻る
                    destination = GuideDestination(position);
                    if (destination.HasValue)
                    {
                        speed = GuideSpeed;
                    }
                }
                else if (CurrentMode != Mode.Wait && Leader && !Leader.isDisabled)
                {
                    Vector3 leader = Leader.transform.position;
                    if (Vector3.Distance(position, leader) > 3f)
                    {
                        var back = -Vector3.ProjectOnPlane(Leader.transform.forward, Vector3.up).normalized;
                        destination = Nav.Sample(leader + back * 2f, out var point) ? point : leader;
                    }
                }
                if (destination.HasValue)
                {
                    agent.SetDestination(destination.Value);
                    // 遠いときは走る(探して歩くときの速さ 3 では、プレイヤーに置いていかれる)。案内中は人が追いつける速さ
                    float distance = Vector3.Distance(position, destination.Value);
                    if (speed == null && distance > 6f)
                    {
                        speed = 6f;
                    }
                    if (speed != null)
                    {
                        agent.OverrideAgent(speed.Value, 12f, 0.2f);
                        enemy.Rigidbody.OverrideFollowPosition(0.2f, speed.Value, -1f);
                    }
                }
                else
                {
                    agent.ResetPath();
                    if (enemy.Rigidbody)
                    {
                        // 止まっている時間が長いと、ゲームが「詰まった」とみなして近くの物を攻撃するので数えない
                        enemy.Rigidbody.notMovingTimer = 0f;
                    }
                }
            }
        }

        // ================================================================
        // 目印のオーブ・片付け(Runner から毎フレーム)
        // ================================================================

        internal static void Tick()
        {
            if (level != LevelGenerator.Instance)
            {
                level = LevelGenerator.Instance;
                Reset();
            }
            if (summonQueued && IsHost && !AllyLateJoin.Holding)
            {
                // 途中参加の人の読み込み中に割られたオーブの分を、今呼び出す
                summonQueued = false;
                var summoner = queuedSummoner && !queuedSummoner.isDisabled ? queuedSummoner : Nav.NearestPlayer(queuedSpot);
                queuedSummoner = null;
                if (summoner)
                {
                    Summon(queuedSpot, summoner);
                }
            }
            if (!IsHost || !Ally)
            {
                return;
            }
            if (!allyParent || (!allyParent.Spawned && !pendingSpawn))
            {
                return;
            }
            UpdateMarker();
        }

        private static void UpdateMarker()
        {
            if (!MarkerOrb.Value || !allyParent.Spawned)
            {
                return;
            }
            Vector3 head = Director.EnemyPosition(Ally.enemy) + Vector3.up * 1.6f;
            if (!marker)
            {
                if (markerName != null)
                {
                    return; // 一度出して壊れた。出し直さない
                }
                SpawnMarker(head);
                return;
            }
            // ばねのように頭の上へ引き寄せる(物理で動かすので、MOD なしの人にも同じように見える)
            var rb = marker.rb;
            marker.OverrideZeroGravity(0.2f);
            Vector3 offset = head - marker.centerPoint;
            if (offset.magnitude > 6f)
            {
                marker.Teleport(head, Quaternion.identity);
            }
            else if (rb && !rb.isKinematic)
            {
                rb.linearVelocity = offset * 8f;
                rb.angularVelocity = Vector3.up * 2f;
            }
            else if (rb && marker.spawned)
            {
                // 物理が止まっている(kinematic)間は速度を設定できない(毎フレーム警告が出る)ので、位置で動かす
                rb.MovePosition(Vector3.Lerp(marker.centerPoint, head, Mathf.Clamp01(Time.deltaTime * 8f)) + (rb.position - marker.centerPoint));
            }
        }

        private static void SpawnMarker(Vector3 position)
        {
            var item = HealOrb();
            if (!item || item.prefab == null)
            {
                markerName = "";
                return;
            }
            var stats = StatsManager.instance;
            namesBeforeMarker = stats ? new HashSet<string>(stats.item.Keys) : new HashSet<string>();
            GameObject obj = SemiFunc.IsMultiplayer()
                ? PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, position, Quaternion.identity, 0, null)
                : (item.prefab.Prefab ? Object.Instantiate(item.prefab.Prefab, position, Quaternion.identity) : null);
            AllyLateJoin.Uncache(obj); // 途中参加の人の読み込みに混ざらないようにする(入ってきたら消して、あとで作り直す)
            marker = obj ? obj.GetComponent<PhysGrabObject>() : null;
            markerAttributes = obj ? obj.GetComponent<ItemAttributes>() : null;
            markerName = markerAttributes ? markerAttributes.instanceName ?? "" : "";
            if (marker && marker.impactDetector)
            {
                marker.impactDetector.destroyDisable = true; // 敵のローブとの見分けに使うので、壊れないようにする
            }
        }

        private static Item HealOrb()
        {
            const string name = "Item Orb Heal";
            if (StatsManager.instance && StatsManager.instance.itemDictionary.TryGetValue(name, out var item) && item)
            {
                return item;
            }
            return Resources.LoadAll<Item>("Items/Removed Items").FirstOrDefault(i => i && i.name == name);
        }

        /// <summary>レベルを出るとき: 目印のオーブを、次のレベルに持ち越されないように消す。</summary>
        [HarmonyPatch(typeof(RunManager), nameof(RunManager.ChangeLevel))]
        private static class ChangeLevelPatch
        {
            private static void Prefix()
            {
                if (IsHost)
                {
                    CleanupMarker();
                }
            }
        }

        private static void CleanupMarker()
        {
            if (marker)
            {
                marker.DestroyPhysGrabObject();
            }
            marker = null;
            // 名前は ItemAttributes の準備のあとで決まるので、最新の名前を読む(壊れていても読めるように Unity の null 判定は使わない)。
            // 目印のオーブ自身の名前だけを消す(出す前からあった名前は、購入したオーブの名前なので消さない)
            string name = (object)markerAttributes != null ? markerAttributes.instanceName : null;
            var stats = StatsManager.instance;
            if (stats && !string.IsNullOrEmpty(name) && (namesBeforeMarker == null || !namesBeforeMarker.Contains(name)))
            {
                stats.item.Remove(name);
                if (PunManager.instance)
                {
                    PunManager.instance.UpdateStat("itemStatBattery", name, 100);
                }
                stats.itemStatBattery.Remove(name);
            }
            namesBeforeMarker = null;
            markerAttributes = null;
        }

        private static void Reset()
        {
            Ally = null;
            allyParent = null;
            Leader = null;
            CurrentMode = Mode.Follow;
            OrderedTarget = null;
            Peaceful = false;
            GuideToTruck = false;
            GuideTarget = null;
            guideFor = null;
            attackTarget = null;
            swingTarget = null;
            pendingSpawn = false;
            respawnTimerSet = false;
            used = false;
            marker = null;
            markerName = null;
            markerAttributes = null;
            namesBeforeMarker = null;
            heldForJoin = false;
            summonQueued = false;
            queuedSummoner = null;
            allyPath = null;
            allyViewIds = null;
            allyLevelPrefix = 0;
            allyCreatedAt = 0f;
            robeSearched = robeRef != null;
            AllyChat.Reset();
        }
    }
}
