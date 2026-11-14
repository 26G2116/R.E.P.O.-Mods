using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AIMonsters
{
    /// <summary>AI に動かさせる敵の種類ごとの処理。</summary>
    internal interface IEnemyDriver
    {
        /// <summary>AI に伝える名前(ゲーム内の英名)。</summary>
        string TypeName { get; }

        /// <summary>AI への説明(この敵の得意・不得意、各行動でどう動くか)。</summary>
        string Rules { get; }

        /// <summary>設定で有効か。</summary>
        bool Enabled { get; }

        bool Matches(Enemy enemy);

        /// <summary>この敵がその人を見えているか(AI に任せ始める・やめる判定と、AI に伝える情報)。</summary>
        bool Sees(Enemy enemy, PlayerAvatar player);

        /// <summary>この敵だけの情報を状況に足す(今の状態など)。</summary>
        void Describe(Enemy enemy, JObject info);

        /// <summary>新しい指示を受け取った(または指示がなくなった: order = null)。</summary>
        void Apply(Enemy enemy, Order order);

        /// <summary>AI が動かしている間、毎フレーム(ホスト)。</summary>
        void Tick(Enemy enemy, Order order);

        /// <summary>AI が動かすのをやめた(普段の動きに戻す)。敵がもう消えていることもある。</summary>
        void Release(Enemy enemy);
    }

    /// <summary>AI からの指示。</summary>
    internal sealed class Order
    {
        internal string Action;
        internal PlayerAvatar Player;
        internal ValuableObject Valuable;
        internal string Why;
        internal float Received;

        public override string ToString()
        {
            string target = Player ? $" → {Player.playerName}" : Valuable ? $" → {Valuable.name}" : "";
            return $"{Action}{target}({Why})";
        }
    }

    /// <summary>
    /// 敵の AI の司令塔(ホストだけ)。
    /// - 対象の敵が、だれか 1 人を「見えている」(EnemyVision.VisionsTriggered が 1 以上)ようになったら AI に任せ始める
    /// - 誰も見えない状態が設定の秒数続いたら、普段の動きに戻す
    /// - 任せている敵を全部まとめて、最短 2 秒ごとに 1 回 API を呼ぶ(返事が来るまでは次を送らない)
    /// - 距離・見えているか・貴重品のそばに誰がいるかは、ここで計算して渡す
    /// </summary>
    internal static class Director
    {
        private const string Section = "1. 敵の AI";

        internal static ConfigEntry<bool> Enabled;
        private static ConfigEntry<float> Interval;
        private static ConfigEntry<float> ReleaseSeconds;
        private static ConfigEntry<float> ValuableRange;
        private static ConfigEntry<int> ValuableCount;
        private static ConfigEntry<float> PlayerRange;
        private static ConfigEntry<float> OrderLifetime;

        /// <summary>動かせる敵の種類(敵ごとのファイルで登録する)。</summary>
        internal static readonly List<IEnemyDriver> Drivers = new List<IEnemyDriver>();

        private sealed class Controlled
        {
            internal Enemy Enemy;
            internal IEnemyDriver Driver;
            internal int Id;
            internal float LastSeen;
            internal Order Order;
        }

        private static readonly List<Controlled> Controlleds = new List<Controlled>();
        private static readonly AzureChannel Channel = new AzureChannel("敵");
        private static LevelGenerator level;
        private static int generation;
        private static int nextId = 1;
        private static float scanTimer;
        private static float nextRequest;
        private static float nextErrorLog;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "有効", true, "対象の敵がプレイヤーを見つけ始めたら、AI に動かさせる");
            Interval = cfg.Bind(Section, "呼び出しの最短間隔(秒)", 2f,
                new ConfigDescription("API を呼ぶ間隔の最短。返事が遅いときは、返事が来てから次を送る", new AcceptableValueRange<float>(0.5f, 30f)));
            ReleaseSeconds = cfg.Bind(Section, "見失ってから戻すまでの秒数", 10f,
                new ConfigDescription("誰も見えない状態がこの秒数続いたら、AI をやめて普段の動きに戻す", new AcceptableValueRange<float>(1f, 120f)));
            PlayerRange = cfg.Bind(Section, "伝えるプレイヤーの範囲(m)", 40f,
                new ConfigDescription("敵からこの距離までのプレイヤーを AI に伝える", new AcceptableValueRange<float>(5f, 200f)));
            ValuableRange = cfg.Bind(Section, "伝える貴重品の範囲(m)", 15f,
                new ConfigDescription("敵からこの距離までの貴重品を AI に伝える", new AcceptableValueRange<float>(0f, 60f)));
            ValuableCount = cfg.Bind(Section, "伝える貴重品の最大数", 8,
                new ConfigDescription("値段の高い順にこの数まで伝える(多いほど送るトークンが増える)", new AcceptableValueRange<int>(0, 30)));
            OrderLifetime = cfg.Bind(Section, "指示の有効時間(秒)", 6f,
                new ConfigDescription("新しい指示が来ないまま、この秒数たったら指示をやめて普段の動きにする(API が止まったとき用)",
                    new AcceptableValueRange<float>(1f, 60f)));
        }

        private static bool Active =>
            Plugin.Enabled.Value && Enabled.Value && SemiFunc.IsMasterClientOrSingleplayer() && SemiFunc.RunIsLevel()
            && LevelGenerator.Instance && LevelGenerator.Instance.Generated;

        // ================================================================
        // 毎フレーム
        // ================================================================

        internal static void Tick()
        {
            if (level != LevelGenerator.Instance)
            {
                // レベルが変わった(前のレベルの敵はもういない)
                level = LevelGenerator.Instance;
                ReleaseAll();
                generation++;
                nextId = 1;
            }
            if (!Active || Drivers.Count == 0)
            {
                if (Controlleds.Count > 0)
                {
                    ReleaseAll();
                }
                return;
            }

            scanTimer -= Time.deltaTime;
            if (scanTimer <= 0f)
            {
                scanTimer = 0.25f;
                Scan();
            }

            foreach (var c in Controlleds.ToList())
            {
                if (c.Order != null && Time.time - c.Order.Received > OrderLifetime.Value)
                {
                    Plugin.Verbose($"{Label(c)}: 新しい指示が来ないので、指示をやめます");
                    SetOrder(c, null);
                }
                Guard(c, () => c.Driver.Tick(c.Enemy, c.Order));
            }

            if (Controlleds.Count > 0 && !Channel.Busy && Time.time >= nextRequest)
            {
                Request();
            }
        }

        /// <summary>任せ始める・やめる敵を決める。</summary>
        private static void Scan()
        {
            // やめる: 消えた・倒された・設定で外された・しばらく誰も見えない
            foreach (var c in Controlleds.ToList())
            {
                bool gone = !c.Enemy || !c.Enemy.EnemyParent || !c.Enemy.EnemyParent.Spawned
                            || c.Enemy.CurrentState == EnemyState.Despawn || Dead(c.Enemy) || !c.Driver.Enabled;
                if (!gone && SeesAnyone(c.Driver, c.Enemy))
                {
                    c.LastSeen = Time.time;
                }
                if (gone || Time.time - c.LastSeen > ReleaseSeconds.Value)
                {
                    Plugin.Verbose($"{Label(c)}: {(gone ? "いなくなった" : "見失った")}ので、AI をやめます");
                    Release(c);
                }
            }

            // 始める: 対象の敵が、だれかを見えている
            var director = EnemyDirector.instance;
            if (!director)
            {
                return;
            }
            foreach (var parent in director.enemiesSpawned)
            {
                var enemy = parent ? parent.Enemy : null;
                if (!enemy || !parent.Spawned || enemy.CurrentState == EnemyState.Despawn || Dead(enemy)
                    || Controlleds.Any(c => c.Enemy == enemy))
                {
                    continue;
                }
                var driver = Drivers.FirstOrDefault(d => d.Enabled && d.Matches(enemy));
                if (driver == null || !SeesAnyone(driver, enemy))
                {
                    continue;
                }
                var c = new Controlled { Enemy = enemy, Driver = driver, Id = nextId++, LastSeen = Time.time };
                Controlleds.Add(c);
                Plugin.Verbose($"{Label(c)}: プレイヤーを見つけたので、AI に任せます");
                // 見つけた直後はすぐ判断させる
                nextRequest = Mathf.Min(nextRequest, Time.time);
            }
        }

        private static bool SeesAnyone(IEnemyDriver driver, Enemy enemy) =>
            SemiFunc.PlayerGetList().Any(p => p && !p.isDisabled && driver.Sees(enemy, p));

        private static bool Dead(Enemy enemy) => enemy.HasHealth && enemy.Health && enemy.Health.healthCurrent <= 0;

        private static void Release(Controlled c)
        {
            Controlleds.Remove(c);
            Mark(c.Enemy, false);
            Guard(c, () => c.Driver.Release(c.Enemy));
        }

        private static void ReleaseAll()
        {
            foreach (var c in Controlleds.ToList())
            {
                Release(c);
            }
            Controlleds.Clear();
        }

        private static void SetOrder(Controlled c, Order order)
        {
            c.Order = order;
            // 目印は、AI の指示で動かしている間だけ(返事待ち・期限切れ・roam の間は、ほかの MOD の強化もそのまま動く)
            Mark(c.Enemy, order != null && order.Action != "roam");
            Guard(c, () => c.Driver.Apply(c.Enemy, order));
        }

        /// <summary>敵ごとの処理の例外で、ほかの敵や毎フレームの処理を止めない。</summary>
        private static void Guard(Controlled c, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (Time.realtimeSinceStartup >= nextErrorLog)
                {
                    nextErrorLog = Time.realtimeSinceStartup + 5f;
                    Plugin.Log.LogError($"{Label(c)} の処理でエラー: {e}");
                }
            }
        }

        private static string Label(Controlled c) => $"{c.Driver.TypeName}#{c.Id}";

        /// <summary>AI が動かしている敵なら、今の指示(ない場合は null)を返す。敵ごとのパッチから使う。</summary>
        internal static bool TryGetOrder(Enemy enemy, out Order order)
        {
            order = null;
            if (!enemy)
            {
                return false;
            }
            foreach (var c in Controlleds)
            {
                if (c.Enemy == enemy)
                {
                    order = c.Order;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// AI が動かしている敵に目印(AIMonstersControlled)をつける・外す。
        /// ほかの MOD(MonsterTweaks)は、型を参照せずに GetComponent("AIMonstersControlled") で調べられる。
        /// </summary>
        private static void Mark(Enemy enemy, bool on)
        {
            if (!enemy)
            {
                return;
            }
            var marker = enemy.GetComponent<AIMonstersControlled>();
            if (on && !marker)
            {
                enemy.gameObject.AddComponent<AIMonstersControlled>();
            }
            else if (!on && marker)
            {
                Object.Destroy(marker);
            }
        }

        // ================================================================
        // API
        // ================================================================

        /// <summary>1 回の呼び出しで使った ID と物の対応(返事を読むときに使う)。</summary>
        private sealed class Snapshot
        {
            internal int Generation;
            internal readonly Dictionary<int, Controlled> Enemies = new Dictionary<int, Controlled>();
            internal readonly Dictionary<int, PlayerAvatar> Players = new Dictionary<int, PlayerAvatar>();
            internal readonly Dictionary<int, ValuableObject> Valuables = new Dictionary<int, ValuableObject>();
        }

        private static void Request()
        {
            var snapshot = new Snapshot { Generation = generation };
            string state = BuildState(snapshot);
            string system = BuildSystemPrompt(snapshot.Enemies.Values.Select(c => c.Driver).Distinct());
            if (Channel.Send(system, state, reply => OnReply(snapshot, reply)))
            {
                nextRequest = Time.time + Interval.Value;
                Plugin.Verbose($"AI(敵)に送信: {state}");
            }
            else
            {
                // 環境変数がないなど。すぐには送り直さない
                nextRequest = Time.time + 5f;
            }
        }

        private static string BuildSystemPrompt(IEnumerable<IEnemyDriver> drivers)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("You direct monsters in R.E.P.O., a co-op horror game. Players (robots) carry valuables to an extraction point;");
            text.AppendLine("damaged valuables lose money, which hurts the whole team. Each monster follows your order until the next one (about 2 s).");
            text.AppendLine("The game moves and attacks automatically; you choose the plan. Distances are in meters and already computed for you.");
            text.AppendLine("Actions:");
            text.AppendLine("- chase: go straight for the player.");
            text.AppendLine("- ambush: approach the player from where they are not looking, then strike.");
            text.AppendLine("- flank: circle around to the player's side or back without striking yet.");
            text.AppendLine("- attack_player: attack the player now.");
            text.AppendLine("- attack_valuable: attack the valuable now (damages it and anyone near it).");
            text.AppendLine("- retreat: move away from players.");
            text.AppendLine("- wait: stay still and watch.");
            text.AppendLine("- roam: act normally (let the game decide).");
            text.AppendLine("Monsters:");
            foreach (var driver in drivers)
            {
                text.AppendLine($"- {driver.TypeName}: {PromptFiles.Read(driver.TypeName) ?? driver.Rules}");
            }
            string common = PromptFiles.Read("Common");
            if (common != null)
            {
                text.AppendLine("Extra instructions:");
                text.AppendLine(common);
            }
            text.AppendLine("Give exactly one order per monster. \"player\" is a player id or null; \"valuable\" is a valuable id or null.");
            text.AppendLine("Reply only with JSON: {\"orders\":[{\"enemy\":1,\"action\":\"chase\",\"player\":2,\"valuable\":null,\"why\":\"max 8 words\"}]}");
            return text.ToString();
        }

        private static string BuildState(Snapshot snapshot)
        {
            // プレイヤー(生きている人だけ。ID は今回の呼び出しの中だけの番号)
            var players = SemiFunc.PlayerGetList().Where(p => p && !p.isDisabled).ToList();
            var holding = GrabbedObjects();
            var playerIds = new Dictionary<PlayerAvatar, int>();
            var playerArray = new JArray();
            foreach (var player in players)
            {
                int id = playerIds.Count + 1;
                playerIds[player] = id;
                snapshot.Players[id] = player;
                holding.TryGetValue(player, out var held);
                playerArray.Add(new JObject
                {
                    ["id"] = id,
                    ["hp"] = player.playerHealth ? player.playerHealth.health : 0,
                    ["crouching"] = player.isCrouching || player.isCrawling,
                    ["holding"] = HeldLabel(held),
                });
            }

            // 敵
            var enemyArray = new JArray();
            foreach (var c in Controlleds)
            {
                snapshot.Enemies[c.Id] = c;
                Vector3 position = EnemyPosition(c.Enemy);
                var near = new JArray();
                foreach (var player in players)
                {
                    float distance = Vector3.Distance(position, player.transform.position);
                    if (distance > PlayerRange.Value)
                    {
                        continue;
                    }
                    near.Add(new JObject
                    {
                        ["player"] = playerIds[player],
                        ["dist"] = Round(distance),
                        ["you_see_them"] = c.Driver.Sees(c.Enemy, player),
                        ["they_see_you"] = SeenBy(c.Enemy, player),
                    });
                }
                var info = new JObject
                {
                    ["id"] = c.Id,
                    ["type"] = c.Driver.TypeName,
                    ["hp"] = HealthRatio(c.Enemy),
                    ["current_order"] = c.Order?.Action,
                    ["players"] = near,
                };
                Guard(c, () => c.Driver.Describe(c.Enemy, info));
                enemyArray.Add(info);
            }

            // 貴重品(どれかの敵の近くにあるもの。値段の高い順)
            var valuableArray = new JArray();
            if (ValuableCount.Value > 0 && ValuableRange.Value > 0f)
            {
                var nearby = new List<(ValuableObject valuable, float value)>();
                foreach (var valuable in Object.FindObjectsByType<ValuableObject>(FindObjectsSortMode.None))
                {
                    if (!valuable || valuable.dollarValueCurrent <= 0f)
                    {
                        continue;
                    }
                    Vector3 point = ValuablePosition(valuable);
                    if (Controlleds.Any(c => Vector3.Distance(EnemyPosition(c.Enemy), point) <= ValuableRange.Value))
                    {
                        nearby.Add((valuable, valuable.dollarValueCurrent));
                    }
                }
                foreach (var (valuable, value) in nearby.OrderByDescending(v => v.value).Take(ValuableCount.Value))
                {
                    int id = snapshot.Valuables.Count + 1;
                    snapshot.Valuables[id] = valuable;
                    Vector3 point = ValuablePosition(valuable);
                    var distances = new JObject();
                    foreach (var c in Controlleds)
                    {
                        distances[c.Id.ToString()] = Round(Vector3.Distance(EnemyPosition(c.Enemy), point));
                    }
                    var closest = players.OrderBy(p => Vector3.Distance(p.transform.position, point)).FirstOrDefault();
                    var holder = holding.FirstOrDefault(pair => pair.Value && pair.Value.GetComponent<ValuableObject>() == valuable).Key;
                    valuableArray.Add(new JObject
                    {
                        ["id"] = id,
                        ["value"] = Mathf.RoundToInt(value),
                        ["dist_to_enemy"] = distances,
                        ["nearest_player"] = closest ? playerIds[closest] : (JToken)JValue.CreateNull(),
                        ["nearest_player_dist"] = closest ? Round(Vector3.Distance(closest.transform.position, point)) : (JToken)JValue.CreateNull(),
                        ["held_by"] = holder ? playerIds[holder] : (JToken)JValue.CreateNull(),
                    });
                }
            }

            // 持っている物の表記を貴重品の ID に合わせる
            foreach (JObject p in playerArray)
            {
                var player = snapshot.Players[p.Value<int>("id")];
                if (holding.TryGetValue(player, out var held) && held)
                {
                    var valuable = held.GetComponent<ValuableObject>();
                    var entry = snapshot.Valuables.FirstOrDefault(v => v.Value == valuable);
                    if (valuable && entry.Value)
                    {
                        p["holding"] = $"valuable {entry.Key}";
                    }
                }
            }

            var root = new JObject
            {
                ["enemies"] = enemyArray,
                ["players"] = playerArray,
                ["valuables"] = valuableArray,
            };
            return root.ToString(Formatting.None);
        }

        private static void OnReply(Snapshot snapshot, string reply)
        {
            if (reply == null || snapshot.Generation != generation || !Active)
            {
                return;
            }
            JArray orders;
            try
            {
                orders = JObject.Parse(reply)["orders"] as JArray;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"AI(敵)の返事を読めませんでした: {e.Message}: {reply}");
                return;
            }
            if (orders == null)
            {
                Plugin.Log.LogWarning($"AI(敵)の返事に orders がありません: {reply}");
                return;
            }
            foreach (var token in orders.OfType<JObject>())
            {
                int? id = token.Value<int?>("enemy");
                if (id == null || !snapshot.Enemies.TryGetValue(id.Value, out var c) || !Controlleds.Contains(c))
                {
                    continue;
                }
                var order = new Order
                {
                    Action = (token.Value<string>("action") ?? "roam").Trim().ToLowerInvariant(),
                    Why = token.Value<string>("why") ?? "",
                    Received = Time.time,
                };
                int? player = TryInt(token["player"]);
                if (player != null && snapshot.Players.TryGetValue(player.Value, out var avatar) && avatar && !avatar.isDisabled)
                {
                    order.Player = avatar;
                }
                int? valuable = TryInt(token["valuable"]);
                if (valuable != null && snapshot.Valuables.TryGetValue(valuable.Value, out var target) && target)
                {
                    order.Valuable = target;
                }
                Plugin.Verbose($"{Label(c)}: {order}");
                SetOrder(c, order);
            }
        }

        // ================================================================
        // 状況を調べる
        // ================================================================

        internal static Vector3 EnemyPosition(Enemy enemy)
        {
            if (enemy.CenterTransform)
            {
                return enemy.CenterTransform.position;
            }
            return enemy.HasRigidbody && enemy.Rigidbody ? enemy.Rigidbody.transform.position : enemy.transform.position;
        }

        internal static Vector3 ValuablePosition(ValuableObject valuable)
        {
            var grab = valuable.physGrabObject;
            return grab ? grab.centerPoint : valuable.transform.position;
        }

        /// <summary>敵がその人を見えているか(ゲームの視界の判定で、今見えている)。視界の仕組みがない敵は false。</summary>
        internal static bool VisionSees(Enemy enemy, PlayerAvatar player) =>
            enemy.Vision && player.photonView && enemy.Vision.VisionsTriggered.TryGetValue(player.photonView.ViewID, out int count) && count > 0;

        /// <summary>その人の画面に敵が映っているか(気づかれているか)。</summary>
        internal static bool SeenBy(Enemy enemy, PlayerAvatar player) => enemy.HasOnScreen && enemy.OnScreen && enemy.OnScreen.GetOnScreen(player);

        private static float HealthRatio(Enemy enemy)
        {
            if (!enemy.HasHealth || !enemy.Health || enemy.Health.health <= 0)
            {
                return 1f;
            }
            return Round(Mathf.Clamp01((float)enemy.Health.healthCurrent / enemy.Health.health));
        }

        /// <summary>だれが何をつかんでいるか(つかむ処理はホストが持っているので、全員分が分かる)。</summary>
        private static Dictionary<PlayerAvatar, PhysGrabObject> GrabbedObjects()
        {
            var result = new Dictionary<PlayerAvatar, PhysGrabObject>();
            foreach (var obj in Object.FindObjectsByType<PhysGrabObject>(FindObjectsSortMode.None))
            {
                if (!obj || obj.dead || obj.playerGrabbing == null)
                {
                    continue;
                }
                foreach (var grabber in obj.playerGrabbing)
                {
                    if (grabber && grabber.playerAvatar)
                    {
                        result[grabber.playerAvatar] = obj;
                    }
                }
            }
            return result;
        }

        private static JToken HeldLabel(PhysGrabObject held)
        {
            if (!held)
            {
                return JValue.CreateNull();
            }
            if (held.GetComponent<ValuableObject>())
            {
                return "valuable";
            }
            if (held.GetComponent<ItemMelee>() || held.GetComponent<ItemGun>())
            {
                return "weapon";
            }
            return held.GetComponent<ItemAttributes>() ? "item" : "object";
        }

        private static int? TryInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            try
            {
                return token.Value<int>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static float Round(float value) => Mathf.Round(value * 10f) / 10f;
    }

    /// <summary>AI が動かしている敵の目印(ホストだけ)。ほかの MOD は GetComponent("AIMonstersControlled") で調べる。</summary>
    public sealed class AIMonstersControlled : MonoBehaviour
    {
    }

    /// <summary>
    /// AI への説明を、BepInEx/config/AIMonsters/ のテキストファイルで差し替える。
    /// - 「敵の英名.txt」(例: Loom.txt): その敵の説明の代わりに使う
    /// - 「Common.txt」: 全体への追加の指示
    /// 書き換えたら次の呼び出しから使う(ファイルの更新日時で読み直す)。
    /// </summary>
    internal static class PromptFiles
    {
        private sealed class Entry
        {
            internal DateTime Written;
            internal string Text;
        }

        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();

        internal static string Folder => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "AIMonsters");

        internal static string Read(string name)
        {
            try
            {
                string path = System.IO.Path.Combine(Folder, name + ".txt");
                if (!System.IO.File.Exists(path))
                {
                    Cache.Remove(name);
                    return null;
                }
                var written = System.IO.File.GetLastWriteTimeUtc(path);
                if (!Cache.TryGetValue(name, out var entry) || entry.Written != written)
                {
                    string text = System.IO.File.ReadAllText(path).Trim();
                    entry = new Entry { Written = written, Text = text.Length > 0 ? text : null };
                    Cache[name] = entry;
                    Plugin.Log.LogInfo($"AI への説明 {name}.txt を読み込みました");
                }
                return entry.Text;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"AI への説明 {name}.txt を読めませんでした: {e.Message}");
                return null;
            }
        }
    }
}
