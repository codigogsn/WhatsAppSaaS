namespace WhatsAppSaaS.Domain.Entities;

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = default!;

    public decimal Price { get; set; }
}
