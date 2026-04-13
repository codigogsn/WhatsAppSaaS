using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/gmail")]
[Authorize]
[EnableRateLimiting("admin")]
public sealed class GmailController : ControllerBase
{
    private readonly GmailService _gmail;
    private readonly EmailAIService _emailAi;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<GmailController> _logger;

    public GmailController(GmailService gmail, EmailAIService emailAi, AppDbContext db, IConfiguration config, ILogger<GmailController> logger)
    {
        _gmail = gmail;
        _emailAi = emailAi;
        _db = db;
        _config = config;
        _logger = logger;
    }

    [HttpGet("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        // Only Founder role or global admin key may trigger cross-tenant Gmail sync
        if (!AdminAuth.IsFounder(User) && !AdminAuth.IsGlobalAdminKey(Request, _config))
            return Forbid();
        _logger.LogInformation("Gmail sync triggered");

        var emails = await _gmail.FetchRecentEmailsAsync(20, ct);
        var processed = 0;
        var skipped = 0;

        foreach (var email in emails)
        {
            try
            {
                // Filter
                if (!_gmail.IsRelevantEmail(email))
                {
                    skipped++;
                    continue;
                }

                // Extract sender email address
                var senderEmail = ExtractEmailAddress(email.From);

                // Match to business via customer email or domain
                var business = await MatchBusinessAsync(senderEmail, ct);
                if (business is null)
                {
                    _logger.LogDebug("No business match for sender {Sender}", senderEmail);
                    skipped++;
                    continue;
                }

                // Dedup check
                var exists = await _db.EmailRecords
                    .AnyAsync(e => e.GmailMessageId == email.MessageId && e.BusinessId == business.Id, ct);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                // AI processing
                var aiResult = await _emailAi.ProcessAsync(email.Subject, email.Body, "professional", ct);

                // Persist
                var record = new EmailRecord
                {
                    BusinessId = business.Id,
                    FromEmail = senderEmail,
                    Subject = email.Subject,
                    Body = email.Body.Length > 50000 ? email.Body[..50000] : email.Body,
                    Summary = aiResult.Summary,
                    SuggestedReply = aiResult.SuggestedReply,
                    GmailMessageId = email.MessageId
                };

                _db.EmailRecords.Add(record);
                await _db.SaveChangesAsync(ct);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process email {MessageId}", email.MessageId);
                skipped++;
            }
        }

        _logger.LogInformation("Gmail sync complete: {Processed} processed, {Skipped} skipped", processed, skipped);

        return Ok(new { processed, skipped });
    }

    private async Task<Business?> MatchBusinessAsync(string senderEmail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(senderEmail)) return null;

        var domain = senderEmail.Contains('@') ? senderEmail.Split('@')[1].ToLowerInvariant() : "";

        // Match via customer phone → business (customer might have email-like phone)
        // More practically: match by customer name containing the email, or domain match on business name
        // Simplest approach: find any business that has a customer with matching info,
        // or fall back to first active business (single-tenant MVP)

        // Try matching customer by checking if any customer's name contains the sender email
        var customerMatch = await _db.Customers
            .Where(c => c.BusinessId != null && c.Name != null && c.Name.Contains(senderEmail))
            .Select(c => c.BusinessId)
            .FirstOrDefaultAsync(ct);

        if (customerMatch.HasValue && customerMatch.Value != Guid.Empty)
        {
            return await _db.Businesses.FindAsync(new object[] { customerMatch.Value }, ct);
        }

        // No match found — do not fall back to another tenant
        return null;
    }

    private static string ExtractEmailAddress(string from)
    {
        if (string.IsNullOrWhiteSpace(from)) return "";

        var openAngle = from.LastIndexOf('<');
        var closeAngle = from.LastIndexOf('>');
        if (openAngle >= 0 && closeAngle > openAngle)
            return from[(openAngle + 1)..closeAngle].Trim().ToLowerInvariant();

        return from.Trim().ToLowerInvariant();
    }
}
