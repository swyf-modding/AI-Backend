using System;
using Newtonsoft.Json.Linq;

namespace ScamWYF.AiBackend
{
    /// <summary>
    /// Answers the one call the game makes that is not a chat completion: the request for the prompt
    /// bundle a conversation is frozen against before its first turn.
    /// </summary>
    /// <remarks>
    /// The full release reworked the AI path around an "immutable conversation": before a caller turn,
    /// the objective read or the boss review sends its first request, the game asks its backend to
    /// resolve the current official prompt version. That request carries
    /// <c>resolve_prompt_version: true</c>, an <c>ai_category</c> and no messages, and the backend
    /// answers with a bundle of <c>prompt_version</c>, <c>system_prompt</c>, <c>response_format</c>
    /// and <c>limits</c> that the game then keeps for the whole conversation.
    ///
    /// A chat server cannot answer that: it has no messages to complete. Left to the built-in router
    /// the turn would fail with "Invalid immutable prompt bundle", which is what a healthy release
    /// looks like when the mod only patches the completion call. So the request is answered here
    /// instead, out of the game's own packaged prompts - the same text the game would have used had
    /// the host selected a model directly, and the same text that backs the host's prompt overrides.
    ///
    /// When the bundle cannot be rebuilt, <see cref="TryBuild"/> says so and the caller leaves the
    /// request to the game's own backend, which is the only other thing that can serve it.
    /// </remarks>
    internal static class PromptBundle
    {
        /// <summary>The body field that marks a prompt-resolution request rather than a completion.</summary>
        private const string ResolveField = "resolve_prompt_version";

        /// <summary>True when this body is the game asking its backend for a prompt bundle.</summary>
        public static bool IsResolveRequest(JObject body)
        {
            var flag = body?[ResolveField];
            return flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>();
        }

        /// <summary>
        /// Build the bundle the game expects, or return false if the game's prompts cannot be read.
        /// </summary>
        public static bool TryBuild(JObject body, out JObject bundle)
        {
            bundle = null;
            try
            {
                var responseFormat = body?["response_format"] as JObject;

                bundle = new JObject
                {
                    ["prompt_version"] = PromptVersion(),
                    ["system_prompt"] = SystemPrompt((string)body?["ai_category"]),
                    // The game replaces its own schema with whatever comes back, so echoing the one it
                    // sent keeps validation identical to the local-prompt path.
                    ["response_format"] = responseFormat != null ? (JObject)responseFormat.DeepClone() : new JObject(),
                    ["limits"] = BuildLimits()
                };
                return true;
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                    Plugin.Log.LogError("Could not rebuild the game's prompt bundle: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The base system prompt for a category, by the same path the game reads for itself.
        /// </summary>
        /// <remarks>
        /// The game freezes this text and, for a caller turn, re-appends the per-call and
        /// per-personality sections it stripped when it recorded <c>backend_prompt_length</c>. The
        /// other categories use the returned text whole, so each path has to be the exact one that
        /// category's own conversation was created with.
        /// </remarks>
        private static string SystemPrompt(string category)
        {
            switch (category)
            {
                case "boss_review": return GameplayPrompts.Text("bossReview.systemPrompt");
                case "boss_taunt": return GameplayPrompts.Text("bossTaunt.systemPrompt");
                case "customer_info": return GameplayPrompts.Text("customerInfo.systemPrompt");
                default: return GameplayPrompts.Text("systemPrompt");
            }
        }

        /// <summary>
        /// A version string for the frozen bundle. The game only stores it and echoes it back, but it
        /// is the prompt content's own fingerprint, so two hosts on different overrides do not look
        /// like the same bundle in a log.
        /// </summary>
        private static string PromptVersion()
        {
            var revision = GameplayPrompts.Capture().Revision;
            return string.IsNullOrEmpty(revision) ? "local" : revision;
        }

        /// <summary>
        /// Transport limits the game uses to decide when a conversation has to compact.
        /// </summary>
        /// <remarks>
        /// These are the values the game falls back to when its backend reports none, offered here
        /// without the backend's per-category tightening. A local server has no such budget, and a
        /// tighter one would only make the game drop history for no reason.
        /// </remarks>
        private static JObject BuildLimits()
        {
            return new JObject
            {
                ["maxBodyBytes"] = 131072,
                ["maxInputUnits"] = 32768,
                ["maxMessages"] = 128,
                ["messageFramingUnits"] = 32,
                ["baseInputUnits"] = 3,
                ["providerReserveUnits"] = 1024
            };
        }
    }
}
