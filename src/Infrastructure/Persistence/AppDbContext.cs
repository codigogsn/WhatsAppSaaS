using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.From).IsRequired();
            b.Property(x => x.PhoneNumberId).IsRequired();
            b.Property(x => x.DeliveryType).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();

            // ✅ NUEVO: Status
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

            b.HasMany(x => x.Items)
             .WithOne(i => i.Order)
             .HasForeignKey(i => i.OrderId);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired();
            b.Property(x => x.Quantity).IsRequired();
        });
    }
}
