namespace WhatsAppSaaS.Application.Interfaces;

public interface IInsightsChatService
{
    Task<InsightsChatResponse> AskAsync(InsightsChatRequest request, Guid? businessId, CancellationToken ct);
}

public sealed class InsightsChatRequest
{
    public string Question { get; set; } = "";
    public string Scope { get; set; } = "business"; // "business" or "founder"
}

public sealed class InsightsChatResponse
{
    public string Answer { get; set; } = "";
    public string Confidence { get; set; } = "medium"; // high, medium, low
}
