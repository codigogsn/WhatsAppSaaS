using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<MenuItemAlias> MenuItemAliases => Set<MenuItemAlias>();
    public DbSet<BusinessUser> BusinessUsers => Set<BusinessUser>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<MenuPdf> MenuPdfs => Set<MenuPdf>();

    // ═══ Legacy compatibility converters ═══
    // SQLite migrations created bool as INTEGER and decimal as TEXT.
    // These converters let EF read/write both PostgreSQL native types AND legacy types.

    private static readonly ValueConverter<bool, int> BoolToInt = new(
        v => v ? 1 : 0,
        v => v != 0);

    private static readonly ValueConverter<decimal, string> DecimalToText = new(
        v => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
        v => ParseDecimal(v));

    private static readonly ValueConverter<decimal?, string?> NullableDecimalToText = new(
        v => v.HasValue ? v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        v => ParseNullableDecimal(v));

    private static decimal ParseDecimal(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return 0m;
        return decimal.TryParse(v, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static decimal? ParseNullableDecimal(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        return decimal.TryParse(v, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ═══════════════════════════════════════════
        // ORDER
        // ═══════════════════════════════════════════
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.BusinessId);
            b.Property(x => x.From).IsRequired();
            b.Property(x => x.PhoneNumberId).IsRequired();
            b.Property(x => x.DeliveryType).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.Status).IsRequired().HasDefaultValue("Pending");

            // Text fields
            b.Property(x => x.CustomerName);
            b.Property(x => x.CustomerIdNumber);
            b.Property(x => x.CustomerPhone);
            b.Property(x => x.Address);
            b.Property(x => x.PaymentMethod);
            b.Property(x => x.ReceiverName);
            b.Property(x => x.AdditionalNotes);
            b.Property(x => x.SpecialInstructions);
            b.Property(x => x.LocationText);
            b.Property(x => x.CheckoutCompletedAtUtc);
            b.Property(x => x.LastNotifiedStatus);
            b.Property(x => x.LastNotifiedAtUtc);
            b.Property(x => x.PaymentProofMediaId);
            b.Property(x => x.PaymentProofSubmittedAtUtc);
            b.Property(x => x.PaymentVerifiedAtUtc);
            b.Property(x => x.PaymentVerifiedBy);
            b.Property(x => x.AcceptedAtUtc);
            b.Property(x => x.PreparingAtUtc);
            b.Property(x => x.DeliveredAtUtc);
            b.Property(x => x.CustomerId);

            // ── Bool columns (INTEGER in production) ──
            b.Property(x => x.CheckoutFormSent).HasConversion(BoolToInt);
            b.Property(x => x.CheckoutCompleted).HasConversion(BoolToInt);
            b.Property(x => x.CashChangeRequired).HasConversion(BoolToInt);
            b.Property(x => x.CashChangeReturned).HasConversion(BoolToInt);

            // ── Decimal columns (TEXT in production) ──
            b.Property(x => x.SubtotalAmount).HasConversion(NullableDecimalToText);
            b.Property(x => x.TotalAmount).HasConversion(NullableDecimalToText);
            b.Property(x => x.DeliveryFee).HasConversion(NullableDecimalToText);
            b.Property(x => x.LocationLat).HasConversion(NullableDecimalToText);
            b.Property(x => x.LocationLng).HasConversion(NullableDecimalToText);
            b.Property(x => x.CashTenderedAmount).HasConversion(NullableDecimalToText);
            b.Property(x => x.CashBcvRateUsed).HasConversion(NullableDecimalToText);
            b.Property(x => x.CashChangeAmount).HasConversion(NullableDecimalToText);
            b.Property(x => x.CashChangeAmountBs).HasConversion(NullableDecimalToText);

            // Cash text fields
            b.Property(x => x.CashCurrency);
            b.Property(x => x.CashPayoutBank);
            b.Property(x => x.CashPayoutIdNumber);
            b.Property(x => x.CashPayoutPhone);
            b.Property(x => x.CashChangeReturnedAtUtc);
            b.Property(x => x.CashChangeReturnedBy);
            b.Property(x => x.CashChangeReturnedReference);

            // Indexes
            b.HasIndex(x => x.BusinessId);
            b.HasIndex(x => x.CreatedAtUtc);
            b.HasIndex(x => new { x.BusinessId, x.CheckoutCompleted });

            // Customer FK
            b.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);

            // Items
            b.HasMany(x => x.Items).WithOne(i => i.Order)
                .HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        // ═══════════════════════════════════════════
        // ORDER ITEM
        // ═══════════════════════════════════════════
        modelBuilder.Entity<OrderItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired();
            b.Property(x => x.Quantity).IsRequired();

            // ── Decimal columns (TEXT in production) ──
            b.Property(x => x.UnitPrice).HasConversion(NullableDecimalToText);
            b.Property(x => x.LineTotal).HasConversion(NullableDecimalToText);

            b.HasIndex(x => x.OrderId);
        });

        // ═══════════════════════════════════════════
        // CUSTOMER
        // ═══════════════════════════════════════════
        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.BusinessId);
            b.Property(x => x.PhoneE164).IsRequired();
            b.Property(x => x.Name);

            // ── Decimal column (TEXT in production) ──
            b.Property(x => x.TotalSpent).HasConversion(DecimalToText);

            b.Property(x => x.OrdersCount);
            b.Property(x => x.FirstSeenAtUtc).IsRequired();
            b.Property(x => x.LastSeenAtUtc);
            b.Property(x => x.LastPurchaseAtUtc);

            b.HasIndex(x => new { x.BusinessId, x.PhoneE164 }).IsUnique();
        });

        // ═══════════════════════════════════════════
        // BUSINESS
        // ═══════════════════════════════════════════
        modelBuilder.Entity<Business>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired();
            b.Property(x => x.PhoneNumberId).IsRequired();
            b.Property(x => x.AccessToken).IsRequired();
            b.Property(x => x.AdminKey).IsRequired();
            b.Property(x => x.Greeting).HasMaxLength(500);
            b.Property(x => x.Schedule).HasMaxLength(500);
            b.Property(x => x.Address).HasMaxLength(500);
            b.Property(x => x.LogoUrl).HasMaxLength(500);
            b.Property(x => x.PaymentMobileBank);
            b.Property(x => x.PaymentMobileId);
            b.Property(x => x.PaymentMobilePhone);
            b.Property(x => x.RestaurantType).HasMaxLength(50);
            b.Property(x => x.CurrencyReference).HasMaxLength(20);
            b.Property(x => x.VerticalType).HasMaxLength(30).HasDefaultValue("restaurant");
            b.Property(x => x.NotificationPhone).HasMaxLength(50);
            b.Property(x => x.MenuPdfUrl).HasMaxLength(500);
            b.Property(x => x.CreatedAtUtc).IsRequired();

            // ── Bool column (INTEGER in production) ──
            b.Property(x => x.IsActive).HasConversion(BoolToInt);

            b.HasIndex(x => x.PhoneNumberId).IsUnique();
        });

        // ═══════════════════════════════════════════
        // MENU CATEGORY
        // ═══════════════════════════════════════════
        modelBuilder.Entity<MenuCategory>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.Property(x => x.SortOrder);
            b.Property(x => x.CreatedAtUtc).IsRequired();

            // ── Bool column (INTEGER in production) ──
            b.Property(x => x.IsActive).HasConversion(BoolToInt);

            b.HasOne(x => x.Business).WithMany(bz => bz.MenuCategories)
                .HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Items).WithOne(i => i.Category)
                .HasForeignKey(i => i.CategoryId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.BusinessId);
        });

        // ═══════════════════════════════════════════
        // MENU ITEM
        // ═══════════════════════════════════════════
        modelBuilder.Entity<MenuItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.SortOrder);
            b.Property(x => x.CreatedAtUtc).IsRequired();

            // ── Decimal column (TEXT in production) ──
            b.Property(x => x.Price).HasConversion(DecimalToText);

            // ── Bool column (INTEGER in production) ──
            b.Property(x => x.IsAvailable).HasConversion(BoolToInt);

            b.HasMany(x => x.Aliases).WithOne(a => a.MenuItem)
                .HasForeignKey(a => a.MenuItemId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.CategoryId);
        });

        // ═══════════════════════════════════════════
        // MENU ITEM ALIAS
        // ═══════════════════════════════════════════
        modelBuilder.Entity<MenuItemAlias>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Alias).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.MenuItemId);
        });

        // ═══════════════════════════════════════════
        // BUSINESS USER
        // ═══════════════════════════════════════════
        modelBuilder.Entity<BusinessUser>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.BusinessId);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.Email).IsRequired().HasMaxLength(320);
            b.Property(x => x.PasswordHash).IsRequired();
            b.Property(x => x.Role).IsRequired().HasMaxLength(50);
            b.Property(x => x.CreatedAtUtc).IsRequired();

            // ── Bool column (INTEGER in production) ──
            b.Property(x => x.IsActive).HasConversion(BoolToInt);

            b.HasOne(x => x.Business).WithMany()
                .HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.BusinessId, x.Email }).IsUnique();
        });

        // ═══════════════════════════════════════════
        // REMAINING ENTITIES (no legacy type issues)
        // ═══════════════════════════════════════════
        modelBuilder.Entity<MenuPdf>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.BusinessId).IsRequired();
            b.Property(x => x.Data).IsRequired();
            b.Property(x => x.ContentType).IsRequired().HasMaxLength(100);
            b.Property(x => x.UploadedAtUtc).IsRequired();
            b.HasIndex(x => x.BusinessId).IsUnique();
        });

        modelBuilder.Entity<BackgroundJob>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.JobType).IsRequired().HasMaxLength(100);
            b.Property(x => x.PayloadJson).IsRequired();
            b.Property(x => x.Status).IsRequired().HasMaxLength(20);
            b.Property(x => x.RetryCount);
            b.Property(x => x.MaxRetries);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.Property(x => x.ScheduledAtUtc).IsRequired();
            b.Property(x => x.LockedAtUtc);
            b.Property(x => x.CompletedAtUtc);
            b.Property(x => x.BusinessId);
            b.HasIndex(x => new { x.Status, x.ScheduledAtUtc });
        });

        modelBuilder.Entity<ConversationState>(b =>
        {
            b.HasKey(x => x.ConversationId);
            b.Property(x => x.ConversationId).HasMaxLength(256);
            b.Property(x => x.BusinessId);
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.Property(x => x.StateJson).IsRequired();
            b.HasMany(x => x.ProcessedMessages).WithOne(p => p.Conversation)
                .HasForeignKey(p => p.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ConversationId).HasMaxLength(256).IsRequired();
            b.Property(x => x.MessageId).HasMaxLength(256).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.ConversationId, x.MessageId }).IsUnique();
        });

        modelBuilder.Entity<ExchangeRate>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.RateDate).IsRequired();
            b.Property(x => x.UsdRate).HasPrecision(12, 2).IsRequired();
            b.Property(x => x.EurRate).HasPrecision(12, 2).IsRequired();
            b.Property(x => x.Source).HasMaxLength(50).IsRequired();
            b.Property(x => x.FetchedAtUtc).IsRequired();
            b.HasIndex(x => x.RateDate).IsUnique();
        });
    }
}
