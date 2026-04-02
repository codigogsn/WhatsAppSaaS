namespace WhatsAppSaaS.Application.Interfaces;

public interface IFounderInsightsService
{
    Task<FounderInsightsResponse> GetInsightsAsync(CancellationToken cancellationToken);
}

public sealed class FounderInsightsResponse
{
    // Decision-oriented state
    public string PlatformStateTitle { get; init; } = "";
    public string PlatformStateSummary { get; init; } = "";
    public string MainOpportunity { get; init; } = "";
    public string MainRisk { get; init; } = "";
    public FounderActionableRecommendation PrimaryRecommendation { get; init; } = new();

    public FounderInsightsSummary Summary { get; init; } = new();
    public List<FounderAlert> Alerts { get; init; } = [];
    public List<FounderInsightEntry> Insights { get; init; } = [];
    public List<FounderRecommendation> Recommendations { get; init; } = [];
    public List<FounderBusinessRank> TopBusinesses { get; init; } = [];
}

public sealed class FounderActionableRecommendation
{
    public string Title { get; init; } = "";
    public string Action { get; init; } = "";
    public string Impact { get; init; } = "";
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
