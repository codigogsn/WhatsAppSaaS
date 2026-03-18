using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/promotions")]
[EnableRateLimiting("admin")]
public class AdminPromotionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminPromotionsController> _logger;

    // Simple lock to prevent concurrent sends
    private static int _sending;

    public AdminPromotionsController(
        AppDbContext db, IWhatsAppClient whatsAppClient,
        IConfiguration config, ILogger<AdminPromotionsController> logger)
    {
        _db = db;
        _whatsAppClient = whatsAppClient;
        _config = config;
        _logger = logger;
    }

    public sealed class RecipientsRequest
    {
        public string Filter { get; set; } = "all";
        public Guid? BusinessId { get; set; }
    }

    public sealed class SendPromotionRequest
    {
        public string Message { get; set; } = "";
        public string Filter { get; set; } = "all";
        public Guid? BusinessId { get; set; }
    }

    /// <summary>
    /// GET /api/admin/promotions/recipients?filter=all&businessId=xxx
    /// Returns count of matching recipients for the given filter.
    /// </summary>
    [HttpGet("recipients")]
    public async Task<IActionResult> GetRecipientCount(
        [FromQuery] string filter = "all",
        [FromQuery] Guid? businessId = null,
        CancellationToken ct = default)
    {
        if (!IsAdmin()) return Unauthorized();

        try
        {
            var recipients = await QueryRecipientsAsync(filter, businessId, ct);
            return Ok(new { filter, count = recipients.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query promotion recipients");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/admin/promotions/send
    /// Sends a promotional message to filtered recipients.
    /// </summary>
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendPromotionRequest req, CancellationToken ct)
    {
        if (!IsAdmin()) return Unauthorized();

        var message = req.Message?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(message))
            return BadRequest(new { error = "Message cannot be empty" });

        if (message.Length > 4096)
            return BadRequest(new { error = "Message too long (max 4096 characters)" });

        // Prevent concurrent sends
        if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
            return Conflict(new { error = "A promotion is already being sent. Please wait." });

        try
        {
            var recipients = await QueryRecipientsAsync(req.Filter, req.BusinessId, ct);

            _logger.LogInformation(
                "PROMOTION: starting send — filter={Filter} matched={Count} messageLen={Len}",
                req.Filter, recipients.Count, message.Length);

            int sent = 0, skipped = 0, failed = 0;
            var failedSamples = new List<object>();

            foreach (var r in recipients)
            {
                if (ct.IsCancellationRequested) break;

                // Pre-send validation
                var precheck = ValidateRecipient(r);
                if (precheck is not null)
                {
                    failed++;
                    AddFailedSample(failedSamples, r.Phone, precheck, precheck);
                    _logger.LogWarning("PROMOTION: skipped {Phone} — {Reason}", r.Phone, precheck);
                    continue;
                }

                try
                {
                    var success = await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
                    {
                        To = r.Phone,
                        Body = message,
                        PhoneNumberId = r.PhoneNumberId,
                        AccessToken = r.AccessToken
                    }, ct);

                    if (success)
                        sent++;
                    else
                    {
                        failed++;
                        AddFailedSample(failedSamples, r.Phone, "provider_rejected",
                            "WhatsApp API returned failure — check server logs for details");
                        _logger.LogWarning("PROMOTION: provider rejected send to {Phone}", r.Phone);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    var reason = NormalizeErrorReason(ex.Message);
                    AddFailedSample(failedSamples, r.Phone, reason,
                        ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
                    _logger.LogWarning(ex, "PROMOTION: exception sending to {Phone} reason={Reason}",
                        r.Phone, reason);
                }

                // Small delay to avoid rate limiting
                await Task.Delay(200, ct);
            }

            _logger.LogInformation(
                "PROMOTION: completed — sent={Sent} skipped={Skipped} failed={Failed} total={Total}",
                sent, skipped, failed, recipients.Count);

            return Ok(new
            {
                matched = recipients.Count,
                sent,
                skipped,
                failed,
                failedSamples
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PROMOTION: unhandled error");
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
        }
    }

    private sealed record Recipient(string Phone, string PhoneNumberId, string? AccessToken);

    private async Task<List<Recipient>> QueryRecipientsAsync(string filter, Guid? businessId, CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // Build WHERE clauses
        var where = new List<string> { "c.\"PhoneE164\" IS NOT NULL", "c.\"PhoneE164\" != ''" };
        var parameters = new List<(string name, object value)>();

        if (businessId.HasValue)
        {
            where.Add("c.\"BusinessId\" = @bid");
            parameters.Add(("bid", businessId.Value));
        }

        switch (filter)
        {
            case "has_orders":
                where.Add("c.\"OrdersCount\" >= 1");
                break;
            case "recent_30d":
                where.Add("c.\"LastSeenAtUtc\" >= @cutoff");
                parameters.Add(("cutoff", DateTime.UtcNow.AddDays(-30)));
                break;
            case "repeat":
                where.Add("c.\"OrdersCount\" >= 2");
                break;
            // "all" — no extra filter
        }

        var sql = $"""
            SELECT DISTINCT c."PhoneE164", b."PhoneNumberId", b."AccessToken"
            FROM "Customers" c
            INNER JOIN "Businesses" b ON CAST(b."Id" AS TEXT) = CAST(c."BusinessId" AS TEXT)
            WHERE {string.Join(" AND ", where)}
              AND b."IsActive" = true
            ORDER BY c."PhoneE164"
        """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var results = new List<Recipient>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var phone = reader.GetString(0);
            var phoneNumberId = reader.GetString(1);
            var accessToken = reader.IsDBNull(2) ? null : reader.GetString(2);
            results.Add(new Recipient(phone, phoneNumberId, accessToken));
        }

        return results;
    }

    private static string? ValidateRecipient(Recipient r)
    {
        if (string.IsNullOrWhiteSpace(r.Phone) || r.Phone.Length < 8)
            return "invalid_phone";
        if (string.IsNullOrWhiteSpace(r.PhoneNumberId))
            return "missing_phone_number_id";
        if (string.IsNullOrWhiteSpace(r.AccessToken))
            return "missing_access_token";
        return null;
    }

    private static void AddFailedSample(List<object> samples, string phone, string reason, string details)
    {
        if (samples.Count >= 10) return;
        samples.Add(new { phoneE164 = phone, reason, details });
    }

    private static string NormalizeErrorReason(string message)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("not a valid whatsapp") || m.Contains("not on whatsapp"))
            return "not_whatsapp_user";
        if (m.Contains("outside allowed") || m.Contains("24-hour") || m.Contains("messaging window"))
            return "outside_allowed_messaging_rule";
        if (m.Contains("recipient") && m.Contains("not allowed"))
            return "recipient_not_allowed";
        if (m.Contains("invalid") && m.Contains("phone"))
            return "invalid_phone";
        if (m.Contains("rate limit") || m.Contains("throttl"))
            return "rate_limited";
        return "unknown_error";
    }

    private bool IsAdmin()
    {
        var adminKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey)) return false;
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(headerKey.ToString()),
            Encoding.UTF8.GetBytes(adminKey));
    }
}
