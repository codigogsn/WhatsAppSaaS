using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

public sealed record MediaDownloadResult(byte[] Data, string ContentType);

public interface IWhatsAppClient
{
    Task<bool> SendTextMessageAsync(OutgoingMessage message, CancellationToken cancellationToken = default);
    Task<MediaDownloadResult?> GetMediaAsync(string mediaId, string? accessToken = null, CancellationToken cancellationToken = default);
}
