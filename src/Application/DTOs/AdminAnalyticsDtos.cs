using System;
using System.Collections.Generic;

namespace WhatsAppSaaS.Application.DTOs;

public sealed class AnalyticsSummaryDto
{
    public int TotalOrders { get; set; }
    public int CompletedOrders { get; set; }
    public int PendingOrders { get; set; }

    public decimal TotalRevenue { get; set; }

    public int TotalCustomers { get; set; }
}

public sealed class TopProductDto
{
    public string Name { get; set; } = string.Empty;
    public int TotalQuantity { get; set; }
    public decimal TotalRevenue { get; set; }
}

public sealed class CustomerAnalyticsDto
{
    public string PhoneE164 { get; set; } = string.Empty;
    public string? Name { get; set; }

    public decimal TotalSpent { get; set; }
    public int OrdersCount { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime? LastPurchaseAtUtc { get; set; }
}
