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
        _logger.LogInformation("CheckoutReminderWorker started — poll={Poll}s r1={R1}min r2={R2}min",
            PollInterval.TotalSeconds, Reminder1Delay.TotalMinutes, Reminder2Delay.TotalMinutes);

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

        int scanned = 0, noPending = 0, staleCleared = 0, userReplied = 0,
            notReady = 0, reminder1Sent = 0, reminder2Sent = 0, errors = 0;

        foreach (var conv in conversations)
        {
            try
            {
                var result = await ProcessConversationAsync(conv, db, stateStore, ct);
                scanned++;
                switch (result)
                {
                    case ProcessResult.NoPending: noPending++; break;
                    case ProcessResult.StaleCleared: staleCleared++; break;
                    case ProcessResult.UserReplied: userReplied++; break;
                    case ProcessResult.NotReady: notReady++; break;
                    case ProcessResult.Reminder1Sent: reminder1Sent++; break;
                    case ProcessResult.Reminder2Sent: reminder2Sent++; break;
                }
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogWarning(ex, "REMINDER: error processing {ConversationId}", conv.ConversationId);
            }
        }

        if (scanned > 0 || conversations.Count > 0)
        {
            _logger.LogInformation(
                "REMINDER SCAN: total={Total} scanned={Scanned} noPending={NoPending} " +
                "staleCleared={StaleCleared} userReplied={UserReplied} notReady={NotReady} " +
                "r1Sent={R1Sent} r2Sent={R2Sent} errors={Errors}",
                conversations.Count, scanned, noPending, staleCleared, userReplied,
                notReady, reminder1Sent, reminder2Sent, errors);
        }
    }

    private enum ProcessResult { NoPending, StaleCleared, UserReplied, NotReady, Reminder1Sent, Reminder2Sent }

    private async Task<ProcessResult> ProcessConversationAsync(
        ConversationState conv, AppDbContext db, IConversationStateStore stateStore, CancellationToken ct)
    {
        var state = Deserialize(conv.StateJson);
        if (state is null) return ProcessResult.NoPending;

        // No pending checkout
        if (!state.CheckoutPendingSinceUtc.HasValue)
            return ProcessResult.NoPending;

        // Already completed / reset — clear stale pending
        if (state.Items.Count == 0)
        {
            _logger.LogDebug("REMINDER: clearing stale pending for {Conv} — no items", conv.ConversationId);
            ClearReminderState(state);
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            return ProcessResult.StaleCleared;
        }

        // User replied after checkout started — stop reminders
        // Use 30-second tolerance to avoid same-cycle false positives
        if (state.LastActivityUtc.HasValue
            && state.LastActivityUtc.Value > state.CheckoutPendingSinceUtc.Value.AddSeconds(30))
        {
            _logger.LogDebug("REMINDER: user replied for {Conv} — lastActivity={Activity} checkoutPending={Pending}",
                conv.ConversationId, state.LastActivityUtc, state.CheckoutPendingSinceUtc);
            ClearReminderState(state);
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            return ProcessResult.UserReplied;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - state.CheckoutPendingSinceUtc.Value;

        // Parse conversation ID: "{from}:{phoneNumberId}"
        var parts = conv.ConversationId.Split(':', 2);
        if (parts.Length != 2)
        {
            _logger.LogWarning("REMINDER: invalid conversationId format: {Conv}", conv.ConversationId);
            return ProcessResult.NoPending;
        }
        var to = parts[0];
        var phoneNumberId = parts[1];

        if (string.IsNullOrWhiteSpace(to) || to.Length < 8)
        {
            _logger.LogDebug("REMINDER: invalid phone for {Conv}", conv.ConversationId);
            return ProcessResult.NoPending;
        }

        // Resolve access token from business
        string? accessToken = null;
        if (conv.BusinessId.HasValue)
        {
            accessToken = await GetAccessTokenAsync(db, conv.BusinessId.Value, ct);
        }

        // Reminder 1: after 5 minutes
        if (!state.Reminder1SentAtUtc.HasValue && elapsed >= Reminder1Delay)
        {
            _logger.LogInformation(
                "REMINDER 1: sending for {Conv} elapsed={Elapsed}min to={To} phoneNumberId={PNI} hasToken={HasToken}",
                conv.ConversationId, elapsed.TotalMinutes.ToString("F1"), to, phoneNumberId,
                !string.IsNullOrWhiteSpace(accessToken));
            await EnqueueReminderAsync(db, to, phoneNumberId, accessToken,
                Reminder1Text, conv.BusinessId, ct);
            state.Reminder1SentAtUtc = now;
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            return ProcessResult.Reminder1Sent;
        }

        // Reminder 2: after 15 minutes total
        if (state.Reminder1SentAtUtc.HasValue && !state.Reminder2SentAtUtc.HasValue && elapsed >= Reminder2Delay)
        {
            _logger.LogInformation(
                "REMINDER 2: sending for {Conv} elapsed={Elapsed}min to={To}",
                conv.ConversationId, elapsed.TotalMinutes.ToString("F1"), to);
            await EnqueueReminderAsync(db, to, phoneNumberId, accessToken,
                Reminder2Text, conv.BusinessId, ct);
            state.Reminder2SentAtUtc = now;
            // Clear pending — no more reminders
            state.CheckoutPendingSinceUtc = null;
            await stateStore.SaveAsync(conv.ConversationId, state, ct);
            return ProcessResult.Reminder2Sent;
        }

        return ProcessResult.NotReady;
    }

    private static void ClearReminderState(ConversationFields state)
    {
        state.CheckoutPendingSinceUtc = null;
        state.Reminder1SentAtUtc = null;
        state.Reminder2SentAtUtc = null;
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

    private async Task<string?> GetAccessTokenAsync(AppDbContext db, Guid businessId, CancellationToken ct)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """SELECT "AccessToken" FROM "Businesses" WHERE CAST("Id" AS TEXT) = @id LIMIT 1""";
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = businessId.ToString();
            cmd.Parameters.Add(p);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REMINDER: failed to resolve access token for business {BusinessId}", businessId);
            return null;
        }
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
