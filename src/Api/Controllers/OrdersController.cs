using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly ILogger<OrdersController> _logger;
    private readonly IConfiguration _config;

    public OrdersController(AppDbContext context, IWhatsAppClient whatsAppClient, ILogger<OrdersController> logger, IConfiguration config)
    {
        _context = context;
        _whatsAppClient = whatsAppClient;
        _logger = logger;
        _config = config;
    }

    // GET /api/orders?take=50&status=Pending&businessId=xxx
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int take = 50, [FromQuery] string? status = null, [FromQuery] Guid? businessId = null)
    {
        if (take < 1) take = 1;
        if (take > 200) take = 200;

        var q = _context.Orders.AsNoTracking().Include(o => o.Items).AsQueryable();

        if (businessId.HasValue)
            q = q.Where(o => o.BusinessId == businessId.Value);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(o => o.Status == status);

        var data = await q
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(take)
            .Select(o => new
            {
                o.Id,
                o.Status,
                o.CustomerName,
                o.CustomerIdNumber,
                o.CustomerPhone,
                o.Address,
                o.PaymentMethod,
                o.DeliveryType,
                o.CreatedAtUtc,
                o.LocationLat,
                o.LocationLng,
                o.LocationText,
                o.CheckoutFormSent,
                o.CheckoutCompleted,
                o.CheckoutCompletedAtUtc,
                o.LastNotifiedStatus,
                o.LastNotifiedAtUtc,
                o.SpecialInstructions,
                o.SubtotalAmount,
                o.TotalAmount,
                o.PaymentProofMediaId,
                o.PaymentProofSubmittedAtUtc,
                o.PaymentVerifiedAtUtc,
                o.PaymentVerifiedBy,
                PaymentProofExists = o.PaymentProofMediaId != null,
                PaymentVerificationStatus = o.PaymentProofMediaId == null ? "none"
                    : o.PaymentVerifiedAtUtc != null ? "verified" : "pending",
                // Cash payment details
                o.CashCurrency, o.CashTenderedAmount, o.CashBcvRateUsed,
                o.CashChangeRequired, o.CashChangeAmount, o.CashChangeAmountBs,
                o.CashPayoutBank, o.CashPayoutIdNumber, o.CashPayoutPhone,
                o.CashChangeReturned, o.CashChangeReturnedAtUtc, o.CashChangeReturnedReference,
                Items = o.Items.Select(i => new { i.Id, i.Name, i.Quantity, i.UnitPrice, i.LineTotal })
            })
            .ToListAsync();

        return Ok(data);
    }

    // GET /api/orders/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var o = await _context.Orders.AsNoTracking()
            .Include(x => x.Items)
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.CustomerName,
                x.CustomerIdNumber,
                x.CustomerPhone,
                x.Address,
                x.PaymentMethod,
                x.DeliveryType,
                x.CreatedAtUtc,
                x.LocationLat,
                x.LocationLng,
                x.LocationText,
                x.CheckoutFormSent,
                x.CheckoutCompleted,
                x.CheckoutCompletedAtUtc,
                x.LastNotifiedStatus,
                x.LastNotifiedAtUtc,
                x.SpecialInstructions,
                x.PaymentProofMediaId,
                x.PaymentProofSubmittedAtUtc,
                x.PaymentVerifiedAtUtc,
                x.PaymentVerifiedBy,
                x.SubtotalAmount,
                x.TotalAmount,
                PaymentProofExists = x.PaymentProofMediaId != null,
                PaymentVerificationStatus = x.PaymentProofMediaId == null ? "none"
                    : x.PaymentVerifiedAtUtc != null ? "verified" : "pending",
                x.CashCurrency, x.CashTenderedAmount, x.CashBcvRateUsed,
                x.CashChangeRequired, x.CashChangeAmount, x.CashChangeAmountBs,
                x.CashPayoutBank, x.CashPayoutIdNumber, x.CashPayoutPhone,
                x.CashChangeReturned, x.CashChangeReturnedAtUtc, x.CashChangeReturnedReference,
                Items = x.Items.Select(i => new { i.Id, i.Name, i.Quantity, i.UnitPrice, i.LineTotal })
            })
            .SingleOrDefaultAsync();

        if (o is null) return NotFound(new { error = "Order not found" });
        return Ok(o);
    }

    public sealed class UpdateStatusRequest
    {
        public string? Status { get; set; }
    }

    // PATCH /api/orders/{id}/status  { "status": "Preparing" }
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest request)
    {
        var raw = (request?.Status ?? "").Trim();

        if (string.IsNullOrWhiteSpace(raw))
            return BadRequest(new { error = "Missing status" });

        // Canonical statuses
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pending"] = "Pending",
            ["Preparing"] = "Preparing",
            ["OutForDelivery"] = "OutForDelivery",
            ["Completed"] = "Completed",
            ["Cancelled"] = "Cancelled"
        };

        if (!canonical.TryGetValue(raw, out var newStatus))
            return BadRequest(new { error = "Invalid status", allowed = canonical.Values.OrderBy(x => x).ToArray() });

        // Retry DB (transient EndOfStream / stream read)
        const int maxAttempts = 3;
        var delaysMs = new[] { 150, 400, 900 };

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var order = await _context.Orders
                    .Include(o => o.Items)
                    .SingleOrDefaultAsync(o => o.Id == id);

                if (order is null)
                    return NotFound(new { error = "Order not found" });

                // Idempotencia: si no cambia, no notificar
                if (string.Equals(order.Status, newStatus, StringComparison.OrdinalIgnoreCase))
                {
                    return Ok(new
                    {
                        success = true,
                        id = order.Id,
                        status = order.Status,
                        notified = false,
                        reason = "status_unchanged",
                        to = order.From,
                        phoneNumberId = order.PhoneNumberId
                    });
                }

                order.Status = newStatus;

                await _context.SaveChangesAsync();

                // WhatsApp notification (best-effort, no tumba la request)
                var (shouldNotify, message) = MapStatusToMessage(order.Status);

                bool sendOk = false;
                bool notified = false;
                string? notifyReason = null;

                // 🛡️ Blindaje anti doble notificación
                // Solo enviar si shouldNotify AND LastNotifiedStatus != current Status
                if (shouldNotify &&
                    !string.IsNullOrWhiteSpace(order.From) &&
                    !string.IsNullOrWhiteSpace(order.PhoneNumberId))
                {
                    if (string.Equals(order.LastNotifiedStatus, order.Status, StringComparison.OrdinalIgnoreCase))
                    {
                        notified = false;
                        sendOk = false;
                        notifyReason = "already_notified_for_status";
                    }
                    else
                    {
                        try
                        {
                            sendOk = await _whatsAppClient.SendTextMessageAsync(
                                new OutgoingMessage
                                {
                                    To = order.From!,
                                    PhoneNumberId = order.PhoneNumberId!,
                                    Body = message
                                });

                            notified = sendOk;

                            // ✅ Solo si se envió OK, persistimos el guard
                            if (sendOk)
                            {
                                order.LastNotifiedStatus = order.Status;
                                order.LastNotifiedAtUtc = DateTime.UtcNow;

                                try
                                {
                                    await _context.SaveChangesAsync();
                                }
                                catch (Exception ex)
                                {
                                    // Best-effort: no tumbar el endpoint si falla guardar el guard
                                    _logger.LogError(ex,
                                        "WhatsApp sent but failed persisting notification guard for order {OrderId}",
                                        order.Id);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed sending WhatsApp notification for order {OrderId}", order.Id);
                            notified = false;
                            sendOk = false;
                            notifyReason = "send_exception";
                        }
                    }
                }
                else
                {
                    notifyReason = shouldNotify ? "missing_to_or_phoneNumberId" : "shouldNotify_false";
                }

                return Ok(new
                {
                    success = true,
                    id = order.Id,
                    status = order.Status,
                    notified,
                    to = order.From,
                    phoneNumberId = order.PhoneNumberId,
                    sendOk,
                    notifyReason,
                    lastNotifiedStatus = order.LastNotifiedStatus,
                    lastNotifiedAtUtc = order.LastNotifiedAtUtc
                });
            }
            catch (Exception ex) when (IsTransientDb(ex))
            {
                _logger.LogWarning(ex,
                    "Transient DB error in UpdateStatus attempt {Attempt}/{MaxAttempts} for order {OrderId}",
                    attempt, maxAttempts, id);

                if (attempt == maxAttempts)
                {
                    return StatusCode(503, new
                    {
                        error = "DB temporarily unavailable. Please retry.",
                        code = "db_transient_failure"
                    });
                }

                await Task.Delay(delaysMs[Math.Min(attempt - 1, delaysMs.Length - 1)]);
            }
        }

        return StatusCode(503, new { error = "DB temporarily unavailable. Please retry." });
    }

    // PATCH /api/orders/{id}/verify-payment
    [HttpPatch("{id:guid}/verify-payment")]
    public async Task<IActionResult> VerifyPayment(Guid id)
    {
        var order = await _context.Orders.SingleOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound(new { error = "Order not found" });

        order.PaymentVerifiedAtUtc = DateTime.UtcNow;
        order.PaymentVerifiedBy = "dashboard";
        await _context.SaveChangesAsync();

        // Notify customer
        if (!string.IsNullOrWhiteSpace(order.From) && !string.IsNullOrWhiteSpace(order.PhoneNumberId))
        {
            try
            {
                await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
                {
                    To = order.From,
                    PhoneNumberId = order.PhoneNumberId,
                    Body = "\u2705 Tu pago ha sido verificado. \u00a1Gracias!"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify customer about payment verification for order {OrderId}", id);
            }
        }

        return Ok(new { success = true, id = order.Id, paymentVerifiedAtUtc = order.PaymentVerifiedAtUtc });
    }

    // PATCH /api/orders/{id}/reject-payment
    [HttpPatch("{id:guid}/reject-payment")]
    public async Task<IActionResult> RejectPayment(Guid id)
    {
        var order = await _context.Orders.SingleOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound(new { error = "Order not found" });

        order.PaymentVerifiedAtUtc = null;
        order.PaymentVerifiedBy = null;
        order.PaymentProofMediaId = null;
        order.PaymentProofSubmittedAtUtc = null;
        await _context.SaveChangesAsync();

        // Clear cached proof file
        DeleteProofCache(id);

        // Notify customer
        if (!string.IsNullOrWhiteSpace(order.From) && !string.IsNullOrWhiteSpace(order.PhoneNumberId))
        {
            try
            {
                await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
                {
                    To = order.From,
                    PhoneNumberId = order.PhoneNumberId,
                    Body = "\u26a0\ufe0f Tu comprobante de pago fue rechazado. Por favor env\u00eda un nuevo comprobante."
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify customer about payment rejection for order {OrderId}", id);
            }
        }

        return Ok(new { success = true, id = order.Id, paymentRejected = true });
    }

    // PATCH /api/orders/{id}/cash-change-returned
    // Full raw ADO.NET — EF reads on Orders fail due to integer/text column type mismatches
    [HttpPatch("{id:guid}/cash-change-returned")]
    public async Task<IActionResult> MarkCashChangeReturned(Guid id, [FromBody] CashChangeReturnedRequest? req = null)
    {
        if (!IsAdmin()) return Unauthorized();

        try
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

            // Step 1: Read order fields with raw SQL (immune to type mismatches)
            string? fromPhone = null, phoneNumberId = null, customerName = null, businessIdStr = null;
            bool cashChangeRequired = false, cashChangeReturned = false;
            decimal cashChangeAmountBs = 0, cashChangeAmount = 0;

            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = """
                    SELECT "From", "PhoneNumberId", "CustomerName", CAST("BusinessId" AS TEXT),
                           "CashChangeRequired", "CashChangeReturned",
                           "CashChangeAmountBs", "CashChangeAmount"
                    FROM "Orders" WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@oid) LIMIT 1
                """;
                var p = readCmd.CreateParameter(); p.ParameterName = "oid"; p.Value = id.ToString();
                readCmd.Parameters.Add(p);

                using var reader = await readCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return NotFound(new { error = "Order not found" });

                fromPhone = reader.IsDBNull(0) ? null : reader.GetString(0);
                phoneNumberId = reader.IsDBNull(1) ? null : reader.GetString(1);
                customerName = reader.IsDBNull(2) ? null : reader.GetString(2);
                businessIdStr = reader.IsDBNull(3) ? null : reader.GetString(3);

                var rawReq = reader.IsDBNull(4) ? null : reader.GetValue(4);
                cashChangeRequired = rawReq switch { bool b => b, int i => i != 0, long l => l != 0,
                    string s => s is "1" or "true" or "True", _ => false };

                var rawRet = reader.IsDBNull(5) ? null : reader.GetValue(5);
                cashChangeReturned = rawRet switch { bool b => b, int i => i != 0, long l => l != 0,
                    string s => s is "1" or "true" or "True", _ => false };

                if (!reader.IsDBNull(6))
                    decimal.TryParse(reader.GetValue(6)?.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out cashChangeAmountBs);
                if (!reader.IsDBNull(7))
                    decimal.TryParse(reader.GetValue(7)?.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out cashChangeAmount);
            }

            if (!cashChangeRequired)
                return BadRequest(new { error = "No change required for this order" });
            if (cashChangeReturned)
                return Ok(new { success = true, alreadyReturned = true });

            // Step 2: Update with raw SQL
            var now = DateTime.UtcNow;
            using (var updCmd = conn.CreateCommand())
            {
                updCmd.CommandText = """
                    UPDATE "Orders"
                    SET "CashChangeReturned" = true,
                        "CashChangeReturnedAtUtc" = @ts,
                        "CashChangeReturnedBy" = 'dashboard',
                        "CashChangeReturnedReference" = @ref
                    WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@oid)
                """;
                var p1 = updCmd.CreateParameter(); p1.ParameterName = "oid"; p1.Value = id.ToString(); updCmd.Parameters.Add(p1);
                var p2 = updCmd.CreateParameter(); p2.ParameterName = "ts"; p2.Value = now; updCmd.Parameters.Add(p2);
                var p3 = updCmd.CreateParameter(); p3.ParameterName = "ref"; p3.Value = (object?)req?.Reference ?? DBNull.Value; updCmd.Parameters.Add(p3);
                await updCmd.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("CASH CHANGE RETURNED: OrderId={OrderId} Ref={Ref}", id, req?.Reference);

            // Step 3: Send WhatsApp notification
            if (!string.IsNullOrWhiteSpace(fromPhone) && !string.IsNullOrWhiteSpace(phoneNumberId))
            {
                try
                {
                    string? accessToken = null;
                    if (!string.IsNullOrWhiteSpace(businessIdStr))
                    {
                        using var tokenCmd = conn.CreateCommand();
                        tokenCmd.CommandText = """SELECT "AccessToken" FROM "Businesses" WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@bid) LIMIT 1""";
                        var tp = tokenCmd.CreateParameter(); tp.ParameterName = "bid"; tp.Value = businessIdStr; tokenCmd.Parameters.Add(tp);
                        var tokenResult = await tokenCmd.ExecuteScalarAsync();
                        accessToken = tokenResult as string;
                    }

                    var orderCode = id.ToString("N")[..8].ToUpperInvariant();
                    var amount = cashChangeAmountBs > 0 ? cashChangeAmountBs : cashChangeAmount;
                    var body = FormatCashChangeNotification(customerName, orderCode, amount, req?.Reference);

                    _logger.LogInformation("Sending cash change WhatsApp to {To} for order {OrderId}", fromPhone, id);
                    await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
                    {
                        To = fromPhone, PhoneNumberId = phoneNumberId, AccessToken = accessToken, Body = body
                    });
                    _logger.LogInformation("Cash change WhatsApp sent for order {OrderId}", id);
                }
                catch (Exception whatsEx)
                {
                    _logger.LogError(whatsEx, "WhatsApp send failed for cash change return, OrderId={OrderId}", id);
                }
            }

            return Ok(new { success = true, id, cashChangeReturnedAtUtc = now });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CASH CHANGE RETURN FAILED: OrderId={OrderId} Ref={Ref} Ex={ExType}: {ExMsg}",
                id, req?.Reference, ex.GetType().Name, ex.Message);
            if (ex.InnerException != null)
                _logger.LogError("INNER: {Inner}", ex.InnerException.Message);
            return StatusCode(500, new { error = $"Failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    public sealed class CashChangeReturnedRequest
    {
        public string? Reference { get; set; }
    }

    // GET /api/orders/{id}/payment-proof
    [HttpGet("{id:guid}/payment-proof")]
    public async Task<IActionResult> GetPaymentProof(Guid id, CancellationToken ct)
    {
        if (!IsAdmin())
            return Unauthorized(new { error = "Missing or invalid X-Admin-Key" });

        try
        {
            var order = await _context.Orders.AsNoTracking()
                .Where(o => o.Id == id)
                .Select(o => new { o.PaymentProofMediaId, o.BusinessId })
                .SingleOrDefaultAsync(ct);

            if (order is null)
                return NotFound(new { error = "Order not found" });

            if (string.IsNullOrWhiteSpace(order.PaymentProofMediaId))
                return NotFound(new { error = "No payment proof on this order" });

            // ── Local cache: serve from disk if previously downloaded ──
            var (cachedData, cachedType) = await ReadProofCacheAsync(id, ct);
            if (cachedData is not null)
            {
                _logger.LogDebug("GetPaymentProof: serving from cache for orderId={OrderId}", id);
                Response.Headers["Cache-Control"] = "private, max-age=300";
                return File(cachedData, SafeContentType(cachedType!));
            }

            _logger.LogInformation(
                "GetPaymentProof: orderId={OrderId}, businessId={BusinessId}, mediaId={MediaId}",
                id, order.BusinessId, order.PaymentProofMediaId);

            // Tenant isolation: resolve business access token
            string? bizToken = null;
            if (order.BusinessId.HasValue)
            {
                try
                {
                    var biz = await _context.Businesses.AsNoTracking()
                        .Where(b => b.Id == order.BusinessId.Value && b.IsActive)
                        .Select(b => new { b.AccessToken })
                        .FirstOrDefaultAsync(ct);
                    bizToken = biz?.AccessToken;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "GetPaymentProof: failed to look up business {BusinessId} for order {OrderId}. Falling back to env/appsettings token.",
                        order.BusinessId, id);
                }

                if (string.IsNullOrWhiteSpace(bizToken))
                    _logger.LogWarning(
                        "GetPaymentProof: no access token found for business {BusinessId} (order {OrderId}). Will fall back to env/appsettings token.",
                        order.BusinessId, id);
            }

            var result = await _whatsAppClient.GetMediaAsync(order.PaymentProofMediaId, bizToken, ct);
            if (result is null)
            {
                _logger.LogWarning(
                    "GetPaymentProof: GetMediaAsync returned null for mediaId={MediaId}, orderId={OrderId}",
                    order.PaymentProofMediaId, id);
                return StatusCode(502, new { error = "No se pudo descargar el comprobante desde WhatsApp. El archivo puede haber expirado." });
            }

            _logger.LogInformation(
                "GetPaymentProof: downloaded {Bytes} bytes, contentType={ContentType} for orderId={OrderId}",
                result.Data.Length, result.ContentType, id);

            // Cache locally for future requests
            await WriteProofCacheAsync(id, result.Data, result.ContentType, ct);

            var contentType = SafeContentType(result.ContentType);
            Response.Headers["Cache-Control"] = "private, max-age=300";
            return File(result.Data, contentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "GetPaymentProof: unhandled exception for orderId={OrderId}", id);
            return StatusCode(500, new { error = "Error interno al obtener el comprobante. Intenta de nuevo." });
        }
    }

    // ── Proof file cache helpers ──

    private static readonly string ProofCacheDir =
        Path.Combine(AppContext.BaseDirectory, "data", "proofs");

    private static string ProofBinPath(Guid orderId) =>
        Path.Combine(ProofCacheDir, orderId.ToString("N") + ".bin");

    private static string ProofTypePath(Guid orderId) =>
        Path.Combine(ProofCacheDir, orderId.ToString("N") + ".type");

    private static async Task<(byte[]? data, string? contentType)> ReadProofCacheAsync(Guid orderId, CancellationToken ct)
    {
        var binPath = ProofBinPath(orderId);
        var typePath = ProofTypePath(orderId);
        if (!System.IO.File.Exists(binPath) || !System.IO.File.Exists(typePath))
            return (null, null);
        try
        {
            var data = await System.IO.File.ReadAllBytesAsync(binPath, ct);
            var contentType = (await System.IO.File.ReadAllTextAsync(typePath, ct)).Trim();
            if (data.Length == 0 || string.IsNullOrWhiteSpace(contentType))
                return (null, null);
            return (data, contentType);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task WriteProofCacheAsync(Guid orderId, byte[] data, string contentType, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ProofCacheDir);
            await System.IO.File.WriteAllBytesAsync(ProofBinPath(orderId), data, ct);
            await System.IO.File.WriteAllTextAsync(ProofTypePath(orderId), contentType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache proof for orderId={OrderId}", orderId);
        }
    }

    private void DeleteProofCache(Guid orderId)
    {
        try
        {
            var bin = ProofBinPath(orderId);
            var type = ProofTypePath(orderId);
            if (System.IO.File.Exists(bin)) System.IO.File.Delete(bin);
            if (System.IO.File.Exists(type)) System.IO.File.Delete(type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached proof for orderId={OrderId}", orderId);
        }
    }

    private static string FormatCashChangeNotification(string? customerName, string orderCode, decimal amount, string? reference)
    {
        var name = string.IsNullOrWhiteSpace(customerName) ? "estimado cliente" : customerName.Trim();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\u2705 *Vuelto enviado*");
        sb.AppendLine();
        sb.AppendLine($"Hola, {name}.");
        sb.AppendLine($"Te confirmamos que el vuelto de tu pedido *#{orderCode}* ya fue transferido.");
        sb.AppendLine();
        sb.AppendLine($"\ud83d\udcb5 *Monto devuelto:* Bs. {amount:N2}");
        if (!string.IsNullOrWhiteSpace(reference))
            sb.AppendLine($"\ud83c\udfe6 *Referencia:* {reference}");
        sb.AppendLine();
        sb.Append("Gracias por tu compra.");
        return sb.ToString();
    }

    private static readonly HashSet<string> AllowedProofTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "application/pdf", "image/gif"
    };

    private static string SafeContentType(string ct) =>
        AllowedProofTypes.Contains(ct) ? ct : "application/octet-stream";

    private bool IsAdmin()
    {
        var adminKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey)) return false;
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(headerKey.ToString()),
            Encoding.UTF8.GetBytes(adminKey));
    }

    private static (bool shouldNotify, string message) MapStatusToMessage(string status)
    {
        // status ya viene canonical ("Preparing", etc.)
        return status switch
        {
            "Preparing" => (true, "🍔 Tu pedido está en preparación."),
            "OutForDelivery" => (true, "🚗 Tu pedido va en camino."),
            "Completed" => (true, "✅ Tu pedido fue entregado. ¡Gracias!"),
            _ => (false, "")
        };
    }

    private static bool IsTransientDb(Exception ex)
    {
        // Lo que te está pasando: InvalidOperationException -> NpgsqlException -> EndOfStreamException
        Exception? e = ex;

        while (e is not null)
        {
            var name = e.GetType().FullName ?? "";
            var msg = e.Message ?? "";

            if (name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("EndOfStreamException", StringComparison.OrdinalIgnoreCase)) return true;

            if (msg.Contains("transient failure", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Exception while reading from stream", StringComparison.OrdinalIgnoreCase)) return true;

            e = e.InnerException;
        }

        return false;
    }
}
