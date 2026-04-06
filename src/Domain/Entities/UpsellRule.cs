namespace WhatsAppSaaS.Domain.Entities;

public class UpsellRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BusinessId { get; set; }
    public Business? Business { get; set; }

    public Guid SourceCategoryId { get; set; }
    public MenuCategory? SourceCategory { get; set; }

    public Guid? SuggestedMenuItemId { get; set; }
    public MenuItem? SuggestedMenuItem { get; set; }

    public string? SuggestionLabel { get; set; }

    public string? CustomMessage { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
