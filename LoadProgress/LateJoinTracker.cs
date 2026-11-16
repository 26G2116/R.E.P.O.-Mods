using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace LoadProgress
{
    /// <summary>途中参加の 7 段階。ビット番号として使う。</summary>
    internal static class JoinSteps
    {
        public const int Count = 7;

        public const int Joined = 0;    // 部屋に入った
        public const int Avatar = 1;    // 参加者のキャラがこちらの画面にできた
        public const int Modules = 2;   // マップのパーツの受信完了(ModulesReadyRPC)
        public const int Spawn = 3;     // スポーン完了(PlayerSpawnedRPC。ホストだけが受け取る)
        public const int Enemies = 4;   // 敵の受信完了(EnemyReadyRPC)
        public const int Generated = 5; // 参加者の画面で生成完了(見た目の同期が届いた)
        public const int Moving = 6;    // 動き始めた

        public const int AllMask = (1 << Count) - 1;

        public static readonly string[] Labels = { "参加", "キャラ生成", "マップ受信", "スポーン", "敵受信", "生成完了", "動き始め" };

        public static int Bit(int step) => 1 << step;

        public static bool Has(int mask, int step) => (mask & Bit(step)) != 0;

        /// <summary>段階は順番に進むので、ある段階が終わっていればそれより前も終わったとみなす。</summary>
        public static int FillLower(int mask)
        {
            for (int i = Count - 1; i >= 0; i--)
            {
                if (Has(mask, i))
                {
                    return mask | ((1 << (i + 1)) - 1);
                }
            }
            return mask;
        }

        /// <summary>まだ終わっていない最初の段階。全部終わっていれば Count。</summary>
        public static int Current(int mask)
        {
            for (int i = 0; i < Count; i++)
            {
                if (!Has(mask, i)) return i;
            }
            return Count;
        }
    }

    /// <summary>
    /// レベルの途中で入ってきた人(途中参加 MOD)の読み込みの進み具合を集める。
    /// 参加者が MOD を入れていなくても、参加者の PC がゲーム本来の仕組みで送ってくる合図から段階が分かる。
    /// 参加者も LoadProgress を入れていれば、その人の報告(NavMesh 構築など)も使う。
    /// </summary>
    internal class LateJoinTracker : MonoBehaviourPunCallbacks
    {
        internal static LateJoinTracker Instance;

        private const float PollInterval = 0.1f;
        /// <summary>この時間を過ぎたら、終わっていなくても表示をやめる。</summary>
        private const float GiveUpSeconds = 600f;
        /// <summary>動いたとみなす距離(生成完了の時点の位置から)。</summary>
        private const float MoveDistance = 0.75f;
        /// <summary>生成完了のあと、この秒数動かなくても完了とみなす。</summary>
        internal const float IdleDoneSeconds = 30f;

        internal sealed class Joiner
        {
            public int Actor;
            public string Name;
            public float JoinedAt;
            public int Mask;
            public float StepSince;
            public int WarnLevel;
            public float DoneAt = -1f;
            public float LeftAt = -1f;
            public bool Modded;
            /// <summary>参加者自身の LoadProgress の報告(10 段階のビット)。</summary>
            public int Reported;
            public bool GeneratedPosSet;
            public Vector3 GeneratedPos;
            public string Detail;
            /// <summary>マップの送り直しをした回数と、次にしてよい時刻。</summary>
            public int MapResends;
            public float NextMapResendAt;
            /// <summary>読み込みを先に進めたか(マップ受信が終わらないときの最後の手段)。</summary>
            public bool ForcePassed;
            /// <summary>参加者の LoadProgress からの、マップの段階の報告(MapReport)。なければ null。</summary>
            public MapReport.Report MapReport;
        }

        internal readonly List<Joiner> Joiners = new List<Joiner>();
        private readonly HashSet<int> cosmeticsFrom = new HashSet<int>();
        private LevelGenerator trackedLevel;
        private float nextPoll;

        private void Awake()
        {
            Instance = this;
        }

        // ---------------------------------------------------------------- 部屋の出入り

        public override void OnPlayerEnteredRoom(Player newPlayer)
        {
            if (newPlayer == null || newPlayer.IsLocal || !Plugin.LateJoinEnabled.Value || !InPlayableLevel())
            {
                return;
            }
            Joiners.RemoveAll(j => j.Actor == newPlayer.ActorNumber);
            cosmeticsFrom.Remove(newPlayer.ActorNumber);
            float now = Time.unscaledTime;
            Joiners.Add(new Joiner
            {
                Actor = newPlayer.ActorNumber,
                Name = string.IsNullOrEmpty(newPlayer.NickName) ? $"Player {newPlayer.ActorNumber}" : newPlayer.NickName,
                JoinedAt = now,
                StepSince = now,
                Mask = JoinSteps.Bit(JoinSteps.Joined),
            });
            trackedLevel = LevelGenerator.Instance;
            Log($"{newPlayer.NickName} が途中参加しました。読み込みの進み具合を表示します");
        }

        public override void OnPlayerLeftRoom(Player otherPlayer)
        {
            if (otherPlayer == null)
            {
                return;
            }
            foreach (var joiner in Joiners)
            {
                if (joiner.Actor == otherPlayer.ActorNumber && joiner.DoneAt < 0f && joiner.LeftAt < 0f)
                {
                    MarkLeft(joiner);
                }
            }
        }

        public override void OnLeftRoom()
        {
            Joiners.Clear();
            cosmeticsFrom.Clear();
        }

        /// <summary>レベル(ショップ・トラックを含む)を遊んでいる最中か。メニューや読み込み中の途中参加は数えない。</summary>
        private static bool InPlayableLevel()
        {
            if (!PhotonNetwork.InRoom || GameManager.instance == null || !SemiFunc.IsMultiplayer() || RunManager.instance == null)
            {
                return false;
            }
            var lg = LevelGenerator.Instance;
            return lg != null && lg.Generated && !SemiFunc.MenuLevel();
        }

        // ---------------------------------------------------------------- 合図

        /// <summary>参加者の見た目の同期が届いた(参加者は自分の画面で生成が終わってから送る)。</summary>
        internal void CosmeticsFrom(Player sender)
        {
            if (sender != null && !sender.IsLocal)
            {
                cosmeticsFrom.Add(sender.ActorNumber);
            }
        }

        /// <summary>
        /// 参加者自身の LoadProgress の報告(ProgressTracker が受け取って渡す)。
        /// observed は本人が自分の PC で直接確かめた段階だけ(全員共通の合図や、前の段階を埋めた推定を含まない)。
        /// 0.1.0 の参加者は送ってこないので -1(MOD を入れていることだけ分かる)。
        /// </summary>
        internal void Report(int actor, int observed)
        {
            foreach (var joiner in Joiners)
            {
                if (joiner.Actor == actor)
                {
                    joiner.Modded = true;
                    if (observed >= 0)
                    {
                        joiner.Reported |= observed & Steps.AllMask;
                    }
                }
            }
        }

        /// <summary>参加者の PC からの、マップの段階の報告(MapReport が受け取って渡す)。</summary>
        internal void MapReported(int actor, MapReport.Report report)
        {
            foreach (var joiner in Joiners)
            {
                if (joiner.Actor == actor)
                {
                    joiner.Modded = true;
                    joiner.MapReport = report;
                }
            }
        }

        // ---------------------------------------------------------------- 毎フレーム

        private void Update()
        {
            if (Joiners.Count == 0)
            {
                return;
            }
            var lg = LevelGenerator.Instance;
            if (!PhotonNetwork.InRoom || lg != trackedLevel)
            {
                // 部屋を出た・レベルが変わった: 途中参加の人も、ほかの人と同じようにレベルの読み込みから入り直す
                Joiners.Clear();
                cosmeticsFrom.Clear();
                return;
            }
            float now = Time.unscaledTime;
            if (now >= nextPoll)
            {
                nextPoll = now + PollInterval;
                foreach (var joiner in Joiners)
                {
                    if (joiner.DoneAt < 0f && joiner.LeftAt < 0f)
                    {
                        Poll(joiner, lg, now);
                    }
                }
            }
            float hide = Mathf.Max(0f, Plugin.LateJoinHideSeconds.Value);
            Joiners.RemoveAll(j => (j.DoneAt >= 0f && now - j.DoneAt > hide) || (j.LeftAt >= 0f && now - j.LeftAt > hide)
                                   || now - j.JoinedAt > GiveUpSeconds);
        }

        private void Poll(Joiner joiner, LevelGenerator lg, float now)
        {
            var room = PhotonNetwork.CurrentRoom;
            Player player = room?.GetPlayer(joiner.Actor);
            if (player == null)
            {
                MarkLeft(joiner); // 抜けた合図を受け取れなかったときの保険
                return;
            }
            bool isMaster = PhotonNetwork.IsMasterClient;
            PlayerAvatar avatar = FindAvatar(joiner.Actor);

            int m = joiner.Mask | JoinSteps.Bit(JoinSteps.Joined);
            if (avatar != null || (isMaster && NetworkManager.instance != null && NetworkManager.instance.instantiatedPlayerAvatarsList.Contains(player)))
            {
                m |= JoinSteps.Bit(JoinSteps.Avatar);
            }
            if (lg.ModulesReadyPlayerList.Contains(player)) m |= JoinSteps.Bit(JoinSteps.Modules);
            if (isMaster && lg.SpawnedReadyPlayerList.Contains(player)) m |= JoinSteps.Bit(JoinSteps.Spawn);
            if (lg.EnemyReadyPlayerList.Contains(player)) m |= JoinSteps.Bit(JoinSteps.Enemies);
            if (cosmeticsFrom.Contains(joiner.Actor)) m |= JoinSteps.Bit(JoinSteps.Generated);

            // 参加者自身の報告
            int r = joiner.Reported;
            if (Steps.Has(r, Steps.Avatar)) m |= JoinSteps.Bit(JoinSteps.Avatar);
            if (Steps.Has(r, Steps.Modules)) m |= JoinSteps.Bit(JoinSteps.Modules);
            if (Steps.Has(r, Steps.Spawn)) m |= JoinSteps.Bit(JoinSteps.Spawn);
            if (Steps.Has(r, Steps.Enemies)) m |= JoinSteps.Bit(JoinSteps.Enemies);
            if (Steps.Has(r, Steps.Generated)) m |= JoinSteps.Bit(JoinSteps.Generated);

            // 敵のいない所(ショップ・トラック)では敵の受信の合図が来ないので、スポーンが済んだら飛ばす
            if (!SemiFunc.RunIsLevel() && JoinSteps.Has(m, JoinSteps.Spawn))
            {
                m |= JoinSteps.Bit(JoinSteps.Enemies);
            }
            // 前の段階を埋める。ただし敵の受信の合図はスポーンより先に届くことがあるので、敵の受信からは埋めない
            // (生成完了からは、敵の受信も含めて全部埋める)
            int enemies = m & JoinSteps.Bit(JoinSteps.Enemies);
            m = JoinSteps.FillLower(m & ~JoinSteps.Bit(JoinSteps.Enemies)) | enemies;

            // 動き始めた: 生成完了の時点の位置から動いた
            if (JoinSteps.Has(m, JoinSteps.Generated) && avatar != null)
            {
                Vector3 position = avatar.transform.position;
                if (!joiner.GeneratedPosSet)
                {
                    joiner.GeneratedPosSet = true;
                    joiner.GeneratedPos = position;
                }
                else
                {
                    Vector3 delta = position - joiner.GeneratedPos;
                    delta.y = 0f;
                    if (delta.magnitude > MoveDistance)
                    {
                        m |= JoinSteps.Bit(JoinSteps.Moving);
                    }
                }
            }

            int added = m & ~joiner.Mask;
            if (added != 0)
            {
                for (int i = 0; i < JoinSteps.Count; i++)
                {
                    if (JoinSteps.Has(added, i) && i != JoinSteps.Joined)
                    {
                        Log($"{joiner.Name}: {i + 1} {JoinSteps.Labels[i]} 完了 ({now - joiner.JoinedAt:0.0}s)");
                    }
                }
                joiner.Mask = m;
                joiner.StepSince = now;
                joiner.WarnLevel = 0;
            }
            joiner.Detail = Detail(joiner);

            if (JoinSteps.Has(m, JoinSteps.Moving))
            {
                joiner.DoneAt = now;
                Log($"{joiner.Name}: 読み込み完了 (所要 {now - joiner.JoinedAt:0.0}s)");
                return;
            }
            float stalled = now - joiner.StepSince;
            int current = JoinSteps.Current(m);
            if (current == JoinSteps.Moving)
            {
                // 読み込みは終わっていて、動くのを待っているだけ(止まっているとは言わない)
                if (stalled >= IdleDoneSeconds)
                {
                    joiner.DoneAt = now;
                    Log($"{joiner.Name}: 読み込み完了(まだ動いていません。生成完了まで {joiner.StepSince - joiner.JoinedAt:0.0}s)");
                }
                return;
            }

            // マップ受信で止まっている: ホストが足りないかもしれないマップの情報を送り直す
            if (current == JoinSteps.Modules && isMaster && Plugin.LateJoinMapResend.Value && JoinSteps.Has(m, JoinSteps.Avatar)
                && now - joiner.JoinedAt >= Plugin.LateJoinMapResendSeconds.Value && joiner.MapResends < MaxMapResends
                && now >= joiner.NextMapResendAt)
            {
                ResendMap(joiner, player, lg, joiner.MapResends == 0);
                joiner.MapResends++;
                joiner.NextMapResendAt = now + MapResendInterval;
            }
            // 送り直しても終わらない: 最後の手段として、読み込みを先に進めさせる
            else if (current == JoinSteps.Modules && isMaster && Plugin.LateJoinMapForcePass.Value && JoinSteps.Has(m, JoinSteps.Avatar)
                     && !joiner.ForcePassed && (!Plugin.LateJoinMapResend.Value || joiner.MapResends >= MaxMapResends)
                     && now >= joiner.NextMapResendAt // 最後の送り直しのあと、効くかどうか待ってから
                     && now - joiner.JoinedAt >= Plugin.LateJoinMapForcePassSeconds.Value)
            {
                joiner.ForcePassed = true;
                ForcePass(joiner, player, lg);
            }

            // 止まっている段階を記録する(どこで止まったかを、あとでログから分かるように)
            if (joiner.WarnLevel < 2 && stalled >= Plugin.LateJoinStuckSeconds.Value)
            {
                joiner.WarnLevel = 2;
                Plugin.Log.LogWarning($"[途中参加] {joiner.Name}: 「{current + 1} {JoinSteps.Labels[current]}」で {stalled:0} 秒止まっています(読み込みが終わらない可能性があります)");
            }
            else if (joiner.WarnLevel < 1 && stalled >= Plugin.LateJoinWarnSeconds.Value)
            {
                joiner.WarnLevel = 1;
                Plugin.Log.LogWarning($"[途中参加] {joiner.Name}: 「{current + 1} {JoinSteps.Labels[current]}」で {stalled:0} 秒止まっています");
            }
        }

        private const int MaxMapResends = 3;
        private const float MapResendInterval = 6f;

        /// <summary>
        /// 参加者の画面のマップの段階を、足りないパーツを待たずに抜けさせる(ホストだけ)。
        /// - 「全員そろった」合図(AllPlayerSpawnedRPC)を送る(これがないと、マップの段階に入れない)
        /// - パーツの数を、参加者がすでに準備できた数(報告がなければ 0)にする。ゲームは「準備できた数 >= パーツの数」で先に進む
        /// 届いていないパーツは、その人の画面では欠けたままになる。
        /// </summary>
        private void ForcePass(Joiner joiner, Player player, LevelGenerator lg)
        {
            int amount = 0;
            if (joiner.MapReport != null)
            {
                amount = Mathf.Clamp(joiner.MapReport.Spawned, 0, lg.ModulesSpawnedTarget);
            }
            try
            {
                SendAllPlayersReady(player);
                lg.PhotonView.RPC("ModuleAmountRPC", player, amount);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[途中参加] 読み込みを先に進められませんでした: {e.Message}");
                return;
            }
            string report = joiner.MapReport != null
                ? $"本人の報告では、準備できたパーツ {joiner.MapReport.Spawned} / {joiner.MapReport.Target}"
                : "本人の報告はありません(LoadProgress なし)";
            Plugin.Log.LogWarning($"[途中参加] {joiner.Name}: マップ受信が終わらないので、読み込みを先に進めさせました" +
                                  $"(パーツの数を {amount} にしました。{report})。この人の画面では、マップの一部が欠けているかもしれません");
        }

        /// <summary>「全員そろった」合図を、その人にだけ送る(受け取った側は、マップの段階に入れるようになるだけ。何度送っても同じ)。</summary>
        private static void SendAllPlayersReady(Player player)
        {
            var network = NetworkManager.instance;
            if (network != null && network.photonView != null)
            {
                network.photonView.RPC("AllPlayerSpawnedRPC", player);
            }
        }

        /// <summary>
        /// マップ受信で止まった参加者に、マップの情報を送り直す(ホストだけ)。
        ///
        /// 参加者の画面は、マップのパーツ(Module)が「全部で何個か」の合図(ModuleAmountRPC、部屋の記録に残る)を受け取り、
        /// その数だけパーツのつながりの合図(ModuleConnectionSetRPC。記録に残らない)を受け取るまで先に進まない。
        /// つながりの合図は途中参加 MOD(LateJoinNow)が送り直しているが、無効になっているパーツは数えていない。
        /// - 無効になっているパーツのつながりの合図を送る(最初の 1 回だけ。同じパーツに 2 回送ると向きの処理が二重になるので、
        ///   途中参加 MOD が送ったパーツには送らない)
        /// - 「全部で何個か」を送り直す。ホストで送れるパーツが目標の数より少ない(パーツが消えている)なら、送れる数にする
        /// どの場合も、パーツの数をログに出す(原因を調べるため)。
        /// </summary>
        private void ResendMap(Joiner joiner, Player player, LevelGenerator lg, bool first)
        {
            if (lg == null || lg.PhotonView == null)
            {
                return;
            }
            int total = 0, inactive = 0, notSetup = 0, noView = 0, mega = 0, settable = 0, sent = 0;
            foreach (Module module in Object.FindObjectsByType<Module>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (module == null)
                {
                    continue;
                }
                total++;
                bool active = module.gameObject.activeInHierarchy;
                if (!active) inactive++;
                bool isMega = module.type == Module.Type.Mega;
                if (isMega) mega++;
                PhotonView view = module.GetComponent<PhotonView>();
                if (view == null || view.ViewID == 0)
                {
                    noView++;
                    continue;
                }
                if (!module.SetupDone)
                {
                    notSetup++;
                    continue;
                }
                settable++;
                if (!active && first)
                {
                    try
                    {
                        if (isMega)
                        {
                            view.RPC("ModuleConnectionSetMegaRPC", player, module.ConnectingTopRight, module.ConnectingTopLeft,
                                module.ConnectingRightTop, module.ConnectingRightBot, module.ConnectingBottomRight, module.ConnectingBottomLeft,
                                module.ConnectingLeftTop, module.ConnectingLeftBot, module.First);
                        }
                        else
                        {
                            view.RPC("ModuleConnectionSetRPC", player, module.ConnectingTop, module.ConnectingBottom,
                                module.ConnectingRight, module.ConnectingLeft, module.First);
                        }
                        sent++;
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"[途中参加] マップのパーツを送れませんでした: {e.Message}");
                    }
                }
            }
            int target = lg.ModulesSpawnedTarget;
            int amount = settable > 0 && settable < target ? settable : target;
            try
            {
                SendAllPlayersReady(player); // マップの段階の手前で止まっている場合のため
                lg.PhotonView.RPC("ModuleAmountRPC", player, amount);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[途中参加] パーツの数を送れませんでした: {e.Message}");
            }
            Plugin.Log.LogWarning($"[途中参加] {joiner.Name}: マップ受信で止まっているので送り直しました({joiner.MapResends + 1} 回目)。" +
                                  $"ホストのパーツ: 見つかった {total}、目標 {target}、無効 {inactive}、準備前 {notSetup}、ID なし {noView}、大きいパーツ {mega}。" +
                                  $"無効のパーツ {sent} 個のつながりと、パーツの数 {amount} を送りました");
        }

        private static string Detail(Joiner joiner)
        {
            if (!joiner.Modded)
            {
                return null;
            }
            // 参加者が MOD を入れていれば、マップ受信とスポーンの間(貴重品・NavMesh)も分かる
            if (JoinSteps.Current(joiner.Mask) == JoinSteps.Spawn)
            {
                if (Steps.Has(joiner.Reported, Steps.NavMesh)) return "NavMesh完了";
                if (Steps.Has(joiner.Reported, Steps.Items)) return "NavMesh構築中";
                return "貴重品受信中";
            }
            return null;
        }

        private void MarkLeft(Joiner joiner)
        {
            joiner.LeftAt = Time.unscaledTime;
            int current = JoinSteps.Current(joiner.Mask);
            string where = current < JoinSteps.Count ? $"「{current + 1} {JoinSteps.Labels[current]}」の途中" : "最後まで済んだあと";
            Log($"{joiner.Name}: 読み込みが終わる前に抜けました({where}、参加から {joiner.LeftAt - joiner.JoinedAt:0.0}s)");
        }

        private static PlayerAvatar FindAvatar(int actor)
        {
            GameDirector gd = GameDirector.instance;
            if (gd == null || gd.PlayerList == null) return null;
            foreach (PlayerAvatar avatar in gd.PlayerList)
            {
                if (avatar != null && avatar.photonView != null && avatar.photonView.Owner != null
                    && avatar.photonView.Owner.ActorNumber == actor)
                {
                    return avatar;
                }
            }
            return null;
        }

        private static void Log(string message)
        {
            if (Plugin.LogTransitions.Value)
            {
                Plugin.Log.LogInfo("[途中参加] " + message);
            }
        }

        // ---------------------------------------------------------------- 見た目の同期(Harmony)

        [HarmonyPatch(typeof(PlayerCosmetics), nameof(PlayerCosmetics.SetupColorsRPC))]
        private static class ColorsPatch
        {
            private static void Postfix(PhotonMessageInfo _info) => Instance?.CosmeticsFrom(_info.Sender);
        }

        [HarmonyPatch(typeof(PlayerCosmetics), nameof(PlayerCosmetics.SetupColorsAllRPC))]
        private static class ColorsAllPatch
        {
            private static void Postfix(PhotonMessageInfo _info) => Instance?.CosmeticsFrom(_info.Sender);
        }

        [HarmonyPatch(typeof(PlayerCosmetics), nameof(PlayerCosmetics.SetupCosmeticsRPC))]
        private static class CosmeticsPatch
        {
            private static void Postfix(PhotonMessageInfo _info) => Instance?.CosmeticsFrom(_info.Sender);
        }
    }
}
