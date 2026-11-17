using HarmonyLib;

namespace ArenaTweaks
{
    /// <summary>
    /// テスト用: 一人でもバトルロワイヤルを複数人のときと同じように動かす。
    /// ゲームは参加人数(numberOfPlayers)が 1 人だと、始まってすぐ床を全部落として「Game Over」にし、王冠やアイテムも動かさない。
    /// 参加人数を 2 人と思わせる(生き残りは実際に数えるので 1 人。王冠の檻は 15 秒ほどで開く)。
    /// </summary>
    internal static class SoloTest
    {
        internal static bool On => Plugin.SoloArena.Value && !SemiFunc.IsMultiplayer();
    }

    /// <summary>一人のときはゲームがアイテムを置かないので、置く。</summary>
    [HarmonyPatch(typeof(Arena), nameof(Arena.ArenaInit))]
    internal static class SoloArenaInitPatch
    {
        private static void Postfix(Arena __instance)
        {
            if (!SoloTest.On)
            {
                return;
            }
            Plugin.Log.LogInfo("一人でバトルロワイヤルを試します(テスト用の設定)");
            __instance.ArenaInitMultiplayer();
        }
    }

    [HarmonyPatch(typeof(Arena), nameof(Arena.Start))]
    internal static class SoloArenaStartPatch
    {
        private static void Postfix(Arena __instance)
        {
            if (!SoloTest.On || __instance.numberOfPlayers > 1)
            {
                return;
            }
            __instance.numberOfPlayers = 2;
            __instance.playersAlive = 2;
            __instance.playersAlivePrev = 2;
        }
    }
}
