using System;
using Photon.Pun;

namespace AIMonsters
{
    /// <summary>
    /// 味方のローブの発言。ローブ自身は声を出せないので、ローブから一番近いプレイヤーのセミボットが、
    /// 頭に「Robe:」をつけてしゃべる(チャットと同じ仕組みなので MOD なしの人にも聞こえる)。
    /// 近くのチャットを読んで AI で答える処理は、このあと追加する。
    /// </summary>
    internal static class AllyChat
    {
        internal const string Prefix = "Robe: ";

        /// <summary>決まった文をしゃべる。</summary>
        internal static void SayFixed(string text) => Say(text);

        internal static void Say(string text)
        {
            var robe = AllyRobe.Ally;
            if (!robe || string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            var speaker = Nav.NearestPlayer(Director.EnemyPosition(robe.enemy));
            if (!speaker)
            {
                return;
            }
            string message = Prefix + text.Trim();
            string locale = IsAscii(message) ? "en-US" : "ja-JP";
            try
            {
                if (SemiFunc.IsMultiplayer())
                {
                    speaker.photonView.RPC("ChatMessageSendRPC", RpcTarget.All, message, false, locale);
                }
                else
                {
                    speaker.ChatMessageSpeak(message, false, locale);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"味方のローブ: しゃべらせられませんでした: {e.Message}");
            }
        }

        internal static bool IsAscii(string text)
        {
            foreach (char c in text)
            {
                if (c > 127)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>レベルが変わったとき。</summary>
        internal static void Reset()
        {
        }
    }
}
