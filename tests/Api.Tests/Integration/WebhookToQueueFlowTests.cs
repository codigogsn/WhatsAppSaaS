using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Api.Tests.Controllers;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Integration;

public class WebhookToQueueFlowTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;
    private readonly IServiceProvider _services;

    public WebhookToQueueFlowTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var conn = _connection;
        var factory = new WebApplicationFactory<Program>();
        var webApp = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                TestHelpers.ReplaceDbContext(services, conn);
                TestHelpers.AddMockWhatsAppClient(services);
            });
        });
        _client = webApp.CreateClient();
        _services = webApp.Services;
        TestHelpers.SeedTestBusiness(_services);
    }

    public void Dispose()
    {
        _client.Dispose();
        _connection.Dispose();
    }

    private static StringContent JsonPayload(string messageId, string text) =>
        new($$"""
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
                            "id": "{{messageId}}",
                            "timestamp": "1234567890",
                            "text": { "body": "{{text}}" },
                            "type": "text"
                        }]
                    },
                    "field": "messages"
                }]
            }]
        }
        """, Encoding.UTF8, "application/json");

    /// <summary>Builds a batched webhook with multiple messages in one delivery.</summary>
    private static StringContent BatchedJsonPayload(params (string id, string from, string text)[] messages)
    {
        var msgArray = string.Join(",\n", messages.Select(m => $$"""
            {
                "from": "{{m.from}}",
                "id": "{{m.id}}",
                "timestamp": "1234567890",
                "text": { "body": "{{m.text}}" },
                "type": "text"
            }
        """));

        return new($$"""
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
                        "messages": [{{msgArray}}]
                    },
                    "field": "messages"
                }]
            }]
        }
        """, Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Webhook_Enqueues_And_Worker_Processes_Full_Flow()
    {
        // Step 1: POST webhook payload
        var response = await _client.PostAsync("/webhook", JsonPayload("wamid.flow-test-1", "hola"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 2: Verify message was enqueued (InMemoryMessageQueue in test)
        var queue = _services.GetRequiredService<IMessageQueue>();
        var message = await queue.DequeueAsync(CancellationToken.None);
        message.Should().NotBeNull("webhook should have enqueued the message");
        message!.BusinessContext.PhoneNumberId.Should().Be("123456789");

        // Each queue item now contains exactly ONE message
        message.Payload.Entry.Should().HaveCount(1);
        message.Payload.Entry[0].Changes.Should().HaveCount(1);
        message.Payload.Entry[0].Changes[0].Value!.Messages.Should().HaveCount(1);

        // Step 3: Process the message manually (same as worker would)
        using var scope = _services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookProcessor>();
        await processor.ProcessAsync(message.Payload, message.BusinessContext, CancellationToken.None);

        // Step 4: Verify message was marked as processed in DB
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var processed = await db.ProcessedMessages
            .AnyAsync(p => p.MessageId == "wamid.flow-test-1");
        processed.Should().BeTrue("processor should mark the message as processed");

        // Step 5: Verify conversation state was created
        var convState = await db.ConversationStates
            .AnyAsync(c => c.ConversationId == "5511999999999:123456789");
        convState.Should().BeTrue("processor should create conversation state");
    }

    [Fact]
    public async Task Duplicate_Webhook_Is_Not_Processed_Twice()
    {
        // Send same message twice
        var response1 = await _client.PostAsync("/webhook", JsonPayload("wamid.dedup-test-1", "hola"));
        var response2 = await _client.PostAsync("/webhook", JsonPayload("wamid.dedup-test-1", "hola"));
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        // Dequeue and process both
        var queue = _services.GetRequiredService<IMessageQueue>();

        var msg1 = await queue.DequeueAsync(CancellationToken.None);
        msg1.Should().NotBeNull();

        using (var scope1 = _services.CreateScope())
        {
            var processor1 = scope1.ServiceProvider.GetRequiredService<IWebhookProcessor>();
            await processor1.ProcessAsync(msg1!.Payload, msg1.BusinessContext, CancellationToken.None);
        }

        var msg2 = await queue.DequeueAsync(CancellationToken.None);
        msg2.Should().NotBeNull();

        using (var scope2 = _services.CreateScope())
        {
            var processor2 = scope2.ServiceProvider.GetRequiredService<IWebhookProcessor>();
            // Second processing should be silently skipped by atomic dedup claim
            await processor2.ProcessAsync(msg2!.Payload, msg2.BusinessContext, CancellationToken.None);
        }

        // Only one ProcessedMessage row should exist
        using var verifyScope = _services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.ProcessedMessages
            .CountAsync(p => p.MessageId == "wamid.dedup-test-1");
        count.Should().Be(1, "duplicate message should not create a second ProcessedMessage row");
    }

    [Fact]
    public async Task Queue_Is_Empty_After_Processing()
    {
        var response = await _client.PostAsync("/webhook", JsonPayload("wamid.empty-test-1", "hola"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var queue = _services.GetRequiredService<IMessageQueue>();

        // Dequeue the message
        var msg = await queue.DequeueAsync(CancellationToken.None);
        msg.Should().NotBeNull();

        // Process it
        using var scope = _services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookProcessor>();
        await processor.ProcessAsync(msg!.Payload, msg.BusinessContext, CancellationToken.None);

        // Queue should now be empty (InMemoryMessageQueue blocks, so use timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Func<Task> act = () => queue.DequeueAsync(cts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>(
            "queue should be empty after processing the only message");
    }

    [Fact]
    public async Task Batched_Webhook_Creates_One_Queue_Item_Per_Message()
    {
        // Send a batched webhook with 3 messages from 2 different senders
        var payload = BatchedJsonPayload(
            ("wamid.batch-1", "5511111111111", "hello"),
            ("wamid.batch-2", "5511111111111", "world"),
            ("wamid.batch-3", "5522222222222", "hola"));

        var response = await _client.PostAsync("/webhook", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var queue = _services.GetRequiredService<IMessageQueue>();

        // Should have 3 queue items, each with exactly 1 message
        var items = new List<WhatsAppSaaS.Application.Interfaces.QueuedMessage>();
        for (int i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var item = await queue.DequeueAsync(cts.Token);
            item.Should().NotBeNull($"expected 3 queue items, got {i}");
            items.Add(item!);
        }

        // Each item should contain exactly 1 message
        foreach (var item in items)
        {
            var messages = item.Payload.Entry
                .SelectMany(e => e.Changes)
                .SelectMany(c => c.Value?.Messages ?? [])
                .ToList();
            messages.Should().HaveCount(1, "each queue item should contain exactly one message");
        }

        // All 3 message IDs should be represented
        var allMsgIds = items
            .SelectMany(i => i.Payload.Entry.SelectMany(e => e.Changes).SelectMany(c => c.Value?.Messages ?? []))
            .Select(m => m.Id)
            .OrderBy(id => id)
            .ToList();
        allMsgIds.Should().BeEquivalentTo(["wamid.batch-1", "wamid.batch-2", "wamid.batch-3"]);
    }

    [Fact]
    public async Task Concurrent_Duplicate_Processing_Only_Executes_Once()
    {
        // Post a single message
        var response = await _client.PostAsync("/webhook", JsonPayload("wamid.concurrent-1", "hola"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var queue = _services.GetRequiredService<IMessageQueue>();
        var msg = await queue.DequeueAsync(CancellationToken.None);
        msg.Should().NotBeNull();

        // Simulate two concurrent ProcessAsync calls for the same message
        // (as would happen if Meta re-delivers or queue re-claims)
        using var scope1 = _services.CreateScope();
        using var scope2 = _services.CreateScope();
        var processor1 = scope1.ServiceProvider.GetRequiredService<IWebhookProcessor>();
        var processor2 = scope2.ServiceProvider.GetRequiredService<IWebhookProcessor>();

        // Run both concurrently
        var task1 = processor1.ProcessAsync(msg!.Payload, msg.BusinessContext, CancellationToken.None);
        var task2 = processor2.ProcessAsync(msg.Payload, msg.BusinessContext, CancellationToken.None);
        await Task.WhenAll(task1, task2);

        // Only one ProcessedMessage row should exist — atomic claim prevents double execution
        using var verifyScope = _services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.ProcessedMessages
            .CountAsync(p => p.MessageId == "wamid.concurrent-1");
        count.Should().Be(1, "atomic claim should prevent duplicate processing");
    }
}
