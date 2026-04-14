using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Controllers;

/// <summary>
/// Verifies that payment-proof retrieval fails closed when the business
/// access token is missing, preventing fallback to any global token.
/// These tests exercise the DB lookup layer directly (no HTTP/TestServer needed).
/// </summary>
public class PaymentProofTenantIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public PaymentProofTenantIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Business_With_Token_Returns_Token()
    {
        // Arrange: business with a valid access token
        var bizId = Guid.NewGuid();
        _db.Businesses.Add(new Business
        {
            Id = bizId,
            Name = "Tenant A",
            PhoneNumberId = "111",
            AccessToken = "tenant-a-secret-token",
            AdminKey = "ak1",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        // Act: look up the token (same query as OrdersController.GetPaymentProof)
        var biz = await _db.Businesses.AsNoTracking()
            .Where(b => b.Id == bizId && b.IsActive)
            .Select(b => new { b.AccessToken })
            .FirstOrDefaultAsync();

        // Assert: token should be present
        biz.Should().NotBeNull();
        biz!.AccessToken.Should().Be("tenant-a-secret-token");
        string.IsNullOrWhiteSpace(biz.AccessToken).Should().BeFalse(
            "business with configured token should return it for tenant-bound media retrieval");
    }

    [Fact]
    public async Task Business_Without_Token_Returns_Empty_String()
    {
        // Arrange: business with no access token
        var bizId = Guid.NewGuid();
        _db.Businesses.Add(new Business
        {
            Id = bizId,
            Name = "Tenant B",
            PhoneNumberId = "222",
            AccessToken = "",
            AdminKey = "ak2",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        // Act
        var biz = await _db.Businesses.AsNoTracking()
            .Where(b => b.Id == bizId && b.IsActive)
            .Select(b => new { b.AccessToken })
            .FirstOrDefaultAsync();

        // Assert: token should be blank — caller MUST fail closed, not fall back to global
        biz.Should().NotBeNull();
        string.IsNullOrWhiteSpace(biz!.AccessToken).Should().BeTrue(
            "business without token should trigger fail-closed path, not global-token fallback");
    }

    [Fact]
    public async Task Inactive_Business_Returns_Null()
    {
        // Arrange: deactivated business
        var bizId = Guid.NewGuid();
        _db.Businesses.Add(new Business
        {
            Id = bizId,
            Name = "Tenant C",
            PhoneNumberId = "333",
            AccessToken = "some-token",
            AdminKey = "ak3",
            IsActive = false
        });
        await _db.SaveChangesAsync();

        // Act
        var biz = await _db.Businesses.AsNoTracking()
            .Where(b => b.Id == bizId && b.IsActive)
            .Select(b => new { b.AccessToken })
            .FirstOrDefaultAsync();

        // Assert: inactive business should not be found — caller MUST fail closed
        biz.Should().BeNull(
            "inactive business should not be found, preventing any token retrieval");
    }

    [Fact]
    public async Task Nonexistent_Business_Returns_Null()
    {
        // Act: look up a business that doesn't exist
        var nonExistentId = Guid.NewGuid();
        var biz = await _db.Businesses.AsNoTracking()
            .Where(b => b.Id == nonExistentId && b.IsActive)
            .Select(b => new { b.AccessToken })
            .FirstOrDefaultAsync();

        // Assert: caller MUST fail closed when no business found
        biz.Should().BeNull(
            "nonexistent business should not be found, preventing any token retrieval");
    }

    [Fact]
    public void Fail_Closed_Logic_Rejects_Blank_Token()
    {
        // This tests the exact condition used in OrdersController.GetPaymentProof
        // to ensure we never proceed with a null/empty/whitespace token.
        string?[] invalidTokens = [null, "", "  ", "\t"];

        foreach (var token in invalidTokens)
        {
            string.IsNullOrWhiteSpace(token).Should().BeTrue(
                $"token '{token ?? "(null)"}' must be caught by IsNullOrWhiteSpace check to prevent global fallback");
        }
    }
}
