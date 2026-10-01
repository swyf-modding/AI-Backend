using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScamWYF.Modding.Core;

namespace ScamWYF.AiBackend
{
    /// <summary>
    /// A backend that can answer the game's chat-completion requests. Other BepInEx plugins can
    /// implement this and register it to take over routing without touching this mod.
    /// </summary>
    public interface IChatBackend
    {
        string Name { get; }

        /// <summary>
        /// Return false to decline this request and let the next backend (or the built-in router)
        /// handle it. The request is an OpenAI-style chat-completion body; the response must be
        /// one too, with the reply in choices[0].message.content.
        /// </summary>
        bool TryComplete(JObject request, CancellationToken cancellationToken, out UniTask<JObject> response);
    }

    /// <summary>Extension point for other mods.</summary>
    public static class AiBackendApi
    {
        private static readonly List<IChatBackend> Backends = new List<IChatBackend>();

        /// <summary>Most recently registered backend is asked first.</summary>
        public static void Register(IChatBackend backend)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            lock (Backends)
            {
                Backends.Remove(backend);
                Backends.Insert(0, backend);
            }
            Plugin.Log.LogInfo("Registered chat backend: " + backend.Name);
        }

        public static void Unregister(IChatBackend backend)
        {
            if (backend == null) return;
            lock (Backends) { Backends.Remove(backend); }
        }

        internal static IChatBackend[] Snapshot()
        {
            lock (Backends) { return Backends.ToArray(); }
        }
    }

    /// <summary>
    /// Points the game's AI calls at your own server instead of the hosted backend.
    /// </summary>
    /// <remarks>
    /// Everything the game asks an LLM - caller dialogue, the objective detector, the review writer -
    /// funnels through one method, KolkataApi.CompleteOpenRouterAsync. One Harmony prefix there
    /// covers the whole game, and the hosted path is skipped entirely, Steam lobby and backend auth
    /// included.
    /// </remarks>
    [BepInPlugin(PluginGuid, "Scam WYF AI Backend", "1.0.0")]
    public sealed class Plugin : ScamMod
    {
        public const string PluginGuid = "com.community.scamwyf.aibackend";

        /// <summary>Unity build this mod was written against. Checked on load.</summary>
        private const string BuiltAgainst = "6000.3.10f1";

        // Harmony prefixes are static, so the logger the fallback path needs is too.
        internal static ManualLogSource Log;

        internal static Config Cfg;

        protected override void OnModLoad()
        {
            Log = ModLog;
            GameBuild.CheckUnityVersion(ModLog, ModId, BuiltAgainst);

            Cfg = new Config(base.Config);
            Cfg.ApplyLimits(new ModSettings(base.Config, ModLog, ModId));

            if (Cfg.Mode.Value == BackendMode.Passthrough)
            {
                ModLog.LogInfo("Mode = Passthrough; leaving the game's own AI backend alone.");
                return;
            }

            AiRouter.Initialize(Cfg, ModLog);

            if (!PatchCoordinator.TryPatch(
                    this,
                    typeof(KolkataApi),
                    "CompleteOpenRouterAsync",
                    new[] { typeof(JObject), typeof(CancellationToken), typeof(bool) },
                    "route AI calls to " + Cfg.BaseUrl.Value,
                    new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(CompletePrefix)))))
            {
                // PatchCoordinator has already logged what it expected and what this build has.
                ModLog.LogWarning("No patch applied; the game will keep using its own backend.");
                return;
            }

            ModLog.LogInfo(string.Format(
                "AI requests are now going to {0} (mode {1}, model '{2}').",
                Cfg.BaseUrl.Value,
                Cfg.Mode.Value,
                string.IsNullOrEmpty(Cfg.Model.Value.Trim()) ? "<whatever the game asks for>" : Cfg.Model.Value.Trim()));
        }

        /// <summary>
        /// Replaces the hosted proxy call.
        /// </summary>
        private static bool CompletePrefix(JObject body, CancellationToken cancellationToken, ref UniTask<JObject> __result)
        {
            try
            {
                foreach (var backend in AiBackendApi.Snapshot())
                {
                    UniTask<JObject> response;
                    if (!backend.TryComplete(body, cancellationToken, out response)) continue;
                    __result = response;
                    return false;
                }

                __result = AiRouter.Complete(body, cancellationToken);
                return false;
            }
            catch (Exception ex)
            {
                // Never take the game down over a routing bug: fall through to the stock backend.
                Log.LogError("Routing failed, falling back to the game's own backend: " + ex);
                return true;
            }
        }
    }
}