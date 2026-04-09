using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WhatsAppSaaS.Application.Services;

public sealed class GmailService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GmailService> _logger;

    public GmailService(HttpClient httpClient, ILogger<GmailService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<GmailEmail>> FetchRecentEmailsAsync(int maxResults = 20, CancellationToken ct = default)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogError("Failed to obtain Gmail access token");
            return new List<GmailEmail>();
        }

        var results = new List<GmailEmail>();

        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var listUrl = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults={maxResults}&labelIds=INBOX";
            var listResponse = await _httpClient.GetAsync(listUrl, ct);

            if (!listResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Gmail list failed: {Status}", listResponse.StatusCode);
                return results;
            }

            var listJson = await listResponse.Content.ReadAsStringAsync(ct);
            using var listDoc = JsonDocument.Parse(listJson);

            if (!listDoc.RootElement.TryGetProperty("messages", out var messages))
            {
                _logger.LogInformation("No messages in Gmail inbox");
                return results;
            }

            foreach (var msgRef in messages.EnumerateArray())
            {
                var msgId = msgRef.GetProperty("id").GetString();
                if (msgId is null) continue;

                try
                {
                    var detail = await FetchMessageDetailAsync(msgId, accessToken, ct);
                    if (detail is not null)
                        results.Add(detail);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch Gmail message {MessageId}", msgId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gmail fetch failed");
        }

        _logger.LogInformation("Fetched {Count} emails from Gmail", results.Count);
        return results;
    }

    private async Task<GmailEmail?> FetchMessageDetailAsync(string messageId, string accessToken, CancellationToken ct)
    {
        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{messageId}?format=full";
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var headers = root.GetProperty("payload").GetProperty("headers");
        var subject = GetHeader(headers, "Subject") ?? "";
        var from = GetHeader(headers, "From") ?? "";
        var snippet = root.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";

        var body = ExtractBody(root.GetProperty("payload"));

        return new GmailEmail
        {
            MessageId = messageId,
            Subject = subject,
            From = from,
            Snippet = snippet,
            Body = body
        };
    }

    private static string ExtractBody(JsonElement payload)
    {
        // Try text/plain first
        if (payload.TryGetProperty("body", out var body) &&
            body.TryGetProperty("data", out var data) &&
            data.GetString() is { } d)
        {
            var mimeType = payload.TryGetProperty("mimeType", out var mt) ? mt.GetString() : "";
            if (mimeType == "text/plain")
                return DecodeBase64Url(d);
        }

        // Check parts
        if (payload.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                var mime = part.TryGetProperty("mimeType", out var m) ? m.GetString() : "";
                if (mime == "text/plain" &&
                    part.TryGetProperty("body", out var partBody) &&
                    partBody.TryGetProperty("data", out var partData) &&
                    partData.GetString() is { } pd)
                {
                    return DecodeBase64Url(pd);
                }
            }

            // Recurse into multipart
            foreach (var part in parts.EnumerateArray())
            {
                var result = ExtractBody(part);
                if (!string.IsNullOrEmpty(result)) return result;
            }
        }

        return "";
    }

    private static string DecodeBase64Url(string base64Url)
    {
        try
        {
            var base64 = base64Url.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch
        {
            return "";
        }
    }

    private static string? GetHeader(JsonElement headers, string name)
    {
        foreach (var h in headers.EnumerateArray())
        {
            if (string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                return h.GetProperty("value").GetString();
        }
        return null;
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var clientId = Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET");
        var refreshToken = Environment.GetEnvironmentVariable("GMAIL_REFRESH_TOKEN");

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogError("Gmail OAuth credentials not configured (GMAIL_CLIENT_ID, GMAIL_CLIENT_SECRET, GMAIL_REFRESH_TOKEN)");
            return null;
        }

        var tokenRequest = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("grant_type", "refresh_token")
        });

        var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", tokenRequest, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Gmail token refresh failed: {Status} {Body}", response.StatusCode, err);
            return null;
        }

        var tokenJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(tokenJson);
        return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
    }

    public bool IsRelevantEmail(GmailEmail email)
    {
        if (string.IsNullOrWhiteSpace(email.Subject)) return false;

        var bodyLower = (email.Body + " " + email.Subject).ToLowerInvariant();

        // Skip newsletters
        if (bodyLower.Contains("unsubscribe") || bodyLower.Contains("click here to unsubscribe"))
            return false;

        // Keep: invoices, payments, client communication
        var relevantKeywords = new[] { "invoice", "payment", "factura", "pago", "order", "pedido", "quote", "cotización", "proposal", "propuesta", "contract", "contrato", "meeting", "reunión", "project", "proyecto", "delivery", "entrega", "budget", "presupuesto" };
        return relevantKeywords.Any(k => bodyLower.Contains(k));
    }
}

public class GmailEmail
{
    public string MessageId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
