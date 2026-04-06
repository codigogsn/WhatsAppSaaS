namespace WhatsAppSaaS.Domain.Entities;

public class ExtraMenuItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExtraId { get; set; }
    public Extra? Extra { get; set; }

    public Guid MenuItemId { get; set; }
    public MenuItem? MenuItem { get; set; }
}
