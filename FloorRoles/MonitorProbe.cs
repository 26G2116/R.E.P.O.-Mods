using System;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FloorRoles
{
    /// <summary>
    /// トラックのモニターと同じ設定の、見えない文字(ホストの PC だけ)。送る文章が何行になるか、枠に何行入るかを測る。
    /// モニター本体の文字は使わない(測ると表示中の文章の状態が変わるため)。
    ///
    /// モニターは、枠からはみ出した行を行数に数えない。ゲームは「12 行を超えたら上の行を消す」ので、
    /// 12 行が枠に収まらない大きさの文字だと、古い文章が消えずに残り、下が見切れる。
    /// そのため、はみ出しても全部の行を数える設定で測る。
    /// </summary>
    internal sealed class MonitorProbe : IDisposable
    {
        private readonly GameObject go;
        private readonly TextMeshProUGUI probe;
        private readonly TextMeshProUGUI source;
        private readonly float usableHeight;

        private MonitorProbe(GameObject go, TextMeshProUGUI probe, TextMeshProUGUI source, float usableHeight)
        {
            this.go = go;
            this.probe = probe;
            this.source = source;
            this.usableHeight = usableHeight;
        }

        /// <summary>作れなければ null。</summary>
        internal static MonitorProbe Create()
        {
            var screen = TruckScreenText.instance;
            var source = screen ? screen.textMesh : null;
            if (!source)
            {
                return null;
            }
            GameObject go = null;
            try
            {
                go = new GameObject("FloorRoles.MonitorProbe", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(source.transform.parent, false);
                var sourceRect = source.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = sourceRect.pivot;
                rect.sizeDelta = sourceRect.rect.size;
                rect.localScale = sourceRect.localScale;
                rect.localRotation = sourceRect.localRotation;

                var probe = go.AddComponent<TextMeshProUGUI>();
                probe.font = source.font;
                probe.fontSharedMaterial = source.fontSharedMaterial;
                probe.fontStyle = source.fontStyle;
                probe.enableAutoSizing = false;
                probe.fontSize = source.fontSize;
                probe.textWrappingMode = source.textWrappingMode;
                probe.overflowMode = TextOverflowModes.Overflow; // はみ出した行も数える
                probe.margin = source.margin;
                probe.lineSpacing = source.lineSpacing;
                probe.lineSpacingAdjustment = source.lineSpacingAdjustment;
                probe.paragraphSpacing = source.paragraphSpacing;
                probe.characterSpacing = source.characterSpacing;
                probe.wordSpacing = source.wordSpacing;
                probe.alignment = source.alignment;
                probe.richText = source.richText;
                probe.raycastTarget = false;
                probe.color = Color.clear;

                var margin = source.margin; // 左・上・右・下
                float usable = sourceRect.rect.height - margin.y - margin.w;
                return new MonitorProbe(go, probe, source, usable);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"モニターを測れませんでした: {e.Message}");
                if (go)
                {
                    Object.Destroy(go);
                }
                return null;
            }
        }

        /// <summary>文章が何行になるか(折り返しを含む)。</summary>
        internal int CountLines(string text)
        {
            probe.text = SemiFunc.EmojiText(text);
            probe.ForceMeshUpdate(true, true);
            return probe.textInfo != null ? probe.textInfo.lineCount : 0;
        }

        /// <summary>行と行の間隔(大きさ・行の高さを指定した行)。</summary>
        private float Pitch(int size, int lineHeight)
        {
            const int count = 12;
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }
                builder.Append($"<line-height={lineHeight}%>");
                builder.Append(size >= 100 ? "あ" : $"<size={size}%>あ</size>");
            }
            builder.Append("</line-height>");
            if (CountLines(builder.ToString()) < count)
            {
                return -1f;
            }
            var lines = probe.textInfo.lineInfo;
            return (lines[0].baseline - lines[count - 1].baseline) / (count - 1);
        }

        /// <summary>
        /// 文字の大きさに合わせた行の高さ。line-height タグは文字の大きさ(size タグ)によらず、元の大きさの行の高さに対する割合なので、
        /// そのままだと文字を小さくしても行の間隔が縮まらない(枠に入る行が増えない)。
        /// </summary>
        internal static int ScaledLineHeight(int lineHeight, int size)
        {
            return size >= 100 ? lineHeight : Mathf.Max(1, Mathf.RoundToInt(lineHeight * size / 100f));
        }

        /// <summary>送る文章全体(押し出し用の行を含む)が、枠の高さに収まるか。測れなければ true。</summary>
        internal bool FitsText(string text)
        {
            int count = CountLines(text);
            if (count <= 0 || usableHeight <= 0f)
            {
                return true;
            }
            var lines = probe.textInfo.lineInfo;
            float height = lines[0].ascender - lines[count - 1].descender;
            return height <= 0f || height <= usableHeight;
        }

        /// <summary>指定した大きさの行 lines 行が、枠に収まるか(lineHeight は 100% の文字のときの行の高さ)。</summary>
        internal bool Fits(int size, int lineHeight, int lines)
        {
            float pitch = Pitch(size, ScaledLineHeight(lineHeight, size));
            if (pitch <= 0f || usableHeight <= 0f || !Plausible(pitch, size, lineHeight))
            {
                return true; // 測れなければ、大きさは変えない
            }
            return pitch * lines <= usableHeight;
        }

        /// <summary>
        /// 測った行の間隔がもっともらしいか(文字の大きさに比例しているか)。
        /// ときどき、測る文字の配置がまだ済んでいないなどで、小さい文字なのに間隔が大きく出たり、0 以下になったりする。
        /// そのまま使うと文字がどんどん小さくなる(30% まで)ので、おかしい値は使わない。
        /// </summary>
        private bool Plausible(float pitch, int size, int lineHeight)
        {
            if (size >= 100)
            {
                return true;
            }
            float reference = Pitch(100, lineHeight);
            if (reference <= 0f)
            {
                return false;
            }
            float expected = reference * size / 100f;
            return Mathf.Abs(pitch - expected) <= expected * 0.5f;
        }

        /// <summary>
        /// 文章を枠の上端へ寄せるための、最後に足す見えない 1 行(モニターは下端そろえで、ほかの人の PC の設定は変えられないため、
        /// 文章の下に高さのある見えない行を置いて押し上げる)。finalText は、押し出し用の行が消えたあとの最終的な文章。
        /// 下には、あとから来るゲームのメッセージ用に spareLines 行分のすき間を残す。測れなければ空文字(下端のまま)。
        /// </summary>
        internal string TopPadding(string finalText, int size, int lineHeight, int spareLines)
        {
            if (usableHeight <= 0f)
            {
                return "";
            }
            float pitch = Pitch(size, ScaledLineHeight(lineHeight, size));
            float spare = pitch > 0f ? pitch * Mathf.Max(0, spareLines) : 0f;
            float limit = usableHeight * 0.97f - spare; // ぎりぎりだと見切れ扱いになるので、少し余らせる
            int lo = 0;
            int hi = 20000;
            if (Height(finalText + Pad(lo)) > limit)
            {
                return "";
            }
            while (hi - lo > 5)
            {
                int mid = (lo + hi) / 2;
                if (Height(finalText + Pad(mid)) <= limit)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo > 0 ? Pad(lo) : "";
        }

        private static string Pad(int lineHeight) =>
            $"<line-height={lineHeight}%>\n<size=1%><color=#00000000>.</color></size></line-height>";

        /// <summary>文章の 1 行目の上から最後の行の下までの高さ。測れなければ 0。</summary>
        private float Height(string text)
        {
            int count = CountLines(text);
            if (count <= 0)
            {
                return 0f;
            }
            var lines = probe.textInfo.lineInfo;
            return lines[0].ascender - lines[count - 1].descender;
        }

        /// <summary>ログ用の説明。</summary>
        internal string Describe(int size, int lineHeight)
        {
            float pitch = Pitch(size, ScaledLineHeight(lineHeight, size));
            int capacity = pitch > 0f ? Mathf.FloorToInt(usableHeight / pitch) : -1;
            return $"枠の高さ {usableHeight:0.#}、行の間隔 {pitch:0.##}、枠に入る行 {capacity}、" +
                   $"はみ出し {source.overflowMode}、配置 {source.alignment}、元の文字 {source.fontSize:0.#}";
        }

        public void Dispose()
        {
            if (go)
            {
                // 同じフレームのうちに消えるので、画面には映らない
                Object.Destroy(go);
            }
        }
    }
}
