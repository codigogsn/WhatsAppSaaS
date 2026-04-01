using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Messaging;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Controllers;

public class InMemoryMessageQueueTests
{
    [Fact]
    public async Task EnqueueDequeue_SingleMessage_RoundTrips()
    {
        var queue = new InMemoryMessageQueue();
        var ctx = new BusinessContext(Guid.NewGuid(), "123", "tok");
        var payload = new WebhookPayload { Object = "whatsapp_business_account" };
        var msg = new QueuedMessage(payload, ctx);

        await queue.EnqueueAsync(msg);
        var result = await queue.DequeueAsync();

        result.Should().NotBeNull();
        result!.BusinessContext.PhoneNumberId.Should().Be("123");
        result.Payload.Object.Should().Be("whatsapp_business_account");
    }

    [Fact]
    public async Task EnqueueDequeue_PreservesOrder()
    {
        var queue = new InMemoryMessageQueue();

        for (var i = 0; i < 5; i++)
        {
            var ctx = new BusinessContext(Guid.NewGuid(), $"phone-{i}", "tok");
            await queue.EnqueueAsync(new QueuedMessage(new WebhookPayload(), ctx));
        }

        for (var i = 0; i < 5; i++)
        {
            var result = await queue.DequeueAsync();
            result!.BusinessContext.PhoneNumberId.Should().Be($"phone-{i}");
        }
    }

    [Fact]
    public async Task Dequeue_BlocksUntilEnqueued()
    {
        var queue = new InMemoryMessageQueue();
        var dequeued = false;

        var dequeueTask = Task.Run(async () =>
        {
            await queue.DequeueAsync();
            dequeued = true;
        });

        await Task.Delay(100);
        dequeued.Should().BeFalse();

        await queue.EnqueueAsync(new QueuedMessage(new WebhookPayload(),
            new BusinessContext(Guid.NewGuid(), "x", "y")));

        await Task.WhenAny(dequeueTask, Task.Delay(2000));
        dequeued.Should().BeTrue();
    }

    [Fact]
    public async Task Dequeue_CancellationThrows()
    {
        var queue = new InMemoryMessageQueue();
        using var cts = new CancellationTokenSource(100);

        var act = () => queue.DequeueAsync(cts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConcurrentEnqueue_AllMessagesDequeued()
    {
        var queue = new InMemoryMessageQueue();
        const int count = 100;

        var enqueueTasks = Enumerable.Range(0, count).Select(i =>
            queue.EnqueueAsync(new QueuedMessage(new WebhookPayload(),
                new BusinessContext(Guid.NewGuid(), $"p-{i}", "t"))).AsTask());

        await Task.WhenAll(enqueueTasks);

        var results = new List<QueuedMessage>();
        for (var i = 0; i < count; i++)
            results.Add((await queue.DequeueAsync())!);

        results.Should().HaveCount(count);
        results.Select(r => r.BusinessContext.PhoneNumberId).Distinct().Should().HaveCount(count);
    }
}

public class WebhookQueueIntegrationTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;
    private readonly IServiceProvider _services;

    public WebhookQueueIntegrationTests()
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

    [Fact]
    public async Task Post_Webhook_EnqueuesAndReturnsImmediately()
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
                            "id": "wamid.queue-test-1",
                            "timestamp": "1234567890",
                            "text": { "body": "hello queue" },
                            "type": "text"
                        }]
                    },
                    "field": "messages"
                }]
            }]
        }
        """;

        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _client.PostAsync("/webhook", content);
        sw.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Enqueue should be near-instant (well under 1 second)
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public async Task Post_Webhook_MessageProcessedByWorker()
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
                            "id": "wamid.worker-test-1",
                            "timestamp": "1234567890",
                            "text": { "body": "hello worker" },
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

        // Wait for the background worker to process
        await Task.Delay(3000);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var processed = await db.ProcessedMessages
            .AnyAsync(p => p.MessageId == "wamid.worker-test-1");

        processed.Should().BeTrue("the background worker should have processed the message");
    }

    [Fact]
    public void MessageQueue_IsRegisteredAsSingleton()
    {
        var queue1 = _services.GetRequiredService<IMessageQueue>();
        var queue2 = _services.GetRequiredService<IMessageQueue>();

        queue1.Should().BeSameAs(queue2);
    }
}
