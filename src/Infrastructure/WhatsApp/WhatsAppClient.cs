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
        var phoneNumberId = !string.IsNullOrEmpty(message.PhoneNumberId)
            ? message.PhoneNumberId
            : _options.PhoneNumberId;

        var url = $"https://graph.facebook.com/{_options.ApiVersion}/{phoneNumberId}/messages";

        var request = new SendMessageRequest
        {
            To = message.To,
            Text = new SendMessageText { Body = message.Body }
        };

        _logger.LogDebug(
            "Sending message to {To} via phone {PhoneNumberId}",
            message.To, phoneNumberId);

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);
            httpRequest.Content = JsonContent.Create(request, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Message sent successfully to {To}, status: {StatusCode}",
                    message.To, (int)response.StatusCode);
                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Failed to send message to {To}. Status: {StatusCode}, Response: {ResponseBody}",
                message.To, (int)response.StatusCode, errorBody);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error sending message to {To} via {Url}",
                message.To, url);
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex,
                "Timeout sending message to {To} via {Url}",
                message.To, url);
            return false;
        }
    }
}
