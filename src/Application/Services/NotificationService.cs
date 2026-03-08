using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class NotificationService : INotificationService
{
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly IBackgroundJobService? _jobService;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IWhatsAppClient whatsAppClient, ILogger<NotificationService> logger, IBackgroundJobService? jobService = null)
    {
        _whatsAppClient = whatsAppClient;
        _logger = logger;
        _jobService = jobService;
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

        // If background job service is available, enqueue for retry-safe delivery
        if (_jobService is not null)
        {
            try
            {
                await _jobService.EnqueueAsync("SendNotification", new
                {
                    To = business.NotificationPhone,
                    Body = body,
                    PhoneNumberId = business.PhoneNumberId,
                    AccessToken = business.AccessToken
                }, business.BusinessId, maxRetries: 3, ct: ct);

                _logger.LogInformation("Staff notification enqueued for business {BusinessId}", business.BusinessId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue notification job, falling back to direct send");
            }
        }

        // Fallback: direct send (best-effort, no retry)
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
