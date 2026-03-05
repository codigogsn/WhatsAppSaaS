using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Controllers;

internal static class TestHelpers
{
    /// <summary>
    /// Replaces the production DbContext registration (pool or regular) with an in-memory SQLite
    /// backed by the given open connection. Also seeds a test Business row.
    /// </summary>
    internal static void ReplaceDbContext(IServiceCollection services, SqliteConnection connection)
    {
        // Remove the production DbContext registration
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                     || d.ServiceType == typeof(AppDbContext))
            .ToList();
        foreach (var d in toRemove)
            services.Remove(d);

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connection));
    }

    /// <summary>
    /// Seeds test data after the host is built and migrations have run.
    /// Must be called after CreateClient() / accessing Services.
    /// </summary>
    internal static void SeedTestBusiness(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.Businesses.Any(b => b.PhoneNumberId == "123456789"))
        {
            db.Businesses.Add(new Business
            {
                Name = "Test Business",
                PhoneNumberId = "123456789",
                AccessToken = "test-token",
                AdminKey = "test-admin-key",
                IsActive = true
            });
            db.SaveChanges();
        }
    }

    internal static void AddMockWhatsAppClient(IServiceCollection services)
    {
        var mockClient = new Mock<IWhatsAppClient>();
        mockClient
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        services.AddScoped(_ => mockClient.Object);
    }
}

public class WebhookControllerIntegrationTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;

    public WebhookControllerIntegrationTests()
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
        TestHelpers.SeedTestBusiness(webApp.Services);
    }

    public void Dispose()
    {
        _client.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Get_Webhook_WithValidToken_ReturnsChallenge()
    {
        var response = await _client.GetAsync(
            "/webhook?hub.mode=subscribe&hub.verify_token=dev-verify-token-123&hub.challenge=test_challenge_string");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("test_challenge_string");
    }

    [Fact]
    public async Task Get_Webhook_WithInvalidToken_Returns403()
    {
        var response = await _client.GetAsync(
            "/webhook?hub.mode=subscribe&hub.verify_token=wrong-token&hub.challenge=test");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_Webhook_WithWrongMode_Returns403()
    {
        var response = await _client.GetAsync(
            "/webhook?hub.mode=unsubscribe&hub.verify_token=dev-verify-token-123&hub.challenge=test");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_Webhook_WithValidPayload_Returns200()
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
                            "id": "wamid.test",
                            "timestamp": "1234567890",
                            "text": { "body": "hello" },
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
    }

    [Fact]
    public async Task Post_Webhook_WithInvalidJson_Returns400()
    {
        var content = new StringContent("not json", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_Health_Returns200()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_ApiWebhook_WithValidToken_ReturnsChallenge()
    {
        var response = await _client.GetAsync(
            "/api/webhook?hub.mode=subscribe&hub.verify_token=dev-verify-token-123&hub.challenge=api_route_test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("api_route_test");
    }

    [Fact]
    public async Task Post_ApiWebhook_WithValidPayload_Returns200()
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
                            "id": "wamid.api-route-test",
                            "timestamp": "1234567890",
                            "text": { "body": "hello" },
                            "type": "text"
                        }]
                    },
                    "field": "messages"
                }]
            }]
        }
        """;

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/webhook", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public class WebhookSignatureTests : IDisposable
{
    private const string TestAppSecret = "test-secret-key-12345";
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;

    public WebhookSignatureTests()
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

                services.PostConfigure<WhatsAppSaaS.Application.Common.WhatsAppOptions>(opts =>
                {
                    opts.RequireSignatureValidation = true;
                    opts.AppSecret = TestAppSecret;
                });
            });
        });
        _client = webApp.CreateClient();
        TestHelpers.SeedTestBusiness(webApp.Services);
    }

    public void Dispose()
    {
        _client.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Post_Webhook_WithoutSignature_Returns401()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[]}""";
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_Webhook_WithInvalidSignature_Returns401()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[]}""";
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook") { Content = content };
        request.Headers.Add("X-Hub-Signature-256", "sha256=invalidhex");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_Webhook_WithValidSignature_Returns200()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[]}""";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TestAppSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var sig = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook") { Content = content };
        request.Headers.Add("X-Hub-Signature-256", sig);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public class WebhookHardeningTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;

    public WebhookHardeningTests()
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
        TestHelpers.SeedTestBusiness(webApp.Services);
    }

    public void Dispose()
    {
        _client.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Post_Webhook_NullMessages_Returns200()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[{"id":"123","changes":[{"value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"123456789"},"messages":null},"field":"messages"}]}]}""";
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_Webhook_EmptyEntry_Returns200()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[]}""";
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_Webhook_MissingPhoneNumberId_Returns200()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[{"id":"123","changes":[{"value":{"messaging_product":"whatsapp","metadata":{},"messages":[{"from":"5511999999999","id":"wamid.test","text":{"body":"hi"},"type":"text"}]},"field":"messages"}]}]}""";
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_Webhook_LongMessage_Returns200()
    {
        var longText = new string('A', 5000);
        var payload = $$"""{"object":"whatsapp_business_account","entry":[{"id":"123","changes":[{"value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"123456789"},"contacts":[{"profile":{"name":"Test"},"wa_id":"5511999999999"}],"messages":[{"from":"5511999999999","id":"wamid.long","text":{"body":"{{longText}}"},"type":"text","timestamp":"1234567890"}]},"field":"messages"}]}]}""";
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"object\":null}")]
    public async Task Post_Webhook_MalformedPayloads_DoesNotCrash(string payload)
    {
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/webhook", content);
        // Should return 200 or 400 but never 500
        ((int)response.StatusCode).Should().BeLessThan(500);
    }
}

public class WebhookIdempotencyTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;
    private readonly IServiceProvider _services;

    public WebhookIdempotencyTests()
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
    public async Task Post_Webhook_SameMessageTwice_OnlyOneProcessedMessageRow()
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
                            "id": "wamid.idempotent-test-1",
                            "timestamp": "1234567890",
                            "text": { "body": "hola" },
                            "type": "text"
                        }]
                    },
                    "field": "messages"
                }]
            }]
        }
        """;

        var content1 = new StringContent(payload, Encoding.UTF8, "application/json");
        var response1 = await _client.PostAsync("/webhook", content1);
        response1.StatusCode.Should().Be(HttpStatusCode.OK);

        var content2 = new StringContent(payload, Encoding.UTF8, "application/json");
        var response2 = await _client.PostAsync("/webhook", content2);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.ProcessedMessages
            .CountAsync(p => p.MessageId == "wamid.idempotent-test-1");

        count.Should().Be(1);
    }
}
