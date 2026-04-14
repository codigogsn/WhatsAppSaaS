using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Infrastructure.Persistence;

public sealed class ConversationStateStore : IConversationStateStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<ConversationStateStore> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ConversationStateStore(AppDbContext db, ILogger<ConversationStateStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ConversationFields> GetOrCreateAsync(string conversationId, Guid? businessId, CancellationToken ct = default)
    {
        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);

        if (entity is null)
        {
            entity = new ConversationState
            {
                ConversationId = conversationId,
                BusinessId = businessId,
                UpdatedAtUtc = DateTime.UtcNow,
                StateJson = "{}"
            };
            _db.ConversationStates.Add(entity);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                // Race: another thread inserted first. Detach our failed entity and re-fetch.
                var tracked = _db.ChangeTracker.Entries<ConversationState>()
                    .FirstOrDefault(e => e.Entity.ConversationId == conversationId);
                if (tracked is not null)
                    tracked.State = EntityState.Detached;

                entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
                if (entity is not null)
                {
                    entity.UpdatedAtUtc = DateTime.UtcNow;
                    try { return JsonSerializer.Deserialize<ConversationFields>(entity.StateJson, JsonOpts) ?? new ConversationFields(); }
                    catch { _logger.LogWarning("Corrupt StateJson for {ConversationId}", conversationId); return new ConversationFields(); }
                }
            }

            return new ConversationFields();
        }

        entity.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            return JsonSerializer.Deserialize<ConversationFields>(entity.StateJson, JsonOpts) ?? new ConversationFields();
        }
        catch
        {
            _logger.LogWarning("Corrupt StateJson for {ConversationId}", conversationId);
            return new ConversationFields();
        }
    }

    public async Task SaveAsync(string conversationId, ConversationFields fields, CancellationToken ct = default)
    {
        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null) return;

        entity.StateJson = JsonSerializer.Serialize(fields, JsonOpts);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsMessageProcessedAsync(string conversationId, string messageId, CancellationToken ct = default)
    {
        return await _db.ProcessedMessages
            .AnyAsync(p => p.ConversationId == conversationId && p.MessageId == messageId, ct);
    }

    public async Task<bool> MarkMessageProcessedAsync(string conversationId, string messageId, CancellationToken ct = default)
    {
        _db.ProcessedMessages.Add(new ProcessedMessage
        {
            ConversationId = conversationId,
            MessageId = messageId,
            CreatedAtUtc = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(ct);
            return true; // newly inserted
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // Unique index IX_ProcessedMessages_ConversationId_MessageId caught the duplicate.
            // Detach the failed entity so the DbContext remains usable for subsequent operations.
            var entry = _db.ChangeTracker.Entries<ProcessedMessage>()
                .FirstOrDefault(e => e.Entity.ConversationId == conversationId && e.Entity.MessageId == messageId);
            if (entry is not null)
                entry.State = EntityState.Detached;
            return false; // duplicate
        }
    }

    public async Task UnclaimMessageAsync(string conversationId, string messageId, CancellationToken ct = default)
    {
        var entity = await _db.ProcessedMessages
            .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.MessageId == messageId, ct);
        if (entity is not null)
        {
            _db.ProcessedMessages.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        // Npgsql: SqlState 23505 = unique_violation
        if (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
            return true;
        // SQLite (dev): UNIQUE constraint failed
        if (ex.InnerException?.Message?.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return false;
    }

    public async Task PurgeOldStatesAsync(TimeSpan ttl, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - ttl;

        // Clean up old conversation states
        var old = await _db.ConversationStates
            .Where(s => s.UpdatedAtUtc < cutoff)
            .ToListAsync(ct);

        if (old.Count > 0)
        {
            _db.ConversationStates.RemoveRange(old);
            await _db.SaveChangesAsync(ct);
        }

        // Processed-message tombstones are retained for 7 days (not tied to conversation TTL)
        // to ensure webhook replay protection outlives delayed upstream retries.
        var dedupCutoff = DateTime.UtcNow.AddDays(-7);
        var oldMsgs = await _db.ProcessedMessages
            .Where(p => p.CreatedAtUtc < dedupCutoff)
            .ToListAsync(ct);

        if (oldMsgs.Count > 0)
        {
            _db.ProcessedMessages.RemoveRange(oldMsgs);
            await _db.SaveChangesAsync(ct);
        }
    }
}
