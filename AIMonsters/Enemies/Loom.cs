using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace AIMonsters
{
    /// <summary>
    /// ルーム(クラス名 EnemyShadow)を AI に動かさせる。ホストだけで動く。
    /// ゲーム本来の動き: 狙う相手を 1 人決め(ChooseTarget はランダム)、その人へ歩き(Follow)、
    /// 両手が相手の位置に届いたら叩きつける(Slap)。叩きつけの衝撃波はプレイヤーと物の両方に当たる。
    /// 物を叩く仕組み(StuckAttack: 詰まったときに近くの物を叩く)があるので、貴重品への攻撃はこれを使う。
    /// - 相手の指示: 狙う相手をその人にする(叩きつけはゲームが自動で行う)
    /// - flank: 相手の背後へ回り込んでから近づく
    /// - attack_valuable: 貴重品のそばまで歩き、StuckAttack でその貴重品を叩く
    /// - retreat: プレイヤーから離れる / wait: その場で止まる / roam: ゲームに任せる
    /// ルームには「見えた回数」の仕組みがない(プレハブ次第)ので、見えているかは目からの見通しと距離でも判定する。
    /// </summary>
    internal sealed class LoomDriver : IEnemyDriver
    {
        private const string Section = "2. Loom(ルーム)";

        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<float> sightRange;
        private static ConfigEntry<float> slapReach;

        internal static readonly LoomDriver Instance = new LoomDriver();

        internal static void Bind(ConfigFile cfg)
        {
            enabled = cfg.Bind(Section, "有効", true, "ルームを AI に動かさせる");
            sightRange = cfg.Bind(Section, "見えるとみなす距離(m)", 12f,
                new ConfigDescription("ルームの目からこの距離以内で、間に壁がないプレイヤーを「見えている」とみなす(AI に任せ始める・やめる判定)",
                    new AcceptableValueRange<float>(2f, 40f)));
            slapReach = cfg.Bind(Section, "貴重品を叩き始める距離(m)", 2.2f,
                new ConfigDescription("貴重品を攻撃するとき、この距離まで近づいたら叩く", new AcceptableValueRange<float>(1f, 5f)));
            Director.Drivers.Add(Instance);
        }

        public string TypeName => "Loom";

        public string Rules =>
            "Tall, slow stalker. It walks toward one target player and slams both hands down when its hands reach them (about 1-3 m). " +
            "The slam sends a shockwave that knocks back and hurts everyone close, and smashes valuables near the impact. No ranged attack, cannot run. " +
            "Best: attack_valuable on an expensive valuable a player is carrying or standing next to (the shockwave hits both). " +
            "Pick isolated or low-hp players. flank walks around to the target's back first. Chasing players far away wastes time.";

        public bool Enabled => enabled.Value;

        public bool Matches(Enemy enemy) => Shadow(enemy);

        private static readonly ConditionalWeakTable<Enemy, EnemyShadow> Shadows = new ConditionalWeakTable<Enemy, EnemyShadow>();

        private static EnemyShadow Shadow(Enemy enemy)
        {
            if (!enemy)
            {
                return null;
            }
            if (Shadows.TryGetValue(enemy, out var cached))
            {
                return cached;
            }
            var shadow = enemy.GetComponent<EnemyShadow>();
            if (!shadow)
            {
                shadow = enemy.GetComponentInParent<EnemyShadow>();
            }
            if (!shadow)
            {
                shadow = enemy.GetComponentInChildren<EnemyShadow>();
            }
            Shadows.Add(enemy, shadow);
            return shadow;
        }

        public bool Sees(Enemy enemy, PlayerAvatar player)
        {
            if (Director.VisionSees(enemy, player))
            {
                return true;
            }
            var shadow = Shadow(enemy);
            if (!shadow || !player.PlayerVisionTarget)
            {
                return false;
            }
            Vector3 eye = shadow.visionTransform ? shadow.visionTransform.position : Director.EnemyPosition(enemy) + Vector3.up;
            Vector3 target = player.PlayerVisionTarget.VisionTransform.position;
            Vector3 to = target - eye;
            if (to.magnitude > sightRange.Value)
            {
                return false;
            }
            // ゲーム本来の VisionBlocked と同じ層(壁と、扉などの蝶番の物)
            return !Physics.Raycast(eye, to, to.magnitude, LayerMask.GetMask("Default", "PhysGrabObjectHinge"), QueryTriggerInteraction.Ignore);
        }

        public void Describe(Enemy enemy, JObject info)
        {
            var shadow = Shadow(enemy);
            if (!shadow)
            {
                return;
            }
            info["state"] = shadow.currentState.ToString();
        }

        private sealed class Memo
        {
            /// <summary>この MOD が始めた貴重品への叩きつけの最中か。</summary>
            internal bool ValuableSlap;
            internal float NextValuableSlap;
            internal float NextPoint;
            internal Vector3 Point;
            internal bool HasPoint;
        }

        private static readonly ConditionalWeakTable<EnemyShadow, Memo> Memos = new ConditionalWeakTable<EnemyShadow, Memo>();

        public void Apply(Enemy enemy, Order order)
        {
            var shadow = Shadow(enemy);
            if (shadow)
            {
                Memos.GetOrCreateValue(shadow).NextPoint = 0f; // 目的地を選び直す
            }
        }

        private static bool WantsPlayer(Order order) =>
            order != null && order.Player && !order.Player.isDisabled
            && (order.Action == "chase" || order.Action == "ambush" || order.Action == "attack_player" || order.Action == "flank");

        /// <summary>指示があるときは、ゲームに任せず Follow で動かす(Follow 以外の状態から戻す)。</summary>
        private static bool Drives(Order order) =>
            order != null && (WantsPlayer(order) || (order.Action == "attack_valuable" && order.Valuable) || order.Action == "retreat" || order.Action == "wait");

        public void Tick(Enemy enemy, Order order)
        {
            var shadow = Shadow(enemy);
            if (!shadow || enemy.IsStunned() || enemy.CurrentState == EnemyState.Despawn)
            {
                return;
            }
            var state = shadow.currentState;
            if (WantsPlayer(order) && shadow.playerTarget != order.Player
                && (state == EnemyShadow.State.ChooseTarget || state == EnemyShadow.State.Follow || state == EnemyShadow.State.Leave))
            {
                shadow.UpdatePlayerTarget(order.Player);
            }
            // 貴重品・離れる・待つの指示でも、Follow には狙う相手が要る(いないとゲームが ChooseTarget に戻す)
            if (Drives(order) && !WantsPlayer(order) && (!shadow.playerTarget || shadow.playerTarget.isDisabled)
                && state != EnemyShadow.State.Spawn)
            {
                var nearest = NearestPlayer(Director.EnemyPosition(enemy));
                if (nearest)
                {
                    shadow.UpdatePlayerTarget(nearest);
                }
            }
            if (Drives(order) && (state == EnemyShadow.State.ChooseTarget || state == EnemyShadow.State.Leave)
                && shadow.playerTarget && !shadow.playerTarget.isDisabled && !shadow.stateImpulse)
            {
                shadow.UpdateState(EnemyShadow.State.Follow);
            }
        }

        public void Release(Enemy enemy)
        {
            if (!enemy)
            {
                return;
            }
            var shadow = Shadow(enemy);
            if (shadow && Memos.TryGetValue(shadow, out var memo) && memo.ValuableSlap)
            {
                memo.ValuableSlap = false;
                shadow.isStuckAttacking = false;
                shadow.stuckAttackAttempts = 0;
            }
        }

        // ---------------- 歩く先を指示に合わせる(ゲームが毎フレーム目的地を決めた後で上書きする) ----------------

        [HarmonyPatch(typeof(EnemyShadow), nameof(EnemyShadow.StateFollow))]
        private static class FollowPatch
        {
            private static void Postfix(EnemyShadow __instance)
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer() || __instance.currentState != EnemyShadow.State.Follow)
                {
                    return;
                }
                var enemy = __instance.enemy;
                var memo = Memos.GetOrCreateValue(__instance);
                if (memo.ValuableSlap)
                {
                    // 貴重品への叩きつけが終わって Follow に戻った。詰まったときの攻撃の続きにならないように戻す
                    memo.ValuableSlap = false;
                    __instance.isStuckAttacking = false;
                    __instance.stuckAttackAttempts = 0;
                    __instance.stuckAttackTarget = Vector3.zero;
                }
                if (!Director.TryGetOrder(enemy, out var order) || order == null)
                {
                    return;
                }
                var agent = enemy.NavMeshAgent;
                Vector3 position = __instance.transform.position;
                switch (order.Action)
                {
                    case "attack_valuable":
                        if (order.Valuable && order.Valuable.physGrabObject)
                        {
                            Vector3 point = Director.ValuablePosition(order.Valuable);
                            Vector3 flat = point - position;
                            flat.y = 0f;
                            if (flat.magnitude <= slapReach.Value && Mathf.Abs(point.y - position.y) < 2.5f)
                            {
                                if (Time.time < memo.NextValuableSlap)
                                {
                                    // 次に叩けるまで、貴重品の前で待つ(ゲームの目的地 = 狙う相手のほうへ歩いていかない)
                                    agent.ResetPath();
                                    if (enemy.Rigidbody)
                                    {
                                        enemy.Rigidbody.notMovingTimer = 0f;
                                    }
                                }
                                else
                                {
                                    memo.NextValuableSlap = Time.time + 3f;
                                    memo.ValuableSlap = true;
                                    __instance.stuckAttackTarget = point;
                                    __instance.stuckAttackAttempts = 0;
                                    __instance.UpdateState(EnemyShadow.State.StuckAttack);
                                    Plugin.Verbose($"Loom: 貴重品 {order.Valuable.name}(${order.Valuable.dollarValueCurrent:0})を叩きます");
                                }
                            }
                            else
                            {
                                agent.SetDestination(point);
                            }
                        }
                        break;
                    case "flank":
                        if (order.Player && !order.Player.isDisabled)
                        {
                            if (Time.time >= memo.NextPoint)
                            {
                                memo.NextPoint = Time.time + 0.5f;
                                memo.HasPoint = BehindPoint(order.Player, 4f, out memo.Point);
                            }
                            // 回り込み先に着く(または相手のすぐ近くにいる)までは回り込み先へ。着いたらゲームどおり相手へ
                            if (memo.HasPoint && Vector3.Distance(position, memo.Point) > 1.5f && __instance.distanceFromPlayer > 2.5f)
                            {
                                agent.SetDestination(memo.Point);
                            }
                        }
                        break;
                    case "retreat":
                        if (Time.time >= memo.NextPoint)
                        {
                            memo.NextPoint = Time.time + 1f;
                            memo.HasPoint = AwayPoint(position, 8f, out memo.Point);
                        }
                        if (memo.HasPoint)
                        {
                            agent.SetDestination(memo.Point);
                        }
                        break;
                    case "wait":
                        agent.ResetPath();
                        // 止まっている時間が長いと、ゲームが「詰まった」とみなして近くの物を叩いたり去ったりするので数えない
                        if (enemy.Rigidbody)
                        {
                            enemy.Rigidbody.notMovingTimer = 0f;
                        }
                        break;
                }
            }
        }

        /// <summary>相手の背後 distance(m)くらいで、歩ける地点。</summary>
        private static bool BehindPoint(PlayerAvatar player, float distance, out Vector3 point)
        {
            var vision = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform : player.transform;
            Vector3 back = -Vector3.ProjectOnPlane(vision.forward, Vector3.up).normalized;
            if (back.sqrMagnitude < 0.01f)
            {
                back = -player.transform.forward;
            }
            return Sample(player.transform.position + back * distance, out point);
        }

        private static PlayerAvatar NearestPlayer(Vector3 from)
        {
            PlayerAvatar nearest = null;
            float best = float.MaxValue;
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled)
                {
                    continue;
                }
                float d = Vector3.Distance(from, player.transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = player;
                }
            }
            return nearest;
        }

        /// <summary>一番近いプレイヤーから離れる向きに distance(m)くらいで、歩ける地点。</summary>
        private static bool AwayPoint(Vector3 from, float distance, out Vector3 point)
        {
            var nearest = NearestPlayer(from);
            Vector3 away = nearest ? Vector3.ProjectOnPlane(from - nearest.transform.position, Vector3.up).normalized : Vector3.zero;
            if (away.sqrMagnitude < 0.01f)
            {
                away = Random.insideUnitSphere;
                away.y = 0f;
                away.Normalize();
            }
            return Sample(from + away * distance, out point);
        }

        private static bool Sample(Vector3 desired, out Vector3 point)
        {
            if (NavMesh.SamplePosition(desired, out var hit, 3f, NavMesh.AllAreas))
            {
                point = hit.position;
                return true;
            }
            point = desired;
            return false;
        }
    }
}
