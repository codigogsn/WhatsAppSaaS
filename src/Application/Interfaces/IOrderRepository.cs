using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IOrderRepository
{
    Task AddOrderAsync(Order order, CancellationToken ct = default);
}

