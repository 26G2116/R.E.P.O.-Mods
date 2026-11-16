using System;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaRaceFixes
{
    /// <summary>
    /// ArenaRaceCosmeticBoxTrap 4.0.0 の修正。
    /// 11 月 5 日の更新で、物を壊す処理(PhysGrabObjectImpactDetector.DestroyObject)に引数(forced)が増えた。
    /// 元の MOD の爆発の処理(Explode)は古い形で呼ぶので、触れた瞬間にエラーになって爆発せず、警告がログに出続ける。
    /// 触れたかを調べる処理(TryExplodeFromCollider)を差し替え、爆発は今の形の DestroyObject で起こす。
    /// 触れたかの判定(ホストか・出てすぐではないか・アリーナか・プレイヤーやカートか)は、元の MOD の処理をそのまま使う。
    /// </summary>
    internal static class CosmeticBoxTrap
    {
        internal const string TargetGuid = "REPOJP.ArenaRaceCosmeticBoxTrap";
        private const string TargetType = "REPOJP.ArenaRaceCosmeticBoxTrap.ArenaRaceCosmeticBoxTrapTrigger";

        private static AccessTools.FieldRef<object, bool> exploded;
        private static AccessTools.FieldRef<object, PhysGrabObjectImpactDetector> impactDetector;
        private static FastInvokeHandler canExplodeNow;
        private static FastInvokeHandler isValidTouchTarget;
        private static int errors;
        private static int explosions;

        internal static bool Patch(Harmony harmony)
        {
            Type type = AccessTools.TypeByName(TargetType);
            if (type == null)
            {
                return false; // 入っていない
            }
            MethodInfo target = AccessTools.Method(type, "TryExplodeFromCollider", new[] { typeof(Collider), typeof(string) });
            MethodInfo canExplode = AccessTools.Method(type, "CanExplodeNow", Type.EmptyTypes);
            MethodInfo isValid = AccessTools.Method(type, "IsValidTouchTarget", new[] { typeof(Collider) });
            FieldInfo explodedField = AccessTools.Field(type, "exploded");
            FieldInfo detectorField = AccessTools.Field(type, "impactDetector");
            if (target == null || target.ReturnType != typeof(bool) || canExplode == null || canExplode.ReturnType != typeof(bool) ||
                isValid == null || isValid.ReturnType != typeof(bool) || explodedField?.FieldType != typeof(bool) ||
                detectorField?.FieldType != typeof(PhysGrabObjectImpactDetector))
            {
                Plugin.Log.LogWarning("ArenaRaceCosmeticBoxTrap の版が想定と違うので直しません(爆発の処理の形が違います)");
                return false;
            }
            exploded = AccessTools.FieldRefAccess<object, bool>(explodedField);
            impactDetector = AccessTools.FieldRefAccess<object, PhysGrabObjectImpactDetector>(detectorField);
            canExplodeNow = MethodInvoker.GetHandler(canExplode);
            isValidTouchTarget = MethodInvoker.GetHandler(isValid);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(CosmeticBoxTrap), nameof(Prefix)));
            CheckSpawnMethod();
            return true;
        }

        /// <summary>
        /// ボックスを出す処理(SpawnCosmeticBox)にも、更新で場所が変わったゲームの処理(PrefabRef.Prefab、1 人プレイのときだけ使う)がある。
        /// ゲームの仕組み上たぶんそのまま動くが、動かないとボックスがまったく出なくなるので、起動時に一度コンパイルさせて確かめる。
        /// </summary>
        private static void CheckSpawnMethod()
        {
            MethodInfo spawn = AccessTools.Method(AccessTools.TypeByName("REPOJP.ArenaRaceCosmeticBoxTrap.ArenaRacePhysGrabObjectSpawnerStartPatch"),
                "SpawnCosmeticBox");
            if (spawn == null)
            {
                return;
            }
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(spawn.MethodHandle);
                Plugin.Log.LogInfo("ArenaRaceCosmeticBoxTrap のボックスを出す処理は、今のゲームで動きます");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ArenaRaceCosmeticBoxTrap のボックスを出す処理が、今のゲームで動かないかもしれません" +
                                      $"(ボックスが出ないときは、このログを作者に伝えてください): {e.GetType().Name}: {e.Message}");
            }
        }

        // 引数の名前は元の処理と同じにする(Harmony が名前で結びつける)
        private static bool Prefix(MonoBehaviour __instance, Collider other, string reason, ref bool __result)
        {
            __result = false;
            try
            {
                if (!__instance || exploded(__instance) || !other || !(bool)canExplodeNow(__instance))
                {
                    return false;
                }
                Transform self = __instance.transform;
                if (other.transform == self || other.transform.IsChildOf(self))
                {
                    return false;
                }
                if (!(bool)isValidTouchTarget(__instance, other))
                {
                    return false;
                }
                Explode(__instance, reason, other.gameObject.name);
                __result = true;
            }
            catch (Exception e)
            {
                if (errors++ < 5)
                {
                    Plugin.Log.LogWarning($"コスメボックスの爆発の判定でエラー: {e}");
                }
            }
            return false; // 元の(古い形の DestroyObject を呼ぶ)処理は動かさない
        }

        private static void Explode(MonoBehaviour trigger, string reason, string targetName)
        {
            exploded(trigger) = true;
            PhysGrabObjectImpactDetector detector = impactDetector(trigger);
            if (!detector)
            {
                detector = trigger.GetComponent<PhysGrabObjectImpactDetector>();
                impactDetector(trigger) = detector;
            }
            if (explosions++ < 50)
            {
                Plugin.Log.LogInfo($"コスメボックスが爆発しました({reason}、触れたもの: {targetName})");
            }
            if (detector)
            {
                // ゲームのコスメボックスの爆発(CosmeticWorldObject)と同じ呼び方
                detector.DestroyObject(true, false);
                return;
            }
            // 物を壊す部品がないときは、元の MOD と同じく、そのまま消す
            if (SemiFunc.IsMultiplayer())
            {
                PhotonView view = trigger.GetComponent<PhotonView>();
                if (view)
                {
                    PhotonNetwork.Destroy(trigger.gameObject);
                    return;
                }
            }
            Object.Destroy(trigger.gameObject);
        }
    }
}
