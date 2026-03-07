namespace WhatsAppSaaS.Domain.Entities;

public class MenuItemAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MenuItemId { get; set; }
    public MenuItem? MenuItem { get; set; }

    public string Alias { get; set; } = "";
}
