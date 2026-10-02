using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

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
    ///
    /// Settings live in a .cfg that is meant to be edited in a text editor, so they are watched rather
    /// than read once: change the URL or the model in Notepad while the game is running and the next
    /// request uses it. The tab in the mod menu shows what is actually in effect, which is the thing
    /// worth having when a reply is not what you expected and you cannot tell which of six settings
    /// you got wrong.
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

        /// <summary>Counts for the menu tab. Cheap enough to read every frame, which is what it does.</summary>
        private static int _requests;
        private static int _failures;
        private static string _lastError;
        private static string _lastOutcome;

        protected override void OnModLoad()
        {
            Log = ModLog;
            GameBuild.CheckUnityVersion(ModLog, ModId, BuiltAgainst);

            Cfg = new Config(base.Config);
            Cfg.ApplyLimits(new ModSettings(base.Config, ModLog, ModId));

            // The router reads Cfg on every request, so most settings take effect the moment they are
            // re-read. Mode is the exception: switching to or from Passthrough changes whether the patch
            // is installed at all, which OnConfigReloaded handles.
            WatchConfig();

            ModMenu.AddPage(this, "AI Backend", BuildPage, -20);

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

            // Only once the router is live: the menu must not advertise a backend that isn't there.
            MenuStatus.TryInstall(this, Cfg);
        }

        protected override void OnConfigReloaded()
        {
            // The file was re-read, so the clamped values need re-deriving: a hand-edited timeout of
            // -1 has to become 1 before the request path ever sees it.
            Cfg.ApplyLimits(new ModSettings(base.Config, ModLog, ModId));
            Cfg.Describe(ModLog);

            // Mode is the one setting that decides whether the patch exists, so a change to it has to
            // be applied rather than picked up on the next request.
            if (Cfg.Mode.Value == BackendMode.Passthrough)
            {
                ModLog.LogInfo("Mode is now Passthrough; this mod's patch has been removed and the " +
                                "game's own AI backend is back in charge.");
                PatchCoordinator.UnpatchOwner(ModId);
                MenuStatus.TryUninstall(this);
                return;
            }

            if (PatchCoordinator.Installed.Length == 0)
            {
                ModLog.LogInfo("Mode is no longer Passthrough; re-installing the router patch.");
                AiRouter.Initialize(Cfg, ModLog);

                if (PatchCoordinator.TryPatch(
                        this,
                        typeof(KolkataApi),
                        "CompleteOpenRouterAsync",
                        new[] { typeof(JObject), typeof(CancellationToken), typeof(bool) },
                        "route AI calls to " + Cfg.BaseUrl.Value,
                        new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(CompletePrefix)))))
                {
                    MenuStatus.TryInstall(this, Cfg);
                }
            }

            // The menu may be open on this tab, and it is showing the old values.
            ModMenu.Refresh();
        }

        /// <summary>
        /// Replaces the hosted proxy call.
        /// </summary>
        private static bool CompletePrefix(JObject body, CancellationToken cancellationToken, ref UniTask<JObject> __result)
        {
            _requests++;

            try
            {
                foreach (var backend in AiBackendApi.Snapshot())
                {
                    UniTask<JObject> response;
                    if (!backend.TryComplete(body, cancellationToken, out response)) continue;
                    __result = response;
                    _lastOutcome = "answered by " + backend.Name;
                    return false;
                }

                __result = AiRouter.Complete(body, cancellationToken);
                _lastOutcome = "sent to " + Provider();
                return false;
            }
            catch (Exception ex)
            {
                // Never take the game down over a routing bug: fall through to the stock backend.
                _failures++;
                _lastError = ex.Message;
                Log.LogError("Routing failed, falling back to the game's own backend: " + ex);
                return true;
            }
        }

        internal static void RecordFailure(string reason)
        {
            _failures++;
            _lastError = reason;
        }

        internal static void RecordSuccess(string outcome)
        {
            _lastOutcome = outcome;
            _lastError = null;
        }

        // ---------------------------------------------------------------- the menu tab

        /// <summary>
        /// Shows what the mod is actually doing, above the generated settings form.
        /// </summary>
        /// <remarks>
        /// The endpoint, model and provider are all read back from the config each time the tab is
        /// drawn, so they cannot go stale while the menu sits open. The request counts are what makes a
        /// bad setup diagnosable: zero requests means the patch is not firing, requests with failures
        /// means the server is rejecting them, and the last error says how.
        /// </remarks>
        private void BuildPage(VisualElement page)
        {
            // The menu's page host is already a scroll view; adding another here would take the wheel
            // first and make the menu feel stuck.
            var scroll = page;

            Widgets.Heading(scroll, "Where requests are going");

            if (Cfg.Mode.Value == BackendMode.Passthrough)
            {
                Widgets.Note(scroll,
                    "Mode is Passthrough: this mod is not intercepting anything and the game is using " +
                    "its own hosted AI backend.", true, false);
            }
            else
            {
                Widgets.FieldRow(scroll, "Endpoint", SafeBaseUrl());
                Widgets.FieldRow(scroll, "Mode", Cfg.Mode.Value.ToString());
                Widgets.FieldRow(scroll, "Model", ModelOrGameDefault());
                Widgets.FieldRow(scroll, "Provider", Provider());
                Widgets.FieldRow(scroll, "Token limit", Cfg.GameMaxTokens());
            }

            Widgets.Spacer(scroll, 8f);
            Widgets.Heading(scroll, "This session");

            Widgets.FieldRow(scroll, "Requests", _requests.ToString());
            Widgets.FieldRow(scroll, "Failures", _failures.ToString());
            if (!string.IsNullOrEmpty(_lastOutcome)) Widgets.FieldRow(scroll, "Last request", _lastOutcome);
            if (!string.IsNullOrEmpty(_lastError)) Widgets.Note(scroll, "Last error: " + _lastError, true, true);

            var reloads = Watcher != null ? Watcher.ReloadCount : 0;
            Widgets.FieldRow(scroll, "Config reloads", reloads.ToString());

            Widgets.Spacer(scroll, 6f);

            var row = Widgets.WrapRow(scroll);
            Widgets.DescribedButton(row, "Re-read config", "Reload the file now, discarding nothing",
                delegate { ReloadConfig(); });

            var extra = AiBackendApi.Snapshot();
            if (extra.Length > 0)
            {
                Widgets.Spacer(scroll, 6f);
                Widgets.Heading(scroll, "Registered backends");
                Widgets.Paragraph(scroll,
                    "Another mod is providing these. They are tried before this mod's own router, in " +
                    "reverse order of registration, and the first one that accepts a request handles it.");

                foreach (var backend in extra) Widgets.FieldRow(scroll, backend.Name, "");
            }

            Widgets.Spacer(scroll, 8f);

            // The generated form: every setting, with the descriptions this mod wrote for the .cfg, in
            // collapsible groups. It comes last and starts closed, because the live values above are what
            // somebody opening this tab is actually looking for, and twenty settings are not.
            var fold = Widgets.Section(scroll, "All settings", false);
            var body = Widgets.SectionBody(fold);
            if (body == null)
            {
                ConfigEditor.Build(scroll);
                return;
            }

            Widgets.Paragraph(body,
                "Editing here writes straight to " + Config.ConfigFilePath +
                ". Editing that file while the game is running works too: it is watched, and the change " +
                "is picked up within about half a second.");

            ConfigEditor.Build(body);
        }

        private static string SafeBaseUrl()
        {
            try
            {
                var url = Cfg.BaseUrl.Value;
                return string.IsNullOrEmpty(url.Trim()) ? "not set" : url.Trim();
            }
            catch (Exception ex)
            {
                return "unreadable: " + ex.Message;
            }
        }

        private static string ModelOrGameDefault()
        {
            try
            {
                var model = Cfg.Model.Value;
                return string.IsNullOrEmpty(model.Trim())
                    ? "whatever the game asks for"
                    : model.Trim();
            }
            catch (Exception)
            {
                return "unreadable";
            }
        }

        private static string Provider()
        {
            try
            {
                return MenuStatus.DescribeProvider(Cfg);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}