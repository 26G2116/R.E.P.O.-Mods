using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AIMonsters
{
    /// <summary>
    /// クリーンアップクルー(クラス名 EnemyBombThrower。頭の顔爆弾を投げる)を AI に動かさせる。ホストだけで動く。
    /// ゲーム本来の動き: 見つけたら気づき(Notice)、相手から 4〜6m の距離を保ち(GotoPlayer / BackAwayPlayer)、
    /// 頭が生えていて見通せれば顔爆弾を投げる(Attack)。顔爆弾は狙う相手(playerTarget)を追って飛ぶ。近いと殴る(Melee)。
    /// - 相手の指示: 狙う相手をその人にする(距離の取り方・投げるのはゲームが自動で行う)
    /// - attack_valuable: 顔爆弾は人しか追わないので、その貴重品を持っている人(いなければ一番近い人)を狙う
    /// - retreat: 狙う相手から後ずさりする(投げられるときは投げる) / wait: その場で止まる / roam: ゲームに任せる
    /// MonsterTweaks の分身方式(上空で待機する体と入れ替わる)とは、Director の引き継ぎで両立する(参照はしない)。
    /// </summary>
    internal sealed class CleanupCrewDriver : IEnemyDriver
    {
        private const string Section = "4. Cleanup Crew(クリーンアップクルー)";

        private static ConfigEntry<bool> enabled;

        internal static readonly CleanupCrewDriver Instance = new CleanupCrewDriver();

        internal static void Bind(ConfigFile cfg)
        {
            enabled = cfg.Bind(Section, "有効", true, "クリーンアップクルーを AI に動かさせる");
            Director.Drivers.Add(Instance);
        }

        public string TypeName => "CleanupCrew";

        public string Rules =>
            "Ranged bomber. It keeps 4-6 m away from its target and throws its face bomb, which flies after that target and explodes " +
            "(big damage to players and nearby valuables). Weak up close and while its head regrows. " +
            "Best: target a player standing near other players or carrying an expensive valuable. " +
            "attack_valuable throws at the player holding or standing nearest to that valuable. " +
            "retreat backs away from the target while still able to throw. Never chase into melee range.";

        public bool Enabled => enabled.Value;

        private static readonly ConditionalWeakTable<Enemy, EnemyBombThrower> Crews = new ConditionalWeakTable<Enemy, EnemyBombThrower>();

        private static EnemyBombThrower Crew(Enemy enemy)
        {
            if (!enemy)
            {
                return null;
            }
            if (Crews.TryGetValue(enemy, out var cached))
            {
                return cached;
            }
            var crew = enemy.GetComponent<EnemyBombThrower>();
            if (!crew)
            {
                crew = enemy.GetComponentInParent<EnemyBombThrower>();
            }
            if (!crew)
            {
                crew = enemy.GetComponentInChildren<EnemyBombThrower>();
            }
            Crews.Add(enemy, crew);
            return crew;
        }

        public bool Matches(Enemy enemy) => Crew(enemy);

        public bool Sees(Enemy enemy, PlayerAvatar player) => Director.VisionSees(enemy, player);

        public void Describe(Enemy enemy, JObject info)
        {
            var crew = Crew(enemy);
            if (!crew)
            {
                return;
            }
            info["state"] = crew.currentState.ToString();
            info["head_ready"] = crew.headGrown && crew.headAttackCooldown <= 0f;
        }

        public void Apply(Enemy enemy, Order order)
        {
        }

        public void Release(Enemy enemy)
        {
        }

        /// <summary>この指示で狙う相手。</summary>
        private static PlayerAvatar TargetOf(Order order)
        {
            if (order == null)
            {
                return null;
            }
            switch (order.Action)
            {
                case "chase":
                case "ambush":
                case "flank":
                case "attack_player":
                case "retreat":
                case "wait":
                    return order.Player && !order.Player.isDisabled ? order.Player : null;
                case "attack_valuable":
                    return order.Valuable ? HolderOrNearest(order.Valuable) : null;
                default:
                    return null;
            }
        }

        private static PlayerAvatar HolderOrNearest(ValuableObject valuable)
        {
            var grab = valuable.physGrabObject;
            var holder = grab && grab.playerGrabbing != null
                ? grab.playerGrabbing.Where(g => g && g.playerAvatar && !g.playerAvatar.isDisabled).Select(g => g.playerAvatar).FirstOrDefault()
                : null;
            return holder ? holder : Nav.NearestPlayer(Director.ValuablePosition(valuable));
        }

        /// <summary>実行できる指示か(できないときは、見つけたときの判断をゲームに任せる)。</summary>
        private static bool Executable(Order order) =>
            order != null && (TargetOf(order) || order.Action == "retreat" || order.Action == "wait");

        private static bool Idle(EnemyBombThrower.State state) =>
            state == EnemyBombThrower.State.Idle || state == EnemyBombThrower.State.Roam
            || state == EnemyBombThrower.State.Investigate || state == EnemyBombThrower.State.Leave;

        public void Tick(Enemy enemy, Order order)
        {
            var crew = Crew(enemy);
            if (!crew || enemy.IsStunned() || enemy.CurrentState == EnemyState.Despawn || !Executable(order) || crew.stateImpulse)
            {
                return;
            }
            // ゲームと同じく、跳んでいる最中と、頭を投げて戻るのを待っている間は判断しない(OnVision と同じ条件)
            if (enemy.Jump && (enemy.Jump.jumpingDelay || enemy.Jump.jumping))
            {
                return;
            }
            var state = crew.currentState;
            var target = TargetOf(order);
            if (!target)
            {
                // retreat / wait で相手の指定がない: 今の相手のまま(いなければ一番近い人)
                target = crew.playerTarget && !crew.playerTarget.isDisabled ? crew.playerTarget : Nav.NearestPlayer(Director.EnemyPosition(enemy));
                if (!target)
                {
                    return;
                }
            }
            bool engaged = state == EnemyBombThrower.State.GotoPlayer || state == EnemyBombThrower.State.BackAwayPlayer
                           || state == EnemyBombThrower.State.Notice;
            if (crew.playerTarget != target && (engaged || Idle(state)))
            {
                crew.UpdatePlayerTarget(target);
            }
            if (Idle(state))
            {
                if (crew.head && crew.head.currentState != EnemyBombThrowerHead.State.Disabled)
                {
                    return; // 頭が飛んでいる間は、ゲームも見つけた相手へ向かわない
                }
                crew.UpdateState(EnemyBombThrower.State.Notice);
                return;
            }
            if (order.Action == "retreat" && state == EnemyBombThrower.State.GotoPlayer)
            {
                crew.UpdateState(EnemyBombThrower.State.BackAwayPlayer);
                return;
            }
            if ((state == EnemyBombThrower.State.GotoPlayer || state == EnemyBombThrower.State.BackAwayPlayer) && Director.VisionSees(enemy, target))
            {
                // 見えている間は追うのをやめない(ゲーム本来も、見えるたびに続ける)
                crew.stateTimer = Mathf.Max(crew.stateTimer, 2f);
            }
        }

        // ---------------- 見つけたときの判断: 実行できる指示があるときは AI に任せる ----------------

        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.OnVision))]
        private static class VisionPatch
        {
            private static bool Prefix(EnemyBombThrower __instance)
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer())
                {
                    return true;
                }
                return !Director.TryGetOrder(__instance.enemy, out var order) || !Executable(order);
            }
        }

        // ---------------- wait: 近づきも後ずさりもしない(投げられる・殴れるときはゲームどおり攻撃する) ----------------

        private static bool Waiting(EnemyBombThrower crew) =>
            SemiFunc.IsMasterClientOrSingleplayer() && !crew.stateImpulse && crew.playerTarget && !crew.playerTarget.isDisabled
            && Director.TryGetOrder(crew.enemy, out var order) && order != null && order.Action == "wait";

        /// <summary>GotoPlayer / BackAwayPlayer の代わり: 攻撃の判定だけして、その場で相手のほうを向く。</summary>
        private static bool HoldPosition(EnemyBombThrower crew)
        {
            if (!Waiting(crew))
            {
                return true;
            }
            if (crew.AttackSetLogic() || crew.MeleeSetLogic())
            {
                return false;
            }
            crew.AgentReset();
            crew.RotationStateSet(EnemyBombThrower.RotationState.LookTarget);
            return false;
        }

        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.StateGotoPlayer))]
        private static class GotoPatch
        {
            private static bool Prefix(EnemyBombThrower __instance) => HoldPosition(__instance);
        }

        [HarmonyPatch(typeof(EnemyBombThrower), nameof(EnemyBombThrower.StateBackAwayPlayer))]
        private static class BackAwayPatch
        {
            private static bool Prefix(EnemyBombThrower __instance) => HoldPosition(__instance);
        }
    }
}
