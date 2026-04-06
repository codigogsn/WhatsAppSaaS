namespace WhatsAppSaaS.Domain.Entities;

public class ExtraMenuCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExtraId { get; set; }
    public Extra? Extra { get; set; }

    public Guid MenuCategoryId { get; set; }
    public MenuCategory? MenuCategory { get; set; }
}
