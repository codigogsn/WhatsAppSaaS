namespace WhatsAppSaaS.Domain.Entities;

public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrderId { get; set; }
    public Order Order { get; set; } = default!;

    public string Name { get; set; } = default!;
    public int Quantity { get; set; }
}
