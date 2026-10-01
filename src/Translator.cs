using System;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScamWYF.AiBackend
{
    /// <summary>
    /// Rewrites the OpenRouter-shaped body the game builds into something a third-party server
    /// will accept, and rewrites the answer back into the shape the game expects to read.
    /// </summary>
    internal static class Translator
    {
        private static readonly Regex ThinkBlock = new Regex(
            @"<\s*(think|thinking|reasoning)\s*>.*?<\s*/\s*\1\s*>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex UnclosedThinkPrefix = new Regex(
            @"\A\s*<\s*(think|thinking|reasoning)\s*>.*?(<\s*/\s*\1\s*>|\z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // ---------------------------------------------------------------- request

        public static JObject BuildRequest(JObject gameBody, Config cfg, out StructuredOutputMode effectiveMode)
        {
            var mode = cfg.StructuredOutputs.Value;
            if (mode == StructuredOutputMode.Auto)
            {
                mode = cfg.Mode.Value == BackendMode.OllamaNative
                    ? StructuredOutputMode.JsonSchema   // Ollama takes a schema in its own "format" field
                    : StructuredOutputMode.JsonSchema;
            }
            effectiveMode = mode;

            var messages = gameBody["messages"] as JArray;
            messages = messages != null ? (JArray)messages.DeepClone() : new JArray();

            var schema = ExtractSchema(gameBody["response_format"] as JObject);

            // When the server cannot be told to honour a schema, say it in words instead.
            if (schema != null && (mode == StructuredOutputMode.JsonObject || mode == StructuredOutputMode.Prompt))
                AppendToSystemMessage(messages, DescribeSchema(schema));

            return cfg.Mode.Value == BackendMode.OllamaNative
                ? BuildOllamaRequest(gameBody, messages, schema, cfg)
                : BuildOpenAiRequest(gameBody, messages, schema, cfg, mode);
        }

        private static JObject BuildOpenAiRequest(JObject gameBody, JArray messages, JObject schema, Config cfg, StructuredOutputMode mode)
        {
            var body = (JObject)gameBody.DeepClone();
            body["messages"] = messages;

            // "provider" and "session_id" are OpenRouter extensions. OpenAI 400s on unknown fields.
            if (!cfg.KeepOpenRouterFields.Value)
            {
                body.Remove("provider");
                body.Remove("session_id");
            }

            var model = cfg.Model.Value;
            if (!string.IsNullOrEmpty(model.Trim()))
                body["model"] = model.Trim();

            ApplyTokenLimit(body, cfg);
            ApplySampling(body, cfg);

            body.Remove("response_format");
            if (schema != null)
            {
                if (mode == StructuredOutputMode.JsonSchema)
                    body["response_format"] = gameBody["response_format"].DeepClone();
                else if (mode == StructuredOutputMode.JsonObject)
                    body["response_format"] = new JObject { ["type"] = "json_object" };
                // Prompt: nothing, the instruction is already in the system message.
            }

            return body;
        }

        private static JObject BuildOllamaRequest(JObject gameBody, JArray messages, JObject schema, Config cfg)
        {
            var model = cfg.Model.Value.Trim();
            if (string.IsNullOrEmpty(model))
                model = (string)gameBody["model"] ?? "";

            var options = new JObject();

            var temperature = cfg.TemperatureOverride.Value >= 0f
                ? (float?)cfg.TemperatureOverride.Value
                : ReadFloat(gameBody, "temperature");
            if (temperature.HasValue) options["temperature"] = temperature.Value;

            var topP = cfg.TopPOverride.Value >= 0f
                ? (float?)cfg.TopPOverride.Value
                : ReadFloat(gameBody, "top_p");
            if (topP.HasValue) options["top_p"] = topP.Value;

            var tokens = ResolveTokenLimit(ReadInt(gameBody, "max_tokens"), cfg);
            if (tokens.HasValue) options["num_predict"] = tokens.Value;

            var body = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["stream"] = false
            };
            if (options.Count > 0) body["options"] = options;

            if (schema != null)
            {
                var mode = cfg.StructuredOutputs.Value;
                if (mode == StructuredOutputMode.Auto || mode == StructuredOutputMode.JsonSchema)
                    body["format"] = schema.DeepClone();
                else if (mode == StructuredOutputMode.JsonObject)
                    body["format"] = "json";
            }

            return body;
        }

        private static void ApplyTokenLimit(JObject body, Config cfg)
        {
            var current = ReadInt(body, "max_tokens") ?? ReadInt(body, "max_completion_tokens");
            body.Remove("max_tokens");
            body.Remove("max_completion_tokens");

            var resolved = ResolveTokenLimit(current, cfg);
            if (!resolved.HasValue) return;

            var field = cfg.MaxTokensFieldName.Value;
            if (string.IsNullOrEmpty(field.Trim())) field = "max_tokens";
            body[field.Trim()] = resolved.Value;
        }

        private static int? ResolveTokenLimit(int? gameValue, Config cfg)
        {
            var value = gameValue;
            if (cfg.MaxTokensOverride.Value > 0) value = cfg.MaxTokensOverride.Value;
            if (cfg.MinMaxTokens.Value > 0 && (!value.HasValue || value.Value < cfg.MinMaxTokens.Value))
                value = cfg.MinMaxTokens.Value;
            return value;
        }

        private static void ApplySampling(JObject body, Config cfg)
        {
            if (cfg.TemperatureOverride.Value >= 0f) body["temperature"] = cfg.TemperatureOverride.Value;
            if (cfg.TopPOverride.Value >= 0f) body["top_p"] = cfg.TopPOverride.Value;
        }

        // ---------------------------------------------------------------- response

        /// <summary>
        /// Normalises whatever the server said into the OpenAI chat-completion shape the game
        /// reads: choices[0].message.content.
        /// </summary>
        public static JObject NormalizeResponse(JObject raw, Config cfg)
        {
            if (raw == null) throw new InvalidOperationException("The AI server returned an empty response.");

            var content = ReadContent(raw);
            if (content == null)
            {
                var error = raw["error"];
                if (error != null)
                    throw new InvalidOperationException("The AI server returned an error: " + error.ToString(Formatting.None));
                throw new InvalidOperationException(
                    "The AI server's response had no assistant text. Body: " + Truncate(raw.ToString(Formatting.None), 600));
            }

            if (cfg.StripThinkTags.Value) content = StripThinking(content);

            return new JObject
            {
                ["id"] = (string)raw["id"] ?? Guid.NewGuid().ToString("D"),
                ["object"] = "chat.completion",
                ["model"] = (string)raw["model"] ?? "",
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["index"] = 0,
                        ["finish_reason"] = (string)raw["finish_reason"] ?? "stop",
                        ["message"] = new JObject
                        {
                            ["role"] = "assistant",
                            ["content"] = content
                        }
                    }
                }
            };
        }

        private static string ReadContent(JObject raw)
        {
            // OpenAI-compatible
            var token = raw["choices"] is JArray choices && choices.Count > 0
                ? choices[0]["message"]?["content"]
                : null;

            // Ollama native
            if (IsEmpty(token)) token = raw["message"]?["content"];

            // Ollama /api/generate and a few llama.cpp builds
            if (IsEmpty(token)) token = raw["response"];

            if (IsEmpty(token)) return null;

            if (token.Type == JTokenType.String) return token.Value<string>();

            // Some servers return content as an array of parts.
            if (token is JArray parts)
            {
                var sb = new StringBuilder();
                foreach (var part in parts)
                {
                    var text = part["text"]?.Value<string>();
                    if (!string.IsNullOrEmpty(text)) sb.Append(text);
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }

            return token.ToString(Formatting.None);
        }

        private static bool IsEmpty(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return true;
            return token.Type == JTokenType.String && string.IsNullOrEmpty(token.Value<string>());
        }

        public static string StripThinking(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var cleaned = ThinkBlock.Replace(text, "");
            // A model that hit its token limit mid-thought leaves an unclosed opening tag.
            if (cleaned.TrimStart().StartsWith("<", StringComparison.Ordinal))
                cleaned = UnclosedThinkPrefix.Replace(cleaned, "");
            return cleaned.Trim();
        }

        // ---------------------------------------------------------------- schema helpers

        private static JObject ExtractSchema(JObject responseFormat)
        {
            if (responseFormat == null) return null;
            return responseFormat["json_schema"]?["schema"] as JObject;
        }

        private static string DescribeSchema(JObject schema)
        {
            return
                "OUTPUT FORMAT (enforced by the game, not optional): reply with exactly one JSON " +
                "object and nothing else. No prose before or after it, no markdown, no code fences, " +
                "no trailing commentary. Include every required property and no properties beyond " +
                "those listed. The object must validate against this JSON Schema:\n" +
                schema.ToString(Formatting.None);
        }

        private static void AppendToSystemMessage(JArray messages, string instruction)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i] as JObject;
                if (message == null || (string)message["role"] != "system") continue;
                message["content"] = (string)message["content"] + "\n\n" + instruction;
                return;
            }
            messages.Insert(0, new JObject { ["role"] = "system", ["content"] = instruction });
        }

        // ---------------------------------------------------------------- misc

        private static int? ReadInt(JObject body, string name)
        {
            var token = body[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            try { return token.Value<int>(); } catch { return null; }
        }

        private static float? ReadFloat(JObject body, string name)
        {
            var token = body[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            try { return token.Value<float>(); } catch { return null; }
        }

        public static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "... (" + text.Length + " chars)";
        }
    }
}
