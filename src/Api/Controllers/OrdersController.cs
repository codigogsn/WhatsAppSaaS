using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Domain.Exceptions;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("admin")]
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
    // Raw ADO.NET: immune to EF bool/decimal type mismatches on legacy columns
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int take = 50, [FromQuery] string? status = null, [FromQuery] Guid? businessId = null)
    {
        if (!IsAdmin()) return Unauthorized(new { error = "Missing or invalid X-Admin-Key" });

        // Founder can view any business; other JWT users are scoped to their own
        var jwtBizId = GetJwtBusinessId();
        var jwtRole = User.FindFirstValue(ClaimTypes.Role);
        if (jwtBizId.HasValue && jwtRole != "Founder")
            businessId = jwtBizId.Value;

        if (take < 1) take = 1;
        if (take > 200) take = 200;

        try
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

            var where = "WHERE 1=1";
            if (businessId.HasValue) where += " AND o.\"BusinessId\" = @bid";
            if (!string.IsNullOrWhiteSpace(status)) where += " AND o.\"Status\" = @status";

            // Query orders
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT * FROM "Orders" o {where}
                ORDER BY o."CreatedAtUtc" DESC LIMIT @take
            """;
            AddP(cmd, "take", take);
            if (businessId.HasValue) AddP(cmd, "bid", businessId.Value);
            if (!string.IsNullOrWhiteSpace(status)) AddP(cmd, "status", status);

            var orders = new List<(Dictionary<string, object?> Data, string Id)>();
            using (var r = await cmd.ExecuteReaderAsync())
            {
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < r.FieldCount; i++) cols.Add(r.GetName(i));
                while (await r.ReadAsync())
                {
                    var row = MapOrderRow(r, cols);
                    var id = cols.Contains("Id") && r["Id"] is not DBNull ? r["Id"]?.ToString() ?? "" : "";
                    orders.Add((row, id));
                }
            }

            // Query items for all fetched orders
            var orderIds = orders.Select(o => o.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var itemsByOrder = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
            if (orderIds.Count > 0)
            {
                using var itemCmd = conn.CreateCommand();
                var pNames = new List<string>();
                for (var idx = 0; idx < orderIds.Count; idx++)
                {
                    pNames.Add($"@oid{idx}");
                    AddP(itemCmd, $"oid{idx}", Guid.Parse(orderIds[idx]));
                }
                itemCmd.CommandText = $"""
                    SELECT "OrderId", "Id", "Name", "Quantity", "UnitPrice", "LineTotal"
                    FROM "OrderItems" WHERE "OrderId" IN ({string.Join(",", pNames)})
                """;
                using var ir = await itemCmd.ExecuteReaderAsync();
                while (await ir.ReadAsync())
                {
                    var oid = ir["OrderId"]?.ToString() ?? "";
                    if (!itemsByOrder.ContainsKey(oid)) itemsByOrder[oid] = new List<object>();
                    decimal? up = null, lt = null;
                    if (ir["UnitPrice"] is not DBNull) decimal.TryParse(ir["UnitPrice"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var upv) ; up = ir["UnitPrice"] is DBNull ? null : decimal.TryParse(ir["UnitPrice"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var upv2) ? upv2 : null;
                    lt = ir["LineTotal"] is DBNull ? null : decimal.TryParse(ir["LineTotal"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ltv) ? ltv : null;
                    itemsByOrder[oid].Add(new {
                        id = ir["Id"]?.ToString(),
                        name = ir["Name"]?.ToString(),
                        quantity = Convert.ToInt32(ir["Quantity"]),
                        unitPrice = up,
                        lineTotal = lt
                    });
                }
            }

            var results = orders.Select(o => {
                o.Data["items"] = itemsByOrder.TryGetValue(o.Id, out var items) ? items : new List<object>();
                return (object)o.Data;
            }).ToList();

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GET /api/orders failed");
            return StatusCode(500, new { error = "Unexpected server error" });
        }
    }

    // GET /api/orders/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!IsAdmin()) return Unauthorized(new { error = "Missing or invalid X-Admin-Key" });
        var jwtBizId = GetJwtBusinessId();
        try
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

            var bizScope = jwtBizId.HasValue ? """ AND CAST("BusinessId" AS TEXT) = @bid""" : "";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""SELECT * FROM "Orders" WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@oid){bizScope} LIMIT 1""";
            AddP(cmd, "oid", id.ToString());
            if (jwtBizId.HasValue) AddP(cmd, "bid", jwtBizId.Value.ToString());

            Dictionary<string, object?>? orderData = null;
            string orderId = "";
            using (var r = await cmd.ExecuteReaderAsync())
            {
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < r.FieldCount; i++) cols.Add(r.GetName(i));
                if (!await r.ReadAsync()) return NotFound(new { error = "Order not found" });
                orderData = MapOrderRow(r, cols);
                orderId = orderData["id"]?.ToString() ?? "";
            }

            // Fetch items separately
            var items = new List<object>();
            if (!string.IsNullOrEmpty(orderId))
            {
                using var itemCmd = conn.CreateCommand();
                itemCmd.CommandText = """SELECT "Id","Name","Quantity","UnitPrice","LineTotal" FROM "OrderItems" WHERE LOWER(CAST("OrderId" AS TEXT)) = LOWER(@oid)""";
                AddP(itemCmd, "oid", orderId);
                using var ir = await itemCmd.ExecuteReaderAsync();
                while (await ir.ReadAsync())
                {
                    decimal? up = ir["UnitPrice"] is DBNull ? null : decimal.TryParse(ir["UnitPrice"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : null;
                    decimal? lt = ir["LineTotal"] is DBNull ? null : decimal.TryParse(ir["LineTotal"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;
                    items.Add(new { id = ir["Id"]?.ToString(), name = ir["Name"]?.ToString(), quantity = Convert.ToInt32(ir["Quantity"]), unitPrice = up, lineTotal = lt });
                }
            }
            orderData["items"] = items;
            return Ok(orderData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GET /api/orders/{Id} failed", id);
            return StatusCode(500, new { error = "Unexpected server error" });
        }
    }

    private static Dictionary<string, object?> MapOrderRow(System.Data.Common.DbDataReader r, HashSet<string> cols)
    {
        string? Col(string n) => cols.Contains(n) && r[n] is not DBNull ? r[n]?.ToString() : null;
        decimal? Dec(string n) {
            if (!cols.Contains(n) || r[n] is DBNull) return null;
            var raw = r[n];
            if (raw is decimal d) return d;
            return decimal.TryParse(raw?.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        bool Bool(string n) {
            if (!cols.Contains(n) || r[n] is DBNull) return false;
            var raw = r[n];
            return raw switch { bool b => b, int i => i != 0, long l => l != 0,
                string s => s is "1" or "true" or "True" or "t", _ => false };
        }
        DateTime? Dt(string n) {
            if (!cols.Contains(n) || r[n] is DBNull) return null;
            return r[n] is DateTime dt ? dt : DateTime.TryParse(r[n]?.ToString(), out var p) ? p : null;
        }

        var proofId = Col("PaymentProofMediaId");
        var verifiedAt = Dt("PaymentVerifiedAtUtc");

        return new Dictionary<string, object?>
        {
            ["id"] = Col("Id"),
            ["status"] = Col("Status"),
            ["customerName"] = !string.IsNullOrWhiteSpace(Col("CustomerName")) ? WebhookProcessor.NormalizeDisplayName(Col("CustomerName")!) : Col("CustomerName"),
            ["customerIdNumber"] = Col("CustomerIdNumber"),
            ["customerPhone"] = Col("CustomerPhone"),
            ["address"] = Col("Address"),
            ["paymentMethod"] = Col("PaymentMethod"),
            ["deliveryType"] = Col("DeliveryType"),
            ["createdAtUtc"] = Dt("CreatedAtUtc"),
            ["locationLat"] = Dec("LocationLat"),
            ["locationLng"] = Dec("LocationLng"),
            ["locationText"] = Col("LocationText"),
            ["checkoutFormSent"] = Bool("CheckoutFormSent"),
            ["checkoutCompleted"] = Bool("CheckoutCompleted"),
            ["checkoutCompletedAtUtc"] = Dt("CheckoutCompletedAtUtc"),
            ["lastNotifiedStatus"] = Col("LastNotifiedStatus"),
            ["lastNotifiedAtUtc"] = Dt("LastNotifiedAtUtc"),
            ["specialInstructions"] = Col("SpecialInstructions"),
            ["subtotalAmount"] = Dec("SubtotalAmount"),
            ["totalAmount"] = Dec("TotalAmount"),
            ["paymentProofMediaId"] = proofId,
            ["paymentProofSubmittedAtUtc"] = Dt("PaymentProofSubmittedAtUtc"),
            ["paymentVerifiedAtUtc"] = verifiedAt,
            ["paymentVerifiedBy"] = Col("PaymentVerifiedBy"),
            ["paymentProofExists"] = proofId != null,
            ["paymentVerificationStatus"] = proofId == null ? "none" : verifiedAt != null ? "verified" : "pending",
            ["cashCurrency"] = Col("CashCurrency"),
            ["cashTenderedAmount"] = Dec("CashTenderedAmount"),
            ["cashBcvRateUsed"] = Dec("CashBcvRateUsed"),
            ["cashChangeRequired"] = Bool("CashChangeRequired"),
            ["cashChangeAmount"] = Dec("CashChangeAmount"),
            ["cashChangeAmountBs"] = Dec("CashChangeAmountBs"),
            ["cashPayoutBank"] = Col("CashPayoutBank"),
            ["cashPayoutIdNumber"] = Col("CashPayoutIdNumber"),
            ["cashPayoutPhone"] = Col("CashPayoutPhone"),
            ["cashChangeReturned"] = Bool("CashChangeReturned"),
            ["cashChangeReturnedAtUtc"] = Dt("CashChangeReturnedAtUtc"),
            ["cashChangeReturnedReference"] = Col("CashChangeReturnedReference"),
        };
    }


    public sealed class UpdateStatusRequest
    {
        public string? Status { get; set; }
    }

    // PATCH /api/orders/{id}/status  { "status": "Preparing" }
    // Raw ADO.NET: immune to EF materialization type mismatches on legacy columns
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest request)
    {
        if (!IsAdmin()) return Unauthorized(new { error = "Missing or invalid X-Admin-Key" });
        var jwtBizId = GetJwtBusinessId();
        var raw = (request?.Status ?? "").Trim();

        if (string.IsNullOrWhiteSpace(raw))
            return BadRequest(new { error = "Missing status" });

        // Canonical statuses
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pending"] = "Pending",
            ["Preparing"] = "Preparing",
            ["ReadyForPickup"] = "ReadyForPickup",
            ["OutForDelivery"] = "OutForDelivery",
            ["Completed"] = "Completed",
            ["Cancelled"] = "Cancelled"
        };

        if (!canonical.TryGetValue(raw, out var newStatus))
            return BadRequest(new { error = "Invalid status", allowed = canonical.Values.OrderBy(x => x).ToArray() });

        try
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

            // Step 1: Read current order fields with raw SQL
            string? currentStatus = null, fromPhone = null, phoneNumberId = null, lastNotifiedStatus = null;
            DateTime? lastNotifiedAtUtc = null;
            bool found = false;

            using (var readCmd = conn.CreateCommand())
            {
                var bizScope = jwtBizId.HasValue ? """ AND CAST("BusinessId" AS TEXT) = @bid""" : "";
                readCmd.CommandText = $"""
                    SELECT "Status", "From", "PhoneNumberId", "LastNotifiedStatus", "LastNotifiedAtUtc"
                    FROM "Orders" WHERE "Id" = @oid{bizScope} LIMIT 1
                """;
                AddP(readCmd, "oid", id);
                if (jwtBizId.HasValue) AddP(readCmd, "bid", jwtBizId.Value.ToString());

                using var r = await readCmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    found = true;
                    currentStatus = r["Status"] is not DBNull ? r["Status"]?.ToString() : null;
                    fromPhone = r["From"] is not DBNull ? r["From"]?.ToString() : null;
                    phoneNumberId = r["PhoneNumberId"] is not DBNull ? r["PhoneNumberId"]?.ToString() : null;
                    lastNotifiedStatus = r["LastNotifiedStatus"] is not DBNull ? r["LastNotifiedStatus"]?.ToString() : null;
                    lastNotifiedAtUtc = r["LastNotifiedAtUtc"] is DBNull ? null
                        : r["LastNotifiedAtUtc"] is DateTime dt ? dt
                        : DateTime.TryParse(r["LastNotifiedAtUtc"]?.ToString(), out var p) ? p : null;
                }
            }

            if (!found)
                return NotFound(new { error = "Order not found" });

            // Idempotencia: si no cambia, no notificar
            if (string.Equals(currentStatus, newStatus, StringComparison.OrdinalIgnoreCase))
            {
                return Ok(new
                {
                    success = true,
                    id,
                    status = currentStatus,
                    notified = false,
                    reason = "status_unchanged",
                    to = fromPhone,
                    phoneNumberId
                });
            }

            // Validate transition: only allow forward progression (+ cancel from any)
            var allowedTransitions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pending"]        = new(StringComparer.OrdinalIgnoreCase) { "Preparing", "Cancelled" },
                ["Preparing"]      = new(StringComparer.OrdinalIgnoreCase) { "ReadyForPickup", "OutForDelivery", "Cancelled" },
                ["ReadyForPickup"] = new(StringComparer.OrdinalIgnoreCase) { "OutForDelivery", "Completed", "Cancelled" },
                ["OutForDelivery"] = new(StringComparer.OrdinalIgnoreCase) { "Completed", "Cancelled" },
                ["Completed"]      = new(StringComparer.OrdinalIgnoreCase) {},
                ["Cancelled"]      = new(StringComparer.OrdinalIgnoreCase) {}
            };

            if (currentStatus != null
                && allowedTransitions.TryGetValue(currentStatus, out var allowed)
                && !allowed.Contains(newStatus))
            {
                return BadRequest(new
                {
                    error = $"Cannot change status from '{currentStatus}' to '{newStatus}'",
                    currentStatus,
                    requestedStatus = newStatus,
                    allowed = allowed.OrderBy(s => s).ToArray()
                });
            }

            // Step 2: Update status with optimistic concurrency guard
            using (var updCmd = conn.CreateCommand())
            {
                updCmd.CommandText = """
                    UPDATE "Orders" SET "Status" = @status
                    WHERE "Id" = @oid AND "Status" = @expected
                """;
                AddP(updCmd, "oid", id);
                AddP(updCmd, "status", newStatus);
                AddP(updCmd, "expected", currentStatus!);
                var rows = await updCmd.ExecuteNonQueryAsync();

                if (rows == 0)
                {
                    _logger.LogWarning(
                        "ORDER CONFLICT: order {OrderId} status was changed by another user (expected={Expected}, requested={Requested})",
                        id, currentStatus, newStatus);
                    return Conflict(new { error = "Order was updated by another user. Please refresh and try again.", orderId = id });
                }
            }

            // Step 3: WhatsApp notification (best-effort)
            var (shouldNotify, message) = MapStatusToMessage(newStatus);

            bool sendOk = false;
            bool notified = false;
            string? notifyReason = null;

            if (shouldNotify &&
                !string.IsNullOrWhiteSpace(fromPhone) &&
                !string.IsNullOrWhiteSpace(phoneNumberId))
            {
                if (string.Equals(lastNotifiedStatus, newStatus, StringComparison.OrdinalIgnoreCase))
                {
                    notifyReason = "already_notified_for_status";
                }
                else
                {
                    try
                    {
                        sendOk = await _whatsAppClient.SendTextMessageAsync(
                            new OutgoingMessage
                            {
                                To = fromPhone!,
                                PhoneNumberId = phoneNumberId!,
                                Body = message
                            });

                        notified = sendOk;

                        if (sendOk)
                        {
                            lastNotifiedStatus = newStatus;
                            lastNotifiedAtUtc = DateTime.UtcNow;

                            try
                            {
                                using var guardCmd = conn.CreateCommand();
                                guardCmd.CommandText = """
                                    UPDATE "Orders"
                                    SET "LastNotifiedStatus" = @lns, "LastNotifiedAtUtc" = @lna
                                    WHERE "Id" = @oid
                                """;
                                AddP(guardCmd, "oid", id);
                                AddP(guardCmd, "lns", newStatus);
                                AddP(guardCmd, "lna", lastNotifiedAtUtc!);
                                await guardCmd.ExecuteNonQueryAsync();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex,
                                    "WhatsApp sent but failed persisting notification guard for order {OrderId}", id);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed sending WhatsApp notification for order {OrderId}", id);
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
                id,
                status = newStatus,
                notified,
                to = fromPhone,
                phoneNumberId,
                sendOk,
                notifyReason,
                lastNotifiedStatus,
                lastNotifiedAtUtc
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PATCH /api/orders/{Id}/status failed", id);
            return StatusCode(500, new { error = "Unexpected server error" });
        }
    }

    // PATCH /api/orders/{id}/verify-payment
    [HttpPatch("{id:guid}/verify-payment")]
    public async Task<IActionResult> VerifyPayment(Guid id)
    {
        if (!IsAdmin()) return Unauthorized(new { error = "Missing or invalid X-Admin-Key" });
        var jwtBizId = GetJwtBusinessId();
        var order = jwtBizId.HasValue
            ? await _context.Orders.SingleOrDefaultAsync(o => o.Id == id && o.BusinessId == jwtBizId.Value)
            : await _context.Orders.SingleOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound(new { error = "Order not found" });

        order.PaymentVerifiedAtUtc = DateTime.UtcNow;
        order.PaymentVerifiedBy = "dashboard";

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "HTTP 409 due to concurrency conflict on OrderId={OrderId}", id);
            return Conflict(new { error = "Order was updated by another process. Please retry." });
        }

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
        if (!IsAdmin()) return Unauthorized(new { error = "Missing or invalid X-Admin-Key" });
        var jwtBizId = GetJwtBusinessId();
        var order = jwtBizId.HasValue
            ? await _context.Orders.SingleOrDefaultAsync(o => o.Id == id && o.BusinessId == jwtBizId.Value)
            : await _context.Orders.SingleOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound(new { error = "Order not found" });

        order.PaymentVerifiedAtUtc = null;
        order.PaymentVerifiedBy = null;
        order.PaymentProofMediaId = null;
        order.PaymentProofSubmittedAtUtc = null;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "HTTP 409 due to concurrency conflict on OrderId={OrderId}", id);
            return Conflict(new { error = "Order was updated by another process. Please retry." });
        }

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
        var jwtBizId = GetJwtBusinessId();

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
                var bizScope = jwtBizId.HasValue ? """ AND CAST("BusinessId" AS TEXT) = @bid""" : "";
                readCmd.CommandText = $"""
                    SELECT "From", "PhoneNumberId", "CustomerName", CAST("BusinessId" AS TEXT),
                           "CashChangeRequired", "CashChangeReturned",
                           "CashChangeAmountBs", "CashChangeAmount"
                    FROM "Orders" WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@oid){bizScope} LIMIT 1
                """;
                var p = readCmd.CreateParameter(); p.ParameterName = "oid"; p.Value = id.ToString();
                readCmd.Parameters.Add(p);
                if (jwtBizId.HasValue) AddP(readCmd, "bid", jwtBizId.Value.ToString());

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
            return StatusCode(500, new { error = "Unexpected server error" });
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
        var jwtBizId = GetJwtBusinessId();

        try
        {
            var order = await _context.Orders.AsNoTracking()
                .Where(o => o.Id == id)
                .Select(o => new { o.PaymentProofMediaId, o.BusinessId })
                .SingleOrDefaultAsync(ct);

            if (order is null)
                return NotFound(new { error = "Order not found" });
            if (jwtBizId.HasValue && order.BusinessId != jwtBizId.Value)
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

    private static void AddP(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
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

    private Guid? GetJwtBusinessId()
    {
        var claim = User.FindFirstValue("businessId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private bool IsAdmin()
    {
        // Path 1: valid JWT with any staff role
        var role = User.FindFirstValue(ClaimTypes.Role);
        if (role is "Founder" or "Owner" or "Manager" or "Operator")
            return true;

        // Path 2: valid X-Admin-Key header
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
            "ReadyForPickup" => (true, "✅ Tu pedido está listo para retirar en tienda."),
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
