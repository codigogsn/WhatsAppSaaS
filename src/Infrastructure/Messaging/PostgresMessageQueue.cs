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
        var payload = JsonSerializer.Serialize(message, JsonOpts);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "WebhookQueue" ("Id", "Payload", "CreatedAtUtc", "AttemptCount")
            VALUES (@id, @payload, @now, 0)
        """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogDebug("QUEUE ENQUEUED: item persisted to WebhookQueue");
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

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "WebhookQueue"
            SET "LastError" = @err, "ClaimedAtUtc" = NULL
            WHERE "Id" = @id AND "ProcessedAtUtc" IS NULL
        """;
        cmd.Parameters.AddWithValue("id", itemId);
        cmd.Parameters.AddWithValue("err", error.Length > 2000 ? error[..2000] : error);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("QUEUE FAILED: item {Id} returned to queue", itemId);
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
