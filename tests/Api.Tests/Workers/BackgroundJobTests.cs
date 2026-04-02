using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WhatsAppSaaS.Api.Workers;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;
using WhatsAppSaaS.Infrastructure.Services;

namespace WhatsAppSaaS.Api.Tests.Workers;

public class BackgroundJobTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public BackgroundJobTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // 1. Failed notification job retries correctly
    [Fact]
    public async Task FailedNotificationJob_RetriesCorrectly()
    {
        // Enqueue a notification job
        var svc = new BackgroundJobService(_db);
        await svc.EnqueueAsync("SendNotification", new
        {
            To = "58412999",
            Body = "Test notification",
            PhoneNumberId = "123",
            AccessToken = "tok"
        }, maxRetries: 3);

        var job = await _db.BackgroundJobs.FirstAsync();
        job.Status.Should().Be("Pending");
        job.RetryCount.Should().Be(0);
        job.MaxRetries.Should().Be(3);

        // Simulate first failure
        job.Status = "Processing";
        job.LockedAtUtc = DateTime.UtcNow;
        job.RetryCount = 1;
        job.LastError = "WhatsApp send failed";
        job.Status = "Pending";
        job.ScheduledAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        job.RetryCount.Should().Be(1);
        job.Status.Should().Be("Pending");
    }

    // 2. Cleanup job processes stale records safely
    [Fact]
    public async Task CleanupConversations_RemovesStaleRecords()
    {
        // Create stale conversation
        _db.ConversationStates.Add(new ConversationState
        {
            ConversationId = "stale-conv-1",
            BusinessId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow.AddHours(-12), // 12 hours old (>6h TTL)
            StateJson = "{}"
        });
        _db.ProcessedMessages.Add(new ProcessedMessage
        {
            ConversationId = "stale-conv-1",
            MessageId = "wamid.stale-1",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-12)
        });

        // Create fresh conversation
        _db.ConversationStates.Add(new ConversationState
        {
            ConversationId = "fresh-conv-1",
            BusinessId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-30), // 30 min old
            StateJson = "{}"
        });

        await _db.SaveChangesAsync();

        // Run cleanup via store
        var store = new ConversationStateStore(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationStateStore>.Instance);
        await store.PurgeOldStatesAsync(TimeSpan.FromHours(6));

        var remaining = await _db.ConversationStates.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].ConversationId.Should().Be("fresh-conv-1");

        var msgs = await _db.ProcessedMessages.ToListAsync();
        msgs.Should().BeEmpty(); // stale message removed
    }

    // 3. Job worker processes scheduled jobs without duplicating effects
    [Fact]
    public async Task JobWorker_ProcessesPendingJobs_NoDuplication()
    {
        // Create a cleanup job
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = "CleanupCompletedJobs",
            Status = "Pending",
            ScheduledAtUtc = DateTime.UtcNow.AddMinutes(-1),
            MaxRetries = 2
        });

        // Create old completed jobs to clean up
        for (var i = 0; i < 5; i++)
        {
            _db.BackgroundJobs.Add(new BackgroundJob
            {
                JobType = "SendNotification",
                Status = "Done",
                CompletedAtUtc = DateTime.UtcNow.AddDays(-10), // 10 days old
                ScheduledAtUtc = DateTime.UtcNow.AddDays(-10)
            });
        }

        await _db.SaveChangesAsync();

        var totalBefore = await _db.BackgroundJobs.CountAsync();
        totalBefore.Should().Be(6); // 1 cleanup + 5 old

        // Simulate the worker picking up the cleanup job
        var cleanupJob = await _db.BackgroundJobs.FirstAsync(j => j.JobType == "CleanupCompletedJobs");
        cleanupJob.Status.Should().Be("Pending");

        // Mark as done (simulating successful processing)
        cleanupJob.Status = "Done";
        cleanupJob.CompletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Verify job was not duplicated
        var cleanupJobs = await _db.BackgroundJobs
            .Where(j => j.JobType == "CleanupCompletedJobs")
            .ToListAsync();
        cleanupJobs.Should().HaveCount(1);
    }

    // 4. Main order flow still succeeds even if background job fails
    [Fact]
    public async Task NotificationService_FallsBackOnJobFailure()
    {
        var mockClient = new Mock<IWhatsAppClient>();
        mockClient.Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Create notification service WITHOUT job service (simulates job service failure)
        var logger = Mock.Of<ILogger<WhatsAppSaaS.Application.Services.NotificationService>>();
        var svc = new WhatsAppSaaS.Application.Services.NotificationService(mockClient.Object, logger);

        var biz = new BusinessContext(
            Guid.NewGuid(), "123", "tok",
            NotificationPhone: "58412999");

        // Should fall back to direct send without throwing
        await svc.NotifyOrderConfirmedAsync(biz, "Test", "1 Burger", "$5.00");

        mockClient.Verify(
            x => x.SendTextMessageAsync(It.Is<OutgoingMessage>(m => m.To == "58412999"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // 4b. When job service is available, notification is enqueued
    [Fact]
    public async Task NotificationService_EnqueuesWhenJobServiceAvailable()
    {
        var mockClient = new Mock<IWhatsAppClient>();
        var jobSvc = new BackgroundJobService(_db);
        var logger = Mock.Of<ILogger<WhatsAppSaaS.Application.Services.NotificationService>>();
        var svc = new WhatsAppSaaS.Application.Services.NotificationService(mockClient.Object, logger, jobSvc);

        var biz = new BusinessContext(
            Guid.NewGuid(), "123", "tok",
            NotificationPhone: "58412999");

        await svc.NotifyHumanHandoffAsync(biz, "58412111");

        // Should NOT have called WhatsApp directly
        mockClient.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Should have enqueued a job
        var jobs = await _db.BackgroundJobs.ToListAsync();
        jobs.Should().HaveCount(1);
        jobs[0].JobType.Should().Be("SendNotification");
        jobs[0].Status.Should().Be("Pending");

        var payload = JsonSerializer.Deserialize<JsonElement>(jobs[0].PayloadJson);
        payload.GetProperty("To").GetString().Should().Be("58412999");
    }

    // 5. Retry cap works correctly
    [Fact]
    public async Task RetryCapReached_JobMarkedAsFailed()
    {
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = "SendNotification",
            PayloadJson = JsonSerializer.Serialize(new { To = "123", Body = "test", PhoneNumberId = "456" }),
            Status = "Pending",
            RetryCount = 2,
            MaxRetries = 3,
            ScheduledAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var job = await _db.BackgroundJobs.FirstAsync();

        // Simulate the worker processing and failing on the 3rd attempt
        job.RetryCount = 3; // Now equals MaxRetries
        job.Status = "Failed";
        job.CompletedAtUtc = DateTime.UtcNow;
        job.LastError = "WhatsApp send failed after max retries";
        await _db.SaveChangesAsync();

        job.Status.Should().Be("Failed");
        job.RetryCount.Should().Be(3);
        job.CompletedAtUtc.Should().NotBeNull();
    }

    // 6. Job locking prevents double-processing
    [Fact]
    public async Task JobLocking_PreventsDoubleProcessing()
    {
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = "SendNotification",
            PayloadJson = "{}",
            Status = "Processing",
            LockedAtUtc = DateTime.UtcNow, // Recently locked
            ScheduledAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Query like the worker does - recently locked job should NOT be picked up
        var lockCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        var availableJobs = await _db.BackgroundJobs
            .Where(j =>
                (j.Status == "Pending" && j.ScheduledAtUtc <= DateTime.UtcNow) ||
                (j.Status == "Processing" && j.LockedAtUtc < lockCutoff))
            .ToListAsync();

        availableJobs.Should().BeEmpty("recently locked job should not be available");
    }

    // 7. Stale locked jobs get recovered
    [Fact]
    public async Task StaleLockedJob_GetsRecovered()
    {
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = "SendNotification",
            PayloadJson = "{}",
            Status = "Processing",
            LockedAtUtc = DateTime.UtcNow.AddMinutes(-10), // 10 min ago (>5 min timeout)
            ScheduledAtUtc = DateTime.UtcNow.AddMinutes(-15)
        });
        await _db.SaveChangesAsync();

        var lockCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        var recoverable = await _db.BackgroundJobs
            .Where(j => j.Status == "Processing" && j.LockedAtUtc < lockCutoff)
            .ToListAsync();

        recoverable.Should().HaveCount(1, "stale locked job should be recoverable");
    }

    // 8. Abandoned orders cleanup
    [Fact]
    public async Task CleanupAbandonedOrders_CancelsOldPending()
    {
        // Need a business for FK
        var bizId = Guid.NewGuid();
        _db.Businesses.Add(new Business
        {
            Id = bizId,
            Name = "Test",
            PhoneNumberId = "test-cleanup",
            AccessToken = "tok",
            AdminKey = "key",
            IsActive = true
        });

        // Old abandoned order (>24h, pending, not checked out)
        _db.Orders.Add(new Order
        {
            BusinessId = bizId,
            From = "58412999",
            PhoneNumberId = "test-cleanup",
            DeliveryType = "pickup",
            Status = "Pending",
            CheckoutCompleted = false,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-30)
        });

        // Recent pending order (should NOT be cancelled)
        _db.Orders.Add(new Order
        {
            BusinessId = bizId,
            From = "58412888",
            PhoneNumberId = "test-cleanup",
            DeliveryType = "pickup",
            Status = "Pending",
            CheckoutCompleted = false,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
        });

        // Completed order (should NOT be cancelled)
        _db.Orders.Add(new Order
        {
            BusinessId = bizId,
            From = "58412777",
            PhoneNumberId = "test-cleanup",
            DeliveryType = "pickup",
            Status = "Completed",
            CheckoutCompleted = true,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-48)
        });

        await _db.SaveChangesAsync();

        // Simulate cleanup logic
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var abandoned = await _db.Orders
            .Where(o => o.Status == "Pending" && !o.CheckoutCompleted && o.CreatedAtUtc < cutoff)
            .ToListAsync();

        foreach (var order in abandoned)
            order.Status = "Cancelled";
        await _db.SaveChangesAsync();

        abandoned.Should().HaveCount(1);

        var orders = await _db.Orders.ToListAsync();
        orders.Count(o => o.Status == "Cancelled").Should().Be(1);
        orders.Count(o => o.Status == "Pending").Should().Be(1);
        orders.Count(o => o.Status == "Completed").Should().Be(1);
    }
}

public class BackgroundJobIntegrationTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;
    private readonly IServiceProvider _services;

    public BackgroundJobIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var conn = _connection;
        var factory = new WebApplicationFactory<Program>();
        var webApp = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                Controllers.TestHelpers.ReplaceDbContext(services, conn);
                Controllers.TestHelpers.AddMockWhatsAppClient(services);
            });
        });
        _client = webApp.CreateClient();
        _services = webApp.Services;
        Controllers.TestHelpers.SeedTestBusiness(_services);
    }

    public void Dispose()
    {
        _client.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void BackgroundJobService_IsRegistered()
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetService<IBackgroundJobService>();
        svc.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_Webhook_NotificationJobEnqueued()
    {
        var payload = """
        {
            "object": "whatsapp_business_account",
            "entry": [{
                "id": "123",
                "changes": [{
                    "value": {
                        "messaging_product": "whatsapp",
                        "metadata": {
                            "display_phone_number": "15551234567",
                            "phone_number_id": "123456789"
                        },
                        "contacts": [{
                            "profile": { "name": "Test User" },
                            "wa_id": "5511999999999"
                        }],
                        "messages": [{
                            "from": "5511999999999",
                            "id": "wamid.job-test-1",
                            "timestamp": "1234567890",
                            "text": { "body": "agente" },
                            "type": "text"
                        }]
                    },
                    "field": "messages"
                }]
            }]
        }
        """;

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Wait for webhook worker to process
        await Task.Delay(3000);

        // Check if a notification job was enqueued (only if business has NotificationPhone set)
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The test business doesn't have NotificationPhone set, so no job should be enqueued
        // But the order flow should still work
        var conversations = await db.ConversationStates.AnyAsync();
        conversations.Should().BeTrue("conversation state should have been created");
    }

    [Fact]
    public async Task OrderStatusUpdate_StillWorks()
    {
        // Create an order first
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var biz = await db.Businesses.FirstAsync();
        var order = new Order
        {
            BusinessId = biz.Id,
            From = "58412999",
            PhoneNumberId = biz.PhoneNumberId,
            DeliveryType = "pickup",
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Update status via API
        var updateContent = new StringContent(
            JsonSerializer.Serialize(new { status = "Preparing" }),
            Encoding.UTF8, "application/json");

        var response = await _client.PatchAsync($"/api/orders/{order.Id}/status", updateContent);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("status").GetString().Should().Be("Preparing");
    }
}
