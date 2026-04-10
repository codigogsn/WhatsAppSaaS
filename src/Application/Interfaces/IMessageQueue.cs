using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;

namespace WhatsAppSaaS.Application.Interfaces;

public sealed record QueuedMessage(WebhookPayload Payload, BusinessContext BusinessContext);

public interface IMessageQueue
{
    ValueTask EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default);
    ValueTask EnqueueAsync(QueuedMessage message, string? messageId, CancellationToken cancellationToken = default) =>
        EnqueueAsync(message, cancellationToken);
    ValueTask<QueuedMessage?> DequeueAsync(CancellationToken cancellationToken = default);
}
