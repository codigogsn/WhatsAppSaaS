using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Infrastructure.Ai;

public sealed class AiParser : IAiParser
{
    private readonly HttpClient _httpClient;
    private readonly AiParserOptions _options;
    private readonly ILogger<AiParser> _logger;

    // ── Serializer options shared across calls ──────────
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    // ── The strict system prompt ────────────────────────
    private const string SystemPrompt = """
        You are the intent-parser for a restaurant WhatsApp chatbot.
        Your ONLY job is to analyse the customer's message and return a JSON object.

        RULES:
        1. Reply with **pure JSON only** — no markdown, no explanation, no extra keys.
        2. Use this exact schema (do NOT add or remove fields):

        {
          "intent": "<order_create | reservation_create | human_handoff | general>",
          "args": {
            "order":       { "items": [{"name":"…","quantity":1}], "delivery_type":"pickup|delivery|null", "notes":"…|null" }   // only when intent=order_create
            "reservation": { "date":"YYYY-MM-DD|null", "time":"HH:mm|null", "party_size":<int|null>, "name":"…|null" }          // only when intent=reservation_create
            "handoff":     { "reason":"…" }                                                                                       // only when intent=human_handoff
            "general":     { "topic":"…", "suggested_reply":"…" }                                                                // only when intent=general
          },
          "missing_fields": ["field1","field2"],
          "confidence": 0.00
        }

        3. Populate ONLY the sub-object inside "args" that matches the intent.  Set the other three to null.
        4. "missing_fields" lists fields the customer has NOT yet provided that are required to fulfil the intent:
           - order_create  requires: items (at least one), delivery_type
           - reservation_create requires: date, time, party_size, name
           - human_handoff / general: missing_fields is always []
        5. "confidence" is your estimate (0.0–1.0) of how certain you are about the intent.
        6. If the message is ambiguous or a greeting, use intent=general.
        7. If the customer explicitly asks for a human, manager, or says they have a complaint, use intent=human_handoff.
        8. Always respond in the SAME LANGUAGE as the customer's message for suggested_reply.
        9. Do NOT wrap the JSON in ```json``` code fences.
        """;

