using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSaaS.Application.Common;

namespace WhatsAppSaaS.Application.Services;

public sealed class EmailAIService
{
    private readonly HttpClient _httpClient;
    private readonly AiParserOptions _options;
    private readonly ILogger<EmailAIService> _logger;

    public EmailAIService(HttpClient httpClient, IOptions<AiParserOptions> options, ILogger<EmailAIService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailAIResult> ProcessAsync(string subject, string body, string tone = "professional", CancellationToken ct = default)
    {
        var apiKey = _options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OpenAI API key not configured — returning placeholder");
            return new EmailAIResult
            {
                Summary = "AI unavailable — API key not configured",
                SuggestedReply = ""
            };
        }

        var truncatedBody = body.Length > 2000 ? body[..2000] + "..." : body;

        var systemPrompt = $$"""
            You are a business email assistant. Analyze the email and respond with ONLY valid JSON:
            {
              "summary": "2-3 sentence summary of the email content and intent",
              "suggested_reply": "A short professional reply in {{tone}} tone"
            }
            Rules:
            - Be concise and actionable
            - Match the language of the original email
            - Do NOT wrap in code fences
            """;

        var userPrompt = $"Subject: {subject}\n\nBody:\n{truncatedBody}";

        var requestBody = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            max_tokens = 512,
            temperature = 0.3
        };

        try
        {
            var json = JsonSerializer.Serialize(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI email processing failed: {Status}", response.StatusCode);
                return new EmailAIResult { Summary = "AI processing failed", SuggestedReply = "" };
            }

            using var doc = JsonDocument.Parse(responseBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var nl = content.IndexOf('\n');
                if (nl > 0) content = content[(nl + 1)..];
                if (content.EndsWith("```")) content = content[..^3];
                content = content.Trim();
            }

            var parsed = JsonSerializer.Deserialize<EmailAIResult>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed ?? new EmailAIResult { Summary = content, SuggestedReply = "" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email AI processing error");
            return new EmailAIResult { Summary = "AI processing error", SuggestedReply = "" };
        }
    }
}

public class EmailAIResult
{
    public string Summary { get; set; } = string.Empty;
    public string SuggestedReply { get; set; } = string.Empty;
}
