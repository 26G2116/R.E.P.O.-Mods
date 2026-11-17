using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ArenaTweaks
{
    /// <summary>
    /// バトルロワイヤル(Disposal Arena)のアイテム(ホストの PC)。
    /// ゲームは始まりに「使うアイテム 1・近接武器 5・銃 3(各 30%)・カート 1(30%)・ドローンとオーブ 3(各 30%)・回復 3(各 30%)」を
    /// チームのアイテムとして置き場所に並べ、床が 1 段落ちたあとは 5 秒ごとに中央へ近接武器か銃を出す。その数・確率・間隔を設定で変える。
    /// </summary>
    internal static class ArenaItems
    {
        internal static void Init(Arena arena)
        {
            // 置き場所の順番をばらばらにする(ゲームと同じ)
            var indices = new List<int>();
            for (int i = 0; i < arena.itemVolumes.childCount; i++)
            {
                indices.Add(i);
            }
            for (int j = 0; j < arena.itemVolumes.childCount; j++)
            {
                int r = Random.Range(0, indices.Count);
                arena.itemVolumes.GetChild(j).SetSiblingIndex(indices[r]);
                indices.RemoveAt(r);
            }
            arena.itemsMelee.Shuffle();
            arena.itemsGuns.Shuffle();
            arena.itemsCarts.Shuffle();
            arena.itemsDronesAndOrbs.Shuffle();
            arena.itemsHealth.Shuffle();
            arena.itemsUsables.Shuffle();
            arena.itemsMid = MidPool(arena);

            var picked = new List<Item>();
            int total = 0;
            total += Pick(picked, arena.itemsUsables, Plugin.UsableCount, Plugin.UsableChance);
            total += Pick(picked, arena.itemsMelee, Plugin.MeleeCount, Plugin.MeleeChance);
            total += Pick(picked, arena.itemsGuns, Plugin.GunCount, Plugin.GunChance);
            total += Pick(picked, arena.itemsCarts, Plugin.CartCount, Plugin.CartChance);
            total += Pick(picked, arena.itemsDronesAndOrbs, Plugin.DroneCount, Plugin.DroneChance);
            total += Pick(picked, arena.itemsHealth, Plugin.HealthCount, Plugin.HealthChance);

            ItemManager.instance.ResetAllItems();
            ItemManager.instance.purchasedItems.AddRange(picked);
            ItemManager.instance.GetAllItemVolumesInScene();
            PunManager.instance.TruckPopulateItemVolumes(arena.itemSpawns);
            Plugin.Log.LogInfo($"バトルロワイヤルのアイテム: {total} 個のうち {picked.Count} 個を置きます" +
                               $"(置き場所 {arena.itemVolumes.childCount} か所。置き場所が足りないアイテムは出ません)、中央に出す候補 {arena.itemsMid.Count} 種類");
        }

        /// <summary>1 つずつ確率で選ぶ。数がリストより多ければ、リストの頭からもう一周する(同じアイテムが増える)。</summary>
        private static int Pick(List<Item> picked, List<Item> list, ConfigEntry<int> count, ConfigEntry<int> chance)
        {
            if (list == null || list.Count == 0)
            {
                return 0;
            }
            for (int i = 0; i < count.Value; i++)
            {
                if (Random.Range(0, 100) < chance.Value && list[i % list.Count])
                {
                    picked.Add(list[i % list.Count]);
                }
            }
            return count.Value;
        }

        private static List<Item> MidPool(Arena arena)
        {
            var pool = new List<Item>();
            void Add(List<Item> list)
            {
                if (list != null)
                {
                    pool.AddRange(list.FindAll(x => x && x.prefab != null));
                }
            }
            switch (Plugin.MidItems.Value)
            {
                case MidKind.Melee:
                    Add(arena.itemsMelee);
                    break;
                case MidKind.Guns:
                    Add(arena.itemsGuns);
                    break;
                case MidKind.Everything:
                    Add(arena.itemsUsables);
                    Add(arena.itemsMelee);
                    Add(arena.itemsGuns);
                    Add(arena.itemsCarts);
                    Add(arena.itemsDronesAndOrbs);
                    Add(arena.itemsHealth);
                    break;
                default:
                    Add(arena.itemsMelee);
                    Add(arena.itemsGuns);
                    break;
            }
            return pool;
        }

        /// <summary>中央に武器を出す(ゲームの SpawnMidWeapons と同じ流れで、間隔と始まる段階と種類を設定にする)。</summary>
        internal static void SpawnMid(Arena arena)
        {
            float interval = Plugin.MidInterval.Value;
            if (interval <= 0f || arena.itemsMid == null || arena.itemsMid.Count == 0 || !arena.itemsMidSpawner)
            {
                return;
            }
            if (arena.playersAlive <= 1 || arena.level < Plugin.MidStartLevel.Value || arena.currentState == Arena.States.GameOver)
            {
                return;
            }
            if (arena.midSpawnerTimer <= interval)
            {
                arena.midSpawnerTimer += Time.deltaTime;
                return;
            }
            arena.midSpawnerTimer = 0f;
            Item item = arena.itemsMid[Random.Range(0, arena.itemsMid.Count)];
            if (SemiFunc.IsMultiplayer())
            {
                PhotonNetwork.Instantiate(item.prefab.ResourcePath, arena.itemsMidSpawner.position, arena.itemsMidSpawner.rotation, 0, null);
            }
            else
            {
                Object.Instantiate(item.prefab.Prefab, arena.itemsMidSpawner.position, arena.itemsMidSpawner.rotation);
            }
        }
    }

    [HarmonyPatch(typeof(Arena), nameof(Arena.ArenaInitMultiplayer))]
    internal static class ArenaInitPatch
    {
        private static bool Prefix(Arena __instance)
        {
            if (!Plugin.ItemsEnabled.Value)
            {
                return true;
            }
            try
            {
                ArenaItems.Init(__instance);
            }
            catch (Exception e)
            {
                // 途中まで進んでいるかもしれないので、ゲームの処理はやり直さない(アイテムが少ないだけで試合はできる)
                Plugin.Log.LogError($"バトルロワイヤルのアイテムを置くところでエラー: {e}");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Arena), nameof(Arena.SpawnMidWeapons))]
    internal static class ArenaMidWeaponsPatch
    {
        private static int errors;

        private static bool Prefix(Arena __instance)
        {
            if (!Plugin.ItemsEnabled.Value)
            {
                return true;
            }
            if (!SemiFunc.IsMasterClientOrSingleplayer())
            {
                return false;
            }
            try
            {
                ArenaItems.SpawnMid(__instance);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                {
                    Plugin.Log.LogError($"中央に武器を出すところでエラー: {e}");
                }
            }
            return false;
        }
    }
}
