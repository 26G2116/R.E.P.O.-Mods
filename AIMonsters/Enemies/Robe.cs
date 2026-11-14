using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using UnityEngine;

namespace AIMonsters
{
    /// <summary>
    /// ローブ(EnemyRobe)を AI に動かさせる。ホストだけで動く。
    /// ゲーム本来の動き: 見つけたら突進(TargetPlayer)し、2m まで近づいたら攻撃(Attack)。見失ったら探して歩く(SeekPlayer)。
    /// 詰まったときは近くの物を攻撃する(StuckAttack → Attack)ので、貴重品への攻撃はこれを使う。
    /// - chase / attack_player: その人へ突進する(ゲームの突進と同じ。近づいたら自動で攻撃)
    /// - ambush: 相手の背後へ歩いて回り込み、近づいたら(または気づかれたら)突進する
    /// - flank: 背後へ回り込む(すぐそばまで来たら自動で攻撃)
    /// - attack_valuable: 貴重品のそばまで歩いて攻撃する
    /// - retreat: 離れる / wait: その場で止まる / roam: ゲームに任せる
    /// 歩かせる指示は、ゲームの SeekPlayer(探して歩く。ゆっくり歩く)の目的地を上書きして行う。
    /// 指示がある間は、見つけた瞬間の突進(OnVision)を止める(AI が決める)。
    /// </summary>
    internal sealed class RobeDriver : IEnemyDriver
    {
        private const string Section = "3. Robe(ローブ)";

        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<float> strikeRange;
        private static ConfigEntry<float> noticedStrikeRange;
        private static ConfigEntry<float> attackReach;

        internal static readonly RobeDriver Instance = new RobeDriver();

        internal static void Bind(ConfigFile cfg)
        {
            enabled = cfg.Bind(Section, "有効", true, "ローブを AI に動かさせる");
            strikeRange = cfg.Bind(Section, "待ち伏せから突進する距離(m)", 6f,
                new ConfigDescription("ambush の指示で、相手にこの距離まで近づいたら突進する", new AcceptableValueRange<float>(2f, 20f)));
            noticedStrikeRange = cfg.Bind(Section, "気づかれたら突進する距離(m)", 10f,
                new ConfigDescription("ambush の指示で、相手の画面に映った(気づかれた)ときにこの距離以内なら、すぐ突進する", new AcceptableValueRange<float>(0f, 30f)));
            attackReach = cfg.Bind(Section, "貴重品を攻撃し始める距離(m)", 1.8f,
                new ConfigDescription("貴重品を攻撃するとき、この距離まで近づいたら攻撃する", new AcceptableValueRange<float>(1f, 4f)));
            Director.Drivers.Add(Instance);
        }

        public string TypeName => "Robe";

        public string Rules =>
            "Silent stalker. Moves without sound and is deadly up close: it charges very fast and swings when within 2 m. " +
            "It is fastest while the target is not looking at it, and slows down when the target looks at it. " +
            "Best: ambush a player who does not see it (they_see_you false), especially one carrying a valuable. " +
            "If they already see it and are close, chase. attack_valuable makes it smash a valuable within reach. " +
            "Weak to being watched and to weapons; retreat when low hp.";

        public bool Enabled => enabled.Value;

        private static readonly ConditionalWeakTable<Enemy, EnemyRobe> Robes = new ConditionalWeakTable<Enemy, EnemyRobe>();

        internal static EnemyRobe Robe(Enemy enemy)
        {
            if (!enemy)
            {
                return null;
            }
            if (Robes.TryGetValue(enemy, out var cached))
            {
                return cached;
            }
            var robe = enemy.GetComponent<EnemyRobe>();
            if (!robe)
            {
                robe = enemy.GetComponentInParent<EnemyRobe>();
            }
            if (!robe)
            {
                robe = enemy.GetComponentInChildren<EnemyRobe>();
            }
            Robes.Add(enemy, robe);
            return robe;
        }

        public bool Matches(Enemy enemy) => Robe(enemy);

        public bool Sees(Enemy enemy, PlayerAvatar player) => Director.VisionSees(enemy, player);

        public void Describe(Enemy enemy, JObject info)
        {
            var robe = Robe(enemy);
            if (robe)
            {
                info["state"] = robe.currentState.ToString();
            }
        }

        private sealed class Memo
        {
            internal float NextPoint;
            internal Vector3 Point;
            internal bool HasPoint;
            internal float NextValuableAttack;
            /// <summary>回り込み先に着いた(この指示の間は、相手へ寄っていく)。</summary>
            internal bool Arrived;
        }

