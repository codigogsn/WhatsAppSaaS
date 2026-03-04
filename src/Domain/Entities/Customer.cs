using System;

namespace WhatsAppSaaS.Domain.Entities
{
    public class Customer
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // ✅ Multi-tenant ready (cuando exista Businesses)
        // Por ahora puede quedar null hasta que creemos Businesses y migremos datos
        public Guid? BusinessId { get; set; }

        // ✅ Clave lógica (E.164) por negocio
        public string PhoneE164 { get; set; } = default!;

        public string? Name { get; set; }

        // ✅ Analytics counters (denormalizados)
        public decimal TotalSpent { get; set; } = 0m;
        public int OrdersCount { get; set; } = 0;

        // ✅ Lifecycle
        public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastSeenAtUtc { get; set; }
        public DateTime? LastPurchaseAtUtc { get; set; }
    }
}
