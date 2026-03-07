using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IOrderRepository
{
    Task AddOrderAsync(Order order, CancellationToken ct = default);
    Task<Order?> GetLastCompletedOrderAsync(string fromPhone, Guid businessId, CancellationToken ct = default);
    Task<Customer?> GetCustomerByPhoneAsync(string fromPhone, Guid businessId, CancellationToken ct = default);
}

