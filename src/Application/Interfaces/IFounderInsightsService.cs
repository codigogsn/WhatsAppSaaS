namespace WhatsAppSaaS.Application.Interfaces;

public interface IFounderInsightsService
{
    Task<FounderInsightsResponse> GetInsightsAsync(CancellationToken cancellationToken);
}

public sealed class FounderInsightsResponse
{
    public FounderInsightsSummary Summary { get; init; } = new();
    public List<FounderAlert> Alerts { get; init; } = [];
    public List<FounderInsightEntry> Insights { get; init; } = [];
    public List<FounderRecommendation> Recommendations { get; init; } = [];
    public List<FounderBusinessRank> TopBusinesses { get; init; } = [];
}

public sealed class FounderInsightsSummary
{
    public int TotalOrders { get; init; }
    public decimal TotalRevenue { get; init; }
    public decimal AvgOrdersPerBusiness { get; init; }
}

public sealed class FounderAlert
{
    public string Type { get; init; } = "";
    public string Severity { get; init; } = "medium";
    public string Message { get; init; } = "";
}

public sealed class FounderInsightEntry
{
    public string Type { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class FounderRecommendation
{
    public string Type { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class FounderBusinessRank
{
    public Guid BusinessId { get; init; }
    public string Name { get; init; } = "";
    public int Orders { get; init; }
}
