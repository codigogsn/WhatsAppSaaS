using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IBotService
{
    Task<string> GenerateReplyAsync(IncomingMessage message, CancellationToken cancellationToken = default);
}
