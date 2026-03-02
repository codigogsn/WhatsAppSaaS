using WhatsAppSaaS.Application.DTOs;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IWebhookProcessor
{
    Task ProcessAsync(WebhookPayload payload, CancellationToken cancellationToken = default);
}
