namespace WhatsAppSaaS.Domain.Entities;

public class BackgroundJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JobType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending"; // Pending, Processing, Done, Failed
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public string? LastError { get; set; }
    public DateTime ScheduledAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LockedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Guid? BusinessId { get; set; }
}