        private static readonly ConditionalWeakTable<EnemyRobe, Memo> Memos = new ConditionalWeakTable<EnemyRobe, Memo>();

        public void Apply(Enemy enemy, Order order)
        {
            var robe = Robe(enemy);
            if (robe)
            {
                var memo = Memos.GetOrCreateValue(robe);
                memo.NextPoint = 0f;
                memo.Arrived = false;
            }
        }

        public void Release(Enemy enemy)
        {
        }

        private static bool Charges(Order order) =>
            order != null && order.Player && !order.Player.isDisabled && (order.Action == "chase" || order.Action == "attack_player");

        /// <summary>SeekPlayer の目的地を上書きして歩かせる指示か。</summary>
        private static bool Walks(Order order)
        {
            if (order == null)
            {
                return false;
            }
            switch (order.Action)
            {
                case "ambush":
                case "flank":
                    return order.Player && !order.Player.isDisabled;
                case "attack_valuable":
                    return order.Valuable && order.Valuable.physGrabObject;
                case "retreat":
                case "wait":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>見つけたら突進する状態か(ゲーム本来の OnVision と同じ)。</summary>
        private static bool Wandering(EnemyRobe.State state) =>
            state == EnemyRobe.State.Idle || state == EnemyRobe.State.Roam || state == EnemyRobe.State.Investigate
            || state == EnemyRobe.State.SeekPlayer || state == EnemyRobe.State.Leave;

        public void Tick(Enemy enemy, Order order)
        {
            var robe = Robe(enemy);
            if (!robe || enemy.IsStunned() || enemy.CurrentState == EnemyState.Despawn || order == null)
            {
                return;
            }
            var state = robe.currentState;
            if (Charges(order))
            {
                if (Wandering(state))
                {
                    Strike(robe, order.Player);
                }
                else if (state == EnemyRobe.State.TargetPlayer)
                {
                    if (robe.targetPlayer != order.Player)
                    {
                        SetTarget(robe, order.Player);
                    }
                    else if (Director.VisionSees(enemy, order.Player))
                    {
                        // 見えている間は突進を続ける(ゲーム本来も、見えるたびに続ける)
                        robe.stateTimer = Mathf.Max(robe.stateTimer, 1f);
                    }
                }
                return;
            }
            if (!Walks(order))
            {
                return;
            }
            // 歩かせる指示: SeekPlayer に入れて、目的地を上書きする(FollowPatch)
            if (state == EnemyRobe.State.Idle || state == EnemyRobe.State.Roam || state == EnemyRobe.State.Investigate
                || state == EnemyRobe.State.Leave || state == EnemyRobe.State.TargetPlayer)
            {
                robe.UpdateState(EnemyRobe.State.SeekPlayer);
                return;
            }
            if (state != EnemyRobe.State.SeekPlayer || robe.stateImpulse)
            {
                return;
            }
            Vector3 position = Director.EnemyPosition(enemy);
            switch (order.Action)
            {
                case "ambush":
                {
                    var prey = order.Player;
                    float distance = Vector3.Distance(position, prey.transform.position);
                    bool sight = Nav.LineOfSight(Eyes(robe), Nav.Eyes(prey));
                    bool noticed = Director.SeenBy(enemy, prey);
                    if (sight && (distance <= strikeRange.Value || (noticed && distance <= noticedStrikeRange.Value) || distance <= 2.5f))
                    {
                        Strike(robe, prey);
                    }
                    break;
                }
                case "flank":
                {
                    // 回り込み中でも、すぐそばまで来たら攻撃は自動
                    var prey = order.Player;
                    if (Vector3.Distance(position, prey.transform.position) <= 2.5f && Nav.LineOfSight(Eyes(robe), Nav.Eyes(prey)))
                    {
                        Strike(robe, prey);
                    }
                    break;
                }
                case "attack_valuable":
                {
                    var memo = Memos.GetOrCreateValue(robe);
                    Vector3 point = Director.ValuablePosition(order.Valuable);
                    if (Time.time >= memo.NextValuableAttack && Nav.Flat(position, point) <= attackReach.Value
                        && Mathf.Abs(point.y - position.y) < 2f)
                    {
                        memo.NextValuableAttack = Time.time + 3f;
                        robe.stuckAttackTarget = point;
                        // 攻撃中はプレイヤーのほうへ向き直らないように、狙う相手を外す(向きは StuckAttack で貴重品に合わせる)。
                        // ChaseTimer は突進後 3 秒間 targetPlayer を null チェックせずに読むので、先に止めておく
                        robe.chaseTimer = 0f;
                        robe.targetPlayer = null;
                        robe.UpdateState(EnemyRobe.State.StuckAttack);
                        Plugin.Verbose($"Robe: 貴重品 {order.Valuable.name}(${order.Valuable.dollarValueCurrent:0})を攻撃します");
                    }
                    break;
                }
            }
        }

        private static Vector3 Eyes(EnemyRobe robe) =>
            robe.enemy.Vision && robe.enemy.Vision.VisionTransform ? robe.enemy.Vision.VisionTransform.position : Director.EnemyPosition(robe.enemy) + Vector3.up;

        private static void SetTarget(EnemyRobe robe, PlayerAvatar player)
        {
            robe.targetPlayer = player;
            if (SemiFunc.IsMultiplayer())
            {
                robe.photonView.RPC("TargetPlayerRPC", RpcTarget.All, player.photonView.ViewID);
            }
        }

        private static void Strike(EnemyRobe robe, PlayerAvatar prey)
        {
            // ゲーム本来(OnVision)と同じ順番: 相手を決める → 状態を変える → 全員に相手を知らせる
            robe.targetPlayer = prey;
            robe.UpdateState(EnemyRobe.State.TargetPlayer);
            SetTarget(robe, prey);
            Plugin.Verbose($"Robe: {prey.playerName} へ突進します");
        }

        // ---------------- 見つけた瞬間の突進: 指示があるときは AI に任せる ----------------

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.OnVision))]
        private static class VisionPatch
        {
            private static bool Prefix(EnemyRobe __instance)
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer())
                {
                    return true;
                }
                // 実行できる指示があるときだけ AI に任せる。指示がない(まだ来ていない・期限切れ)とき、roam のとき、
                // 相手が倒れた・貴重品がなくなったなどで実行できないときは、ゲーム本来どおり
                return !Director.TryGetOrder(__instance.enemy, out var order) || !(Charges(order) || Walks(order));
            }
        }

        // ---------------- 歩く先を指示に合わせる(SeekPlayer が目的地を決めた後で上書きする) ----------------

        [HarmonyPatch(typeof(EnemyRobe), nameof(EnemyRobe.StateSeekPlayer))]
        private static class SeekPatch
        {
            private static void Postfix(EnemyRobe __instance)
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer() || __instance.currentState != EnemyRobe.State.SeekPlayer
                    || !Director.TryGetOrder(__instance.enemy, out var order) || !Walks(order))
                {
                    return;
                }
                var enemy = __instance.enemy;
                var agent = enemy.NavMeshAgent;
                var memo = Memos.GetOrCreateValue(__instance);
                Vector3 position = Director.EnemyPosition(enemy);
                // 指示が続く間は、探すのをやめて歩き回る(Roam)状態に戻らない
                __instance.stateTimer = Mathf.Max(__instance.stateTimer, 5f);
                bool hold = false;
                switch (order.Action)
                {
                    case "ambush":
                    case "flank":
                        if (!memo.Arrived && Time.time >= memo.NextPoint)
                        {
                            memo.NextPoint = Time.time + 0.5f;
                            memo.HasPoint = Nav.BehindPoint(order.Player, order.Action == "ambush" ? 5f : 4f, out memo.Point);
                        }
                        if (!memo.Arrived && (!memo.HasPoint || Vector3.Distance(position, memo.Point) <= 1f))
                        {
                            // 回り込み先に着いた(または回り込み先がない)。この指示の間は相手へ寄っていく(回り込み先へ戻らない)
                            memo.Arrived = true;
                        }
                        agent.SetDestination(memo.Arrived ? order.Player.transform.position : memo.Point);
                        break;
                    case "attack_valuable":
                    {
                        Vector3 point = Director.ValuablePosition(order.Valuable);
                        if (Nav.Flat(position, point) > attackReach.Value * 0.8f)
                        {
                            agent.SetDestination(point);
                        }
                        else
                        {
                            // 次に攻撃できるまで、貴重品の前で待つ
                            agent.ResetPath();
                            hold = true;
                        }
                        break;
                    }
                    case "retreat":
                        if (Time.time >= memo.NextPoint)
                        {
                            memo.NextPoint = Time.time + 1f;
                            memo.HasPoint = Nav.AwayPoint(position, 8f, out memo.Point);
                        }
                        if (memo.HasPoint)
                        {
                            agent.SetDestination(memo.Point);
                        }
                        break;
                    case "wait":
                        agent.ResetPath();
                        hold = true;
                        break;
                }
                if (hold && enemy.Rigidbody)
                {
                    // 止まっている時間が長いと、ゲームが「詰まった」とみなして近くの物を攻撃するので数えない
                    enemy.Rigidbody.notMovingTimer = 0f;
                }
            }
        }
    }
}
