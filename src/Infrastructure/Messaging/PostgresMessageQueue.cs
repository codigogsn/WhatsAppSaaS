using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Infrastructure.Messaging;

/// <summary>
/// PostgreSQL-backed persistent message queue. Items survive container restarts.
/// Uses FOR UPDATE SKIP LOCKED for safe concurrent claim.
/// </summary>
public sealed class PostgresMessageQueue : IMessageQueue
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresMessageQueue> _logger;

    /// <summary>Exposed for lightweight health checks from the worker.</summary>
    public string ConnectionString => _connectionString;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PostgresMessageQueue(string connectionString, ILogger<PostgresMessageQueue> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async ValueTask EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        await EnqueueAsync(message, null, cancellationToken);
    }

    public async ValueTask EnqueueAsync(QueuedMessage message, string? messageId, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(message, JsonOpts);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // When messageId is provided, use ON CONFLICT to skip duplicates (enqueue-time dedup).
        // The unique partial index IX_WebhookQueue_MessageId covers unprocessed rows only.
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            cmd.CommandText = """
                INSERT INTO "WebhookQueue" ("Id", "Payload", "CreatedAtUtc", "AttemptCount", "MessageId")
                VALUES (@id, @payload, @now, 0, @msgId)
                ON CONFLICT ("MessageId") WHERE "MessageId" IS NOT NULL AND "ProcessedAtUtc" IS NULL
                DO NOTHING
            """;
            cmd.Parameters.AddWithValue("msgId", messageId);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO "WebhookQueue" ("Id", "Payload", "CreatedAtUtc", "AttemptCount")
                VALUES (@id, @payload, @now, 0)
            """;
        }
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0 && !string.IsNullOrWhiteSpace(messageId))
        {
            // ON CONFLICT DO NOTHING fired — message already in queue (dedup). Safe to ack.
            _logger.LogInformation("QUEUE DEDUP: skipped duplicate enqueue for messageId={MessageId}", messageId);
        }
        else if (rows == 0)
        {
            // Non-dedup path returned 0 rows — INSERT silently failed. Must not ack to Meta.
            _logger.LogError("WEBHOOK ENQUEUE FAILED: INSERT returned 0 rows without conflict. messageId={MessageId}", messageId ?? "(none)");
            throw new InvalidOperationException("Enqueue failed: INSERT returned 0 affected rows.");
        }
        else
        {
            _logger.LogDebug("QUEUE ENQUEUED: item persisted to WebhookQueue messageId={MessageId}", messageId ?? "(none)");
        }
    }

    /// <summary>
    /// Result of a dequeue operation, carrying the DB row ID and attempt count
    /// so each concurrent worker can independently complete/fail its own item.
    /// </summary>
    /// <summary>
    /// Result of a dequeue operation. Carries ownership marker (ClaimedAtUtc)
    /// so each worker can fence its completion/failure against re-claim by others.
    /// </summary>
    public sealed record DequeuedItem(QueuedMessage Message, Guid ItemId, int AttemptCount, DateTime ClaimedAtUtc);

    /// <summary>
    /// Dequeue with explicit item tracking for concurrent workers.
    /// Returns null if queue is empty.
    /// </summary>
    public async ValueTask<DequeuedItem?> DequeueItemAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH next AS (
                SELECT "Id"
                FROM "WebhookQueue"
                WHERE "ProcessedAtUtc" IS NULL
                  AND "AttemptCount" < 5
                  AND ("NextRetryAtUtc" IS NULL OR "NextRetryAtUtc" <= @now)
                  AND ("ClaimedAtUtc" IS NULL OR "ClaimedAtUtc" < @now - interval '15 minutes')
                ORDER BY "CreatedAtUtc"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE "WebhookQueue" q
            SET "ClaimedAtUtc" = @now,
                "AttemptCount" = q."AttemptCount" + 1
            FROM next
            WHERE q."Id" = next."Id"
            RETURNING q."Id", q."Payload", q."AttemptCount", q."ClaimedAtUtc"
        """;
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var id = reader.GetGuid(0);
        var json = reader.GetString(1);
        var attemptCount = reader.GetInt32(2);
        var claimedAtUtc = reader.GetDateTime(3);
        await reader.CloseAsync();

        QueuedMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<QueuedMessage>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "QUEUE: corrupt payload in item {Id}, marking processed", id);
            await MarkProcessedAsync(id, "corrupt_payload: " + ex.Message, cancellationToken);
            return null;
        }

        if (message is null)
        {
            await MarkProcessedAsync(id, "null_after_deserialize", cancellationToken);
            return null;
        }

        return new DequeuedItem(message, id, attemptCount, claimedAtUtc);
    }

    public async ValueTask<QueuedMessage?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var item = await DequeueItemAsync(cancellationToken);
        if (item is null) return null;

        // Legacy single-item tracking for backward compatibility
        _lastDequeuedItemId = item.ItemId;
        _lastDequeuedAttemptCount = item.AttemptCount;
        return item.Message;
    }

    // Legacy single-item tracking — kept for backward compatibility
    private Guid? _lastDequeuedItemId;
    private int _lastDequeuedAttemptCount;

    /// <summary>Returns the DB row ID of the last dequeued item, or null.</summary>
    public Guid? LastDequeuedItemId => _lastDequeuedItemId;

    /// <summary>Returns the attempt count of the last dequeued item (already incremented during dequeue).</summary>
    public int LastDequeuedAttemptCount => _lastDequeuedAttemptCount;

    // ── Explicit ID-based complete/fail (concurrent-safe) ──

    /// <summary>
    /// Mark a specific item as successfully processed.
    /// Ownership-fenced: only succeeds if this worker still holds the claim (ClaimedAtUtc matches).
    /// Returns true if completed; false if ownership was lost (another worker reclaimed the row).
    /// </summary>
    public async Task<bool> CompleteAsync(Guid itemId, DateTime claimedAtUtc, CancellationToken ct)
    {
        var rows = await MarkProcessedOwnedAsync(itemId, claimedAtUtc, null, ct);
        if (rows == 0)
        {
            _logger.LogWarning("QUEUE OWNERSHIP LOST: cannot complete item {Id} — claim was superseded by another worker", itemId);
            return false;
        }
        _logger.LogDebug("QUEUE COMPLETED: item {Id}", itemId);
        return true;
    }

    /// <summary>
    /// Record a processing failure for a specific item.
    /// Ownership-fenced: only succeeds if this worker still holds the claim.
    /// Returns (isTerminal, ownershipHeld). If ownership was lost, returns (false, false).
    /// </summary>
    public async Task<(bool IsTerminal, bool OwnershipHeld)> FailAsync(Guid itemId, int attemptCount, DateTime claimedAtUtc, string error, CancellationToken ct)
    {
        const int maxAttempts = 5;
        if (attemptCount >= maxAttempts)
        {
            var truncated = error.Length > 2000 ? error[..2000] : error;
            var rows = await MarkProcessedOwnedAsync(itemId, claimedAtUtc, "PERMANENT_FAILURE: " + truncated, ct);
            if (rows == 0)
            {
                _logger.LogWarning("QUEUE OWNERSHIP LOST: cannot mark terminal failure for item {Id} — claim was superseded", itemId);
                return (false, false);
            }
            _logger.LogWarning("QUEUE PERMANENT FAILURE: item {Id} exhausted all {MaxAttempts} retries", itemId, maxAttempts);
            return (true, true);
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "WebhookQueue"
            SET "LastError" = @err,
                "ClaimedAtUtc" = NULL,
                "NextRetryAtUtc" = now() + CASE "AttemptCount"
                    WHEN 1 THEN interval '30 seconds'
                    WHEN 2 THEN interval '2 minutes'
                    WHEN 3 THEN interval '10 minutes'
                    ELSE interval '30 minutes'
                END
            WHERE "Id" = @id AND "ProcessedAtUtc" IS NULL AND "ClaimedAtUtc" = @claimedAt
        """;
        cmd.Parameters.AddWithValue("id", itemId);
        cmd.Parameters.AddWithValue("err", error.Length > 2000 ? error[..2000] : error);
        cmd.Parameters.AddWithValue("claimedAt", claimedAtUtc);
        var rows2 = await cmd.ExecuteNonQueryAsync(ct);
        if (rows2 == 0)
        {
            _logger.LogWarning("QUEUE OWNERSHIP LOST: cannot schedule retry for item {Id} — claim was superseded", itemId);
            return (false, false);
        }
        _logger.LogDebug("QUEUE FAILED: item {Id} scheduled for retry with backoff", itemId);
        return (false, true);
    }

    /// <summary>
    /// Extend the claim lease for an item being actively processed.
    /// Ownership-fenced: only succeeds if this worker still holds the claim.
    /// Returns the new ClaimedAtUtc if successful, or null if ownership was lost.
    /// </summary>
    public async Task<DateTime?> RenewClaimAsync(Guid itemId, DateTime currentClaimedAtUtc, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "WebhookQueue"
            SET "ClaimedAtUtc" = now()
            WHERE "Id" = @id AND "ProcessedAtUtc" IS NULL AND "ClaimedAtUtc" = @claimedAt
            RETURNING "ClaimedAtUtc"
        """;
        cmd.Parameters.AddWithValue("id", itemId);
        cmd.Parameters.AddWithValue("claimedAt", currentClaimedAtUtc);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime dt ? dt : null;
    }

    // ── Legacy instance-based complete/fail (backward compat) ──

    public async Task CompleteLastAsync(CancellationToken ct)
    {
        if (_lastDequeuedItemId is not { } itemId) return;
        // Legacy path uses unfenced MarkProcessedAsync (single-worker sequential mode)
        await MarkProcessedAsync(itemId, null, ct);
        _logger.LogDebug("QUEUE COMPLETED: item {Id}", itemId);
        _lastDequeuedItemId = null;
    }

    /// <summary>
    /// Records a processing failure. Returns true when max retries are exhausted (terminal failure).
    /// Legacy unfenced path for single-worker sequential mode.
    /// </summary>
    public async Task<bool> FailLastAsync(string error, CancellationToken ct)
    {
        if (_lastDequeuedItemId is not { } itemId) return false;

        const int maxAttempts = 5;
        if (_lastDequeuedAttemptCount >= maxAttempts)
        {
            var truncated = error.Length > 2000 ? error[..2000] : error;
            await MarkProcessedAsync(itemId, "PERMANENT_FAILURE: " + truncated, ct);
            _logger.LogWarning("QUEUE PERMANENT FAILURE: item {Id} exhausted all {MaxAttempts} retries", itemId, maxAttempts);
            _lastDequeuedItemId = null;
            return true;
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "WebhookQueue"
            SET "LastError" = @err,
                "ClaimedAtUtc" = NULL,
                "NextRetryAtUtc" = now() + CASE "AttemptCount"
                    WHEN 1 THEN interval '30 seconds'
                    WHEN 2 THEN interval '2 minutes'
                    WHEN 3 THEN interval '10 minutes'
                    ELSE interval '30 minutes'
                END
            WHERE "Id" = @id AND "ProcessedAtUtc" IS NULL
        """;
        cmd.Parameters.AddWithValue("id", itemId);
        cmd.Parameters.AddWithValue("err", error.Length > 2000 ? error[..2000] : error);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("QUEUE FAILED: item {Id} scheduled for retry with backoff", itemId);
        _lastDequeuedItemId = null;
        return false;
    }

    /// <summary>
    /// Ownership-fenced mark-processed: only updates if ClaimedAtUtc matches.
    /// Returns the number of rows affected (0 = ownership lost).
    /// </summary>
    private async Task<int> MarkProcessedOwnedAsync(Guid id, DateTime claimedAtUtc, string? error, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "WebhookQueue"
            SET "ProcessedAtUtc" = @now, "LastError" = @err
            WHERE "Id" = @id AND "ClaimedAtUtc" = @claimedAt
        """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("claimedAt", claimedAtUtc);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkProcessedAsync(Guid id, string? error, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "WebhookQueue"
            SET "ProcessedAtUtc" = @now, "LastError" = @err
            WHERE "Id" = @id
        """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
