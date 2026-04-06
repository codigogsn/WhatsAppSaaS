namespace WhatsAppSaaS.Domain.Entities;

public class Extra
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BusinessId { get; set; }
    public Business? Business { get; set; }

    public string Name { get; set; } = "";

    public decimal? AdditivePrice { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<ExtraMenuItem> MenuItems { get; set; } = new();
    public List<ExtraMenuCategory> MenuCategories { get; set; } = new();
}
