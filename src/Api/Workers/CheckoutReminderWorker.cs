using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Workers;

/// <summary>
/// Polls conversation states every 60s and sends up to 2 inactivity
/// reminders for unfinished checkouts. Reuses the existing
/// SendNotification job type so messages go through BackgroundJobWorker.
/// </summary>
public sealed class CheckoutReminderWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CheckoutReminderWorker> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Reminder1Delay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Reminder2Delay = TimeSpan.FromMinutes(15);
    private const string Reminder1Text = "\u00bfQuieres que termine tu pedido? \ud83d\ude0a";
    private const string Reminder2Text = "Tu pedido sigue disponible \ud83d\udc40";

    public CheckoutReminderWorker(IServiceScopeFactory scopeFactory, ILogger<CheckoutReminderWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CheckoutReminderWorker started");

        // Wait 30s on startup to let other services initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndSendRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckoutReminderWorker scan error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("CheckoutReminderWorker stopped");
    }

    private async Task ScanAndSendRemindersAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stateStore = scope.ServiceProvider.GetRequiredService<IConversationStateStore>();

        // Load all recently-updated conversations (active within last 30 min)
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var conversations = await db.ConversationStates
            .Where(c => c.UpdatedAtUtc > cutoff && c.BusinessId != null)
            .ToListAsync(ct);

        foreach (var conv in conversations)
        {
            try
            {
                await ProcessConversationAsync(conv, db, stateStore, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CheckoutReminderWorker: error processing {ConversationId}", conv.ConversationId);
            }
        }
    }

    private async Task ProcessConversationAsync(
        ConversationState conv, AppDbContext db, IConversationStateStore stateStore, CancellationToken ct)
    {
        var state = Deserialize(conv.StateJson);
        if (state is null) return;

        // No pending checkout
        if (!state.CheckoutPendingSinceUtc.HasValue) return;

        // Already completed / reset — clear stale pending
        if (state.Items.Count == 0 || !state.CheckoutFormSent)
        {
            state.CheckoutPendingSinceUtc = null;
            state.Reminder1SentAtUtc = null;
            state.Reminder2SentAtUtc = null;
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            return;
        }

        // User replied after checkout started — stop reminders
        if (state.LastActivityUtc.HasValue && state.LastActivityUtc > state.CheckoutPendingSinceUtc)
        {
            state.CheckoutPendingSinceUtc = null;
            state.Reminder1SentAtUtc = null;
            state.Reminder2SentAtUtc = null;
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - state.CheckoutPendingSinceUtc.Value;

        // Parse conversation ID: "{from}:{phoneNumberId}"
        var parts = conv.ConversationId.Split(':', 2);
        if (parts.Length != 2) return;
        var to = parts[0];
        var phoneNumberId = parts[1];

        // Resolve access token from business
        string? accessToken = null;
        if (conv.BusinessId.HasValue)
        {
            accessToken = await GetAccessTokenAsync(db, conv.BusinessId.Value, ct);
        }

        // Reminder 1: after 5 minutes
        if (!state.Reminder1SentAtUtc.HasValue && elapsed >= Reminder1Delay)
        {
            await EnqueueReminderAsync(db, to, phoneNumberId, accessToken,
                Reminder1Text, conv.BusinessId, ct);
            state.Reminder1SentAtUtc = now;
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            _logger.LogInformation("Checkout reminder 1 sent for {ConversationId}", conv.ConversationId);
            return;
        }

        // Reminder 2: after 15 minutes
        if (state.Reminder1SentAtUtc.HasValue && !state.Reminder2SentAtUtc.HasValue && elapsed >= Reminder2Delay)
        {
            await EnqueueReminderAsync(db, to, phoneNumberId, accessToken,
                Reminder2Text, conv.BusinessId, ct);
            state.Reminder2SentAtUtc = now;
            // Clear pending — no more reminders
            state.CheckoutPendingSinceUtc = null;
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            _logger.LogInformation("Checkout reminder 2 sent for {ConversationId}", conv.ConversationId);
        }
    }

    private static async Task EnqueueReminderAsync(
        AppDbContext db, string to, string phoneNumberId, string? accessToken,
        string body, Guid? businessId, CancellationToken ct)
    {
        var payload = new BackgroundJobWorker.NotificationPayload
        {
            To = to,
            Body = body,
            PhoneNumberId = phoneNumberId,
            AccessToken = accessToken
        };

        db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = "SendNotification",
            PayloadJson = JsonSerializer.Serialize(payload),
            BusinessId = businessId,
            MaxRetries = 2
        });

        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> GetAccessTokenAsync(AppDbContext db, Guid businessId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "AccessToken" FROM "Businesses" WHERE "Id" = @id LIMIT 1""";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = businessId;
        cmd.Parameters.Add(p);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static ConversationFields? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ConversationFields>(json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch
        {
            return null;
        }
    }
}
