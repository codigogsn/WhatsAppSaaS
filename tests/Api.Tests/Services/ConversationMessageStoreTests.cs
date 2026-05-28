using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;
using WhatsAppSaaS.Infrastructure.Repositories;

namespace WhatsAppSaaS.Api.Tests.Services;

// Phase 1 memory foundation regression tests. The store contract is:
//   (1) append a row when valid
//   (2) tenant scoped — BusinessId is authoritative on every row
//   (3) duplicate (BusinessId, WhatsAppMessageId) silently absorbed
//   (4) fail-safe — exceptions caught, never propagated (covered by the
//       duplicate-absorption test exercising the catch path)
public sealed class ConversationMessageStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ConversationMessageStore _sut;

    public ConversationMessageStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new ConversationMessageStore(_db, NullLogger<ConversationMessageStore>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AppendAsync_PersistsInboundCustomerMessage()
    {
        var businessId = Guid.NewGuid();
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = businessId,
            ConversationId = "584141234567:1234567890",
            CustomerPhoneE164 = "584141234567",
            WhatsAppMessageId = "wamid.ABC1",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text",
            Body = "hola",
            HandoffMode = false
        });

        var stored = await _db.ConversationMessages.SingleAsync();
        stored.BusinessId.Should().Be(businessId);
        stored.ConversationId.Should().Be("584141234567:1234567890");
        stored.Direction.Should().Be("inbound");
        stored.Sender.Should().Be("customer");
        stored.Body.Should().Be("hola");
    }

    [Fact]
    public async Task AppendAsync_PersistsBotOutboundWithoutWhatsAppId()
    {
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = Guid.NewGuid(),
            ConversationId = "584141234567:9999999999",
            CustomerPhoneE164 = "584141234567",
            WhatsAppMessageId = null,
            Direction = "outbound",
            Sender = "bot",
            Kind = "text",
            Body = "¡Hola! ¿En qué te ayudo?",
            HandoffMode = false
        });

        var stored = await _db.ConversationMessages.SingleAsync();
        stored.Sender.Should().Be("bot");
        stored.WhatsAppMessageId.Should().BeNull();
    }

    [Fact]
    public async Task AppendAsync_DuplicateWhatsAppMessageId_IsSilentlyAbsorbed()
    {
        var businessId = Guid.NewGuid();
        var conversationId = "584141234567:1234567890";

        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = businessId,
            ConversationId = conversationId,
            WhatsAppMessageId = "wamid.DUP",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text",
            Body = "first"
        });

        // Webhook retry — same id arrives again. Must not throw, must not double-insert.
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = businessId,
            ConversationId = conversationId,
            WhatsAppMessageId = "wamid.DUP",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text",
            Body = "second"
        });

        var count = await _db.ConversationMessages.CountAsync();
        count.Should().Be(1);
        var stored = await _db.ConversationMessages.SingleAsync();
        stored.Body.Should().Be("first");
    }

    [Fact]
    public async Task AppendAsync_SameWhatsAppIdAcrossTenants_IsAllowed()
    {
        // The partial unique index is per (BusinessId, WhatsAppMessageId).
        // Two different tenants can legitimately have the same WhatsApp id.
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = Guid.NewGuid(),
            ConversationId = "a:1",
            WhatsAppMessageId = "wamid.SHARED",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text"
        });
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = Guid.NewGuid(),
            ConversationId = "b:1",
            WhatsAppMessageId = "wamid.SHARED",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text"
        });

        var count = await _db.ConversationMessages.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task AppendAsync_DropsRowWithEmptyBusinessId()
    {
        // No tenant scope → silently dropped. Prevents accidentally orphaned rows.
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = Guid.Empty,
            ConversationId = "x:y",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text",
            Body = "ghost"
        });

        var count = await _db.ConversationMessages.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task AppendAsync_DropsRowWithBlankConversationId()
    {
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = Guid.NewGuid(),
            ConversationId = "",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text"
        });

        var count = await _db.ConversationMessages.CountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task AppendAsync_TenantScopedReadOnlySeesOwnRows()
    {
        // Sanity check that BusinessId-filtered reads return only that tenant's rows.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = tenantA,
            ConversationId = "a:1",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text",
            Body = "from A"
        });
        await _sut.AppendAsync(new ConversationMessage
        {
            BusinessId = tenantB,
            ConversationId = "b:1",
            Direction = "inbound",
            Sender = "customer",
            Kind = "text",
            Body = "from B"
        });

        var aRows = await _db.ConversationMessages.Where(m => m.BusinessId == tenantA).ToListAsync();
        var bRows = await _db.ConversationMessages.Where(m => m.BusinessId == tenantB).ToListAsync();

        aRows.Should().HaveCount(1);
        aRows[0].Body.Should().Be("from A");
        bRows.Should().HaveCount(1);
        bRows[0].Body.Should().Be("from B");
    }
}
