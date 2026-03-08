using System.Net;
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

public class PaymentProofTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;
    private readonly WebApplicationFactory<Program> _webApp;
    private readonly Mock<IWhatsAppClient> _whatsAppMock;
    private const string AdminKey = "test-proof-admin-key";

    public PaymentProofTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _whatsAppMock = new Mock<IWhatsAppClient>();
        _whatsAppMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var conn = _connection;
        var mock = _whatsAppMock;
        var factory = new WebApplicationFactory<Program>();
        _webApp = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                TestHelpers.ReplaceDbContext(services, conn);
                services.AddScoped(_ => mock.Object);
            });
            builder.UseSetting("ADMIN_KEY", AdminKey);
        });
        _client = _webApp.CreateClient();

        // Ensure DB + seed
        using var scope = _webApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _client.Dispose();
        _connection.Dispose();
    }

    private HttpRequestMessage WithAdmin(HttpRequestMessage req)
    {
        req.Headers.Add("X-Admin-Key", AdminKey);
        return req;
    }

    private async Task<(Guid orderId, Guid bizId)> SeedOrderWithProof(string mediaId = "wamid.proof123")
    {
        using var scope = _webApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var biz = new Business
        {
            Name = "Proof Test Biz",
            PhoneNumberId = "proof-phone-001",
            AccessToken = "biz-token-proof",
            AdminKey = AdminKey,
            IsActive = true
        };
        db.Businesses.Add(biz);

        var order = new Order
        {
            BusinessId = biz.Id,
            From = "5511999999999",
            PhoneNumberId = "proof-phone-001",
            DeliveryType = "delivery",
            Status = "Pending",
            PaymentProofMediaId = mediaId,
            PaymentProofSubmittedAtUtc = DateTime.UtcNow,
            CustomerName = "Test Customer"
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return (order.Id, biz.Id);
    }

    private async Task<Guid> SeedOrderOtherTenant()
    {
        using var scope = _webApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var biz = new Business
        {
            Name = "Other Tenant Biz",
            PhoneNumberId = "other-phone-001",
            AccessToken = "other-token",
            AdminKey = "other-admin-key",
            IsActive = true
        };
        db.Businesses.Add(biz);

        var order = new Order
        {
            BusinessId = biz.Id,
            From = "5500000000000",
            PhoneNumberId = "other-phone-001",
            DeliveryType = "pickup",
            Status = "Pending",
            PaymentProofMediaId = "wamid.other-proof",
            PaymentProofSubmittedAtUtc = DateTime.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    // ── Auth Tests ──

    [Fact]
    public async Task GetPaymentProof_WithoutAdminKey_Returns401()
    {
        var (orderId, _) = await SeedOrderWithProof();

        var res = await _client.GetAsync($"/api/orders/{orderId}/payment-proof");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPaymentProof_WithWrongAdminKey_Returns401()
    {
        var (orderId, _) = await SeedOrderWithProof();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}/payment-proof");
        req.Headers.Add("X-Admin-Key", "wrong-key");
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPaymentProof_WithAdminKey_ValidOrder_CallsWhatsAppMedia()
    {
        var (orderId, _) = await SeedOrderWithProof("wamid.test-media-id");

        // Setup mock to return test image
        _whatsAppMock
            .Setup(x => x.GetMediaAsync("wamid.test-media-id", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownloadResult(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg"));

        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}/payment-proof"));
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        var data = await res.Content.ReadAsByteArrayAsync();
        data.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetPaymentProof_OrderNotFound_Returns404()
    {
        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{Guid.NewGuid()}/payment-proof"));
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPaymentProof_OrderWithoutProof_Returns404()
    {
        // Seed order without proof
        using var scope = _webApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = new Order
        {
            From = "5511000000000",
            PhoneNumberId = "proof-phone-001",
            DeliveryType = "pickup",
            Status = "Pending"
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{order.Id}/payment-proof"));
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPaymentProof_WhatsAppFails_Returns502()
    {
        var (orderId, _) = await SeedOrderWithProof("wamid.fail-media");

        _whatsAppMock
            .Setup(x => x.GetMediaAsync("wamid.fail-media", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaDownloadResult?)null);

        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}/payment-proof"));
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be((HttpStatusCode)502);
    }

    [Fact]
    public async Task GetPaymentProof_PassesBusinessTokenToWhatsApp()
    {
        var (orderId, bizId) = await SeedOrderWithProof("wamid.token-check");
        string? capturedToken = null;

        _whatsAppMock
            .Setup(x => x.GetMediaAsync("wamid.token-check", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>((_, token, _) => capturedToken = token)
            .ReturnsAsync(new MediaDownloadResult(new byte[] { 0x89, 0x50 }, "image/png"));

        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}/payment-proof"));
        await _client.SendAsync(req);

        capturedToken.Should().Be("biz-token-proof");
    }

    [Fact]
    public async Task GetPaymentProof_PdfContentType_ReturnsPdf()
    {
        var (orderId, _) = await SeedOrderWithProof("wamid.pdf-proof");

        _whatsAppMock
            .Setup(x => x.GetMediaAsync("wamid.pdf-proof", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownloadResult(new byte[] { 0x25, 0x50, 0x44, 0x46 }, "application/pdf"));

        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}/payment-proof"));
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task GetPaymentProof_UnsafeContentType_FallsBackToOctetStream()
    {
        var (orderId, _) = await SeedOrderWithProof("wamid.unsafe-media");

        _whatsAppMock
            .Setup(x => x.GetMediaAsync("wamid.unsafe-media", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaDownloadResult(new byte[] { 0x00 }, "text/html"));

        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}/payment-proof"));
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
    }

    // ── Projection Tests ──

    [Fact]
    public async Task OrdersList_IncludesPaymentProofFields()
    {
        var (orderId, bizId) = await SeedOrderWithProof();

        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Get, $"/api/orders?businessId={bizId}"));
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("paymentProofExists");
        body.Should().Contain("paymentVerificationStatus");
        body.Should().Contain("paymentProofMediaId");
    }
}
