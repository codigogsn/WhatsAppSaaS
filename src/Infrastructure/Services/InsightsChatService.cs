using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class InsightsChatService : IInsightsChatService
{
    private readonly IBusinessInsightsService _bizInsights;
    private readonly IFounderInsightsService _founderInsights;
    private readonly HttpClient _http;
    private readonly AiParserOptions _options;
    private readonly ILogger<InsightsChatService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const string SystemPrompt = """
        You are a concise business analyst for a restaurant WhatsApp ordering platform.
        Answer the user's question using ONLY the provided business data.
        Do NOT invent data. Do NOT speculate beyond what is given.
        Keep answers short (2-4 sentences max), actionable, and in the same language as the question.
        If the data is insufficient to answer, say so clearly.
        """;

    public InsightsChatService(
        IBusinessInsightsService bizInsights,
        IFounderInsightsService founderInsights,
        HttpClient http,
        IOptions<AiParserOptions> options,
        ILogger<InsightsChatService> logger)
    {
        _bizInsights = bizInsights;
        _founderInsights = founderInsights;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<InsightsChatResponse> AskAsync(InsightsChatRequest request, Guid? businessId, CancellationToken ct)
    {
        // 1. Gather context from existing insights services (no direct DB access)
        string contextJson;
        try
        {
            if (request.Scope == "founder")
            {
                var data = await _founderInsights.GetInsightsAsync(ct);
                contextJson = JsonSerializer.Serialize(data, JsonOpts);
            }
            else
            {
                if (!businessId.HasValue)
                    return new InsightsChatResponse { Answer = "No business context available.", Confidence = "low" };

                var data = await _bizInsights.GetInsightsAsync(businessId.Value, 30, ct);
                contextJson = JsonSerializer.Serialize(data, JsonOpts);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "INSIGHTS CHAT: failed to gather context for scope={Scope}", request.Scope);
            return Fallback();
        }

        // 2. Call OpenAI
        var apiKey = _options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("INSIGHTS CHAT: OPENAI_API_KEY not configured");
            return Fallback();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var body = new
            {
                model = _options.Model,
                temperature = 0.3,
                max_tokens = 300,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = $"Business data:\n{contextJson}\n\nQuestion: {request.Question}" }
                }
            };

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            httpReq.Content = JsonContent.Create(body, options: JsonOpts);

            using var res = await _http.SendAsync(httpReq, cts.Token);

            if (!res.IsSuccessStatusCode)
            {
                var errBody = await res.Content.ReadAsStringAsync(cts.Token);
                _logger.LogWarning("INSIGHTS CHAT: OpenAI returned {Status}: {Body}", (int)res.StatusCode, errBody);
                return Fallback();
            }

            var raw = await res.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            _logger.LogInformation("INSIGHTS CHAT: answered for scope={Scope}", request.Scope);

            return new InsightsChatResponse
            {
                Answer = answer.Trim(),
                Confidence = answer.Length > 20 ? "high" : "medium"
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("INSIGHTS CHAT: OpenAI call timed out");
            return Fallback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "INSIGHTS CHAT: OpenAI call failed");
            return Fallback();
        }
    }

    private static InsightsChatResponse Fallback() => new()
    {
        Answer = "Insights are available in the panel above, but the AI assistant is temporarily unavailable.",
        Confidence = "low"
    };
}
