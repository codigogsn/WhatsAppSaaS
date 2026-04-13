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
        if (rows == 0)
            _logger.LogInformation("QUEUE DEDUP: skipped duplicate enqueue for messageId={MessageId}", messageId);
        else
            _logger.LogDebug("QUEUE ENQUEUED: item persisted to WebhookQueue messageId={MessageId}", messageId ?? "(none)");
    }

    public async ValueTask<QueuedMessage?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Atomic claim: SELECT + UPDATE in one statement using CTE with FOR UPDATE SKIP LOCKED
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH next AS (
                SELECT "Id"
                FROM "WebhookQueue"
                WHERE "ProcessedAtUtc" IS NULL
                  AND "AttemptCount" < 5
                  AND ("NextRetryAtUtc" IS NULL OR "NextRetryAtUtc" <= @now)
                  AND ("ClaimedAtUtc" IS NULL OR "ClaimedAtUtc" < @now - interval '5 minutes')
                ORDER BY "CreatedAtUtc"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE "WebhookQueue" q
            SET "ClaimedAtUtc" = @now,
                "AttemptCount" = q."AttemptCount" + 1
            FROM next
            WHERE q."Id" = next."Id"
            RETURNING q."Id", q."Payload"
        """;
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var id = reader.GetGuid(0);
        var json = reader.GetString(1);
        await reader.CloseAsync();

        QueuedMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<QueuedMessage>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "QUEUE: corrupt payload in item {Id}, marking processed", id);
            await MarkProcessedAsync(conn, id, "corrupt_payload: " + ex.Message, cancellationToken);
            return null;
        }

        if (message is null)
        {
            await MarkProcessedAsync(conn, id, "null_after_deserialize", cancellationToken);
            return null;
        }

        // Track the DB row ID so the worker can mark complete/fail
        _lastDequeuedItemId = id;
        return message;
    }

    // Tracks the last dequeued item so the worker can complete/fail it.
    // Thread-safe because there is only one worker consuming sequentially.
    private Guid? _lastDequeuedItemId;

    /// <summary>Returns the DB row ID of the last dequeued item, or null.</summary>
    public Guid? LastDequeuedItemId => _lastDequeuedItemId;

    public async Task CompleteLastAsync(CancellationToken ct)
    {
        if (_lastDequeuedItemId is not { } itemId) return;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await MarkProcessedAsync(conn, itemId, null, ct);
        _logger.LogDebug("QUEUE COMPLETED: item {Id}", itemId);
        _lastDequeuedItemId = null;
    }

    public async Task FailLastAsync(string error, CancellationToken ct)
    {
        if (_lastDequeuedItemId is not { } itemId) return;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Backoff computed in SQL from AttemptCount (already incremented during dequeue):
        // 1→30s, 2→2min, 3→10min, 4→30min
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
    }

    private static async Task MarkProcessedAsync(NpgsqlConnection conn, Guid id, string? error, CancellationToken ct)
    {
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
