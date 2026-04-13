namespace WhatsAppSaaS.Domain.Entities;

public class BusinessUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";

    /// <summary>Owner | Manager | Operator</summary>
    public string Role { get; set; } = "Operator";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Incremented on password change, role change, or deactivation to revoke all existing JWT tokens.</summary>
    public int TokenVersion { get; set; } = 0;

    public Business? Business { get; set; }
}
