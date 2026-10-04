using System;
using BepInEx.Configuration;
using ScamWYF.Modding.Core;

namespace ScamWYF.AiBackend
{
    public enum BackendMode
    {
        /// <summary>Do nothing; the game keeps using its own hosted backend.</summary>
        Passthrough,
        /// <summary>Any OpenAI-style /chat/completions server (OpenAI, OpenRouter, Ollama /v1, LM Studio, llama.cpp, vLLM, Groq...).</summary>
        OpenAiCompatible,
        /// <summary>Ollama's own /api/chat protocol.</summary>
        OllamaNative
    }

    /// <summary>
    /// How hard the model is asked to think before it answers.
    /// </summary>
    public enum ReasoningMode
    {
        /// <summary>Ask for no thinking at all. The default: caller turns are 128 tokens.</summary>
        Off,
        /// <summary>Send no reasoning field and let the server apply its own default.</summary>
        ServerDefault,
        /// <summary>The smallest amount of thinking the server will do.</summary>
        Minimal,
        /// <summary>A little thinking.</summary>
        Low,
        /// <summary>The server's usual amount.</summary>
        Medium,
        /// <summary>As much thinking as it will do.</summary>
        High
    }

    public enum StructuredOutputMode
    {
        /// <summary>Pick a sensible default for the chosen backend mode.</summary>
        Auto,
        /// <summary>Forward the game's strict json_schema response_format untouched.</summary>
        JsonSchema,
        /// <summary>Downgrade to a json_object request and describe the schema in the system prompt.</summary>
        JsonObject,
        /// <summary>Send no response_format at all; describe the schema in the system prompt.</summary>
        Prompt
    }

    internal sealed class Config
    {
        public readonly ConfigEntry<BackendMode> Mode;
        public readonly ConfigEntry<bool> Debug;
        public readonly ConfigEntry<bool> LogBodies;

        public readonly ConfigEntry<string> BaseUrl;
        public readonly ConfigEntry<string> ChatPath;
        public readonly ConfigEntry<string> ApiKey;
        public readonly ConfigEntry<string> ExtraHeaders;
        public readonly ConfigEntry<int> TimeoutSeconds;
        public readonly ConfigEntry<int> MaxAttempts;
        public readonly ConfigEntry<bool> AllowInvalidCertificates;

        public readonly ConfigEntry<string> Model;
        public readonly ConfigEntry<string> ProviderName;
        public readonly ConfigEntry<int> MaxTokensOverride;
        public readonly ConfigEntry<int> MinMaxTokens;
        public readonly ConfigEntry<float> TemperatureOverride;
        public readonly ConfigEntry<float> TopPOverride;

        public readonly ConfigEntry<StructuredOutputMode> StructuredOutputs;
        public readonly ConfigEntry<bool> KeepOpenRouterFields;
        public readonly ConfigEntry<string> MaxTokensFieldName;
        public readonly ConfigEntry<bool> StripThinkTags;

        public readonly ConfigEntry<ReasoningMode> Reasoning;

