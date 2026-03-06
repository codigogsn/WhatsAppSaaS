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
    }

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
}
