using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FloorRoles
{
    internal enum Role
    {
        None = -1,
        Carrier = 0,
        Fighter = 1,
        Special = 2,
    }

    /// <summary>
    /// トラック(ショップのあと)で役職を選び、次のレベルの開始時にステータス報酬を渡す。
    ///
    /// - トラックに入ったら、次のマップを先に抽選し、役職ごとの報酬の候補(運搬・戦闘は 3 種類、特殊は 4 種類)ともらえる数をモニターに出す
    /// - 人数分のロールオーブを出す。トラックを出発したとき、ロールオーブを 1 つだけインベントリに入れている人は、
    ///   入れたスロット(1 運搬 / 2 戦闘 / 3 特殊)の役職になる。0 個・2 個以上なら前に選んだ役職を引き継ぐ(全滅・新しいランでリセット)
    /// - 出発したとき、役職のある人ごとに 1D100 を振り、もらえる数を決める(クリティカル 1〜5 で 3 つ、ファンブル 96〜100 で 1 つ、
    ///   それ以外は 2 つ)。どれをもらえるかは役職の候補からランダム。ダイスの結果と報酬は、次のレベルの開始時にその人がしゃべる
    /// - 報酬はゲーム本来のアップグレードと同じ(全員の PC で同じ処理が動く)ので、MOD なしの参加者にも効く。
    ///   上げるのは出発の瞬間(ショップで使ったのと同じ扱い)で、次のマップの開始時に反映される
    /// - 出発(モニターのチャット)はホストだけができる
    /// 役職特性・マップごとの追加特性は、あとで足す(今は報酬だけ)。
    /// </summary>
    internal static class Roles
    {
        private const string Section = "1. 役職";
        private const string RollOrbAsset = "Item Orb Torque";
        private const string RemovedFolder = "Items/Removed Items";

        internal static ConfigEntry<bool> Enabled;
        private static ConfigEntry<bool> SpawnOrbs;
        private static ConfigEntry<bool> HostOnlyDeparture;
        private static ConfigEntry<bool> PrerollLevel;
        private static ConfigEntry<bool> ShowOnMonitor;
        private static ConfigEntry<bool> ShowTraitPages;
        private static ConfigEntry<float> PageSeconds;
        private static ConfigEntry<string> PageCommand;
        private static ConfigEntry<int> RewardCount;
        private static ConfigEntry<int> RewardAmount;
        private static ConfigEntry<int> SpeedCap;
        private static ConfigEntry<int> LaunchCap;
        private static ConfigEntry<int> MonitorTextSize;
        private static ConfigEntry<int> MonitorLineHeight;
        private static ConfigEntry<int> MonitorSpareLines;
        private static ConfigEntry<bool> RewardDice;
        private static ConfigEntry<bool> KeepRoles;
        private static ConfigEntry<bool> KeptRoleRewards;
        private static ConfigEntry<int> DiceCriticalMax;
        private static ConfigEntry<int> DiceFumbleMin;
        private static ConfigEntry<int> CriticalRewardCount;
        private static ConfigEntry<int> FumbleRewardCount;

        private class Stat
        {
            internal string Key;   // アップグレードの名前(ゲーム内部)
            internal string Label; // 表示名(しゃべるとき)
            internal Func<int> Cap; // 上限(0 以下 = なし)
        }

        private static readonly Stat[][] RoleStats =
        {
            new[] // 運搬: スタミナ・パワー系
            {
                new Stat { Key = "Stamina", Label = "Stamina" },
                new Stat { Key = "CrouchRest", Label = "Crouch Rest" },
                new Stat { Key = "Strength", Label = "Strength" },
            },
            new[] // 戦闘: 体力・移動系
            {
                new Stat { Key = "Health", Label = "Health" },
                new Stat { Key = "Speed", Label = "Sprint Speed", Cap = () => SpeedCap.Value },
                new Stat { Key = "Range", Label = "Range" },
            },
            new[] // 特殊: その他(タンブル系)
            {
                new Stat { Key = "ExtraJump", Label = "Extra Jump" },
                new Stat { Key = "TumbleWings", Label = "Tumble Wings" },
                new Stat { Key = "Launch", Label = "Tumble Launch", Cap = () => LaunchCap.Value },
                new Stat { Key = "TumbleClimb", Label = "Tumble Climb" },
            },
        };

        private static readonly string[] RoleNames = { "運搬", "戦闘", "特殊" };
        /// <summary>役職特性の名前(モニターに出す)。</summary>
        internal static readonly string[] TraitNames = { "引っ越し用運搬ロボ", "避雷針", "ギャンブラー" };

        // ---- このトラックの状態 ----
        private static int lobbyGeneration = -1;
        private static Level nextLevel;
        /// <summary>このトラックの準備(次のマップの抽選など)が済んだか。出発時の報酬はこれが立っているときだけ渡す。</summary>
        private static bool lobbyPrepared;
        private static readonly List<ItemAttributes> SpawnedOrbs = new List<ItemAttributes>();
        /// <summary>ロールオーブを出す前からあった「購入したアイテム」の名前(片付けで消さない)。</summary>
        private static readonly HashSet<string> ItemsBeforeOrbs = new HashSet<string>();

        // ---- 出発時に渡した報酬(次のレベルの開始時にしゃべらせる) ----
        private static readonly Dictionary<string, Role> NextRoles = new Dictionary<string, Role>();
        /// <summary>最後に選んだ役職(次のトラックで選ばなければ引き継ぐ)。同じランの間だけ覚える。</summary>
        private static readonly Dictionary<string, Role> KeptRoles = new Dictionary<string, Role>();
        /// <summary>KeptRoles を覚えたときのセーブ(別のセーブを読み込んだら忘れる)。</summary>
        private static string keptSave;
        /// <summary>今回の出発で、前の役職を引き継いだ人。</summary>
        private static readonly HashSet<string> InheritedRoles = new HashSet<string>();
        private static readonly Dictionary<string, string> Announcements = new Dictionary<string, string>();
        private static int announceGeneration = -1;
        private static bool announceScheduled;

        /// <summary>今のレベルでの役職(役職特性をあとで足すときに使う)。</summary>
        internal static readonly Dictionary<string, Role> CurrentRoles = new Dictionary<string, Role>();

        private static Item rollOrb;
        private static float departureWarnTime;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "トラックで役職を選び、次のレベルでステータス報酬を渡す");
            SpawnOrbs = cfg.Bind(Section, "ロールオーブを出す", true, "トラックに入ったとき、人数分のロールオーブを各プレイヤーの前に出す");
            HostOnlyDeparture = cfg.Bind(Section, "出発はホストだけ", true, "トラックのモニター(チャット)で出発できるのをホストだけにする");
            PrerollLevel = cfg.Bind(Section, "次のマップを先に決める", true, "トラックに入った時点で次のマップを抽選し、モニターに出す");
            ShowOnMonitor = cfg.Bind(Section, "モニターに表示", true, "役職ごとの報酬をトラックのモニターに出す");
            ShowTraitPages = cfg.Bind(Section, "役職特性の説明をモニターに出す", true,
                "報酬の一覧のあとに、役職特性(運搬・戦闘・特殊)の説明を 1 役職ずつ順番にモニターに出す(一覧と交互に切り替わる)");
            PageSeconds = cfg.Bind(Section, "モニターのページを切り替える秒数", 15f,
                new ConfigDescription("報酬の一覧と役職特性の説明を自動で切り替える間隔(0 = 自動では切り替えず、コマンドだけで切り替える)",
                    new AcceptableValueRange<float>(0f, 120f)));
            PageCommand = cfg.Bind(Section, "ページ切り替えのコマンド", "/s",
                "トラックでチャットに打つと、モニターのページを切り替える(だれでも使える)。「/s」で次、「/s 0」で報酬の一覧、「/s 1」〜「/s 3」で運搬・戦闘・特殊");
            MonitorTextSize = cfg.Bind(Section, "モニターの文字の大きさ(%)", 70,
                new ConfigDescription("モニターに出す役職の説明の文字の大きさの上限(枠に収まらなければ、自動で小さくする)", new AcceptableValueRange<int>(40, 100)));
            MonitorLineHeight = cfg.Bind(Section, "モニターの行の高さ(%)", 85,
                new ConfigDescription("役職の説明の 1 行の高さ(ふつうの大きさの文字の行を 100 とする)。大きくすると行間が広がるが、下が見切れやすくなる",
                    new AcceptableValueRange<int>(40, 150)));
            MonitorSpareLines = cfg.Bind(Section, "モニターの行数の余裕", 1,
                new ConfigDescription("役職の説明が枠に収まるか確かめるときに足す余裕の行数。まだ下が見切れるなら増やす(文字が小さくなる)",
                    new AcceptableValueRange<int>(0, 6)));
            RewardCount = cfg.Bind(Section, "報酬の数", 2,
                new ConfigDescription("報酬ダイスがクリティカル・ファンブル以外のとき(報酬ダイスなしならいつも)、役職の候補からランダムにいくつもらえるか",
                    new AcceptableValueRange<int>(1, 3)));
            RewardAmount = cfg.Bind(Section, "報酬の上がり幅", 1,
                new ConfigDescription("報酬 1 つあたり、アップグレードをいくつ上げるか", new AcceptableValueRange<int>(1, 5)));
            SpeedCap = cfg.Bind(Section, "スプリント速度の上限", 4,
                new ConfigDescription("役職の報酬でスプリント速度をここまでしか上げない(0 = 上限なし)", new AcceptableValueRange<int>(0, 99)));
            LaunchCap = cfg.Bind(Section, "タンブル発射の上限", 2,
                new ConfigDescription("役職の報酬でタンブル発射をここまでしか上げない(0 = 上限なし)", new AcceptableValueRange<int>(0, 99)));
            KeepRoles = cfg.Bind(Section, "選ばなければ前の役職を引き継ぐ", true,
                "出発のとき、ロールオーブを 1 個だけ入れていない人(0 個・2 個以上)は、前に選んだ役職のままにする(全滅したら・新しいランではリセット)");
            KeptRoleRewards = cfg.Bind(Section, "引き継いだ役職でも報酬をもらう", true,
                "前の役職を引き継いだ人にも、出発のときに役職の報酬(報酬ダイス)を渡す");
            RewardDice = cfg.Bind(Section, "報酬ダイス", true,
                "出発したとき、役職のある人ごとに 1D100 を振り、報酬の数を変える(クリティカルで増える・ファンブルで減る)。結果は次のマップの開始時にしゃべる");
            DiceCriticalMax = cfg.Bind(Section, "報酬ダイスのクリティカルの上限", 5,
                new ConfigDescription("この数字以下ならクリティカル(0 = なし)", new AcceptableValueRange<int>(0, 100)));
            DiceFumbleMin = cfg.Bind(Section, "報酬ダイスのファンブルの下限", 96,
                new ConfigDescription("この数字以上ならファンブル(101 = なし)", new AcceptableValueRange<int>(1, 101)));
            CriticalRewardCount = cfg.Bind(Section, "クリティカルの報酬の数", 3,
                new ConfigDescription("クリティカルのとき、役職の候補からいくつもらえるか(候補の数より多ければ全部)", new AcceptableValueRange<int>(0, 4)));
            FumbleRewardCount = cfg.Bind(Section, "ファンブルの報酬の数", 1,
                new ConfigDescription("ファンブルのとき、役職の候補からいくつもらえるか", new AcceptableValueRange<int>(0, 4)));
        }

        // ================================================================
        // 毎フレーム(ホスト)
        // ================================================================

        internal static void Tick()
        {
            if (!Enabled.Value)
            {
                return;
            }
            if (SemiFunc.RunIsLobby() && lobbyGeneration != Phase.Generation)
            {
                lobbyGeneration = Phase.Generation;
                Runner.Instance.StartCoroutine(LobbyStart(Phase.Generation));
            }
            if (SemiFunc.RunIsLevel() && NextRoles.Count > 0 && announceGeneration != Phase.Generation && !announceScheduled)
            {
                announceScheduled = true;
                Runner.Instance.StartCoroutine(StartLevelRoles(Phase.Generation));
            }
        }

        // ================================================================
        // トラックに入ったとき
        // ================================================================

        private static IEnumerator LobbyStart(int generation)
        {
            // プレイヤーがトラックの中に立つまで少し待つ
            yield return new WaitForSeconds(1.5f);
            if (generation != Phase.Generation || !SemiFunc.RunIsLobby())
            {
                yield break;
            }

            nextLevel = PrerollLevel.Value ? PickNextLevel() : null;
            lobbyPrepared = true;
            SpawnedOrbs.Clear();
            if (SpawnOrbs.Value)
            {
                SpawnRollOrbs();
            }
            Plugin.Verbose($"トラック: 次のマップ {LevelName(nextLevel)}");

            if (!ShowOnMonitor.Value)
            {
                yield break;
            }
            // 税務官がページを最後まで打ち終わってから書く(行と行の間に書くと、続きの行に押し上げられて
            // 12 行の制限で上から消える)。90 秒たっても終わらなければ、打っていない瞬間に書く
            // ページ 0 = 報酬の一覧、1〜3 = 役職特性の説明(運搬・戦闘・特殊)。役職特性の説明を出すときは順番に切り替える
            // コマンド(/s)で切り替えるときは、すぐにそのページを出す(自動の切り替えはそこから数え直す)
            float waited = 0f;
            bool first = true;
            int page = 0;
            float nextPageAt = 0f;
            requestedPage = NoRequest;
            monitorPaging = false;
            while (generation == Phase.Generation && SemiFunc.RunIsLobby())
            {
                bool paging = TraitPagesOn;
                int request = requestedPage;
                bool due;
                if (first)
                {
                    due = Net.TruckPageFinished() || waited >= 90f;
                }
                else if (request != NoRequest)
                {
                    due = true;
                    if (request >= 0)
                    {
                        page = Mathf.Clamp(request, 0, 3);
                    }
                }
                else
                {
                    due = PageSeconds.Value > 0f && Time.time >= nextPageAt;
                }
                if (due && Net.TruckMessage(MonitorText(PageLines(page), page)))
                {
                    if (requestedPage == request)
                    {
                        requestedPage = NoRequest;
                    }
                    page = paging ? (page + 1) % 4 : 0;
                    nextPageAt = Time.time + PageSeconds.Value;
                    monitorPaging = paging;
                    if (first)
                    {
                        first = false;
                        // 古い文章が消えたかを、少しあとでログに出す(見切れの調査用)
                        yield return new WaitForSeconds(2f);
                        var screen = TruckScreenText.instance;
                        if (screen && screen.textMesh)
                        {
                            string text = screen.textMesh.text ?? "";
                            Plugin.Log.LogInfo($"モニター(2 秒後): 数えた行 {screen.textMesh.textInfo.lineCount}、改行 {text.Count(c => c == '\n')}、" +
                                               $"古い文章の名前の行が残っている: {text.Contains(":</b>")}");
                        }
                    }
                    if (!paging)
                    {
                        yield break;
                    }
                }
                else if (first && waited >= 150f)
                {
                    yield break;
                }
                waited += 0.25f;
                yield return new WaitForSeconds(0.25f);
            }
            monitorPaging = false;
        }

        // ---- モニターのページ切り替え ----
        private const int NoRequest = -1;
        private const int RequestNext = -2;
        /// <summary>コマンドで頼まれたページ(NoRequest = なし、RequestNext = 次、0〜3 = そのページ)。</summary>
        private static int requestedPage = NoRequest;
        /// <summary>モニターのページを切り替えている最中か(コマンドを受け付けるか)。</summary>
        private static bool monitorPaging;

        private static bool TraitPagesOn => ShowTraitPages.Value && Traits.Enabled != null && Traits.Enabled.Value;

        /// <summary>
        /// ページ切り替えのコマンド(既定「/s」)。だれが打ってもよい。ホストだけが拾う。
        /// 「/s」で次のページ、「/s 0」で報酬の一覧、「/s 1」〜「/s 3」で運搬・戦闘・特殊の説明。
        /// </summary>
        [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ChatMessageSpeak))]
        private static class PageCommandPatch
        {
            private static bool Prefix(string _message)
            {
                if (!monitorPaging || !Net.IsHost || !SemiFunc.RunIsLobby() || string.IsNullOrEmpty(_message))
                {
                    return true;
                }
                string command = PageCommand.Value.Trim();
                string text = _message.Trim();
                if (command.Length == 0 || !text.StartsWith(command, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string rest = text.Substring(command.Length).Trim();
                if (rest.Length == 0)
                {
                    requestedPage = RequestNext;
                }
                else if (int.TryParse(rest.Normalize(System.Text.NormalizationForm.FormKC), out int number) && number >= 0 && number <= 3)
                {
                    requestedPage = number;
                }
                else
                {
                    return true; // 「/s」で始まる別の言葉(ほかのコマンドなど)
                }
                return false; // コマンド自体はホストの画面では読み上げない
            }
        }

        /// <summary>モニターのページの行(0 = 報酬の一覧、1〜3 = 役職特性の説明)。</summary>
        private static List<string> PageLines(int page)
        {
            if (page <= 0)
            {
                return OverviewLines();
            }
            int role = page - 1;
            var lines = new List<string> { $"<b>{role + 1} {RoleNames[role]}: {TraitNames[role]}</b>  ({page}/3  {PageCommand.Value.Trim()}で次へ)" };
            lines.AddRange(Traits.Description(role));
            return lines;
        }

        private static List<string> OverviewLines()
        {
            // モニターは「12 行を超えたら上の行を消す」が、枠からはみ出した行は数えない(はみ出した行が見えないまま残る)。
            // 表示枠はホストの MOD からは変えられないので、送る文章だけで収める:
            // - 役職の説明の前に、見えないとても薄い行を 12 行入れる。薄い行は必ず枠に入るので行数が 12 を超え、
            //   古い文章・名前の行・薄い行が上から消えていき、最後は「薄い行 + 役職の説明」の 12 行が残る
            // - 長い行は折り返すので、モニターと同じ設定の見えない文字で行数と枠の高さを測り、収まる文字の大きさを選ぶ
            string amount = RewardAmount.Value > 1 ? $"(各+{RewardAmount.Value})" : "";
            var lines = new List<string>();
            if (nextLevel)
            {
                lines.Add("次のマップ: " + LevelName(nextLevel));
            }
            lines.Add(KeepRoles.Value ? "ロールオーブ1個をスロット1~3へ(入れなければ前の役職のまま)" : "ロールオーブ1個をスロット1~3へ");
            for (int role = 0; role < 3; role++)
            {
                lines.Add($"{role + 1} {RoleNames[role]}{TraitLabel(role)}: {string.Join(", ", RoleStats[role].Select(s => s.Label))} の中からランダムに{CountRange(RoleStats[role].Length)}個{amount}");
            }
            if (RewardDice.Value)
            {
                var dice = new List<string>();
                if (DiceCriticalMax.Value >= 1)
                {
                    dice.Add($"{DiceCriticalMax.Value}以下:{CriticalRewardCount.Value}個");
                }
                if (DiceFumbleMin.Value <= 100)
                {
                    dice.Add($"{DiceFumbleMin.Value}以上:{FumbleRewardCount.Value}個");
                }
                dice.Add($"他:{Mathf.Clamp(RewardCount.Value, 1, 3)}個");
                lines.Add("出発時ダイス " + string.Join(" ", dice));
            }
            if (TraitPagesOn)
            {
                lines.Add($"役職特性の説明: チャットで {PageCommand.Value.Trim()} (次) / {PageCommand.Value.Trim()} 1~3");
            }
            return lines;
        }

        private static string MonitorText(List<string> lines, int page)
        {
            // 文字の大きさを決める: 役職の説明が枠に収まる大きさ(設定の大きさから 5% ずつ小さくして探す)
            int size = MonitorTextSize.Value;
            int contentLines = lines.Count;
            string diagnosis = "測定できませんでした";
            using (var probe = MonitorProbe.Create())
            {
                if (probe != null)
                {
                    for (;; size -= 5)
                    {
                        contentLines = probe.CountLines(FormatMonitorLines(lines, size));
                        bool fits = contentLines <= 11 && probe.Fits(size, MonitorLineHeight.Value, contentLines + MonitorSpareLines.Value);
                        diagnosis = probe.Describe(size, MonitorLineHeight.Value);
                        if (fits || size - 5 < 40)
                        {
                            break;
                        }
                    }
                }
            }
            Plugin.Log.LogInfo($"モニター(ページ {page}): 文字 {size}%、{contentLines} 行({diagnosis})");
            return FlushLines(12) + "\n" + FormatMonitorLines(lines, size);
        }

        /// <summary>
        /// 見えない、とても薄い行(古い文章を押し出す用)。最後の 1 行だけは、次の行(役職の説明)との間を
        /// ふつうの行の間隔にする(薄いままだと、役職の説明の 1 行目が上にはみ出す)。
        /// </summary>
        private static string FlushLines(int count)
        {
            var parts = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int height = i == count - 1 ? MonitorLineHeight.Value : 1;
                parts.Add($"<line-height={height}%><size=1%><color=#00000000>.</color></size>");
            }
            return string.Join("\n", parts);
        }

        /// <summary>
        /// 上から 1 行ずつ消えても効き目が残るように、行ごとに大きさと行の高さを指定する。
        /// 行の高さは改行の時点で効くので閉じずに続け、最後の行の終わりで閉じる(あとから来る文章に残さない)
        /// </summary>
        private static string FormatMonitorLines(List<string> lines, int size)
        {
            string lineHeight = $"<line-height={MonitorLineHeight.Value}%>";
            var text = string.Join("\n", lines.Select(l => size >= 100 ? lineHeight + l : $"{lineHeight}<size={size}%>{l}</size>"));
            return text + "</line-height>";
        }

        /// <summary>役職特性が有効なら「[特性名]」。</summary>
        private static string TraitLabel(int role)
        {
            return Traits.Enabled != null && Traits.Enabled.Value ? $"[{TraitNames[role]}]" : "";
        }

        /// <summary>もらえる数の範囲(報酬ダイスの結果ごとの数の、最小〜最大。候補の数より多くはもらえない)。</summary>
        private static string CountRange(int candidates)
        {
            var counts = new List<int> { Mathf.Clamp(RewardCount.Value, 1, 3) };
            if (RewardDice.Value)
            {
                if (DiceCriticalMax.Value >= 1)
                {
                    counts.Add(CriticalRewardCount.Value);
                }
                if (DiceFumbleMin.Value <= 100)
                {
                    counts.Add(FumbleRewardCount.Value);
                }
            }
            int min = Mathf.Min(counts.Min(), candidates);
            int max = Mathf.Min(counts.Max(), candidates);
            return min == max ? $"{min}" : $"{min}~{max}";
        }

        // ---- 次のマップ ----

        /// <summary>ゲームと同じ決め方(前回のマップ以外からランダム)で、次のマップを先に決める。</summary>
        private static Level PickNextLevel()
        {
            var run = RunManager.instance;
            if (!run || run.levels == null || run.levels.Count == 0)
            {
                return null;
            }
            var levels = run.levels.FindAll(l => l);
            var available = levels.FindAll(l => l != run.previousRunLevel);
            if (available.Count == 0)
            {
                available = levels;
            }
            return available.Count > 0 ? available[UnityEngine.Random.Range(0, available.Count)] : null;
        }

        internal static string LevelName(Level level)
        {
            if (!level)
            {
                return "?";
            }
            try
            {
                if (level.NarrativeNameLocalized)
                {
                    string localized = level.NarrativeNameLocalized.GetLocalizedString();
                    if (!string.IsNullOrEmpty(localized))
                    {
                        return localized;
                    }
                }
            }
            catch (Exception)
            {
                // 翻訳がなければ下の名前を使う
            }
            return string.IsNullOrEmpty(level.NarrativeName) ? level.name : level.NarrativeName;
        }

        [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetRunLevel))]
        private static class SetRunLevelPatch
        {
            private static bool Prefix(RunManager __instance)
            {
                if (!Net.IsHost || !Enabled.Value || !nextLevel || __instance.levels == null || !__instance.levels.Contains(nextLevel))
                {
                    return true;
                }
                __instance.levelCurrent = nextLevel;
                Plugin.Verbose($"次のマップを {LevelName(nextLevel)} にしました(トラックで抽選済み)");
                nextLevel = null;
                return false;
            }
        }

        // ---- ロールオーブ ----

        private static Item RollOrb()
        {
            if (rollOrb)
            {
                return rollOrb;
            }
            if (StatsManager.instance && StatsManager.instance.itemDictionary.TryGetValue(RollOrbAsset, out var item) && item)
            {
                rollOrb = item;
                return rollOrb;
            }
            rollOrb = Resources.LoadAll<Item>(RemovedFolder).FirstOrDefault(i => i && i.name == RollOrbAsset);
            if (!rollOrb)
            {
                Plugin.Log.LogWarning($"ロールオーブ({RollOrbAsset})が見つかりませんでした");
            }
            return rollOrb;
        }

        private static bool IsRollOrb(ItemAttributes attributes)
        {
            return attributes && attributes.item && attributes.item.name == RollOrbAsset;
        }

        private static void SpawnRollOrbs()
        {
            var item = RollOrb();
            if (!item)
            {
                return;
            }
            // 出したオーブは、購入済みのロールオーブ(トラックに出なかった分)の名前を借りることがある。
            // その名前は片付けで消さないように、出す前の一覧を覚えておく
            ItemsBeforeOrbs.Clear();
            if (StatsManager.instance)
            {
                ItemsBeforeOrbs.UnionWith(StatsManager.instance.item.Keys);
            }
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || player.isDisabled)
                {
                    continue;
                }
                var forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
                var center = player.transform.position + Vector3.up * 1.0f;
                var position = center + forward * 0.6f;
                if (Physics.Linecast(center, position, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore))
                {
                    position = center; // 目の前が壁なら、その人の位置に出す
                }
                var spawned = Net.SpawnRoomObject(item.prefab.ResourcePath, item.prefab.Prefab, position, Quaternion.identity);
                var attributes = spawned ? spawned.GetComponent<ItemAttributes>() : null;
                if (attributes)
                {
                    SpawnedOrbs.Add(attributes);
                }
            }
            Plugin.Verbose($"ロールオーブを {SpawnedOrbs.Count} 個出しました");
        }

        // ================================================================
        // トラックを出発したとき(ホスト)
        // ================================================================

        internal static void OnLeavingLevel(bool completed, bool failed)
        {
            if (failed)
            {
                KeptRoles.Clear(); // 全滅: ランが終わるので、役職の引き継ぎもおしまい
            }
            if (Enabled.Value && SemiFunc.RunIsLobby() && completed && lobbyPrepared)
            {
                DecideRoles();
                GrantRewards();
            }
            else if (!SemiFunc.RunIsLobby())
            {
                // レベルを出るときは、そのレベルの役職をおしまいにする
                CurrentRoles.Clear();
            }
            lobbyPrepared = false; // 次のトラックで準備し直す
            CleanupOrbs();
            announceScheduled = false;
        }

        private static void DecideRoles()
        {
            NextRoles.Clear();
            Announcements.Clear();
            InheritedRoles.Clear();
            // 新しいラン・別のセーブなら、前の役職は引き継がない
            string save = StatsManager.instance ? StatsManager.instance.saveFileCurrent : null;
            if (save != keptSave || (RunManager.instance && RunManager.instance.levelsCompleted == 0))
            {
                KeptRoles.Clear();
                keptSave = save;
            }
            // インベントリに入っているロールオーブを、持ち主ごとにまとめる
            var spots = new Dictionary<PlayerAvatar, List<int>>();
            foreach (var equippable in Object.FindObjectsByType<ItemEquippable>(FindObjectsSortMode.None))
            {
                if (!equippable || !equippable.IsEquipped() || !IsRollOrb(equippable.GetComponent<ItemAttributes>()))
                {
                    continue;
                }
                var owner = equippable.GetOwnerPlayerAvatar();
                if (!owner)
                {
                    continue;
                }
                if (!spots.TryGetValue(owner, out var list))
                {
                    spots[owner] = list = new List<int>();
                }
                list.Add(equippable.inventorySpotIndex);
            }
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (!player || string.IsNullOrEmpty(player.steamID))
                {
                    continue;
                }
                var role = Role.None;
                bool inherited = false;
                if (spots.TryGetValue(player, out var list) && list.Count == 1 && list[0] >= 0 && list[0] <= 2)
                {
                    role = (Role)list[0];
                    KeptRoles[player.steamID] = role;
                }
                else if (KeepRoles.Value && KeptRoles.TryGetValue(player.steamID, out var kept))
                {
                    // 選ばなかった(ロールオーブが 0 個・2 個以上): 前の役職のまま
                    role = kept;
                    inherited = true;
                    InheritedRoles.Add(player.steamID);
                }
                if (role != Role.None)
                {
                    NextRoles[player.steamID] = role;
                }
                Plugin.Verbose($"役職: {Net.Name(player)} = {(role == Role.None ? "なし" : RoleNames[(int)role])}{(inherited ? "(引き継ぎ)" : "")}(ロールオーブ {(list == null ? 0 : list.Count)} 個)");
            }
        }

        /// <summary>
        /// 出したロールオーブを「購入したアイテム」の記録から消す(次のレベルのトラックに出てこないように)。
        /// 出したアイテムは、ゲームが名前を付けるときに購入したアイテムの一覧へ足してしまう。
        /// </summary>
        private static void CleanupOrbs()
        {
            var stats = StatsManager.instance;
            if (stats)
            {
                foreach (var attributes in SpawnedOrbs)
                {
                    // 壊れたアイテムでも名前は読める(Unity の null 判定は使わない)
                    string name = (object)attributes != null ? attributes.instanceName : null;
                    if (string.IsNullOrEmpty(name) || ItemsBeforeOrbs.Contains(name))
                    {
                        continue;
                    }
                    stats.item.Remove(name);
                    Net.ForgetBattery(name);
                    int hash = name.GetHashCode();
                    RemoveSpot(stats.playerInventorySpot1, hash);
                    RemoveSpot(stats.playerInventorySpot2, hash);
                    RemoveSpot(stats.playerInventorySpot3, hash);
                }
            }
            SpawnedOrbs.Clear();
        }

        private static void RemoveSpot(Dictionary<string, int> spots, int hash)
        {
            if (spots == null)
            {
                return;
            }
            foreach (var key in spots.Where(p => p.Value == hash).Select(p => p.Key).ToList())
            {
                spots.Remove(key);
            }
        }

        [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.ItemRemove))]
        private static class ItemRemovePatch
        {
            // ロールオーブ(電池で使い切る)が壊れても、購入したロールオーブの数を減らさない
            private static bool Prefix(StatsManager __instance, string instanceName)
            {
                if (string.IsNullOrEmpty(instanceName) || ItemsBeforeOrbs.Contains(instanceName) ||
                    !SpawnedOrbs.Any(a => (object)a != null && a.instanceName == instanceName))
                {
                    return true;
                }
                __instance.item.Remove(instanceName);
                Net.ForgetBattery(instanceName);
                return false;
            }
        }

        // ================================================================
        // 報酬は出発した瞬間に渡す(ホスト)
        // ================================================================

        /// <summary>
        /// アップグレードは出発の瞬間(次のマップを読み込む前)に上げる。ショップでアップグレードを使ったのと同じ扱いになり、
        /// 次のマップの開始時に、全員の PC がアップグレードの数を読み直して HP・スタミナ・スプリント速度などに反映する。
        /// (マップが始まってから上げると、読み込みの遅い人の PC で、開始時の読み直しと二重に足されることがある)
        /// </summary>
        private static void GrantRewards()
        {
            foreach (var pair in NextRoles)
            {
                bool inherited = InheritedRoles.Contains(pair.Key);
                string roleLabel = RoleNames[(int)pair.Value] + (inherited ? "(引き継ぎ)" : "");
                if (inherited && !KeptRoleRewards.Value)
                {
                    Announcements[pair.Key] = $"{roleLabel}: 報酬なし";
                    continue;
                }
                // 報酬ダイス: クリティカルなら多く、ファンブルなら少なく、それ以外はいつもの数
                int count = Mathf.Clamp(RewardCount.Value, 1, 3);
                string dice = null;
                if (RewardDice.Value)
                {
                    int value = UnityEngine.Random.Range(1, 101);
                    if (value <= DiceCriticalMax.Value)
                    {
                        count = CriticalRewardCount.Value;
                        dice = $"ダイス{value} クリティカル！";
                    }
                    else if (value >= DiceFumbleMin.Value)
                    {
                        count = FumbleRewardCount.Value;
                        dice = $"ダイス{value} ファンブル！";
                    }
                    else
                    {
                        dice = $"ダイス{value}。";
                    }
                }
                var labels = new List<string>();
                foreach (var stat in ResolveStats(pair.Key, pair.Value, count))
                {
                    int amount = RewardAmount.Value;
                    int cap = stat.Cap != null ? stat.Cap() : 0;
                    if (cap > 0)
                    {
                        amount = Mathf.Min(amount, cap - Net.UpgradeLevel(pair.Key, stat.Key));
                    }
                    if (amount <= 0)
                    {
                        continue;
                    }
                    Net.Upgrade(pair.Key, stat.Key, amount);
                    labels.Add($"{stat.Label}+{amount}");
                }
                string line = $"{roleLabel}: " +
                              (labels.Count > 0 ? string.Join(", ", labels) : count > 0 ? "報酬なし(上限)" : "報酬なし");
                if (dice != null)
                {
                    line = dice + " " + line;
                }
                Announcements[pair.Key] = line;
                Plugin.Verbose($"報酬: {pair.Key} {line}");
            }
        }

        /// <summary>次のレベルが始まったら、その人の役職を有効にし、もらった報酬をしゃべらせる。</summary>
        private static IEnumerator StartLevelRoles(int generation)
        {
            // 全員のアバターができてから
            yield return new WaitForSeconds(2f);
            if (generation != Phase.Generation || !SemiFunc.RunIsLevel())
            {
                announceScheduled = false;
                yield break;
            }
            announceGeneration = generation;
            CurrentRoles.Clear();
            foreach (var pair in NextRoles)
            {
                CurrentRoles[pair.Key] = pair.Value;
                var player = SemiFunc.PlayerAvatarGetFromSteamID(pair.Key);
                if (player && Announcements.TryGetValue(pair.Key, out var line))
                {
                    Net.Speak(player, line);
                }
            }
            NextRoles.Clear();
            Announcements.Clear();
            announceScheduled = false;
        }

        /// <summary>
        /// 役職の候補からランダムに count 個を選ぶ。上限に達しているものは飛ばす(同じ役職の残りに差し替わる)。
        /// </summary>
        private static List<Stat> ResolveStats(string steamID, Role role, int count)
        {
            var pool = RoleStats[(int)role].ToList();
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            return pool.Where(s => !Capped(steamID, s)).Take(Mathf.Max(0, count)).ToList();
        }

        private static bool Capped(string steamID, Stat stat)
        {
            int cap = stat.Cap != null ? stat.Cap() : 0;
            return cap > 0 && Net.UpgradeLevel(steamID, stat.Key) >= cap;
        }

        // ================================================================
        // 出発はホストだけ
        // ================================================================

        [HarmonyPatch(typeof(TruckScreenText), nameof(TruckScreenText.HoldChat))]
        private static class HoldChatPatch
        {
            // モニターのチャットは、つかみ続けて文字が打ち終わると出発の合図になる。
            // 送るのはホストなので、ホスト以外がつかんでいるときは打ち終わる直前で巻き戻す
            private static void Prefix(TruckScreenText __instance)
            {
                if (!Enabled.Value || !HostOnlyDeparture.Value || !SemiFunc.IsMultiplayer() || !SemiFunc.IsMasterClient() || !SemiFunc.RunIsLobby())
                {
                    return;
                }
                if (!__instance.chatActive || !__instance.staticGrabObject || __instance.staticGrabObject.playerGrabbing.Count == 0)
                {
                    return;
                }
                var grabber = __instance.staticGrabObject.playerGrabbing[0];
                if (!grabber || grabber.playerAvatar == SemiFunc.PlayerAvatarLocal())
                {
                    return;
                }
                if (__instance.chatMessageTimer + 1.5f * Time.deltaTime * 2f < __instance.chatMessageTimerMax)
                {
                    return;
                }
                __instance.chatMessageTimer = 0f;
                __instance.chatCharacterIndex = 0;
                if (Time.unscaledTime - departureWarnTime > 4f)
                {
                    departureWarnTime = Time.unscaledTime;
                    Net.Speak(grabber.playerAvatar, "出発はホストだけ！");
                }
            }
        }
    }
}
