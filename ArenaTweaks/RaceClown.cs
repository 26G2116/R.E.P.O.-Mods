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
    /// レースのクラウン(ホストの PC)。
    /// - コースに貴重品が置かれるとき、ときどき代わりにクラウン(敵)を置く。同時に 1 体まで
    /// - レースには敵が歩くための道(ナビメッシュ・地点)がないので、置かれた場所から動かずに、見つけた人をビームで撃つ
    /// - 足場のコースが落ち始めたら隠して、次に貴重品が置かれるときに、その場所へ置き直す(同じ 1 体を使い回す)
    /// </summary>
    internal static class RaceClown
    {
        private static Object sceneKey;
        private static EnemyParent clown;
        private static ArenaRaceTrackPiece clownPiece;
        private static Vector3? wantedPosition;
        private static float freeSince = -999f;
        private static float placedAt;
        private static bool unavailable;
        private static bool lastSpawned;
        private static EnemyBeamer.State? lastBeamerState;
        private static int stateLogs;

        private static void EnsureScene(ArenaRace race)
        {
            if (race == sceneKey)
            {
                return;
            }
            sceneKey = race;
            lastSpawned = false;
            lastBeamerState = null;
            stateLogs = 0;
            clown = null;
            clownPiece = null;
            wantedPosition = null;
            freeSince = -999f;
            unavailable = false;
        }

        /// <summary>Runner から毎フレーム: 足場が落ちたら隠す。</summary>
        internal static void Update()
        {
            ArenaRace race = ArenaRace.instance;
            if (!Plugin.ClownEnabled.Value || !race)
            {
                return;
            }
            EnsureScene(race);
            LogState();
            if (!clown || wantedPosition == null)
            {
                return;
            }
            bool fell = clown.Spawned && clown.Enemy && clown.Enemy.transform.position.y < wantedPosition.Value.y - 10f;
            if (!PieceStillThere(clownPiece) || fell)
            {
                Plugin.Log.LogInfo(fell ? "クラウンが落ちたので隠します" : "クラウンの足場が落ちるので隠します");
                wantedPosition = null;
                clownPiece = null;
                freeSince = Time.time;
                if (clown.Spawned)
                {
                    clown.Despawn();
                }
                clown.DespawnedTimer = 9999f;
                return;
            }
            if (clown.Spawned)
            {
                clown.SpawnedTimer = Mathf.Max(clown.SpawnedTimer, 60f); // 時間で勝手に消えないように
            }
        }

        /// <summary>ログ: クラウンが出た・隠れた、と、動き(ビームの構え・撃つなど)が変わったとき。</summary>
        private static void LogState()
        {
            if (!clown)
            {
                return;
            }
            bool spawned = clown.Spawned;
            if (spawned != lastSpawned)
            {
                lastSpawned = spawned;
                if (spawned && clown.Enemy)
                {
                    Vector3 pos = clown.Enemy.transform.position;
                    float nearest = SemiFunc.PlayerGetAll().Where(p => p && !p.isDisabled)
                        .Select(p => Vector3.Distance(p.transform.position, pos)).DefaultIfEmpty(-1f).Min();
                    Plugin.Log.LogInfo($"クラウンが出てきました(位置 {pos}、いちばん近い人まで {nearest:F0} m)");
                }
                else
                {
                    Plugin.Log.LogInfo("クラウンが隠れました");
                }
            }
            EnemyBeamer beamer = clown.GetComponentInChildren<EnemyBeamer>(true);
            if (beamer && beamer.currentState != lastBeamerState)
            {
                lastBeamerState = beamer.currentState;
                if (stateLogs++ < 60)
                {
                    Plugin.Log.LogInfo($"クラウンの動き: {beamer.currentState}");
                }
            }
        }

        private static bool PieceStillThere(ArenaRaceTrackPiece piece) =>
            piece && !piece.plunging && (!piece.countdownStarted || piece.countdownTimer > 0.5f);

        private static bool IsFree()
        {
            if (!clown)
            {
                return wantedPosition == null || Time.time - placedAt > 15f; // 出すのに失敗したままにしない
            }
            if (wantedPosition == null)
            {
                return Time.time - freeSince >= Plugin.ClownCooldown.Value;
            }
            // 倒されて消えている(置いてからしばらくたっても出ていない)
            return !clown.Spawned && Time.time - placedAt > 10f;
        }

        /// <summary>貴重品を置くところで呼ぶ。クラウンを置いたら true(貴重品は置かない)。</summary>
        internal static bool TryPlace(ArenaRacePhysGrabObjectSpawner spawner)
        {
            ArenaRace race = ArenaRace.instance;
            if (!Plugin.ClownEnabled.Value || !SemiFunc.IsMasterClientOrSingleplayer() || !race)
            {
                return false;
            }
            EnsureScene(race);
            if (unavailable || spawner.volumeType == ValuableVolume.Type.Tiny || spawner.volumeType == ValuableVolume.Type.Small)
            {
                return false;
            }
            ArenaRaceTrackPiece piece = spawner.GetComponentInParent<ArenaRaceTrackPiece>();
            if (!piece || piece.pieceIndex < Plugin.ClownFirstPiece.Value || !IsFree())
            {
                return false;
            }
            if (UnityEngine.Random.value * 100f >= Plugin.ClownChance.Value)
            {
                return false;
            }
            Vector3 position = spawner.transform.position;
            if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 8f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
            {
                position = hit.point;
            }
            wantedPosition = position;
            clownPiece = piece;
            placedAt = Time.time;
            if (clown)
            {
                // 使い回す: 隠れているなら、すぐ出てこさせる(出てくるときに、この場所へ動かす)
                if (clown.Spawned)
                {
                    clown.Enemy.EnemyTeleported(position);
                }
                else
                {
                    clown.DespawnedTimer = 0.3f;
                }
                Plugin.Log.LogInfo($"クラウンをコース {piece.pieceIndex} に置き直します");
                return true;
            }
            if (!Spawn(position))
            {
                wantedPosition = null;
                clownPiece = null;
                return false;
            }
            Plugin.Log.LogInfo($"クラウンをコース {piece.pieceIndex} に置きました");
            return true;
        }

        internal static bool IsOurs(Enemy enemy) => clown && enemy && enemy.EnemyParent == clown;

        /// <summary>
        /// 敵が出てくるとき(SemiFunc.EnemySpawn)。ゲームは出てくる地点を探すが、レースには地点がなく出てこられないので、
        /// 置きたい場所へ動かして出てこさせる。置き場所がないときは隠れたままにする。
        /// </summary>
        internal static bool OnEnemySpawn(Enemy enemy)
        {
            if (wantedPosition == null)
            {
                enemy.EnemyParent.Despawn();
                enemy.EnemyParent.DespawnedTimer = 9999f;
                return false;
            }
            enemy.EnemyTeleported(wantedPosition.Value);
            enemy.EnemyParent.firstSpawnPointUsed = true;
            AddPlayers(enemy);
            return true;
        }

        private static bool Spawn(Vector3 position)
        {
            if (!LevelGenerator.Instance || !LevelGenerator.Instance.EnemyParent || !EnemyDirector.instance)
            {
                Plugin.Log.LogWarning("このレースでは敵を出せませんでした(敵の入れ物がありません)。このレースではクラウンを出しません");
                unavailable = true;
                return false;
            }
            EnemySetup setup = FindSetup(Plugin.ClownEnemyName.Value);
            if (!setup)
            {
                unavailable = true;
                return false;
            }
            foreach (PrefabRef prefab in setup.spawnObjects)
            {
                if (prefab == null)
                {
                    continue;
                }
                GameObject obj;
                try
                {
                    obj = SemiFunc.IsMultiplayer()
                        ? PhotonNetwork.InstantiateRoomObject(prefab.ResourcePath, position, Quaternion.identity, 0, EnemyFloaterHalloweenState.SpawnData(prefab))
                        : Object.Instantiate(prefab.Prefab, position, Quaternion.identity);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"クラウンを出せませんでした({prefab.ResourcePath}): {e}");
                    unavailable = true;
                    return false;
                }
                EnemyParent parent = obj ? obj.GetComponent<EnemyParent>() : null;
                if (!parent)
                {
                    continue;
                }
                parent.SetupDone = true;
                Enemy enemy = obj.GetComponentInChildren<Enemy>();
                if (enemy)
                {
                    enemy.EnemyTeleported(position);
                }
                clown = parent;
                Plugin.Runner.StartCoroutine(AfterSpawn(parent));
                return true; // 1 体だけ
            }
            Plugin.Log.LogWarning($"敵の設定「{setup.name}」に、出せる敵がありませんでした");
            unavailable = true;
            return false;
        }

        private static IEnumerator AfterSpawn(EnemyParent parent)
        {
            yield return new WaitForSeconds(1f);
            if (parent && parent.Enemy)
            {
                AddPlayers(parent.Enemy);
            }
        }

        /// <summary>敵が全員を見つけられるようにする(途中で出した敵には、プレイヤーが登録されないことがある)。</summary>
        private static void AddPlayers(Enemy enemy)
        {
            foreach (PlayerAvatar player in SemiFunc.PlayerGetAll())
            {
                if (player && player.photonView)
                {
                    enemy.PlayerAdded(player.photonView.ViewID);
                }
            }
        }

        private static EnemySetup FindSetup(string name)
        {
            EnemyDirector director = EnemyDirector.instance;
            var all = new List<EnemySetup>();
            foreach (List<EnemySetup> list in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
            {
                if (list != null)
                {
                    all.AddRange(list.Where(x => x && !all.Contains(x)));
                }
            }
            string key = (name ?? "").Trim();
            EnemySetup found = all.FirstOrDefault(x => string.Equals(x.name, "Enemy - " + key, StringComparison.OrdinalIgnoreCase))
                               ?? all.FirstOrDefault(x => x.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                               ?? all.FirstOrDefault(x => x.spawnObjects != null && x.spawnObjects.Any(p => p != null && (p.ResourcePath ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0));
            if (!found)
            {
                Plugin.Log.LogWarning($"敵「{key}」が見つかりませんでした。設定の「敵の名前」に使える名前: {string.Join(", ", all.Select(x => x.name))}");
            }
            return found;
        }
    }

    /// <summary>コースに貴重品を置くところで、代わりにクラウンを置く。</summary>
    [HarmonyPatch(typeof(ArenaRacePhysGrabObjectSpawner), nameof(ArenaRacePhysGrabObjectSpawner.Start))]
    internal static class RaceSpawnerPatch
    {
        private static bool Prefix(ArenaRacePhysGrabObjectSpawner __instance)
        {
            try
            {
                return !RaceClown.TryPlace(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"クラウンを置くところでエラー: {e}");
                return true;
            }
        }
    }

    /// <summary>クラウンが出てくるとき、ゲームの「出てくる地点探し」の代わりに、置き場所へ動かす。</summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.EnemySpawn))]
    internal static class EnemySpawnPatch
    {
        private static bool Prefix(Enemy enemy, ref bool __result)
        {
            if (!RaceClown.IsOurs(enemy))
            {
                return true;
            }
            try
            {
                __result = RaceClown.OnEnemySpawn(enemy);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"クラウンが出てくるところでエラー: {e}");
                return true;
            }
        }
    }
}