    public AiParser(
        HttpClient httpClient,
        IOptions<AiParserOptions> options,
        ILogger<AiParser> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiParseResult> ParseAsync(
        string message,
        string from,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("AiParser invoked for conversation {ConversationId}", conversationId);

        var apiKey = _options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("OPENAI_API_KEY is not configured — attempting local fallback");
            return FallbackWithLocalParse("api_key_missing", message, from);
        }

        // ── Build the chat-completion request ───────────
        var requestBody = new
        {
            model = _options.Model,
            temperature = _options.Temperature,
            max_tokens = _options.MaxTokens,
            response_format = new { type = "json_object" },   // forces JSON mode
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = $"Customer ID: {conversationId}\nMessage: {message}" }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(requestBody, options: JsonOpts);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "OpenAI returned {StatusCode}: {Body}",
                    (int)response.StatusCode, errorBody);
                return FallbackWithLocalParse("llm_http_error", message, from);
            }

            // ── Extract the assistant content ───────────
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var assistantJson = ExtractAssistantContent(raw);

            if (string.IsNullOrWhiteSpace(assistantJson))
            {
                _logger.LogWarning("Empty assistant content in OpenAI response");
                return FallbackWithLocalParse("empty_response", message, from);
            }

            _logger.LogDebug("LLM raw output for {ConversationId}: {Raw}", conversationId, assistantJson);

            // ── Parse into our typed result ─────────────
            var result = JsonSerializer.Deserialize<AiParseResult>(assistantJson, JsonOpts);

            if (result is null)
            {
                _logger.LogWarning("Deserialization returned null for conversation {ConversationId}", conversationId);
                return FallbackWithLocalParse("parse_failed", message, from);
            }

            _logger.LogInformation(
                "AiParser result: intent={Intent} confidence={Confidence} missing=[{Missing}] conversation={ConversationId}",
                result.Intent, result.Confidence,
                string.Join(", ", result.MissingFields),
                conversationId);

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse LLM JSON for conversation {ConversationId}", conversationId);
            return FallbackWithLocalParse("parse_failed", message, from);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error calling OpenAI for conversation {ConversationId}", conversationId);
            return FallbackWithLocalParse("llm_http_error", message, from);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout calling OpenAI for conversation {ConversationId}", conversationId);
            return FallbackWithLocalParse("llm_timeout", message, from);
        }
    }

    // ── Helpers ──────────────────────────────────────────

    /// <summary>
    /// Dig into the OpenAI chat-completion envelope to pull out
    /// choices[0].message.content as a string.
    /// </summary>
    private static string? ExtractAssistantContent(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
    }

    /// <summary>
    /// Attempts a simple local regex parse before falling back to generic response.
    /// Matches patterns like "2 hamburguesas", "1 coca cola y 3 tequeños".
    /// </summary>
    private AiParseResult FallbackWithLocalParse(string reason, string message, string from)
    {
        try
        {
            var items = LocalParseItems(message);
            if (items.Count > 0)
            {
                _logger.LogInformation("AI FALLBACK PARSE: recovered simple order — {Items}",
                    string.Join(", ", items.Select(i => $"{i.Quantity}x {i.Name}")));

                return new AiParseResult
                {
                    Intent = RestaurantIntent.OrderCreate,
                    Confidence = 0.6,
                    MissingFields = ["delivery_type"],
                    Args = new ParsedArgs
                    {
                        Order = new OrderArgs { Items = items }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local fallback parse failed (non-fatal)");
        }

        return Fallback(reason);
    }

    /// <summary>
    /// Extracts quantity+item pairs from simple Spanish order text.
    /// Matches: "2 hamburguesas", "una coca cola", "3 tequeños y 1 pepsi"
    /// </summary>
    private static List<OrderItem> LocalParseItems(string text)
    {
        var items = new List<OrderItem>();
        var normalized = text.Trim().ToLowerInvariant();

        // Split on common separators: "y", ",", "+"
        var segments = Regex.Split(normalized, @"\s*(?:,|\by\b|\+)\s*");

        // Pattern: optional number/word-number + item name (at least 2 chars)
        var itemPattern = new Regex(
            @"^(?:(?<qty>\d{1,2})\s+|(?<word>una?|dos|tres|cuatro|cinco|media)\s+)?(?<name>[a-záéíóúñü][a-záéíóúñü\s]{1,40})$",
            RegexOptions.Compiled);

        var wordToNum = new Dictionary<string, int>
        {
            ["un"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3,
            ["cuatro"] = 4, ["cinco"] = 5, ["media"] = 1
        };

        foreach (var seg in segments)
        {
            var s = seg.Trim();
            if (s.Length < 2) continue;

            var m = itemPattern.Match(s);
            if (!m.Success) continue;

            var qty = 1;
            if (m.Groups["qty"].Success)
                qty = int.Parse(m.Groups["qty"].Value);
            else if (m.Groups["word"].Success && wordToNum.TryGetValue(m.Groups["word"].Value, out var wn))
                qty = wn;

            var name = m.Groups["name"].Value.Trim();
            if (name.Length < 2 || qty < 1 || qty > 20) continue;

            items.Add(new OrderItem { Name = name, Quantity = qty });
        }

        return items;
    }

    /// <summary>
    /// Safe fallback when the LLM call or parse fails and local parse found nothing.
    /// </summary>
    private static AiParseResult Fallback(string reason) => new()
    {
        Intent = RestaurantIntent.General,
        Confidence = 0.2,
        MissingFields = [reason],
        Args = new ParsedArgs
        {
            General = new GeneralArgs
            {
                Topic = "fallback",
                SuggestedReply = null
            }
        }
    };
}
