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
        // Nullable para ser compatible con la base actual y migraciones previas
        public decimal? UnitPrice { get; set; }

        // 🆕 Total de línea (Quantity * UnitPrice)
        // Nullable para evitar conflictos con datos históricos
        public decimal? LineTotal { get; set; }

        // 🔥 Esto evita el loop JSON: Order -> Items -> Order -> Items...
        [JsonIgnore]
        public Order? Order { get; set; }
    }
}
