using System;
using System.Collections.Generic;

namespace WhatsAppSaaS.Application.DTOs;

// ── Existing (kept for backward compat) ──

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

// ── 1) Sales Analytics ──

public sealed class SalesAnalyticsDto
{
    public decimal SalesToday { get; set; }
    public decimal SalesLast7Days { get; set; }
    public decimal SalesLast30Days { get; set; }
    public decimal AverageTicket { get; set; }
    public int OrdersCount { get; set; }
    public Dictionary<int, decimal> SalesByHour { get; set; } = new();
    public Dictionary<string, decimal> SalesByDayOfWeek { get; set; } = new();
}

// ── 2) Product Analytics ──

public sealed class ProductAnalyticsDto
{
    public List<ProductSalesEntry> TopProducts { get; set; } = new();
    public List<ProductSalesEntry> LeastOrderedProducts { get; set; } = new();
    public List<ProductSalesEntry> ProductSalesVolume { get; set; } = new();
    public decimal AverageQuantityPerOrder { get; set; }
}

public sealed class ProductSalesEntry
{
    public string Name { get; set; } = string.Empty;
    public int TotalQuantity { get; set; }
    public decimal TotalRevenue { get; set; }
    public int OrderCount { get; set; }
}

// ── 3) Conversion Analytics ──

public sealed class ConversionAnalyticsDto
{
    public int MessagesReceived { get; set; }
    public int ConversationsStarted { get; set; }
    public int CheckoutStarted { get; set; }
    public int CheckoutCompleted { get; set; }
    public int OrdersCreated { get; set; }
    public int AbandonedConversations { get; set; }
    public decimal ConversionRate { get; set; }
    public decimal AbandonmentRate { get; set; }
}

// ── 4) Operational Analytics ──

public sealed class OperationalAnalyticsDto
{
    public double? AveragePreparationTimeMinutes { get; set; }
    public double? AverageDeliveryTimeMinutes { get; set; }
    public Dictionary<int, int> OrdersByHour { get; set; } = new();
    public Dictionary<string, int> PeakHours { get; set; } = new();
    public Dictionary<string, int> OrdersByDeliveryType { get; set; } = new();
    public Dictionary<string, int> OrdersByStatus { get; set; } = new();
}

// ── 5) Business Intelligence ──

public sealed class BusinessIntelligenceDto
{
    public decimal CustomerLifetimeValue { get; set; }
    public decimal RepeatCustomerRate { get; set; }
    public decimal OrdersPerCustomer { get; set; }
    public List<TopCustomerDto> TopCustomers { get; set; } = new();
    public Dictionary<string, decimal> RevenueByPaymentMethod { get; set; } = new();
    public int TotalCustomers { get; set; }
    public int RepeatCustomers { get; set; }
}

public sealed class TopCustomerDto
{
    public string PhoneE164 { get; set; } = string.Empty;
    public string? Name { get; set; }
    public decimal TotalSpent { get; set; }
    public int OrdersCount { get; set; }
}

// ── 6) Restaurant Insights ──

public sealed class RestaurantInsightsDto
{
    public Dictionary<int, int> BestSellingHours { get; set; } = new();
    public List<ProductSalesEntry> MostPopularItems { get; set; } = new();
    public List<int> SlowHours { get; set; } = new();
    public double? AverageOrderIntervalMinutes { get; set; }
}
