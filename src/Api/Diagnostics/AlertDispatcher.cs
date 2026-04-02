using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Serilog;

namespace WhatsAppSaaS.Api.Diagnostics;

/// <summary>
/// Fires a webhook POST to ALERT_WEBHOOK_URL for critical queue alerts.
/// Never throws. Never blocks. Best-effort only.
/// </summary>
public static class AlertDispatcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly string? WebhookUrl = Environment.GetEnvironmentVariable("ALERT_WEBHOOK_URL");

    public static void TrySendAlert(string message, LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(WebhookUrl)) return;
        if (level < LogLevel.Warning) return;
        if (!message.Contains("QUEUE ALERT")) return;

        // Fire and forget — never block the worker
        _ = Task.Run(async () =>
        {
            try
            {
                await Http.PostAsJsonAsync(WebhookUrl, new { text = message });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "AlertDispatcher: webhook POST failed");
            }
        });
    }
}
