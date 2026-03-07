namespace WhatsAppSaaS.Domain.Entities;

public class MenuItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CategoryId { get; set; }
    public MenuCategory? Category { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public string? Description { get; set; }

    public bool IsAvailable { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<MenuItemAlias> Aliases { get; set; } = new();
}
