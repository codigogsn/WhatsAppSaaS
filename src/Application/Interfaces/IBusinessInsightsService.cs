namespace WhatsAppSaaS.Application.Interfaces;

public interface IBusinessInsightsService
{
    Task<BusinessInsightsResponse> GetInsightsAsync(Guid businessId, int windowDays, CancellationToken cancellationToken);
}

// ── Response DTOs ──

public sealed class BusinessInsightsResponse
{
    public Guid BusinessId { get; init; }
    public int WindowDays { get; init; }
    public DateTime GeneratedAtUtc { get; init; }
    public InsightsMetrics Metrics { get; init; } = new();
    public List<ItemPerformance> TopSellingItems { get; init; } = [];
    public List<ItemPerformance> LowPerformingItems { get; init; } = [];
    public List<HourVolume> PeakHours { get; init; } = [];
    public List<InsightEntry> Insights { get; init; } = [];
    public List<string> Recommendations { get; init; } = [];
    public List<string> Alerts { get; init; } = [];
}

public sealed class InsightsMetrics
{
    public int CompletedOrders { get; init; }
    public decimal AverageTicket { get; init; }
    public decimal AverageItemsPerOrder { get; init; }
    public decimal RepeatCustomerRate { get; init; }
    public decimal ConversionRate { get; init; }
    public int PendingPayments { get; init; }
}

public sealed class ItemPerformance
{
    public string Name { get; init; } = "";
    public int TotalQuantity { get; init; }
    public decimal TotalRevenue { get; init; }
}

public sealed class HourVolume
{
    public int Hour { get; init; }
    public int OrderCount { get; init; }
}

public sealed class InsightEntry
{
    public string Type { get; init; } = "";
    public string Message { get; init; } = "";
    public string Severity { get; init; } = "info"; // info, warning, critical
}
