using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IWhatsAppClient
{
    Task<bool> SendTextMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default);
}
