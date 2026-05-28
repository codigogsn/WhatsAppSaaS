using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Repositories;

// Append-only writer for the ConversationMessages durable log.
//
// Fail-safe contract: this class catches and logs every exception. It never
// throws. Callers are free to await without try/catch — message logging is
// strictly best-effort and must not break the production WhatsApp flow.
//
// Tenant safety: BusinessId on the row is the only authority. There is no
// global query filter; future readers must always include BusinessId in
// their WHERE clause to stay consistent with the rest of the codebase.
//
// Idempotency: inbound rows carry WhatsApp's message id. A unique partial
// index on (BusinessId, WhatsAppMessageId) WHERE NOT NULL absorbs webhook
// retries — duplicates fail with 23505 and we silently move on.
public sealed class ConversationMessageStore : IConversationMessageStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<ConversationMessageStore> _logger;

    public ConversationMessageStore(AppDbContext db, ILogger<ConversationMessageStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task AppendAsync(ConversationMessage message, CancellationToken ct = default)
    {
        if (message is null) return;
        if (message.BusinessId == Guid.Empty) return; // No tenant → drop silently.
        if (string.IsNullOrWhiteSpace(message.ConversationId)) return;

        try
        {
            _db.ConversationMessages.Add(message);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // Webhook retry for the same inbound message — expected, not an error.
            DetachFailed(message);
            _logger.LogDebug(
                "ConversationMessage append: duplicate WhatsAppMessageId ignored businessId={BusinessId} messageId={WhatsAppMessageId}",
                message.BusinessId, message.WhatsAppMessageId);
        }
        catch (Exception ex)
        {
            // Any other failure: log and continue. The production flow must not break.
            DetachFailed(message);
            _logger.LogWarning(ex,
                "ConversationMessage append failed businessId={BusinessId} conversationId={ConversationId} direction={Direction} sender={Sender}",
                message.BusinessId, message.ConversationId, message.Direction, message.Sender);
        }
    }

    private void DetachFailed(ConversationMessage message)
    {
        // Keep the DbContext usable for subsequent operations in the same request.
        var entry = _db.ChangeTracker.Entries<ConversationMessage>()
            .FirstOrDefault(e => ReferenceEquals(e.Entity, message));
        if (entry is not null)
            entry.State = EntityState.Detached;
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
            return true;
        if (ex.InnerException?.Message?.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return false;
    }
}
