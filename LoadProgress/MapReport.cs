using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace LoadProgress
{
    /// <summary>
    /// マップ受信で止まったときの報告。
    /// - 参加者(ホスト以外)の PC: 読み込みがマップの段階で止まったら、自分の PC にあるマップのパーツの一覧と、
    ///   読み込みの状態をログに出し、ホストに送る(ホストも LoadProgress を入れていれば、ホストのログに出る)
    /// - ホストの PC: 届いた報告を、ホストのパーツと比べて「届いていないパーツ」「準備されていないパーツ」をログに出す
    /// </summary>
    internal class MapReport : MonoBehaviour
    {
        internal static MapReport Instance;

        internal const byte EventCode = 174;
        private const float FirstReportSeconds = 10f;
        private const float ReportInterval = 10f;
        private const int MaxReports = 6;
        private const int MaxNamesInLog = 8;

        /// <summary>ホストが受け取った報告の中身。</summary>
        internal sealed class Report
        {
            public int State;
            public bool AllPlayersReady;
            public bool AmountReceived;
            public int Spawned;
            public int Target;
            public int[] SetupViews;
            public int[] NotSetupViews;
        }

        private LevelGenerator watched;
        private float watchStart;
        private int reportsSent;
        private float nextReport;
        private int reportedState = -1;
        private bool eventSubscribed;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDisable()
        {
            if (eventSubscribed && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
            }
            eventSubscribed = false;
        }

        private void Update()
        {
            if (!eventSubscribed && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
                eventSubscribed = true;
            }
            if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient || GameManager.instance == null || !SemiFunc.IsMultiplayer())
            {
                watched = null;
                return;
            }
            float now = Time.unscaledTime;
            LevelGenerator lg = LevelGenerator.Instance;
            if (lg != watched)
            {
                watched = lg;
                watchStart = now;
                reportsSent = 0;
                reportedState = -1;
                nextReport = now + FirstReportSeconds;
            }
            if (lg == null || lg.Generated || !Plugin.LateJoinReportMap.Value)
            {
                return;
            }
            // マップの段階を抜けると、自分の「マップ受信完了」の合図(ModulesReadyRPC)が自分のリストにも入る
            if (lg.ModulesReadyPlayerList.Contains(PhotonNetwork.LocalPlayer))
            {
                return;
            }
            // 報告の回数は、読み込みの状態が変わるたびに数え直す(ホストの生成待ちで使い切らないように)
            if ((int)lg.State != reportedState)
            {
                reportedState = (int)lg.State;
                reportsSent = 0;
            }
            if (reportsSent >= MaxReports || now < nextReport)
            {
                return;
            }
            reportsSent++;
            nextReport = now + ReportInterval;
            SendReport(lg, now - watchStart);
        }

        // ---------------------------------------------------------------- 参加者の PC

        private void SendReport(LevelGenerator lg, float waited)
        {
            var setup = new List<int>();
            var notSetup = new List<int>();
            foreach (Module module in Object.FindObjectsByType<Module>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (module == null)
                {
                    continue;
                }
                PhotonView view = module.GetComponent<PhotonView>();
                int id = view != null ? view.ViewID : 0;
                if (id == 0)
                {
                    continue;
                }
                (module.SetupDone ? setup : notSetup).Add(id);
            }
            Plugin.Log.LogWarning($"[マップ受信] 読み込みがマップの段階で止まっています({waited:0} 秒、{reportsSent} 回目の報告)。" +
                                  $"状態 {lg.State}、全員そろった合図 {YesNo(lg.AllPlayersReady)}、パーツの数の合図 {YesNo(lg.moduleAmountReceived)}、" +
                                  $"準備できたパーツ {lg.ModulesSpawned} / 目標 {lg.ModulesSpawnedTarget}、" +
                                  $"この PC にあるパーツ {setup.Count + notSetup.Count}(準備済み {setup.Count}、準備前 {notSetup.Count}: {string.Join(",", notSetup)})");
            try
            {
                object[] content =
                {
                    (int)lg.State, lg.AllPlayersReady, lg.moduleAmountReceived, lg.ModulesSpawned, lg.ModulesSpawnedTarget,
                    setup.ToArray(), notSetup.ToArray(),
                };
                var options = new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient };
                PhotonNetwork.RaiseEvent(EventCode, content, options, SendOptions.SendReliable);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[マップ受信] ホストに報告できませんでした: {e.Message}");
            }
        }

        private static string YesNo(bool value) => value ? "受け取った" : "まだ";

        // ---------------------------------------------------------------- ホストの PC

        private void OnEvent(EventData data)
        {
            if (data.Code != EventCode || !PhotonNetwork.IsMasterClient || !(data.CustomData is object[] c) || c.Length < 7)
            {
                return;
            }
            Report report;
            try
            {
                report = new Report
                {
                    State = (int)c[0],
                    AllPlayersReady = (bool)c[1],
                    AmountReceived = (bool)c[2],
                    Spawned = (int)c[3],
                    Target = (int)c[4],
                    SetupViews = c[5] as int[] ?? new int[0],
                    NotSetupViews = c[6] as int[] ?? new int[0],
                };
            }
            catch (System.Exception)
            {
                return; // 形の違う報告(別の版など)は無視する
            }
            Player sender = PhotonNetwork.CurrentRoom?.GetPlayer(data.Sender);
            string name = sender != null ? sender.NickName : $"#{data.Sender}";
            LogComparison(name, report);
            LateJoinTracker.Instance?.MapReported(data.Sender, report);
        }

        /// <summary>参加者の報告をホストのパーツと比べてログに出す。</summary>
        private static void LogComparison(string name, Report report)
        {
            var hostModules = new Dictionary<int, Module>();
            foreach (Module module in Object.FindObjectsByType<Module>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                PhotonView view = module != null ? module.GetComponent<PhotonView>() : null;
                if (view != null && view.ViewID != 0 && module.SetupDone)
                {
                    hostModules[view.ViewID] = module;
                }
            }
            var joinerHas = new HashSet<int>(report.SetupViews.Concat(report.NotSetupViews));
            var missing = hostModules.Keys.Where(id => !joinerHas.Contains(id)).OrderBy(id => id).ToList();
            var notSetup = report.NotSetupViews.Where(hostModules.ContainsKey).OrderBy(id => id).ToList();
            string state = System.Enum.IsDefined(typeof(LevelGenerator.LevelState), report.State)
                ? ((LevelGenerator.LevelState)report.State).ToString() : report.State.ToString();
            string where = !report.AllPlayersReady
                ? "マップの前の「全員そろった」合図を待っています"
                : !report.AmountReceived ? "パーツの数の合図を待っています" : "パーツの準備を待っています";
            Plugin.Log.LogWarning($"[途中参加] {name} の PC からの報告: {where}(状態 {state})。" +
                                  $"準備できたパーツ {report.Spawned} / 目標 {report.Target}、" +
                                  $"届いたパーツ {joinerHas.Count}(ホストで準備済みのパーツは {hostModules.Count})。" +
                                  $"届いていない {missing.Count} 個{Names(missing, hostModules)}、" +
                                  $"届いたが準備されていない {notSetup.Count} 個{Names(notSetup, hostModules)}");
        }

        private static string Names(List<int> ids, Dictionary<int, Module> modules)
        {
            if (ids.Count == 0)
            {
                return "";
            }
            var names = ids.Take(MaxNamesInLog).Select(id => $"{id} {modules[id].gameObject.name.Replace("(Clone)", "").Trim()}");
            string more = ids.Count > MaxNamesInLog ? $" ほか {ids.Count - MaxNamesInLog} 個" : "";
            return $"({string.Join("、", names)}{more})";
        }
    }
}
