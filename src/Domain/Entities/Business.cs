namespace WhatsAppSaaS.Domain.Entities;

public class Business
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Nombre visible del negocio
    public string Name { get; set; } = "";

    // Meta WhatsApp Cloud API phone_number_id (clave para resolver tenant desde webhook)
    public string PhoneNumberId { get; set; } = "";

    // Token de acceso (System User permanent token). Más adelante lo ciframos/guardamos mejor.
    public string AccessToken { get; set; } = "";

    // Admin key por negocio (para dashboard/admin). Esto sustituye el single ADMIN_KEY global.
    public string AdminKey { get; set; } = "";

    // Restaurant profile
    public string? Greeting { get; set; }
    public string? Schedule { get; set; }
    public string? Address { get; set; }
    public string? LogoUrl { get; set; }

    // Per-business Pago Móvil config (multi-restaurant)
    public string? PaymentMobileBank { get; set; }
    public string? PaymentMobileId { get; set; }
    public string? PaymentMobilePhone { get; set; }

    // WhatsApp number to receive staff notifications (E.164 format, e.g. "+584141234567")
    public string? NotificationPhone { get; set; }

    // Restaurant vertical template type (burger, pizza, sushi, arepa, cafe)
    public string? RestaurantType { get; set; }

    // Exchange rate reference: "BCV_USD", "BCV_EUR", or null/NONE (no conversion)
    public string? CurrencyReference { get; set; }

    // Business vertical: "restaurant", "fashion", etc. (defaults to "restaurant")
    public string VerticalType { get; set; } = "restaurant";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<MenuCategory> MenuCategories { get; set; } = new();
}
