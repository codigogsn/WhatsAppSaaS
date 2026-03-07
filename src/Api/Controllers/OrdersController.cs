using System;
using System.Collections.Generic;
using System.Linq;
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

    public OrdersController(AppDbContext context, IWhatsAppClient whatsAppClient, ILogger<OrdersController> logger)
    {
        _context = context;
        _whatsAppClient = whatsAppClient;
        _logger = logger;
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
                Items = o.Items.Select(i => new { i.Id, i.Name, i.Quantity })
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
                Items = x.Items.Select(i => new { i.Id, i.Name, i.Quantity })
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
