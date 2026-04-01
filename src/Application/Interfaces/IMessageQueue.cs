using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;

namespace WhatsAppSaaS.Application.Interfaces;

public sealed record QueuedMessage(WebhookPayload Payload, BusinessContext BusinessContext);

public interface IMessageQueue
{
    ValueTask EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default);
    ValueTask<QueuedMessage?> DequeueAsync(CancellationToken cancellationToken = default);
}
