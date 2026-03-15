namespace WhatsAppSaaS.Domain.Entities;

public class MenuPdf
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "application/pdf";
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}
