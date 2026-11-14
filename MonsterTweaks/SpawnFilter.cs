using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MonsterTweaks
{
    /// <summary>
    /// モンスターごとの「出現させる」。ゲームがレベルの敵を選ぶとき(EnemyDirector.PickEnemies。ホストだけが行う)に、
    /// OFF の敵を候補から外す。
    /// - 1 つの出現枠(EnemySetup)に複数の敵が入っているときは、どれか 1 つでも OFF なら外す(例: バンガーとそのまとめ役)
    /// - 一覧にない敵(ほかの MOD の敵など)は外さない
    /// - その難しさの候補が全部 OFF なら、ほかの難しさの候補から選ぶ。それも全部 OFF なら、その 1 体は出さない
    ///   (出す数の合計 totalAmount も合わせて減らす。減らさないと、レベルの生成で足りない分を読みにいって止まる)
    /// </summary>
    internal static class SpawnFilter
    {
        private static readonly Dictionary<EnemySetup, List<EnemyCommon.Entry>> Cache = new Dictionary<EnemySetup, List<EnemyCommon.Entry>>();

        /// <summary>この出現枠に入っている敵の設定(一覧にある敵だけ)。</summary>
        private static List<EnemyCommon.Entry> EntriesOf(EnemySetup setup)
        {
            if (Cache.TryGetValue(setup, out var cached))
            {
                return cached;
            }
            var result = new List<EnemyCommon.Entry>();
            if (setup.spawnObjects != null)
            {
                // まず名前で見分ける(プレハブを読み込むと、レベルの生成中に使わない敵まで同期で読み込んでしまうため)。
                // 敵のプレハブの名前にはクラス名(Enemy を除いた部分。例: EnemySlowWalker → "Slow Walker")が入っている
                foreach (var spawn in setup.spawnObjects)
                {
                    string name = Normalize(spawn?.ResourcePath) + "|" + Normalize(spawn?.PrefabName);
                    foreach (var entry in EnemyCommon.All)
                    {
                        if (!result.Contains(entry) && name.Contains(Normalize(entry.Type.Name.Replace("Enemy", ""))))
                        {
                            result.Add(entry);
                        }
                    }
                }
                if (result.Count > 0)
                {
                    Plugin.Verbose($"出現枠 {setup.name}: {string.Join("、", result.Select(e => e.Name))}(名前で判定)");
                    Cache[setup] = result;
                    return result;
                }
                // 名前で分からないときだけ、中身を調べる(結果は覚えておくので 1 回だけ)
                foreach (var spawn in setup.spawnObjects)
                {
                    var prefab = spawn?.Prefab;
                    if (!prefab)
                    {
                        continue;
                    }
                    foreach (var entry in EnemyCommon.All)
                    {
                        if (!result.Contains(entry) && prefab.GetComponentInChildren(entry.Type, true))
                        {
                            result.Add(entry);
                        }
                    }
                }
            }
            Cache[setup] = result;
            return result;
        }

        private static string Normalize(string text) =>
            string.IsNullOrEmpty(text) ? "" : new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        internal static bool Allowed(EnemySetup setup) => setup && EntriesOf(setup).All(e => e.Spawn.Value);

        private static bool AnyDisabled => EnemyCommon.All.Any(e => !e.Spawn.Value);

        [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.PickEnemies))]
        private static class PickPatch
        {
            private static bool Prefix(EnemyDirector __instance, ref List<EnemySetup> _enemiesList, out bool __state)
            {
                __state = false;
                if (!Util.IsAuthority || _enemiesList == null || !AnyDisabled)
                {
                    return true;
                }
                __state = true;
                var original = _enemiesList;
                var allowed = original.Where(Allowed).ToList();
                if (allowed.Count == 0)
                {
                    // この難しさの候補が全部 OFF: ほかの難しさの候補から選ぶ
                    var others = new[] { __instance.enemiesDifficulty3, __instance.enemiesDifficulty2, __instance.enemiesDifficulty1 }
                        .Where(list => list != null && list != original)
                        .SelectMany(list => list)
                        .Where(Allowed)
                        .Distinct()
                        .ToList();
                    if (others.Count == 0)
                    {
                        Plugin.Verbose("出現させる敵が 1 体もないので、この枠の敵は出しません");
                        return false; // 候補なし: この 1 体は出さない(AmountSetup の後で合計を合わせる)
                    }
                    allowed = others;
                    Plugin.Verbose("この難しさの敵が全部「出現させない」なので、ほかの難しさの敵から選びます");
                }
                // ゲームはこのリストを並べ替えて使うので、元のリストではなく新しいリストを渡す
                _enemiesList = allowed;
                return true;
            }

            /// <summary>
            /// 残った候補が全部ゲームの出現条件(プレイ回数・クリアしたレベル数)に合わないと、ゲームは空の枠(null)を足す。
            /// 空の枠はレベルの生成で止まる原因になるので取り除く(合計は AmountPatch で合わせる)。
            /// </summary>
            private static void Postfix(EnemyDirector __instance, bool __state)
            {
                if (!__state)
                {
                    return;
                }
                var current = __instance.enemyListCurrent;
                var all = __instance.enemyList;
                if (current != null && current.Count > 0 && current[current.Count - 1] == null)
                {
                    current.RemoveAt(current.Count - 1);
                    if (all != null && all.Count > 0 && all[all.Count - 1] == null)
                    {
                        all.RemoveAt(all.Count - 1);
                    }
                    Plugin.Verbose("出現させる敵の中に、今の進み具合で出られる敵がいないので、この枠の敵は出しません");
                }
            }
        }

        /// <summary>出さなかった枠の分、出す数の合計を減らす(今回選んだ数 = enemyListCurrent の数)。</summary>
        [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.AmountSetup))]
        private static class AmountPatch
        {
            private static void Postfix(EnemyDirector __instance)
            {
                if (!Util.IsAuthority || __instance.enemyListCurrent == null)
                {
                    return;
                }
                int picked = __instance.enemyListCurrent.Count;
                if (picked < __instance.totalAmount)
                {
                    Plugin.Log.LogInfo($"「出現させる」が OFF の敵を除いたので、このレベルの敵は {__instance.totalAmount} 体から {picked} 体になります");
                    __instance.totalAmount = picked;
                }
            }
        }
    }
}
