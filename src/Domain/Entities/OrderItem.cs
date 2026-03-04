using System;
using System.Text.Json.Serialization;

namespace WhatsAppSaaS.Domain.Entities
{
    public sealed class OrderItem
    {
        public Guid Id { get; set; }

        public Guid OrderId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Quantity { get; set; }

        // 🆕 Precio unitario guardado al momento de crear la orden
        public decimal UnitPrice { get; set; }

        // 🔥 Esto evita el loop JSON: Order -> Items -> Order -> Items...
        [JsonIgnore]
        public Order? Order { get; set; }
    }
}
