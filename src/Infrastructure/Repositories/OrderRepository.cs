using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Repositories;

public sealed class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderRepository> _logger;

    public OrderRepository(AppDbContext db, ILogger<OrderRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task AddOrderAsync(Order order, CancellationToken ct = default)
    {
        // Use WhatsApp sender (order.From) as primary identifier — this is what
        // greeting lookups use, so it must be the stored key for customer memory.
        var fromE164 = NormalizeToE164(order.From);
        var custPhoneE164 = NormalizeToE164(order.CustomerPhone);
        var phoneE164 = fromE164 ?? custPhoneE164 ?? "+0000000000";

        var now = DateTime.UtcNow;

        // Buscar customer existente (scoped to business)
        // Check both From phone and checkout-form phone (legacy records may use either)
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.BusinessId == order.BusinessId
                && (c.PhoneE164 == phoneE164
                    || (custPhoneE164 != null && c.PhoneE164 == custPhoneE164)), ct);

        if (customer == null)
        {
            customer = new Customer
            {
                BusinessId = order.BusinessId,
                PhoneE164 = phoneE164,
                Name = string.IsNullOrWhiteSpace(order.CustomerName) ? null : order.CustomerName.Trim(),
                TotalSpent = 0m,
                OrdersCount = 0,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now
            };

            _db.Customers.Add(customer);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(customer.Name) && !string.IsNullOrWhiteSpace(order.CustomerName))
                customer.Name = order.CustomerName.Trim();

            // Migrate legacy records: if stored under checkout-form phone, update to WhatsApp sender
            if (fromE164 != null && customer.PhoneE164 != phoneE164)
                customer.PhoneE164 = phoneE164;

            customer.LastSeenAtUtc = now;
        }

        // Link order → customer
        order.CustomerId = customer.Id;

        // Recalcular montos
        order.RecalculateTotal();

        // Customer analytics are updated atomically via raw SQL AFTER SaveChangesAsync
        // to prevent lost-update race conditions from concurrent order creation.
        // (see post-save block below)
        if (order.CheckoutCompleted && !string.IsNullOrWhiteSpace(order.Address))
            customer.LastDeliveryAddress = order.Address;

        // Data guards — ensure monetary fields are never null for EF
        order.SubtotalAmount ??= 0m;
        order.DeliveryFee ??= 0m;
        order.TotalAmount ??= (order.SubtotalAmount ?? 0m) + (order.DeliveryFee ?? 0m);

        _db.Orders.Add(order);

        // Pre-save diagnostic log
        _logger.LogInformation(
            "ORDER SAVE: Id={Id} Biz={Biz} Status={Status} Items={Items} " +
            "Sub={Sub} Fee={Fee} Total={Total} Payment={Pay} Delivery={Del} " +
            "Name={Name} Phone={Phone} Addr={Addr} GPS={Lat},{Lng} " +
            "Cash={CashCur} Tendered={Tend} ChangeReq={ChgReq} ChgAmt={ChgAmt}",
            order.Id, order.BusinessId, order.Status, order.Items.Count,
            order.SubtotalAmount, order.DeliveryFee, order.TotalAmount,
            order.PaymentMethod, order.DeliveryType,
            order.CustomerName, order.CustomerPhone, order.Address,
            order.LocationLat, order.LocationLng,
            order.CashCurrency, order.CashTenderedAmount,
            order.CashChangeRequired, order.CashChangeAmount);

        foreach (var item in order.Items)
        {
            _logger.LogInformation("  ITEM: {Qty}x {Name} @ {Price} = {Total}",
                item.Quantity, item.Name, item.UnitPrice, item.LineTotal);
        }

        // Log EF change tracker entries
        foreach (var entry in _db.ChangeTracker.Entries())
        {
            _logger.LogDebug("  TRACKED: {Entity} [{State}]", entry.Entity.GetType().Name, entry.State);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("ORDER CREATED: {OrderId} | {BusinessId} | {Total}",
                order.Id, order.BusinessId, order.TotalAmount ?? 0m);

            // Atomic SQL increment for customer aggregates — immune to concurrent read-modify-write races.
            // This runs after the order + customer are persisted so the FK is guaranteed to exist.
            if (order.CheckoutCompleted)
            {
                var totalAmount = order.TotalAmount ?? 0m;
                try
                {
                    await _db.Database.ExecuteSqlRawAsync("""
                        UPDATE "Customers"
                        SET "OrdersCount" = "OrdersCount" + 1,
                            "TotalSpent" = "TotalSpent" + @p0,
                            "LastPurchaseAtUtc" = @p1
                        WHERE "Id" = @p2
                        """, totalAmount, now, customer.Id);
                }
                catch (Exception aggEx)
                {
                    _logger.LogError(aggEx, "ORDER SAVE: atomic customer aggregate update failed for CustomerId={CustomerId} OrderId={OrderId}",
                        customer.Id, order.Id);
                    // Non-fatal: the order itself was already persisted successfully
                }
            }

            _logger.LogInformation("ORDER SAVE: SUCCESS Id={Id}", order.Id);
        }
        catch (DbUpdateException dbEx)
        {
            var inner = dbEx.InnerException;
            if (inner is Npgsql.PostgresException pgEx)
            {
                _logger.LogError(pgEx,
                    "ORDER SAVE FAILED (Postgres): SqlState={SqlState} Table={Table} Column={Column} " +
                    "Constraint={Constraint} DataType={DataType} Message={Msg} Detail={Detail} Hint={Hint} Where={Where}",
                    pgEx.SqlState, pgEx.TableName, pgEx.ColumnName,
                    pgEx.ConstraintName, pgEx.Data["DataTypeName"],
                    pgEx.MessageText, pgEx.Detail, pgEx.Hint, pgEx.Where);
            }
            else
            {
                _logger.LogError(dbEx, "ORDER SAVE FAILED (DbUpdate): {Type}: {Message}",
                    inner?.GetType().Name ?? dbEx.GetType().Name,
                    inner?.Message ?? dbEx.Message);
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ORDER SAVE FAILED (unexpected): {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }

    public async Task<Order?> GetLastCompletedOrderAsync(string fromPhone, Guid businessId, CancellationToken ct = default)
    {
        return await _db.Orders
            .Include(o => o.Items)
            .Where(o => o.BusinessId == businessId
                     && o.From == fromPhone
                     && o.CheckoutCompleted)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Customer?> GetCustomerByPhoneAsync(string fromPhone, Guid businessId, CancellationToken ct = default)
    {
        var phoneE164 = NormalizeToE164(fromPhone) ?? fromPhone;
        return await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId && c.PhoneE164 == phoneE164, ct);
    }

    public async Task<bool> AttachPaymentProofAsync(Guid orderId, string mediaId, CancellationToken ct = default)
    {
        var order = await _db.Orders.FindAsync(new object[] { orderId }, ct);
        if (order is null) return false;

        order.PaymentProofMediaId = mediaId;
        order.PaymentProofSubmittedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =========================
    // Helpers
    // =========================

    private static string? NormalizeToE164(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();

        if (trimmed.StartsWith("+"))
        {
            var digits = new string(trimmed.Where(char.IsDigit).ToArray());
            return digits.Length >= 8 ? $"+{digits}" : null;
        }

        var onlyDigits = new string(trimmed.Where(char.IsDigit).ToArray());

        if (onlyDigits.Length < 8)
            return null;

        // Venezuela: 0414xxxxxxx -> +58414xxxxxxx
        if (onlyDigits.StartsWith("0") && onlyDigits.Length >= 10)
            return $"+58{onlyDigits[1..]}";

        if (onlyDigits.StartsWith("58"))
            return $"+{onlyDigits}";

        return $"+{onlyDigits}";
    }
}
