using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Tests.Controllers;

public class WebhookControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public WebhookControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Override WhatsApp client to avoid real HTTP calls
                var mockClient = new Mock<IWhatsAppClient>();
                mockClient
                    .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);

                services.AddScoped(_ => mockClient.Object);
            });
        }).CreateClient();
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
}
