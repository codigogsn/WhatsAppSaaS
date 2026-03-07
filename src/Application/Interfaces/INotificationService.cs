using WhatsAppSaaS.Application.Common;

namespace WhatsAppSaaS.Application.Interfaces;

public interface INotificationService
{
    Task NotifyNewOrderAsync(BusinessContext business, string customerName, string customerPhone, string totalText, CancellationToken ct = default);
    Task NotifyOrderConfirmedAsync(BusinessContext business, string customerName, string itemsSummary, string totalText, CancellationToken ct = default);
    Task NotifyHumanHandoffAsync(BusinessContext business, string customerPhone, CancellationToken ct = default);
    Task NotifyCustomerWaitingAsync(BusinessContext business, string customerPhone, CancellationToken ct = default);
}
