using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ArenaGrandPrix
{
    /// <summary>
    /// トラックのモニターに、試合の結果と総合順位を出す(ホストが税務官のメッセージとして書く。全員の画面に出る)。
    /// モニターは 12 行を超えると上の行を消し、枠からはみ出すと古い文章が消えなくなるので、
    /// 1 ページ 11 行までにし、枠に収まる大きさの文字で書く(FloorRoles と同じやり方)。
    /// </summary>
    internal static class Monitor
    {
        private const int RowsPerPage = 9; // 見出し 1 行 + 9 行 + 次の試合 1 行 = 11 行
        private const int MinSize = 30;
        private static int sizePenalty;
        private static int lastSize = 100;

        internal static IEnumerator Run(LevelGenerator lobby)
        {
            yield return new WaitForSeconds(1.5f);
            float waited = 0f;
            bool first = true;
            int page = 0;
            float nextPageAt = 0f;
            while (LevelGenerator.Instance == lobby && SemiFunc.RunIsLobby() && (Tournament.Active || Tournament.ShowFinalResults))
            {
                List<List<string>> pages = Pages();
                bool due = first ? TruckPageFinished() || waited >= 90f : Time.time >= nextPageAt;
                if (due && pages.Count > 0)
                {
                    page %= pages.Count;
                    if (TruckMessage(Text(pages[page])))
                    {
                        int written = page;
                        page = (page + 1) % pages.Count;
                        nextPageAt = Time.time + Plugin.PageSeconds.Value;
                        first = false;
                        // 見切れていると古い文章が残って次のページも見えなくなるので、打ち直させて小さい文字で書き直す
                        yield return new WaitForSeconds(1.5f);
                        var screen = TruckScreenText.instance;
                        if (screen && screen.textMesh && LevelGenerator.Instance == lobby && SemiFunc.RunIsLobby())
                        {
                            string text = screen.textMesh.text ?? "";
                            if ((screen.textMesh.isTextTruncated || text.Contains(":</b>")) && lastSize > MinSize)
                            {
                                sizePenalty = Mathf.Max(sizePenalty + 10, Plugin.MonitorTextSize.Value - lastSize + 10);
                                Plugin.Log.LogWarning($"モニター: 見切れたので、文字を小さくして書き直します(上限 {CurrentMaxSize()}%)");
                                if (SemiFunc.IsMultiplayer())
                                {
                                    screen.photonView.RPC("InitializeTextTypingRPC", Photon.Pun.RpcTarget.All);
                                }
                                else
                                {
                                    screen.InitializeTextTypingLogic();
                                }
                                page = written;
                                first = true;
                                waited = 0f;
                                continue;
                            }
                        }
                        if (pages.Count == 1)
                        {
                            // 1 ページだけなら書き直さない(結果が変わったら、次のトラックで書く)
                            yield break;
                        }
                    }
                }
                else if (first && waited >= 150f)
                {
                    yield break;
                }
                waited += 0.25f;
                yield return new WaitForSeconds(0.25f);
            }
        }

        private static int CurrentMaxSize() => Mathf.Max(MinSize, Plugin.MonitorTextSize.Value - sizePenalty);

        // ---------------------------------------------------------------- ページの中身

        private static List<List<string>> Pages()
        {
            var pages = new List<List<string>>();
            string footer = $"<color=#7FD4FF>{Escape(Tournament.NextLabel())}</color>";
            if (Tournament.ShowFinalResults)
            {
                var head = new List<string> { $"<color=#FFD700>★ 優勝: {Escape(Tournament.ChampionNames)} ★</color>" };
                AddStandingPages(pages, "【アリーナ大会 最終順位】", footer, head);
                return pages;
            }
            var result = Tournament.LastResult;
            if (result != null)
            {
                var rows = result.Rows.Select(r =>
                    $"<color={Medal(r.Placement)}>{r.Placement}位 {Escape(Short(r.Name))}</color>" +
                    (result.Stage == Tournament.Stage.Final ? "" : $"  <color=#9EFF9E>+{r.Gained}</color>")).ToList();
                if (rows.Count == 0)
                {
                    rows.Add("(順位なし)");
                }
                AddPages(pages, $"【{Escape(result.Title)} の結果】", rows, footer);
            }
            AddStandingPages(pages, $"【総合順位】({Tournament.EventsDone} / {Plugin.EventCount.Value} 試合)", footer, null);
            return pages;
        }

        private static void AddStandingPages(List<List<string>> pages, string title, string footer, List<string> head)
        {
            var rows = new List<string>();
            if (head != null)
            {
                rows.AddRange(head);
            }
            rows.AddRange(Tournament.Ranking().Select(kv => $"<color={Medal(kv.Key)}>{kv.Key}位 {Escape(Short(kv.Value.Name))}  {kv.Value.Points} pt</color>"));
            AddPages(pages, title, rows, footer);
        }

        private static void AddPages(List<List<string>> pages, string title, List<string> rows, string footer)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(rows.Count / (float)RowsPerPage));
            for (int i = 0; i < count; i++)
            {
                var lines = new List<string> { count > 1 ? $"{title} {i + 1}/{count}" : title };
                lines.AddRange(rows.Skip(i * RowsPerPage).Take(RowsPerPage));
                lines.Add(footer);
                pages.Add(lines);
            }
        }

        /// <summary>順位の色(色の指定は行ごとに閉じる。上の行が消えても、下の行の色が崩れないように)。</summary>
        private static string Medal(int placement)
        {
            switch (placement)
            {
                case 1:
                    return "#FFD700";
                case 2:
                    return "#D8D8D8";
                case 3:
                    return "#E3A15B";
                default:
                    return "#FFFFFF";
            }
        }

        private static string Short(string name)
        {
            name = name ?? "?";
            return name.Length > 14 ? name.Substring(0, 13) + "…" : name;
        }

        /// <summary>名前などに入った「&lt;」で、色や大きさの指定が崩れないようにする。</summary>
        private static string Escape(string text) => (text ?? "").Replace("<", "＜").Replace(">", "＞");

        // ---------------------------------------------------------------- 書く

        private static bool TruckPageFinished()
        {
            var screen = TruckScreenText.instance;
            if (!screen || !screen.started || screen.isTyping || screen.pages == null)
            {
                return false;
            }
            int page = screen.currentPageIndex;
            if (page < 0 || page >= screen.pages.Count)
            {
                return true;
            }
            var lines = screen.pages[page].textLines;
            return lines == null || screen.currentLineIndex >= lines.Count;
        }

        private static bool TruckMessage(string text)
        {
            var screen = TruckScreenText.instance;
            if (!screen || screen.isTyping)
            {
                return false;
            }
            screen.MessageSendCustom("", text, 0);
            return true;
        }

        private static string Text(List<string> lines)
        {
            int size = CurrentMaxSize();
            int contentLines = lines.Count;
            string diagnosis = "測定できませんでした";
            string padding = "";
            using (var probe = MonitorProbe.Create())
            {
                if (probe != null)
                {
                    for (;; size -= 5)
                    {
                        contentLines = probe.CountLines(Format(lines, size));
                        bool fits = contentLines <= 11 && probe.Fits(size, Plugin.MonitorLineHeight.Value, contentLines + 1)
                                    && probe.FitsText(Flush(12, size) + "\n" + Format(lines, size));
                        diagnosis = probe.Describe(size, Plugin.MonitorLineHeight.Value);
                        if (fits || size - 5 < MinSize)
                        {
                            break;
                        }
                    }
                    // モニターは下端そろえなので、文章の下に見えない行を足して上端へ寄せる
                    // (書き終わると、押し出し用の行は「12 - 文章の行 - この 1 行」だけ残る)
                    int keep = Mathf.Max(0, 12 - contentLines - 1);
                    string finalText = keep > 0 ? Flush(keep, size) + "\n" + Format(lines, size) : Format(lines, size);
                    padding = probe.TopPadding(finalText, size, Plugin.MonitorLineHeight.Value, SpareLines);
                }
            }
            lastSize = size;
            Plugin.Log.LogInfo($"モニター: 文字 {size}%、{contentLines} 行、{(padding.Length > 0 ? "上端寄せ" : "下端のまま")}({diagnosis})");
            return Flush(12, size) + "\n" + Format(lines, size) + padding;
        }

        /// <summary>あとから来るゲームのメッセージ(トラックに戻った人の回復など)のために、下に空けておく行。</summary>
        private const int SpareLines = 2;

        /// <summary>見えない、とても薄い行(古い文章を押し出す用)。最後の 1 行だけは、ふつうの行の間隔にする。</summary>
        private static string Flush(int count, int size)
        {
            var parts = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int height = i == count - 1 ? MonitorProbe.ScaledLineHeight(Plugin.MonitorLineHeight.Value, size) : 1;
                parts.Add($"<line-height={height}%><size=1%><color=#00000000>.</color></size>");
            }
            return string.Join("\n", parts);
        }

        /// <summary>上から 1 行ずつ消えても効き目が残るように、行ごとに大きさと行の高さを指定する。</summary>
        private static string Format(List<string> lines, int size)
        {
            string lineHeight = $"<line-height={MonitorProbe.ScaledLineHeight(Plugin.MonitorLineHeight.Value, size)}%>";
            var text = string.Join("\n", lines.Select(l => size >= 100 ? lineHeight + l : $"{lineHeight}<size={size}%>{l}</size>"));
            return text + "</line-height>";
        }
    }
}
