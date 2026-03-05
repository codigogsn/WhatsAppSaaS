using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IWebhookProcessor
{
    Task ProcessAsync(WebhookPayload payload, BusinessContext businessContext, CancellationToken cancellationToken = default);
}
