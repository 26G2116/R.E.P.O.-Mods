using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AIMonsters
{
    /// <summary>
    /// Azure OpenAI の Chat Completions を呼ぶ窓口。1 つの窓口は同時に 1 回だけ呼ぶ(返事が来るまで次を送らない)。
    /// 敵の AI と味方ローブの会話で窓口を分け、片方が待たされないようにする。
    /// 返事は JSON(response_format = json_object)で受け取る。
    /// </summary>
    internal sealed class AzureChannel
    {
        private readonly string name;
        private float nextErrorLog;
        private int calls;
        private long promptTokens;
        private long completionTokens;

        internal AzureChannel(string name)
        {
            this.name = name;
        }

        /// <summary>返事を待っているところか。</summary>
        internal bool Busy { get; private set; }

        /// <summary>最後に返事を受け取るまでにかかった秒数(ログ用)。</summary>
        internal float LastLatency { get; private set; }

        /// <summary>
        /// 送る。返事の本文(JSON の文字列)を done に渡す。失敗したら null を渡す。
        /// すでに待っているときや、エンドポイント・キーがないときは送らずに false を返す(done は呼ばない)。
        /// </summary>
        internal bool Send(string system, string user, Action<string> done)
        {
            if (Busy || !Runner.Instance)
            {
                return false;
            }
            if (!Credentials.TryGet(out string url, out string key))
            {
                return false;
            }
            Busy = true;
            Runner.Instance.StartCoroutine(Run(url, key, system, user, done));
            return true;
        }

        private IEnumerator Run(string url, string key, string system, string user, Action<string> done)
        {
            string result = null;
            float started = Time.realtimeSinceStartup;
            UnityWebRequest request = null;
            try
            {
                var body = new JObject
                {
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = system },
                        new JObject { ["role"] = "user", ["content"] = user },
                    },
                    ["max_completion_tokens"] = Plugin.MaxTokens.Value,
                    ["response_format"] = new JObject { ["type"] = "json_object" },
                };
                string effort = Plugin.ReasoningEffort.Value?.Trim();
                if (!string.IsNullOrEmpty(effort))
                {
                    body["reasoning_effort"] = effort;
                }
                request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                {
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None))),
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = Mathf.CeilToInt(Plugin.Timeout.Value),
                };
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("api-key", key);
            }
            catch (Exception e)
            {
                LogError($"送れませんでした: {e.Message}");
                request?.Dispose();
                Finish(done, null);
                yield break;
            }

            yield return request.SendWebRequest();

            try
            {
                LastLatency = Time.realtimeSinceStartup - started;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    LogError($"失敗しました({request.responseCode} {request.error}): {Shorten(request.downloadHandler?.text)}");
                }
                else
                {
                    result = Parse(request.downloadHandler.text);
                }
            }
            catch (Exception e)
            {
                LogError($"返事を読めませんでした: {e.Message}");
                result = null;
            }
            finally
            {
                request.Dispose();
            }
            Finish(done, result);
        }

        private void Finish(Action<string> done, string result)
        {
            Busy = false;
            try
            {
                done?.Invoke(result);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"AI({name})の返事の処理でエラー: {e}");
            }
        }

        private string Parse(string text)
        {
            var root = JObject.Parse(text);
            var usage = root["usage"];
            if (usage != null)
            {
                calls++;
                promptTokens += usage.Value<long?>("prompt_tokens") ?? 0;
                completionTokens += usage.Value<long?>("completion_tokens") ?? 0;
                Plugin.Verbose($"AI({name}): {LastLatency:0.00} 秒、今回 送信 {usage.Value<long?>("prompt_tokens")} / 返事 {usage.Value<long?>("completion_tokens")}、" +
                               $"累計 {calls} 回 送信 {promptTokens} / 返事 {completionTokens}");
            }
            var choice = root["choices"]?[0];
            string content = choice?["message"]?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content))
            {
                LogError($"返事が空でした(finish_reason: {choice?["finish_reason"]})");
                return null;
            }
            return content;
        }

        /// <summary>エラーのログ。同じ窓口からは 10 秒に 1 回まで(API が落ちているときにログを埋めない)。</summary>
        private void LogError(string message)
        {
            if (Time.realtimeSinceStartup < nextErrorLog)
            {
                return;
            }
            nextErrorLog = Time.realtimeSinceStartup + 10f;
            Plugin.Log.LogWarning($"AI({name})の呼び出しに{message}");
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            text = text.Replace('\n', ' ');
            return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
        }
    }

    /// <summary>エンドポイントと API キー(環境変数から読む。キーはログに出さない)。</summary>
    internal static class Credentials
    {
        private static bool warned;

        internal static bool TryGet(out string url, out string key)
        {
            url = null;
            string endpoint = Read(Plugin.EndpointVariable.Value);
            key = Read(Plugin.KeyVariable.Value);
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
            {
                if (!warned)
                {
                    warned = true;
                    Plugin.Log.LogWarning($"環境変数 {Plugin.EndpointVariable.Value} / {Plugin.KeyVariable.Value} が見つからないので、AI を使いません。" +
                                          "環境変数を設定したあと、Steam とゲームを起動し直してください");
                }
                key = null;
                return false;
            }
            warned = false;
            url = $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(Plugin.Deployment.Value.Trim())}/chat/completions" +
                  $"?api-version={Uri.EscapeDataString(Plugin.ApiVersion.Value.Trim())}";
            return true;
        }

        /// <summary>このプロセスの環境変数、なければユーザーの環境変数(起動後に設定した場合)。</summary>
        private static string Read(string variable)
        {
            if (string.IsNullOrWhiteSpace(variable))
            {
                return null;
            }
            string value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    value = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User);
                }
                catch (Exception)
                {
                    value = null;
                }
            }
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
