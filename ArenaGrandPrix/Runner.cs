using System;
using UnityEngine;

namespace ArenaGrandPrix
{
    /// <summary>毎フレームの見回り(ホストの PC)。</summary>
    internal class Runner : MonoBehaviour
    {
        private LevelGenerator monitorFor;
        private int errors;

        private void Update()
        {
            if (!RunManager.instance || !SemiFunc.IsMasterClientOrSingleplayer())
            {
                return;
            }
            try
            {
                Tournament.Tick();
                ArenaTracker.Update();
                LevelGenerator lg = LevelGenerator.Instance;
                if (lg && lg.Generated && lg != monitorFor && SemiFunc.RunIsLobby() && (Tournament.Active || Tournament.ShowFinalResults))
                {
                    monitorFor = lg;
                    StartCoroutine(Monitor.Run(lg));
                }
            }
            catch (Exception e)
            {
                if (errors++ < 5)
                {
                    Plugin.Log.LogError($"見回りでエラー: {e}");
                }
            }
        }
    }
}
