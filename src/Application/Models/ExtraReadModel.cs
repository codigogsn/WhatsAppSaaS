namespace WhatsAppSaaS.Application.Models;

public sealed class ExtraReadModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public decimal? AdditivePrice { get; set; }
    public string AppliesVia { get; set; } = ""; // "Product" or "Category"
    public int SortOrder { get; set; }
}
