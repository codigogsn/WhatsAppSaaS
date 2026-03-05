using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<ConversationState> ConversationStates => Set<ConversationState>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.BusinessId);
            b.Property(x => x.From).IsRequired();
            b.Property(x => x.PhoneNumberId).IsRequired();
            b.Property(x => x.DeliveryType).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();

            b.Property(x => x.Status)
                .IsRequired()
                .HasDefaultValue("Pending");

            // Checkout / planilla
            b.Property(x => x.CustomerName);
            b.Property(x => x.CustomerIdNumber);
            b.Property(x => x.CustomerPhone);
            b.Property(x => x.Address);
            b.Property(x => x.PaymentMethod);
            b.Property(x => x.ReceiverName);
            b.Property(x => x.AdditionalNotes);

            b.Property(x => x.LocationLat).HasPrecision(9, 6);
            b.Property(x => x.LocationLng).HasPrecision(9, 6);
            b.Property(x => x.LocationText);

            b.Property(x => x.CheckoutFormSent).IsRequired();
            b.Property(x => x.CheckoutCompleted).IsRequired();
            b.Property(x => x.CheckoutCompletedAtUtc);

            // 🛡️ Anti doble notificación
            b.Property(x => x.LastNotifiedStatus);
            b.Property(x => x.LastNotifiedAtUtc);

            // 🧾 Montos
            b.Property(x => x.SubtotalAmount).HasPrecision(12, 2);
            b.Property(x => x.DeliveryFee).HasPrecision(12, 2);
            b.Property(x => x.TotalAmount).HasPrecision(12, 2);

            // ⏱️ Operational timestamps
            b.Property(x => x.AcceptedAtUtc);
            b.Property(x => x.PreparingAtUtc);
            b.Property(x => x.DeliveredAtUtc);

            // 📊 Analytics indexes
            b.HasIndex(x => x.BusinessId);
            b.HasIndex(x => x.CreatedAtUtc);
            b.HasIndex(x => new { x.BusinessId, x.CheckoutCompleted });

            // 👤 Customers link
            b.Property(x => x.CustomerId);
            b.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);

            // Items
            b.HasMany(x => x.Items)
                .WithOne(i => i.Order)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired();
            b.Property(x => x.Quantity).IsRequired();

            b.Property(x => x.UnitPrice).HasPrecision(12, 2);
            b.Property(x => x.LineTotal).HasPrecision(12, 2);

            b.HasIndex(x => x.OrderId);
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.BusinessId);

            b.Property(x => x.PhoneE164).IsRequired();
            b.Property(x => x.Name);

            b.Property(x => x.TotalSpent).HasPrecision(12, 2);
            b.Property(x => x.OrdersCount);

            b.Property(x => x.FirstSeenAtUtc).IsRequired();
            b.Property(x => x.LastSeenAtUtc);
            b.Property(x => x.LastPurchaseAtUtc);

            // ✅ Unicidad lógica (multi-tenant ready)
            b.HasIndex(x => new { x.BusinessId, x.PhoneE164 }).IsUnique();
        });

        // ✅ Product: sin configuración explícita para NO depender de props que cambian.
        // EF lo mapeará por convención según tu Product actual.

        modelBuilder.Entity<Business>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.Name).IsRequired();

            b.Property(x => x.PhoneNumberId)
                .IsRequired();

            b.Property(x => x.AccessToken)
                .IsRequired();

            b.Property(x => x.AdminKey)
                .IsRequired();

            b.Property(x => x.IsActive)
                .IsRequired();

            b.Property(x => x.CreatedAtUtc)
                .IsRequired();

            // cada phone_number_id debe ser único
            b.HasIndex(x => x.PhoneNumberId)
                .IsUnique();
        });

        modelBuilder.Entity<ConversationState>(b =>
        {
            b.HasKey(x => x.ConversationId);
            b.Property(x => x.ConversationId).HasMaxLength(256);
            b.Property(x => x.BusinessId);
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.Property(x => x.StateJson).IsRequired();

            b.HasMany(x => x.ProcessedMessages)
                .WithOne(p => p.Conversation)
                .HasForeignKey(p => p.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ConversationId).HasMaxLength(256).IsRequired();
            b.Property(x => x.MessageId).HasMaxLength(256).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();

            b.HasIndex(x => new { x.ConversationId, x.MessageId }).IsUnique();
        });
    }
}
