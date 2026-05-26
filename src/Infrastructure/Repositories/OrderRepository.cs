using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Domain.Exceptions;
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

    public async Task<Order> AddOrderAsync(Order order, CancellationToken ct = default)
    {
        // Use WhatsApp sender (order.From) as primary identifier — this is what
        // greeting lookups use, so it must be the stored key for customer memory.
        var fromE164 = NormalizeToE164(order.From);
        var custPhoneE164 = NormalizeToE164(order.CustomerPhone);
        var phoneE164 = fromE164 ?? custPhoneE164 ?? "+0000000000";

        var now = DateTime.UtcNow;

        // Upsert customer via raw SQL to survive concurrent inserts.
        // INSERT ... ON CONFLICT atomically creates or updates, so two simultaneous
        // order-creation requests for the same phone+business can never lose an order
        // to a unique-constraint violation.
        var custName = string.IsNullOrWhiteSpace(order.CustomerName) ? null : order.CustomerName.Trim();
        var custId = Guid.NewGuid();

        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Customers" ("Id", "BusinessId", "PhoneE164", "Name", "TotalSpent", "OrdersCount", "FirstSeenAtUtc", "LastSeenAtUtc")
            VALUES (@p0, @p1, @p2, @p3, 0, 0, @p4, @p4)
            ON CONFLICT ("BusinessId", "PhoneE164") DO UPDATE
            SET "LastSeenAtUtc" = @p4,
                "Name" = CASE WHEN "Customers"."Name" IS NULL OR "Customers"."Name" = '' THEN EXCLUDED."Name" ELSE "Customers"."Name" END,
                "PhoneE164" = COALESCE(@p5, "Customers"."PhoneE164")
            """, custId, order.BusinessId!, (object)phoneE164, custName as object ?? DBNull.Value, now, phoneE164 as object);

        // Reload the winning customer row (whether we inserted or the other request did)
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.BusinessId == order.BusinessId
                && (c.PhoneE164 == phoneE164
                    || (custPhoneE164 != null && c.PhoneE164 == custPhoneE164)), ct);

        if (customer == null)
        {
            // Defensive fallback — should never happen after upsert
            _logger.LogError("CUSTOMER UPSERT: reload failed for BusinessId={BusinessId} Phone={Phone}", order.BusinessId, phoneE164);
            customer = new Customer
            {
                BusinessId = order.BusinessId,
                PhoneE164 = phoneE164!,
                Name = custName,
                TotalSpent = 0m,
                OrdersCount = 0,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now
            };
            _db.Customers.Add(customer);
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

        // Reject zero/negative-value orders — a completed order with no monetary value
        // indicates a pricing bug upstream. Fail explicitly instead of persisting junk.
        if (order.TotalAmount <= 0m)
        {
            _logger.LogError(
                "ORDER REJECTED: zero or negative total. BusinessId={BusinessId} " +
                "TotalAmount={TotalAmount} SubtotalAmount={SubtotalAmount} DeliveryFee={DeliveryFee} " +
                "Items={ItemCount} CheckoutCompleted={Checkout}",
                order.BusinessId, order.TotalAmount, order.SubtotalAmount, order.DeliveryFee,
                order.Items.Count, order.CheckoutCompleted);
            throw new InvalidOperationException(
                $"Cannot persist order with TotalAmount={order.TotalAmount}. " +
                "Orders must have a positive total.");
        }

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
            // Explicit transaction: Order persistence + Customer aggregate update succeed or fail together.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("ORDER CREATED: {OrderId} | {BusinessId} | {Total}",
                order.Id, order.BusinessId, order.TotalAmount ?? 0m);

            // Atomic SQL increment for customer aggregates — immune to concurrent read-modify-write races.
            if (order.CheckoutCompleted)
            {
                var totalAmount = order.TotalAmount ?? 0m;
                await _db.Database.ExecuteSqlRawAsync("""
                    UPDATE "Customers"
                    SET "OrdersCount" = "OrdersCount" + 1,
                        "TotalSpent" = "TotalSpent" + @p0,
                        "LastPurchaseAtUtc" = @p1
                    WHERE "Id" = @p2
                    """, totalAmount, now, customer.Id);
            }

            await transaction.CommitAsync(ct);
            _logger.LogInformation("ORDER SAVE: SUCCESS Id={Id}", order.Id);
            return order;
        }
        catch (DbUpdateException dbEx) when (IsActivePendingConflict(dbEx))
        {
            // An active Pending order already exists for this (BusinessId, From, PhoneNumberId).
            // Do NOT create a duplicate — detach the rejected graph (Part A) and reuse the
            // existing Pending order (Part B1) so the finalize flow continues against it.
            _logger.LogWarning(
                "ORDER SAVE: IX_Orders_ActivePending conflict — BusinessId={BusinessId} From={From} " +
                "PhoneNumberId={PhoneNumberId}; reusing existing Pending order",
                order.BusinessId, order.From, order.PhoneNumberId);

            // Snapshot incoming.Items BEFORE DetachOrderEntities. EF Core 8 relationship
            // fixup empties the parent Order's nav collection when its tracked OrderItem
            // children are detached, which corrupted production order bb93fb41 (incoming
            // arrived with 1 item, reuse logged newItems=0, the existing row was rewritten
            // to Items=[], Sub=0, Total=DeliveryFee only). The snapshot is the kill switch.
            var incomingSnapshot = order.Items
                .Select(i => new OrderItem
                {
                    Name = i.Name,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    LineTotal = i.LineTotal
                })
                .ToList();

            DetachOrderEntities();
            return await ReuseExistingPendingOrderAsync(order, incomingSnapshot, ct);
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
            // Part A: detach the failed Order + OrderItems so the shared scoped DbContext
            // is not re-flushed by a later SaveChangesAsync (e.g. conversation-state save).
            DetachOrderEntities();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ORDER SAVE FAILED (unexpected): {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            // Part A: detach the failed Order + OrderItems so the shared scoped DbContext
            // is not re-flushed by a later SaveChangesAsync (e.g. conversation-state save).
            DetachOrderEntities();
            throw;
        }
    }

    /// <summary>
    /// True when the failure is the IX_Orders_ActivePending partial-unique-index
    /// violation — i.e. an active Pending order already exists for this customer.
    /// </summary>
    private static bool IsActivePendingConflict(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
           && string.Equals(pg.ConstraintName, "IX_Orders_ActivePending", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Part A — detaches every tracked Order and OrderItem from the shared scoped
    /// DbContext after a failed save, so a later SaveChangesAsync (e.g. conversation-state
    /// persistence at WebhookProcessor) does not re-flush the rejected order INSERT.
    /// Within a webhook message scope the only tracked orders are the failed attempt's.
    /// </summary>
    private void DetachOrderEntities()
    {
        foreach (var entry in _db.ChangeTracker.Entries<OrderItem>().ToList())
            entry.State = EntityState.Detached;
        foreach (var entry in _db.ChangeTracker.Entries<Order>().ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>
    /// Part B1 — reuse path. An active Pending order already exists for this
    /// (BusinessId, From, PhoneNumberId); update it in place from <paramref name="incoming"/>
    /// (customer/payment/location fields, totals) and <paramref name="incomingItems"/>
    /// (a snapshot of the cart captured BEFORE DetachOrderEntities ran, since EF Core's
    /// nav-collection fixup empties incoming.Items when its children are detached first).
    /// Returns the persisted existing order so the caller's finalize flow continues
    /// against the real row.
    /// </summary>
    private async Task<Order> ReuseExistingPendingOrderAsync(
        Order incoming,
        IReadOnlyList<OrderItem> incomingItems,
        CancellationToken ct)
    {
        // Reuse is scoped to the SAME business — multi-business isolation preserved.
        var existing = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.BusinessId == incoming.BusinessId
                && o.From == incoming.From
                && o.PhoneNumberId == incoming.PhoneNumberId
                && o.Status == "Pending", ct);

        if (existing is null)
        {
            // The conflicting row vanished between the failed INSERT and this read
            // (e.g. cancelled concurrently). Nothing to reuse.
            _logger.LogError(
                "ORDER REUSE FAILED: IX_Orders_ActivePending conflict but no Pending order found to reuse — " +
                "BusinessId={BusinessId} From={From} PhoneNumberId={PhoneNumberId}",
                incoming.BusinessId, incoming.From, incoming.PhoneNumberId);
            throw new InvalidOperationException(
                "IX_Orders_ActivePending conflict but no existing Pending order could be loaded for reuse.");
        }

        // Kill switch: refuse to merge an empty / zero-subtotal cart into a previously
        // valid Pending order. Without this guard, the EF fixup loss would silently
        // rewrite a healthy order down to Items=[], Sub=0, Total=DeliveryFee (the
        // exact signature of production incident bb93fb41). The repository must
        // never persist a malformed reuse — let the caller surface a retry message.
        var snapshotSubtotal = incomingItems.Sum(i => (i.UnitPrice ?? 0m) * i.Quantity);
        if (incomingItems.Count == 0 || snapshotSubtotal <= 0m)
        {
            _logger.LogError(
                "ORDER REUSE REJECTED: empty or zero-subtotal incoming cart — " +
                "existingId={ExistingId} rejectedAttemptId={AttemptId} BusinessId={BusinessId} " +
                "From={From} snapshotItems={NewItems} snapshotSubtotal={NewSub}",
                existing.Id, incoming.Id, existing.BusinessId, existing.From,
                incomingItems.Count, snapshotSubtotal);
            DetachOrderEntities();
            throw new InvalidOperationException(
                "Cannot merge an empty or zero-subtotal cart into an existing Pending order.");
        }

        _logger.LogInformation(
            "ORDER REUSED: existingId={ExistingId} rejectedAttemptId={AttemptId} BusinessId={BusinessId} " +
            "From={From} oldTotal={OldTotal} oldItems={OldItems} newItems={NewItems}",
            existing.Id, incoming.Id, existing.BusinessId, existing.From,
            existing.TotalAmount ?? 0m, existing.Items.Count, incomingItems.Count);

        var oldTotal = existing.TotalAmount ?? 0m;

        // ── Replace cart / items (iterate the SNAPSHOT, not incoming.Items) ──
        _db.OrderItems.RemoveRange(existing.Items.ToList());
        existing.Items.Clear();
        foreach (var i in incomingItems)
        {
            existing.Items.Add(new OrderItem
            {
                Name = i.Name,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = i.LineTotal
            });
        }

        // ── Customer / delivery details ──
        existing.CustomerId          = incoming.CustomerId;
        existing.CustomerName        = incoming.CustomerName;
        existing.CustomerIdNumber    = incoming.CustomerIdNumber;
        existing.CustomerPhone       = incoming.CustomerPhone;
        existing.Address             = incoming.Address;
        existing.DeliveryType        = incoming.DeliveryType;
        existing.SpecialInstructions = incoming.SpecialInstructions;

        // ── Location ──
        existing.LocationText = incoming.LocationText;
        existing.LocationLat  = incoming.LocationLat;
        existing.LocationLng  = incoming.LocationLng;

        // ── Payment fields ──
        existing.PaymentMethod              = incoming.PaymentMethod;
        existing.PaymentProofMediaId        = incoming.PaymentProofMediaId;
        existing.PaymentProofSubmittedAtUtc = incoming.PaymentProofSubmittedAtUtc;
        existing.CashCurrency       = incoming.CashCurrency;
        existing.CashTenderedAmount = incoming.CashTenderedAmount;
        existing.CashBcvRateUsed    = incoming.CashBcvRateUsed;
        existing.CashChangeRequired = incoming.CashChangeRequired;
        existing.CashChangeAmount   = incoming.CashChangeAmount;
        existing.CashChangeAmountBs = incoming.CashChangeAmountBs;
        existing.CashPayoutBank     = incoming.CashPayoutBank;
        existing.CashPayoutIdNumber = incoming.CashPayoutIdNumber;
        existing.CashPayoutPhone    = incoming.CashPayoutPhone;

        // ── Checkout flags ──
        existing.CheckoutCompleted      = incoming.CheckoutCompleted;
        existing.CheckoutCompletedAtUtc = incoming.CheckoutCompletedAtUtc;
        existing.CheckoutFormSent       = incoming.CheckoutFormSent;

        // ── Totals ──
        existing.DeliveryFee = incoming.DeliveryFee;
        existing.RecalculateTotal();

        // Structural validation after merge — last line of defense against an
        // entity-level inconsistency producing a ghost order. Mirrors the
        // TotalAmount > 0 guard at AddOrderAsync line ~89 (new-row path), but
        // tightened: the reuse path must also enforce Items>0, Subtotal>0,
        // and Total strictly greater than DeliveryFee. The third clause is
        // the precise signature of the production incident (Total == Fee).
        var mergedItemCount = existing.Items.Count;
        var mergedSubtotal  = existing.SubtotalAmount ?? 0m;
        var mergedTotal     = existing.TotalAmount ?? 0m;
        var mergedFee       = existing.DeliveryFee ?? 0m;
        if (mergedItemCount == 0 || mergedSubtotal <= 0m || mergedTotal <= mergedFee)
        {
            _logger.LogError(
                "ORDER REUSE REJECTED: structurally invalid merge — existingId={ExistingId} " +
                "items={Items} sub={Sub} fee={Fee} total={Total}",
                existing.Id, mergedItemCount, mergedSubtotal, mergedFee, mergedTotal);
            DetachOrderEntities();
            throw new InvalidOperationException(
                "Reused order would be structurally invalid: " +
                $"items={mergedItemCount}, subtotal={mergedSubtotal}, total={mergedTotal}, fee={mergedFee}.");
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await _db.SaveChangesAsync(ct);

            // Analytics preserved: the order was already counted in OrdersCount/TotalSpent
            // at first creation. Do NOT increment OrdersCount again — only adjust TotalSpent
            // by the delta so lifetime spend stays accurate after the cart changed.
            var newTotal = existing.TotalAmount ?? 0m;
            var spendDelta = newTotal - oldTotal;
            if (existing.CheckoutCompleted && existing.CustomerId.HasValue && spendDelta != 0m)
            {
                await _db.Database.ExecuteSqlRawAsync("""
                    UPDATE "Customers"
                    SET "TotalSpent" = "TotalSpent" + @p0,
                        "LastPurchaseAtUtc" = @p1
                    WHERE "Id" = @p2
                    """, spendDelta, DateTime.UtcNow, existing.CustomerId.Value);
            }

            await transaction.CommitAsync(ct);
            _logger.LogInformation(
                "ORDER REUSE SUCCESS: existingId={ExistingId} newTotal={NewTotal} items={Items} spendDelta={Delta}",
                existing.Id, newTotal, existing.Items.Count, spendDelta);
            return existing;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ORDER REUSE FAILED: existingId={ExistingId} {Type}: {Message}",
                existing.Id, ex.GetType().Name, ex.Message);
            // Part A: keep the shared scoped DbContext clean even on a reuse failure.
            DetachOrderEntities();
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

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "CONCURRENCY CONFLICT on OrderId={OrderId}", orderId);
            throw new ConcurrencyException("Order was modified by another process.");
        }

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
