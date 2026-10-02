using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using ScamWYF.Modding.Core;
using UnityEngine.UIElements;

namespace ScamWYF.AiBackend
{
    /// <summary>
    /// Makes the main menu's AI status line describe this mod's server instead of the hosted
    /// backend's credit balance.
    /// </summary>
    /// <remarks>
    /// The game polls its own backend for how many AI credits are left and prints whatever it is
    /// told, whether or not anything is answering. With a custom backend those credits mean nothing
    /// and the popup is an advert for a service this mod is not using, so both are replaced: the
    /// status line names the custom backend, and the popup names the endpoint, model and provider
    /// that are actually in play.
    /// </remarks>
    internal static class MenuStatus
    {
        /// <summary>Type in the game that owns these labels. Internal to ScriptsAssDef, so by name.</summary>
        private const string GameTypeName = "MainMenuConnectionStatus";

        /// <summary>What the AI status line says for as long as this mod is routing.</summary>
        private const string StatusText = "AI BACKEND: CUSTOM BACKEND";

        private static readonly Type[] ColorSignature = { typeof(Label), typeof(bool), typeof(bool) };

        private static Config cfg;
        private static ManualLogSource log;

        private static FieldInfo backendLabel;
        private static FieldInfo warningTitle;
        private static FieldInfo warningBody;
        private static MethodInfo setStatusColor;
        private static bool reportedFailure;

        /// <summary>
        /// Take over the AI status line and the credits popup. Only worth calling once the router
        /// patch has landed, since the menu must not claim a custom backend that is not there.
        /// </summary>
        public static bool TryInstall(ScamMod owner, Config config)
        {
            cfg = config;
            log = owner.ModLog;

            var type = AccessTools.TypeByName(GameTypeName);
            if (type == null)
            {
                log.LogWarning("This build has no " + GameTypeName +
                               "; the menu will keep showing the game's own AI status.");
                return false;
            }

            var refresh = AccessTools.Method(type, "Refresh");
            if (refresh == null)
            {
                log.LogWarning(GameTypeName + ".Refresh has moved; " +
                               "the menu will keep showing the game's own AI status.");
                return false;
            }

            backendLabel = AccessTools.Field(type, "backendLabel");
            warningTitle = AccessTools.Field(type, "warningTitle");
            warningBody = AccessTools.Field(type, "warningBody");
            setStatusColor = AccessTools.Method(type, "SetStatusColor", ColorSignature);

            if (backendLabel == null)
            {
                log.LogWarning(GameTypeName + " has no backendLabel field; " +
                               "the menu will keep showing the game's own AI status.");
                return false;
            }

            return PatchCoordinator.TryPatch(
                owner,
                refresh,
                "describe the custom backend in the menu's AI status",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(MenuStatus), nameof(RefreshPostfix))));
        }

        /// <summary>
        /// Runs after the game has filled the labels in. Overwrites the status line every time, and
        /// the popup only when the popup is the credits-depleted one.
        /// </summary>
        private static void RefreshPostfix(object __instance, bool aiCreditsDepleted)
        {
            // Refresh runs twice a second for as long as the menu is up, so this has to stay cheap
            // and must never throw into the game's own UI code.
            try
            {
                if (cfg == null || cfg.Mode.Value == BackendMode.Passthrough) return;

                var status = backendLabel.GetValue(__instance) as Label;
                if (status != null)
                {
                    status.text = StatusText;
                    if (setStatusColor != null)
                        setStatusColor.Invoke(null, new object[] { status, true, false });
                }

                // Anything else the menu warns about - Steam offline, sign-in failed - is still
                // true of the game, so leave it alone. Only the credits notice is about a service
                // this mod has replaced.
                if (!aiCreditsDepleted) return;

                var title = warningTitle != null ? warningTitle.GetValue(__instance) as Label : null;
                var body = warningBody != null ? warningBody.GetValue(__instance) as Label : null;
                if (body != null) body.text = Describe();
                if (title != null) title.text = "CUSTOM BACKEND";
            }
            catch (Exception ex)
            {
                if (reportedFailure) return;
                reportedFailure = true;
                log.LogError("Could not rewrite the AI status readout: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- text

        private static string Describe()
        {
            var model = Trim(cfg.Model.Value);
            if (model.Length == 0) model = "whatever the game asks for";

            var endpoint = Trim(cfg.BaseUrl.Value);
            if (endpoint.Length == 0) endpoint = "no endpoint configured";

            return string.Format(
                "AI calls are going to {0} ({1}) using model '{2}'.",
                Provider(), endpoint, model) +
                " This is not the game's hosted backend, so its AI credits do not apply here.";
        }

        /// <summary>Provider for the status line: whatever the config names, else guessed from the URL.</summary>
        private static string Provider()
        {
            var configured = Trim(cfg.ProviderName.Value);
            return configured.Length > 0 ? configured : DetectProvider();
        }

        /// <summary>
        /// The endpoint does not say what is serving it, so this recognises the setups the config
        /// file already documents and falls back to the host, which is honest either way.
        /// </summary>
        private static string DetectProvider()
        {
            var url = Trim(cfg.BaseUrl.Value).ToLowerInvariant();
            if (url.Length == 0) return "no provider";

            if (url.Contains("openrouter.ai")) return "OpenRouter";
            if (url.Contains("api.openai.com")) return "OpenAI";
            if (url.Contains("api.anthropic.com")) return "Anthropic";
            if (url.Contains("api.groq.com")) return "Groq";
            if (url.Contains("api.mistral.ai")) return "Mistral";
            if (url.Contains("generativelanguage.googleapis.com")) return "Google";

            // The loopback defaults from the BaseUrl help text.
            if (url.Contains("11434")) return "Ollama";
            if (url.Contains("1234")) return "LM Studio";
            if (url.Contains("8080")) return "llama.cpp";
            if (url.Contains("8000")) return "vLLM";

            return Host(url);
        }

        private static string Host(string url)
        {
            var rest = url;
            var scheme = rest.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) rest = rest.Substring(scheme + 3);

            var end = rest.IndexOfAny(new[] { '/', ':' });
            if (end > 0) rest = rest.Substring(0, end);

            return rest.Length == 0 ? "a custom endpoint" : rest;
        }

        private static string Trim(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Trim();
        }
    }
}