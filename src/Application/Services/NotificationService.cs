using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class NotificationService : INotificationService
{
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IWhatsAppClient whatsAppClient, ILogger<NotificationService> logger)
    {
        _whatsAppClient = whatsAppClient;
        _logger = logger;
    }

    public Task NotifyNewOrderAsync(BusinessContext business, string customerName, string customerPhone, string totalText, CancellationToken ct = default)
        => SendStaffNotificationAsync(business, Msg.NotifyNewOrder(customerName, customerPhone, totalText), ct);

    public Task NotifyOrderConfirmedAsync(BusinessContext business, string customerName, string itemsSummary, string totalText, CancellationToken ct = default)
        => SendStaffNotificationAsync(business, Msg.NotifyOrderConfirmed(customerName, itemsSummary, totalText), ct);

    public Task NotifyHumanHandoffAsync(BusinessContext business, string customerPhone, CancellationToken ct = default)
        => SendStaffNotificationAsync(business, Msg.NotifyHumanHandoff(customerPhone), ct);

    public Task NotifyCustomerWaitingAsync(BusinessContext business, string customerPhone, CancellationToken ct = default)
        => SendStaffNotificationAsync(business, Msg.NotifyCustomerWaiting(customerPhone), ct);

    private async Task SendStaffNotificationAsync(BusinessContext business, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(business.NotificationPhone))
            return;

        try
        {
            await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
            {
                To = business.NotificationPhone,
                Body = body,
                PhoneNumberId = business.PhoneNumberId,
                AccessToken = business.AccessToken
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send staff notification to {Phone} for business {BusinessId}",
                business.NotificationPhone, business.BusinessId);
        }
    }
}
