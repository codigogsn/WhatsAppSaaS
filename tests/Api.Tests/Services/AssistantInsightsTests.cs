using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;
using WhatsAppSaaS.Infrastructure.Services;

namespace WhatsAppSaaS.Api.Tests.Services;

public class AssistantInsightsTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;
    private readonly Guid _bizId = Guid.NewGuid();

    public AssistantInsightsTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _db = new AppDbContext(opts);
        _db.Database.EnsureCreated();

        _db.Businesses.Add(new Business
        {
            Id = _bizId, Name = "Test Restaurant", PhoneNumberId = "111",
            AccessToken = "tok", AdminKey = "key", IsActive = true
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // ── Business Insights: enough data → all state fields present ──

    [Fact]
    public async Task BusinessInsights_WithEnoughData_ProducesStateAndRecommendation()
    {
        SeedOrders(20);
        SeedCustomers(10);

        var svc = new BusinessInsightsService(_db, NullLogger<BusinessInsightsService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new WhatsAppSaaS.Application.Common.BusinessInsightThresholds()));
        var result = await svc.GetInsightsAsync(_bizId, 30, CancellationToken.None);

        result.BusinessStateTitle.Should().NotBeNullOrWhiteSpace();
        result.BusinessStateSummary.Should().NotBeNullOrWhiteSpace();
        result.MainOpportunity.Should().NotBeNullOrWhiteSpace();
        result.MainRisk.Should().NotBeNullOrWhiteSpace();
        result.PrimaryRecommendation.Title.Should().NotBeNullOrWhiteSpace();
        result.PrimaryRecommendation.Action.Should().NotBeNullOrWhiteSpace();
        result.Metrics.CompletedOrders.Should().Be(20);
    }

    // ── Business Insights: <5 orders → metrics present, insights empty ──

    [Fact]
    public async Task BusinessInsights_WithFewOrders_ReturnsMetricsButNoInsights()
    {
        SeedOrders(3);

        var svc = new BusinessInsightsService(_db, NullLogger<BusinessInsightsService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new WhatsAppSaaS.Application.Common.BusinessInsightThresholds()));
        var result = await svc.GetInsightsAsync(_bizId, 30, CancellationToken.None);

        result.Metrics.CompletedOrders.Should().Be(3);
        result.BusinessStateTitle.Should().Contain("inicial");
        result.Insights.Should().BeEmpty();
        result.Alerts.Should().BeEmpty();
        result.Recommendations.Should().BeEmpty();
    }

    // ── Founder Insights: portfolio response DTOs have state fields ──

    [Fact]
    public void FounderInsightsResponse_HasDecisionFields()
    {
        var response = new FounderInsightsResponse
        {
            PlatformStateTitle = "Portafolio estable",
            PlatformStateSummary = "3 negocios activos",
            MainOpportunity = "Expandir negocio líder",
            MainRisk = "Baja conversión en negocio B",
            PrimaryRecommendation = new FounderActionableRecommendation
            {
                Title = "Atender alerta",
                Action = "Revisar pagos pendientes",
                Impact = "Evitar pérdida de ingresos"
            }
        };

        response.PlatformStateTitle.Should().NotBeNullOrWhiteSpace();
        response.PlatformStateSummary.Should().Contain("negocios");
        response.MainOpportunity.Should().NotBeNullOrWhiteSpace();
        response.MainRisk.Should().NotBeNullOrWhiteSpace();
        response.PrimaryRecommendation.Title.Should().Be("Atender alerta");
        response.PrimaryRecommendation.Impact.Should().NotBeNullOrWhiteSpace();
    }

    // ── TrimToMaxSentences works correctly ──

    [Theory]
    [InlineData("One. Two. Three. Four. Five.", 3, "One. Two. Three.")]
    [InlineData("Single sentence only", 4, "Single sentence only")]
    [InlineData("A. B.", 4, "A. B.")]
    [InlineData("", 3, "")]
    public void TrimToMaxSentences_RespectsLimit(string input, int max, string expected)
    {
        // Use reflection to test the private static method
        var method = typeof(InsightsChatService).GetMethod("TrimToMaxSentences",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();

        var result = (string)method!.Invoke(null, new object[] { input, max })!;
        result.Should().Be(expected);
    }

    // ── Low-context guard: short context returns fallback without OpenAI ──

    [Fact]
    public async Task BusinessInsights_EmptyBusiness_ProducesShortContext()
    {
        // No orders, no customers → context will be very thin
        var svc = new BusinessInsightsService(_db, NullLogger<BusinessInsightsService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new WhatsAppSaaS.Application.Common.BusinessInsightThresholds()));
        var result = await svc.GetInsightsAsync(_bizId, 30, CancellationToken.None);

        result.Metrics.CompletedOrders.Should().Be(0);
        // State should indicate early/initial
        result.BusinessStateTitle.Should().Contain("inicial");
    }

    // ── Helpers ──

    private void SeedOrders(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var order = new Order
            {
                BusinessId = _bizId,
                From = $"+5841400000{i:D2}",
                PhoneNumberId = "111",
                DeliveryType = "pickup",
                Status = "Completed",
                CheckoutCompleted = true,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-i),
                TotalAmount = 10m + i,
                Items = new List<OrderItem>
                {
                    new() { Name = "Hamburguesa", Quantity = 1, UnitPrice = 5m, LineTotal = 5m },
                    new() { Name = "Refresco", Quantity = 1, UnitPrice = 2m, LineTotal = 2m }
                }
            };
            _db.Orders.Add(order);
        }
        _db.SaveChanges();
    }

    private void SeedCustomers(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _db.Customers.Add(new Customer
            {
                BusinessId = _bizId,
                PhoneE164 = $"+5841400000{i:D2}",
                Name = $"Customer {i}",
                OrdersCount = i % 3 == 0 ? 3 : 1, // some repeats
                TotalSpent = 20m,
                FirstSeenAtUtc = DateTime.UtcNow.AddDays(-30)
            });
        }
        _db.SaveChanges();
    }
}
