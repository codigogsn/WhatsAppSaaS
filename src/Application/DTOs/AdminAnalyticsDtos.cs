using System;
using System.Collections.Generic;

namespace WhatsAppSaaS.Application.DTOs;

public sealed class AnalyticsSummaryDto
{
    public int OrdersCount { get; set; }
    public int OrdersCompletedCount { get; set; }
    public int CustomersCount { get; set; }

    public decimal TotalRevenue { get; set; } // SUM(Orders.TotalAmount)
    public decimal AvgOrderValue { get; set; } // TotalRevenue / OrdersCount

    public DateTime? LastOrderAtUtc { get; set; }
}

public sealed class TopProductDto
{
    public string Name { get; set; } = string.Empty;
    public int QuantitySold { get; set; }
    public decimal Revenue { get; set; }
}

public sealed class CustomerAnalyticsDto
{
    public string PhoneE164 { get; set; } = string.Empty;
    public string? Name { get; set; }
    public decimal TotalSpent { get; set; }
    public int OrdersCount { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime? LastPurchaseAtUtc { get; set; }
}
