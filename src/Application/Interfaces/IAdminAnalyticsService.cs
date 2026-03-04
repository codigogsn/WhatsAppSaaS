using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhatsAppSaaS.Application.DTOs;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IAdminAnalyticsService
{
    Task<AnalyticsSummaryDto> GetSummaryAsync(CancellationToken ct = default);
    Task<List<TopProductDto>> GetTopProductsAsync(int take = 10, CancellationToken ct = default);
    Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take = 50, CancellationToken ct = default);
}
