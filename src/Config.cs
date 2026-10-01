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
        public readonly ConfigEntry<int> MaxTokensOverride;
        public readonly ConfigEntry<int> MinMaxTokens;
        public readonly ConfigEntry<float> TemperatureOverride;
        public readonly ConfigEntry<float> TopPOverride;

        public readonly ConfigEntry<StructuredOutputMode> StructuredOutputs;
        public readonly ConfigEntry<bool> KeepOpenRouterFields;
        public readonly ConfigEntry<string> MaxTokensFieldName;
        public readonly ConfigEntry<bool> StripThinkTags;

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

            MaxTokensOverride = file.Bind("3 - Model", "MaxTokensOverride", 0,
                "Replace the game's token limit with this. 0 keeps the game's value.");

            MinMaxTokens = file.Bind("3 - Model", "MinMaxTokens", 0,
                "Raise the token limit to at least this. 0 disables.\n" +
                "The game asks for 128 tokens per caller turn, which is fine for an ordinary model but\n" +
                "a reasoning model will spend all of it thinking and return nothing usable. With a\n" +
                "thinking model set this to 1024 or more and leave StripThinkTags on.");

            TemperatureOverride = file.Bind("3 - Model", "TemperatureOverride", -1f,
                "Replace temperature. Negative keeps the game's value, which is 0.9 for callers.");

            TopPOverride = file.Bind("3 - Model", "TopPOverride", -1f,
                "Replace top_p. Negative keeps the game's value, which is 0.95 for callers.");

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
        /// timeout of -1 or an attempt count of 900 at some point. Clamping once at load means the
        /// request path only ever sees sane numbers.
        /// </remarks>
        public void ApplyLimits(ModSettings settings)
        {
            TimeoutSeconds.Value = settings.Int(TimeoutSeconds, 1, 3600);
            MaxAttempts.Value = settings.Int(MaxAttempts, 1, 10);
            MaxTokensOverride.Value = settings.Int(MaxTokensOverride, 0, 1000000);
            MinMaxTokens.Value = settings.Int(MinMaxTokens, 0, 1000000);
        }
    }
}
