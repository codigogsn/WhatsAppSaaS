using Api.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Services;

public class BusinessResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly BusinessResolver _sut;

    public BusinessResolverTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.Migrate();

        _sut = new BusinessResolver(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        // Clean env vars
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", null);
        Environment.SetEnvironmentVariable("WHATSAPP_ADMIN_KEY", null);
        Environment.SetEnvironmentVariable("WHATSAPP_BUSINESS_NAME", null);
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", null);
        Environment.SetEnvironmentVariable("WhatsApp__AdminKey", null);
        Environment.SetEnvironmentVariable("WhatsApp__BusinessName", null);
        Environment.SetEnvironmentVariable("ADMIN_KEY", null);
    }

    // ── ResolveByPhoneNumberIdAsync ──

    [Fact]
    public async Task ResolveByPhoneNumberIdAsync_ActiveBusiness_ReturnsContext()
    {
        _db.Businesses.Add(new Business
        {
            Name = "Test Biz",
            PhoneNumberId = "111222333",
            AccessToken = "tok-abc",
            AdminKey = "key-1",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveByPhoneNumberIdAsync("111222333");

        result.Should().NotBeNull();
        result!.PhoneNumberId.Should().Be("111222333");
        result.AccessToken.Should().Be("tok-abc");
    }

    [Fact]
    public async Task ResolveByPhoneNumberIdAsync_InactiveBusiness_ReturnsNull()
    {
        _db.Businesses.Add(new Business
        {
            Name = "Inactive Biz",
            PhoneNumberId = "444555666",
            AccessToken = "tok-xyz",
            AdminKey = "key-2",
            IsActive = false
        });
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveByPhoneNumberIdAsync("444555666");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByPhoneNumberIdAsync_UnknownPhoneNumberId_ReturnsNull()
    {
        var result = await _sut.ResolveByPhoneNumberIdAsync("999999999");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByPhoneNumberIdAsync_NullOrEmpty_ReturnsNull()
    {
        (await _sut.ResolveByPhoneNumberIdAsync(null)).Should().BeNull();
        (await _sut.ResolveByPhoneNumberIdAsync("")).Should().BeNull();
        (await _sut.ResolveByPhoneNumberIdAsync("   ")).Should().BeNull();
    }

    [Fact]
    public async Task ResolveByPhoneNumberIdAsync_TrimsWhitespace()
    {
        _db.Businesses.Add(new Business
        {
            Name = "Trim Test",
            PhoneNumberId = "777888999",
            AccessToken = "tok-trim",
            AdminKey = "key-3",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveByPhoneNumberIdAsync("  777888999  ");
        result.Should().NotBeNull();
        result!.PhoneNumberId.Should().Be("777888999");
    }

    // ── ResolveOrCreateAsync ──

    [Fact]
    public async Task ResolveOrCreateAsync_ExistingBusiness_ReturnsIt()
    {
        _db.Businesses.Add(new Business
        {
            Name = "Existing",
            PhoneNumberId = "existing-001",
            AccessToken = "tok-exist",
            AdminKey = "key-exist",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveOrCreateAsync("existing-001");

        result.Should().NotBeNull();
        result!.PhoneNumberId.Should().Be("existing-001");
        result.AccessToken.Should().Be("tok-exist");

        // Should not have created a duplicate
        var count = await _db.Businesses.CountAsync(b => b.PhoneNumberId == "existing-001");
        count.Should().Be(1);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_MissingBusiness_CreatesIt()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", "auto-tok");
        Environment.SetEnvironmentVariable("WHATSAPP_ADMIN_KEY", "auto-key");
        Environment.SetEnvironmentVariable("WHATSAPP_BUSINESS_NAME", "Auto Biz");

        var result = await _sut.ResolveOrCreateAsync("new-phone-001");

        result.Should().NotBeNull();
        result!.PhoneNumberId.Should().Be("new-phone-001");
        result.AccessToken.Should().Be("auto-tok");

        // Verify row in DB
        var biz = await _db.Businesses.FirstOrDefaultAsync(b => b.PhoneNumberId == "new-phone-001");
        biz.Should().NotBeNull();
        biz!.Name.Should().Be("Auto Biz");
        biz.AdminKey.Should().Be("auto-key");
        biz.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_CalledTwice_DoesNotDuplicate()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", "tok-nodup");

        var result1 = await _sut.ResolveOrCreateAsync("nodup-001");
        var result2 = await _sut.ResolveOrCreateAsync("nodup-001");

        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1!.BusinessId.Should().Be(result2!.BusinessId);

        var count = await _db.Businesses.CountAsync(b => b.PhoneNumberId == "nodup-001");
        count.Should().Be(1);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_NullInput_ReturnsNull()
    {
        (await _sut.ResolveOrCreateAsync(null)).Should().BeNull();
        (await _sut.ResolveOrCreateAsync("")).Should().BeNull();
    }

    // ── EnvResolve ──

    [Fact]
    public void EnvResolve_FirstKeyWins()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", "primary");
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", "fallback");

        BusinessResolver.EnvResolve("WHATSAPP_ACCESS_TOKEN", "WhatsApp__AccessToken")
            .Should().Be("primary");
    }

    [Fact]
    public void EnvResolve_FallsBackToSecondKey()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", null);
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", "fallback-val");

        BusinessResolver.EnvResolve("WHATSAPP_ACCESS_TOKEN", "WhatsApp__AccessToken")
            .Should().Be("fallback-val");
    }

    [Fact]
    public void EnvResolve_AdminKey_SupportsThreeKeys()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_ADMIN_KEY", null);
        Environment.SetEnvironmentVariable("ADMIN_KEY", "admin-fallback");
        Environment.SetEnvironmentVariable("WhatsApp__AdminKey", "third");

        BusinessResolver.EnvResolve("WHATSAPP_ADMIN_KEY", "ADMIN_KEY", "WhatsApp__AdminKey")
            .Should().Be("admin-fallback");
    }

    [Fact]
    public void EnvResolve_AllMissing_ReturnsNull()
    {
        BusinessResolver.EnvResolve("NONEXISTENT_VAR_1", "NONEXISTENT_VAR_2")
            .Should().BeNull();
    }

    [Fact]
    public void EnvResolve_BusinessName_FallbackChain()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_BUSINESS_NAME", null);
        Environment.SetEnvironmentVariable("WhatsApp__BusinessName", "Config Name");

        BusinessResolver.EnvResolve("WHATSAPP_BUSINESS_NAME", "WhatsApp__BusinessName")
            .Should().Be("Config Name");
    }
}
