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

        internal enum Mode
        {
            Follow,
            Wait,
            Attack,
        }

        /// <summary>今のレベルの味方ローブ(いなければ null)。</summary>
        internal static EnemyRobe Ally { get; private set; }

        /// <summary>ついていく相手(召喚した人。倒れたら一番近い人)。</summary>
        internal static PlayerAvatar Leader;

        internal static Mode CurrentMode = Mode.Follow;

        /// <summary>指示で狙う敵(Attack のとき)。</summary>
        internal static Enemy OrderedTarget;

        private static EnemyParent allyParent;
        private static bool pendingSpawn;
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
            // ほかの MOD(MonsterTweaks)がこのローブの行動を変えないように、AI が動かしている目印をつける
            if (robe.enemy && !robe.enemy.GetComponent<AIMonstersControlled>())
            {
                robe.enemy.gameObject.AddComponent<AIMonstersControlled>();
            }
            Leader = summoner;
            CurrentMode = Mode.Follow;
            OrderedTarget = null;
            pendingSpawn = true;
            pendingSpot = spot;
            Plugin.Log.LogInfo($"味方のローブを呼び出しました({summoner.playerName} がオーブを割りました)");
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
                    __instance.DespawnedTimer = Mathf.Min(__instance.DespawnedTimer, 0.3f);
                }
                else
                {
                    __instance.SpawnedTimer = Mathf.Max(__instance.SpawnedTimer, 60f);
                }
            }
        }

        /// <summary>最初の出現場所を、オーブが割れた場所にする。</summary>
        [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.EnemySpawn))]
        private static class EnemySpawnPatch
        {
            private static bool Prefix(Enemy enemy, ref bool __result)
            {
                if (!pendingSpawn || !enemy || !allyParent || enemy.EnemyParent != allyParent)
                {
                    return true;
                }
                pendingSpawn = false;
                enemy.EnemyTeleported(pendingSpot);
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

        /// <summary>攻撃しに行く敵(指示で決めた敵、なければ近くの敵)。待機中は攻撃しに行かない。</summary>
        private static Enemy ChooseTarget(Enemy self)
        {
            if (CurrentMode == Mode.Wait)
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
            if (!director || AttackRange.Value <= 0f)
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
                if (attackTarget)
                {
                    destination = Director.EnemyPosition(attackTarget);
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
                    // 遠いときは走る(探して歩くときの速さ 3 では、プレイヤーに置いていかれる)
                    float distance = Vector3.Distance(position, destination.Value);
                    if (distance > 6f)
                    {
                        agent.OverrideAgent(6f, 12f, 0.2f);
                        enemy.Rigidbody.OverrideFollowPosition(0.2f, 6f, -1f);
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
            else if (rb)
            {
                rb.linearVelocity = offset * 8f;
                rb.angularVelocity = Vector3.up * 2f;
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
            attackTarget = null;
            swingTarget = null;
            pendingSpawn = false;
            used = false;
            marker = null;
            markerName = null;
            markerAttributes = null;
            namesBeforeMarker = null;
            robeSearched = robeRef != null;
            AllyChat.Reset();
        }
    }
}
