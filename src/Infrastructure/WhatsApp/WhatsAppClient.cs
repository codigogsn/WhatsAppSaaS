using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Infrastructure.WhatsApp;

public sealed class WhatsAppClient : IWhatsAppClient
{
    private readonly HttpClient _httpClient;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppClient> _logger;

    public WhatsAppClient(
        HttpClient httpClient,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendTextMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        var phoneNumberId = message.PhoneNumberId;

        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            phoneNumberId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID");
        }
        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            phoneNumberId = _options.PhoneNumberId;
        }

        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            _logger.LogError("WhatsApp send aborted: missing PhoneNumberId (message/env/options).");
            return false;
        }

        // Token resolution order: env var > message.AccessToken (per-business) > appsettings
        var accessToken = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
                          ?? Environment.GetEnvironmentVariable("META_ACCESS_TOKEN");

        if (string.IsNullOrWhiteSpace(accessToken))
            accessToken = message.AccessToken;

        if (string.IsNullOrWhiteSpace(accessToken))
            accessToken = _options.AccessToken;

        if (string.IsNullOrWhiteSpace(accessToken) || accessToken == "your-access-token-here")
        {
            _logger.LogWarning("WhatsApp send skipped: no valid AccessToken configured. Set WHATSAPP_ACCESS_TOKEN env var, or configure per-business token, or configure WhatsApp:AccessToken in appsettings.");
            return false;
        }

        var url = $"https://graph.facebook.com/{_options.ApiVersion}/{phoneNumberId}/messages";

        // Build payload: document, location request, interactive buttons, or plain text
        SendMessageRequest request;
        if (message.LocationRequest)
        {
            request = new SendMessageRequest
            {
                To = message.To,
                Type = "interactive",
                Interactive = new SendLocationRequestPayload
                {
                    Body = new SendInteractiveBody { Text = message.Body },
                    Action = new SendLocationRequestAction()
                }
            };
        }
        else if (!string.IsNullOrWhiteSpace(message.DocumentUrl))
        {
            request = new SendMessageRequest
            {
                To = message.To,
                Type = "document",
                Document = new SendDocumentPayload
                {
                    Link = message.DocumentUrl,
                    Caption = message.Body,
                    Filename = message.DocumentFilename
                }
            };
        }
        else if (message.Buttons is { Count: > 0 })
        {
            request = new SendMessageRequest
            {
                To = message.To,
                Type = "interactive",
                Interactive = new SendInteractivePayload
                {
                    Body = new SendInteractiveBody { Text = message.Body },
                    Action = new SendInteractiveAction
                    {
                        Buttons = message.Buttons.Select(b => new SendInteractiveButton
                        {
                            Reply = new SendInteractiveReply { Id = b.Id, Title = b.Title }
                        }).ToList()
                    }
                }
            };
        }
        else
        {
            request = new SendMessageRequest
            {
                To = message.To,
                Text = new SendMessageText { Body = message.Body }
            };
        }

        _logger.LogDebug("Sending message to {To} via phone {PhoneNumberId}", message.To, phoneNumberId);

        var retryDelays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };
        var retryableStatuses = new HashSet<int> { 429, 500, 502, 503, 504 };

        for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
                httpRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                httpRequest.Content = JsonContent.Create(request, options: new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Message sent successfully to {To}, status: {StatusCode}", message.To, (int)response.StatusCode);
                    return true;
                }

                var statusCode = (int)response.StatusCode;

                if (retryableStatuses.Contains(statusCode) && attempt < retryDelays.Length)
                {
                    var delay = retryDelays[attempt];

                    // Respect Retry-After header if present
                    if (response.Headers.RetryAfter?.Delta is { } retryAfter && retryAfter.TotalSeconds is > 0 and <= 10)
                        delay = retryAfter;

                    _logger.LogWarning("WhatsApp API returned {StatusCode} for {To}, retrying in {Delay}s (attempt {Attempt})",
                        statusCode, message.To, delay.TotalSeconds, attempt + 1);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Failed to send message to {To}. Status: {StatusCode}, Response: {ResponseBody}",
                    message.To, statusCode, errorBody);

                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error sending message to {To} via {Url}", message.To, url);
                return false;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogError(ex, "Timeout sending message to {To} via {Url}", message.To, url);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending message to {To} via {Url}", message.To, url);
                return false;
            }
        }

        return false;
    }

    public async Task<MediaDownloadResult?> GetMediaAsync(string mediaId, string? accessToken = null, CancellationToken cancellationToken = default)
    {
        // Resolve token: parameter (per-business) > env var > appsettings
        var token = accessToken;
        if (string.IsNullOrWhiteSpace(token))
            token = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
                    ?? Environment.GetEnvironmentVariable("META_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            token = _options.AccessToken;
        if (string.IsNullOrWhiteSpace(token) || token == "your-access-token-here")
        {
            _logger.LogWarning("GetMediaAsync skipped for {MediaId}: no valid AccessToken. " +
                "Checked: parameter={ParamEmpty}, env WHATSAPP_ACCESS_TOKEN={EnvWa}, META_ACCESS_TOKEN={EnvMeta}, appsettings={Appsettings}",
                mediaId,
                string.IsNullOrWhiteSpace(accessToken),
                Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") is not null,
                Environment.GetEnvironmentVariable("META_ACCESS_TOKEN") is not null,
                !string.IsNullOrWhiteSpace(_options.AccessToken));
            return null;
        }

        try
        {
            // Step 1: Get media metadata (URL) from Graph API
            var metaUrl = $"https://graph.facebook.com/{_options.ApiVersion}/{mediaId}";
            _logger.LogDebug("GetMediaAsync step1: fetching metadata from {Url} for {MediaId}", metaUrl, mediaId);

            using var metaReq = new HttpRequestMessage(HttpMethod.Get, metaUrl);
            metaReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var metaRes = await _httpClient.SendAsync(metaReq, cancellationToken);
            if (!metaRes.IsSuccessStatusCode)
            {
                var errBody = await metaRes.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "GetMediaAsync step1 failed: metadata request for {MediaId} returned {Status}. Body: {Body}",
                    mediaId, (int)metaRes.StatusCode, errBody);
                return null;
            }

            var metaJson = await metaRes.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!metaJson.TryGetProperty("url", out var urlProp))
            {
                _logger.LogWarning("GetMediaAsync step1: no 'url' in metadata response for {MediaId}. Keys: {Keys}",
                    mediaId, string.Join(", ", metaJson.EnumerateObject().Select(p => p.Name)));
                return null;
            }
            var downloadUrl = urlProp.GetString();
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                _logger.LogWarning("GetMediaAsync step1: 'url' property is empty for {MediaId}", mediaId);
                return null;
            }

            _logger.LogDebug("GetMediaAsync step2: downloading binary from {Url} for {MediaId}", downloadUrl, mediaId);

            // Step 2: Download binary from the media URL
            // Use a NEW HttpClient request without the default Accept: application/json
            // to avoid WhatsApp CDN rejecting binary download requests
            using var dlReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            dlReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            dlReq.Headers.Accept.Clear();
            dlReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));

            using var dlRes = await _httpClient.SendAsync(dlReq, cancellationToken);
            if (!dlRes.IsSuccessStatusCode)
            {
                var errBody = await dlRes.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "GetMediaAsync step2 failed: binary download for {MediaId} returned {Status}. Body: {Body}",
                    mediaId, (int)dlRes.StatusCode, errBody);
                return null;
            }

            var contentType = dlRes.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var data = await dlRes.Content.ReadAsByteArrayAsync(cancellationToken);

            _logger.LogInformation(
                "GetMediaAsync: success — downloaded {Bytes} bytes for {MediaId}, contentType={ContentType}",
                data.Length, mediaId, contentType);

            return new MediaDownloadResult(data, contentType);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "GetMediaAsync HTTP error for {MediaId}", mediaId);
            return null;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "GetMediaAsync timeout for {MediaId} (30s limit)", mediaId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMediaAsync unexpected error for {MediaId}", mediaId);
            return null;
        }
    }
}
