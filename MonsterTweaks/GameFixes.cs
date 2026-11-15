using System;
using HarmonyLib;

namespace MonsterTweaks
{
    /// <summary>
    /// ゲーム本来の不具合の保護(設定によらずホストで常に動く)。
    /// 狙っていたプレイヤーが抜けて消えると、その参照のまま毎フレーム NullReferenceException を出し続け、
    /// 状態から抜けられなくなる敵がいる。例外が出たら、その敵の狙いを外して普段の状態に戻す。
    /// - ラグラット(EnemyValuableThrower.StateGetValuable): playerTarget の位置を読む
    /// - ウーグリー(EnemyOogly.StatePlayerSpotted)
    /// </summary>
    internal static class GameFixes
    {
        [HarmonyPatch(typeof(EnemyValuableThrower), nameof(EnemyValuableThrower.StateGetValuable))]
        private static class RugratGetValuablePatch
        {
            private static Exception Finalizer(Exception __exception, EnemyValuableThrower __instance)
            {
                if (!(__exception is NullReferenceException) || !Util.IsAuthority || !__instance)
                {
                    return __exception;
                }
                __instance.playerTarget = null;
                __instance.valuableTarget = null;
                __instance.UpdateState(EnemyValuableThrower.State.Leave);
                Plugin.Log.LogInfo("ラグラット: 狙っていたプレイヤーがいなくなったため、立ち去らせました(ゲーム本来の不具合の保護)");
                return null;
            }
        }

        /// <summary>
        /// ヘッドグラブ(EnemyHeadGrabber.StateGotoHead): 回収しに行く頭(headTarget)が消えると(抜けた人の頭は片付けられる)、
        /// 頭の位置を読むところで毎フレーム例外が出続ける。狙いを外して歩き回りに戻す。
        /// </summary>
        [HarmonyPatch(typeof(EnemyHeadGrabber), nameof(EnemyHeadGrabber.StateGotoHead))]
        private static class HeadGrabberGotoHeadPatch
        {
            private static Exception Finalizer(Exception __exception, EnemyHeadGrabber __instance)
            {
                if (!(__exception is NullReferenceException) || !Util.IsAuthority || !__instance)
                {
                    return __exception;
                }
                __instance.headTarget = null;
                __instance.UpdateState(EnemyHeadGrabber.State.Roam);
                Plugin.Log.LogInfo("ヘッドグラブ: 回収しに行く頭がなくなったため、歩き回りに戻しました(ゲーム本来の不具合の保護)");
                return null;
            }
        }

        [HarmonyPatch(typeof(EnemyOogly), nameof(EnemyOogly.StatePlayerSpotted))]
        private static class OoglyPlayerSpottedPatch
        {
            private static Exception Finalizer(Exception __exception, EnemyOogly __instance)
            {
                if (!(__exception is NullReferenceException) || !Util.IsAuthority || !__instance)
                {
                    return __exception;
                }
                __instance.targetPlayer = null;
                __instance.previousTargetPlayer = null;
                __instance.hasTarget = false;
                __instance.StateSet(EnemyOogly.State.CeilingRoam);
                Plugin.Log.LogInfo("ウーグリー: 狙っていたプレイヤーがいなくなったため、天井に戻しました(ゲーム本来の不具合の保護)");
                return null;
            }
        }
    }
}
