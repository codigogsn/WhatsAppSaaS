namespace WhatsAppSaaS.Application.Common;

public sealed class BusinessInsightThresholds
{
    public const string SectionName = "BusinessInsightThresholds";

    public decimal RepeatRateLowPercent { get; set; } = 15m;
    public decimal RepeatRateStrongPercent { get; set; } = 40m;
    public decimal AverageTicketLowUsd { get; set; } = 5m;
    public decimal ConversionLowPercent { get; set; } = 10m;
    public int PendingPaymentsWarning { get; set; } = 3;
    public int MinCompletedOrdersForInsights { get; set; } = 5;
    public int MinCustomersForRepeatRate { get; set; } = 10;
    public int MinConversationsForConversion { get; set; } = 20;
}
