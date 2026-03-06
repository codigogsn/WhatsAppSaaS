using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Startup;

/// <summary>
/// Verifies that auto-migration on startup is safe and non-fatal.
/// The SQLite path is exercised by every integration test via WebApplicationFactory.
/// These tests specifically validate migration safety behaviors.
/// </summary>
public class MigrationSafetyTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MigrationSafetyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void SQLite_Provider_IsDetectedCorrectly()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();

        var isNpgsql = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        isNpgsql.Should().BeFalse("SQLite should NOT trigger Postgres advisory lock path");
    }

    [Fact]
    public void SQLite_MigrateCalledTwice_DoesNotThrow()
    {
        // Simulates the scenario where two processes try to migrate —
        // on SQLite (tests/dev), calling Migrate() twice should be safe.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AppDbContext(options);
        db.Database.Migrate();

        // Second call should be a no-op, not throw
        var act = () => db.Database.Migrate();
        act.Should().NotThrow("calling Migrate() again should be idempotent");
    }

    [Fact]
    public async Task WebApplicationFactory_StartsSuccessfully_WithMigrations()
    {
        // This exercises the full Program.cs startup path including migration
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

        var client = webApp.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        client.Dispose();
    }
}
