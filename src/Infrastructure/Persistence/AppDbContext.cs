using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);

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

            b.HasMany(x => x.Items)
             .WithOne(i => i.Order)
             .HasForeignKey(i => i.OrderId);

            // ✅ Montos (shadow props por nombre: NO revienta si no existen en Order.cs)
            b.Property<decimal?>("SubtotalAmount").HasPrecision(12, 2);
            b.Property<decimal?>("TotalAmount").HasPrecision(12, 2);

            // ✅ Anti doble notificación (shadow props por nombre)
            b.Property<string?>("LastNotifiedStatus");
            b.Property<DateTime?>("LastNotifiedAtUtc");

            // ✅ Multi-tenant futuro (shadow prop)
            // b.Property<Guid?>("BusinessId");
            // b.HasIndex("BusinessId", "CreatedAtUtc");
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired();
            b.Property(x => x.Quantity).IsRequired();

            // ✅ Montos por nombre (shadow props)
            b.Property<decimal?>("UnitPrice").HasPrecision(12, 2);
            b.Property<decimal?>("LineTotal").HasPrecision(12, 2);

            // ✅ Multi-tenant futuro (shadow prop)
            // b.Property<Guid?>("BusinessId");
            // b.HasIndex("BusinessId");
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("Products");
            b.HasKey(x => x.Id);

            // Deja Name required (esto sí existe seguro)
            b.Property(x => x.Name).IsRequired();

            // ✅ Campos por nombre (shadow props) para no depender de tu Product.cs actual
            b.Property<decimal>("UnitPrice").HasPrecision(12, 2);
            b.Property<bool>("IsActive").HasDefaultValue(true);
            b.Property<DateTime>("CreatedAtUtc");

            // ✅ Multi-tenant futuro
            // b.Property<Guid?>("BusinessId");
            // b.HasIndex("BusinessId", "Name").IsUnique();
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("Customers");
            b.HasKey(x => x.Id);

            // ✅ Por nombre para que compile aunque cambies el modelo luego
            b.Property<string>("PhoneE164").IsRequired();
            b.Property<string?>("Name");

            b.Property<decimal>("TotalSpent").HasPrecision(12, 2).HasDefaultValue(0m);
            b.Property<int>("OrdersCount").HasDefaultValue(0);

            b.Property<DateTime?>("LastPurchaseAtUtc");

            // SQLite + Postgres friendly
            b.Property<DateTime>("FirstSeenAtUtc").HasDefaultValueSql("CURRENT_TIMESTAMP");

            // ✅ Multi-tenant futuro
            // b.Property<Guid?>("BusinessId");
            // b.HasIndex("BusinessId", "PhoneE164").IsUnique();
        });
    }
}
