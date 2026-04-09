namespace WhatsAppSaaS.Domain.Entities;

public class EmailRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string FromEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? SuggestedReply { get; set; }
    public string? GmailMessageId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
