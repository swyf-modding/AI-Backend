using System;
using System.Collections;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamWYF.AiBackend
{
    /// <summary>
    /// Owns the actual HTTP call. Lives on a DontDestroyOnLoad object because UnityWebRequest has
    /// to run on the main thread, and hands the result back through a UniTaskCompletionSource so
    /// the game's await resumes on the player loop exactly as it would have.
    /// </summary>
    internal sealed class AiRouter : MonoBehaviour
    {
        private static AiRouter instance;
        private static Config cfg;
        private static ManualLogSource log;

        public static void Initialize(Config config, ManualLogSource logger)
        {
            cfg = config;
            log = logger;
            if (instance != null) return;

            var host = new GameObject("ScamWYF.AiBackend.Router");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            instance = host.AddComponent<AiRouter>();
        }

        public static UniTask<JObject> Complete(JObject gameBody, CancellationToken cancellationToken)
        {
            var source = new UniTaskCompletionSource<JObject>();
            if (instance == null)
            {
                source.TrySetException(new InvalidOperationException(
                    "ScamWYF.AiBackend router was never initialized."));
                return source.Task;
            }

            instance.StartCoroutine(instance.Run(gameBody, cancellationToken, source));
            return source.Task;
        }

        private IEnumerator Run(JObject gameBody, CancellationToken cancellationToken, UniTaskCompletionSource<JObject> source)
        {
            string url;
            byte[] payload;
            try
            {
                url = ResolveUrl();
                StructuredOutputMode mode;
                var body = Translator.BuildRequest(gameBody, cfg, out mode);
                payload = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));

                if (cfg.Debug.Value)
                {
                    log.LogInfo(string.Format("-> {0} model={1} structured={2} bytes={3}",
                        url, body["model"], mode, payload.Length));
                    if (cfg.LogBodies.Value) log.LogInfo("request: " + body.ToString(Formatting.None));
                }
            }
            catch (Exception ex)
            {
                log.LogError("Failed to build the request: " + ex);
                source.TrySetException(ex);
                yield break;
            }

            var attempts = Math.Max(1, cfg.MaxAttempts.Value);
            Exception lastError = null;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    var delay = Math.Min(8f, (float)Math.Pow(2, attempt - 1)) + UnityEngine.Random.Range(0f, 0.25f);
                    if (cfg.Debug.Value) log.LogInfo(string.Format("retry {0}/{1} in {2:F1}s", attempt + 1, attempts, delay));

                    var until = Time.realtimeSinceStartup + delay;
                    while (Time.realtimeSinceStartup < until)
                    {
                        if (cancellationToken.IsCancellationRequested) { source.TrySetCanceled(cancellationToken); yield break; }
                        yield return null;
                    }
                }

                if (cancellationToken.IsCancellationRequested) { source.TrySetCanceled(cancellationToken); yield break; }

                var started = Time.realtimeSinceStartup;
                bool cancelled = false;
                bool retryable = false;
                JObject parsed = null;

                using (var request = new UnityWebRequest(url, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(payload);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = Math.Max(1, cfg.TimeoutSeconds.Value);
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Accept", "application/json");

                    var key = cfg.ApiKey.Value.Trim();
                    if (key.Length > 0) request.SetRequestHeader("Authorization", "Bearer " + key);

                    ApplyExtraHeaders(request);

                    if (cfg.AllowInvalidCertificates.Value)
                        request.certificateHandler = new AcceptAnyCertificate();

                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            request.Abort();
                            cancelled = true;
                            break;
                        }
                        yield return null;
                    }

                    if (!cancelled)
                    {
                        var elapsed = Time.realtimeSinceStartup - started;
                        var status = request.responseCode;
                        var text = request.downloadHandler != null ? request.downloadHandler.text : null;

                        if (request.result == UnityWebRequest.Result.Success)
                        {
                            if (cfg.Debug.Value)
                            {
                                log.LogInfo(string.Format("<- {0} in {1:F2}s", status, elapsed));
                                if (cfg.LogBodies.Value) log.LogInfo("response: " + Translator.Truncate(text, 8000));
                            }

                            try
                            {
                                parsed = Translator.NormalizeResponse(JObject.Parse(text), cfg);
                            }
                            catch (Exception ex)
                            {
                                lastError = ex;
                                log.LogWarning("Could not use the response: " + ex.Message);
                            }
                        }
                        else
                        {
                            retryable = status == 0 || status == 408 || status == 429 || status >= 500;
                            lastError = new InvalidOperationException(string.Format(
                                "{0} returned HTTP {1} ({2}) after {3:F2}s. Body: {4}",
                                url, status, request.error, elapsed, Translator.Truncate(text, 600)));
                            log.LogWarning(lastError.Message);
                        }
                    }
                }

                if (cancelled) { source.TrySetCanceled(cancellationToken); yield break; }
                if (parsed != null) { source.TrySetResult(parsed); yield break; }
                if (!retryable) break;
            }

            source.TrySetException(lastError ?? new InvalidOperationException(
                "The AI request failed and no error was recorded."));
        }

        private void ApplyExtraHeaders(UnityWebRequest request)
        {
            var raw = cfg.ExtraHeaders.Value;
            if (string.IsNullOrEmpty(raw.Trim())) return;

            foreach (var entry in raw.Split('|'))
            {
                var separator = entry.IndexOf(':');
                if (separator <= 0) continue;
                var name = entry.Substring(0, separator).Trim();
                var value = entry.Substring(separator + 1).Trim();
                if (name.Length == 0) continue;
                try { request.SetRequestHeader(name, value); }
                catch (Exception ex) { log.LogWarning("Ignoring header '" + name + "': " + ex.Message); }
            }
        }

        private static string ResolveUrl()
        {
            var baseUrl = cfg.BaseUrl.Value.Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
                throw new InvalidOperationException("BaseUrl is empty; set it in the config file.");

            if (cfg.Mode.Value == BackendMode.OllamaNative)
            {
                if (baseUrl.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase)) return baseUrl;
                if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    baseUrl = baseUrl.Substring(0, baseUrl.Length - 3).TrimEnd('/');
                return baseUrl + "/api/chat";
            }

            var path = cfg.ChatPath.Value.Trim();
            if (path.Length == 0) path = "/chat/completions";
            if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;

            return baseUrl.EndsWith(path, StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + path;
        }

        private sealed class AcceptAnyCertificate : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) { return true; }
        }
    }
}
