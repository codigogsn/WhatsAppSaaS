using System;
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

    Task<SalesAnalyticsDto> GetSalesAsync(Guid businessId, CancellationToken ct = default);
    Task<ProductAnalyticsDto> GetProductAnalyticsAsync(Guid businessId, CancellationToken ct = default);
    Task<ConversionAnalyticsDto> GetConversionAsync(Guid businessId, CancellationToken ct = default);
    Task<OperationalAnalyticsDto> GetOperationalAsync(Guid businessId, CancellationToken ct = default);
    Task<BusinessIntelligenceDto> GetBusinessIntelligenceAsync(Guid businessId, CancellationToken ct = default);
    Task<RestaurantInsightsDto> GetRestaurantInsightsAsync(Guid businessId, CancellationToken ct = default);
}
