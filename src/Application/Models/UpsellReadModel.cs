namespace WhatsAppSaaS.Application.Models;

public sealed class UpsellReadModel
{
    public Guid Id { get; set; }
    public Guid? SuggestedMenuItemId { get; set; }
    public string? SuggestedMenuItemName { get; set; }
    public decimal? SuggestedMenuItemPrice { get; set; }
    public string? SuggestionLabel { get; set; }
    public string? CustomMessage { get; set; }
    public int SortOrder { get; set; }
}
