using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Infrastructure.Persistence;

public sealed class ConversationStateStore : IConversationStateStore
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ConversationStateStore(AppDbContext db)
    {
        _db = db;
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
                    catch { return new ConversationFields(); }
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

        var old = await _db.ConversationStates
            .Where(s => s.UpdatedAtUtc < cutoff)
            .ToListAsync(ct);

        if (old.Count == 0) return;

        var oldIds = old.Select(s => s.ConversationId).ToList();
        var oldMsgs = await _db.ProcessedMessages
            .Where(p => oldIds.Contains(p.ConversationId))
            .ToListAsync(ct);

        _db.ProcessedMessages.RemoveRange(oldMsgs);
        _db.ConversationStates.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
    }
}