        public Config(ConfigFile file)
        {
            Mode = file.Bind("1 - General", "Mode", BackendMode.OpenAiCompatible,
                "Which backend to use.\n" +
                "Passthrough      = leave the game alone and use the built-in hosted AI.\n" +
                "OpenAiCompatible = any server speaking OpenAI /chat/completions.\n" +
                "OllamaNative     = Ollama's own /api/chat endpoint.");

            Debug = file.Bind("1 - General", "Debug", false,
                "Log every request: endpoint, model, status code, timing.");

            LogBodies = file.Bind("1 - General", "LogRequestBodies", false,
                "Also log the full JSON sent and received. Very noisy, and it puts the entire system\n" +
                "prompt and your conversation into LogOutput.log. Requires Debug = true.");

            BaseUrl = file.Bind("2 - Endpoint", "BaseUrl", "http://127.0.0.1:11434/v1",
                "Base URL of your server, without the /chat/completions suffix.\n" +
                "  Ollama      http://127.0.0.1:11434/v1\n" +
                "  LM Studio   http://127.0.0.1:1234/v1\n" +
                "  llama.cpp   http://127.0.0.1:8080/v1\n" +
                "  OpenAI      https://api.openai.com/v1\n" +
                "  OpenRouter  https://openrouter.ai/api/v1\n" +
                "For Mode = OllamaNative give the bare host instead: http://127.0.0.1:11434");

            ChatPath = file.Bind("2 - Endpoint", "ChatPath", "/chat/completions",
                "Path appended to BaseUrl. Ignored when Mode = OllamaNative, which always uses\n" +
                "/api/chat. If BaseUrl already ends with this path it is not appended twice.");

            ApiKey = file.Bind("2 - Endpoint", "ApiKey", "",
                "Sent as an Authorization: Bearer header. Leave empty for local servers.\n" +
                "Whatever you put here is stored in plain text in this file.");

            ExtraHeaders = file.Bind("2 - Endpoint", "ExtraHeaders", "",
                "Extra request headers, pipe separated. Example:\n" +
                "HTTP-Referer: https://example.com|X-Title: Scam With Your Friends");

            TimeoutSeconds = file.Bind("2 - Endpoint", "TimeoutSeconds", 90,
                "Per-attempt timeout in seconds. The game starts calling a caller turn slow at 15s\n" +
                "but still accepts a late answer.");

            MaxAttempts = file.Bind("2 - Endpoint", "MaxAttempts", 3,
                "Attempts per request. Retries only on 408/429/5xx and connection errors.");

            AllowInvalidCertificates = file.Bind("2 - Endpoint", "AllowInvalidCertificates", false,
                "Skip TLS certificate validation. A last resort for Unity's Mono failing to verify an\n" +
                "otherwise valid HTTPS certificate. Never needed for http:// local servers. Leave this\n" +
                "off unless the server is one you control.");

            Model = file.Bind("3 - Model", "Model", "",
                "Model name to send. Empty keeps whatever the game asked for, which is an OpenRouter\n" +
                "id such as google/gemini-2.5-flash-lite and will not exist on your server.\n" +
                "Examples: llama3.1:8b, qwen3:14b, gpt-4o-mini.");

            ProviderName = file.Bind("3 - Model", "ProviderName", "",
                "Name of the provider, shown in the main menu's AI status readout.\n" +
                "Leave empty to work it out from BaseUrl: Ollama, LM Studio, OpenAI, OpenRouter and\n" +
                "so on are recognised, anything else falls back to the host name.\n" +
                "Examples: Ollama, OpenRouter, LM Studio, vLLM.");

            MaxTokensOverride = file.Bind("3 - Model", "MaxTokensOverride", 0,
                "Replace the game's token limit with this. 0 keeps the game's value.");

            MinMaxTokens = file.Bind("3 - Model", "MinMaxTokens", 0,
                "Raise the token limit to at least this. 0 disables.\n" +
                "The game asks for 128 tokens per caller turn, which is fine for an ordinary model but\n" +
                "a reasoning model will spend all of it thinking and return nothing usable. Leave\n" +
                "Reasoning = Off unless you want thinking on purpose; if you do raise it, 1024 or more\n" +
                "and leave StripThinkTags on.");

            TemperatureOverride = file.Bind("3 - Model", "TemperatureOverride", -1f,
                "Replace temperature. Negative keeps the game's value, which is 0.9 for callers.");

            TopPOverride = file.Bind("3 - Model", "TopPOverride", -1f,
                "Replace top_p. Negative keeps the game's value, which is 0.95 for callers.");

            Reasoning = file.Bind("3 - Model", "Reasoning", ReasoningMode.Off,
                "How hard the model should think before answering. Caller turns are 128 tokens and the\n" +
                "game wants an answer fast, so the default is no thinking at all.\n" +
                "Off           = reasoning_effort none. Cheapest and quickest, and the only setting\n" +
                "                 that survives a small token budget.\n" +
                "ServerDefault = send nothing; whatever the server does by default.\n" +
                "Minimal / Low / Medium / High = reasoning_effort of that name.\n" +
                "Anything above Off needs MinMaxTokens of 1024 or more, or the whole budget goes into\n" +
                "thinking and the reply comes back empty.\n" +
                "gpt-5 and older reject \"none\" with a 400; use ServerDefault or Minimal for those.\n" +
                "Ollama native takes this as think = false / true, since it has no effort levels.");

            StructuredOutputs = file.Bind("4 - Compatibility", "StructuredOutputs", StructuredOutputMode.Auto,
                "The game demands a strict JSON object back and discards the turn if it does not parse.\n" +
                "How should that be expressed to your server?\n" +
                "Auto       = JsonSchema for OpenAI-compatible, native format for Ollama.\n" +
                "JsonSchema = forward the strict schema. Needs real structured-output support.\n" +
                "JsonObject = ask for a JSON object and put the schema in the prompt.\n" +
                "Prompt     = send no response_format; put the schema in the prompt only.\n" +
                "If replies keep failing validation, step down: JsonSchema, then JsonObject, then Prompt.");

            KeepOpenRouterFields = file.Bind("4 - Compatibility", "KeepOpenRouterFields", false,
                "Keep the OpenRouter-only 'provider' and 'session_id' body fields. Turn this on only if\n" +
                "you are pointing at OpenRouter itself. Strict servers such as OpenAI reject unknown\n" +
                "body fields with a 400.");

            MaxTokensFieldName = file.Bind("4 - Compatibility", "MaxTokensFieldName", "max_tokens",
                "Name of the token-limit field. OpenAI's newer reasoning models want\n" +
                "max_completion_tokens instead and return 400 for max_tokens.");

            StripThinkTags = file.Bind("4 - Compatibility", "StripThinkTags", true,
                "Remove think / reasoning blocks from the reply before the game sees it.\n" +
                "Harmless for models that do not emit them.");
        }

