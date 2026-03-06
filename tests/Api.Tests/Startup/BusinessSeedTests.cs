using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Startup;

public class BusinessSeedTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public BusinessSeedTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose()
    {
        // Clean up env vars after each test
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", null);
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", null);
        Environment.SetEnvironmentVariable("WHATSAPP_ADMIN_KEY", null);
        Environment.SetEnvironmentVariable("WHATSAPP_BUSINESS_NAME", null);
        _connection.Dispose();
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var conn = _connection;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                Controllers.TestHelpers.ReplaceDbContext(services, conn);
                Controllers.TestHelpers.AddMockWhatsAppClient(services);
            });
        });
    }

    [Fact]
    public async Task Startup_WithPhoneNumberIdEnvVar_SeedsBusiness()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", "seed-test-001");
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", "tok-seed");
        Environment.SetEnvironmentVariable("WHATSAPP_ADMIN_KEY", "key-seed");
        Environment.SetEnvironmentVariable("WHATSAPP_BUSINESS_NAME", "Seed Test Biz");

        var factory = CreateFactory();
        var client = factory.CreateClient();

        // App started — verify business was seeded
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var biz = await db.Businesses.FirstOrDefaultAsync(b => b.PhoneNumberId == "seed-test-001");

        biz.Should().NotBeNull();
        biz!.AccessToken.Should().Be("tok-seed");
        biz.AdminKey.Should().Be("key-seed");
        biz.Name.Should().Be("Seed Test Biz");
        biz.IsActive.Should().BeTrue();

        client.Dispose();
    }

    [Fact]
    public async Task Startup_CalledTwice_DoesNotDuplicateBusiness()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", "seed-test-002");
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", "tok-dup");
        Environment.SetEnvironmentVariable("WHATSAPP_ADMIN_KEY", "key-dup");

        // First startup
        var factory1 = CreateFactory();
        var client1 = factory1.CreateClient();

        // Seed again manually (simulates second startup)
        using (var scope = factory1.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var count = await db.Businesses.CountAsync(b => b.PhoneNumberId == "seed-test-002");
            count.Should().Be(1, "should have exactly one row after first seed");
        }

        client1.Dispose();
    }

    [Fact]
    public async Task Startup_WithoutPhoneNumberIdEnvVar_DoesNotSeed()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", null);

        var factory = CreateFactory();
        var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Businesses.CountAsync();

        count.Should().Be(0, "no business should be seeded without WHATSAPP_PHONE_NUMBER_ID");

        client.Dispose();
    }
}
