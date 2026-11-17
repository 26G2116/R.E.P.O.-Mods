using System;
using UnityEngine;

namespace ArenaTweaks
{
    /// <summary>毎フレームの見回り(ホストの PC)と、復活の手順を動かすコルーチンの置き場。</summary>
    internal class Runner : MonoBehaviour
    {
        private int errors;

        private void Update()
        {
            if (!RunManager.instance || !SemiFunc.IsMasterClientOrSingleplayer())
            {
                return;
            }
            try
            {
                Lives.Update();
                RaceClown.Update();
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