        /// <summary>
        /// Pull every numeric setting back into a range that cannot wedge the game, and say which
        /// one was wrong.
        /// </summary>
        /// <remarks>
        /// This config file is meant to be edited in a text editor, so it is going to end up with a
        /// timeout of -1 or an attempt count of 900 at some point. Clamping means the request path
        /// only ever sees sane numbers.
        ///
        /// Called at load and again after every reload, because the values are re-read from a file
        /// that can be edited underneath the game. Only the clamped values are written back, so a
        /// typo is corrected in the file rather than silently overridden in memory - the file stays
        /// the thing a person edits.
        /// </remarks>
        public void ApplyLimits(ModSettings settings)
        {
            Clamp(TimeoutSeconds, settings.Int(TimeoutSeconds, 1, 3600));
            Clamp(MaxAttempts, settings.Int(MaxAttempts, 1, 10));
            Clamp(MaxTokensOverride, settings.Int(MaxTokensOverride, 0, 1000000));
            Clamp(MinMaxTokens, settings.Int(MinMaxTokens, 0, 1000000));

            // A temperature or top_p left negative means "use the game's value", so those are bounds,
            // not clamps - there is nothing to correct.
            TemperatureOverride.Value = settings.Float(TemperatureOverride, -1f, 2f);
            TopPOverride.Value = settings.Float(TopPOverride, -1f, 1f);
        }

        private static void Clamp<T>(BepInEx.Configuration.ConfigEntry<T> entry, T value) where T : IComparable
        {
            try
            {
                if (entry.Value.CompareTo(value) == 0) return;
                entry.Value = value;
            }
            catch (Exception)
            {
                // The settings object has already complained about an unreadable value; there is
                // nothing useful to add by trying to write one back over it.
            }
        }

        /// <summary>
        /// Log the settings that are actually in effect. Called after a reload, so somebody editing the
        /// file can see in the log exactly which values the mod picked up - which is the question when a
        /// change "did nothing".
        /// </summary>
        public void Describe(BepInEx.Logging.ManualLogSource log)
        {
            if (log == null) return;

            log.LogInfo(string.Format(
                "In effect: mode {0}, endpoint {1}, model '{2}', timeout {3}s, {4} attempt(s).",
                Mode.Value,
                string.IsNullOrEmpty(BaseUrl.Value.Trim()) ? "<not set>" : BaseUrl.Value.Trim(),
                string.IsNullOrEmpty(Model.Value.Trim()) ? "<whatever the game asks for>" : Model.Value.Trim(),
                TimeoutSeconds.Value,
                MaxAttempts.Value));
        }

        /// <summary>
        /// The token limit a caller turn ends up with, for error messages. Resolved the same way
        /// Translator resolves it, so "raise this" and "this is what you have" agree.
        /// </summary>
        public string GameMaxTokens()
        {
            var limit = MinMaxTokens.Value;
            if (MaxTokensOverride.Value > 0) limit = MaxTokensOverride.Value;
            return limit > 0 ? limit + " tokens" : "token limit";
        }
    }
}
