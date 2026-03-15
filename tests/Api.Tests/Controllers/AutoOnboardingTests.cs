using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Controllers;

public class AutoOnboardingTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SqliteConnection _connection;
    private readonly WebApplicationFactory<Program> _webApp;
    private const string AdminKey = "test-admin-key-onboarding";

    public AutoOnboardingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var conn = _connection;
        var factory = new WebApplicationFactory<Program>();
        _webApp = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                TestHelpers.ReplaceDbContext(services, conn);
                TestHelpers.AddMockWhatsAppClient(services);
            });
            builder.UseSetting("ADMIN_KEY", AdminKey);
        });
        _client = _webApp.CreateClient();

        // Ensure DB is created
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

    // 1. Business creation with RestaurantType seeds menu
    [Fact]
    public async Task Create_WithRestaurantType_SeedsMenu()
    {
        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Post, "/api/admin/businesses")
        {
            Content = JsonContent.Create(new
            {
                name = "Burger Joint",
                phoneNumberId = "111222333",
                restaurantType = "burger"
            })
        });

        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = body.RootElement;

        root.GetProperty("menuSeeded").GetBoolean().Should().BeTrue();
        root.GetProperty("templateName").GetString().Should().Be("Hamburguesas");

        var cats = root.GetProperty("defaultCategories");
        cats.GetArrayLength().Should().Be(6);
        var catNames = Enumerable.Range(0, cats.GetArrayLength())
            .Select(i => cats[i].GetString()).ToList();
        catNames.Should().Contain("Hamburguesas");
        catNames.Should().Contain("Perros Calientes");
        catNames.Should().Contain("Papas");
        catNames.Should().Contain("Bebidas");
        catNames.Should().Contain("Combos");
        catNames.Should().Contain("Salsas");

        // Verify items actually in DB
        using var scope = _webApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bizId = Guid.Parse(root.GetProperty("id").GetString()!);
        var dbCats = await db.MenuCategories.Where(c => c.BusinessId == bizId).ToListAsync();
        dbCats.Should().HaveCount(6);
        var dbItems = await db.MenuItems.Where(i => i.Category!.BusinessId == bizId).ToListAsync();
        dbItems.Count.Should().BeGreaterThan(25); // burger template has 31 items
    }

    // 2. Business creation without RestaurantType seeds generic categories
    [Fact]
    public async Task Create_WithoutRestaurantType_SeedsGenericCategories()
    {
        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Post, "/api/admin/businesses")
        {
            Content = JsonContent.Create(new
            {
                name = "Generic Shop",
                phoneNumberId = "444555666"
            })
        });

        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = body.RootElement;

        root.GetProperty("menuSeeded").GetBoolean().Should().BeTrue();

        var cats = root.GetProperty("defaultCategories");
        var catNames = Enumerable.Range(0, cats.GetArrayLength())
            .Select(i => cats[i].GetString()).ToList();
        catNames.Should().BeEquivalentTo(["Combos", "Bebidas"]);
    }

    // 3. Re-running seed does not duplicate items
    [Fact]
    public async Task SeedMenu_AlreadySeeded_SkipsDuplication()
    {
        // First: create business with template
        var createReq = WithAdmin(new HttpRequestMessage(HttpMethod.Post, "/api/admin/businesses")
        {
            Content = JsonContent.Create(new
            {
                name = "Pizza Place",
                phoneNumberId = "777888999",
                restaurantType = "pizza"
            })
        });
        var createRes = await _client.SendAsync(createReq);
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var createBody = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var bizId = createBody.RootElement.GetProperty("id").GetString()!;
        var bizAdminKey = createBody.RootElement.GetProperty("adminKey").GetString()!;

        // Count items after first seed
        using (var scope = _webApp.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = Guid.Parse(bizId);
            var itemCount = await db.MenuItems.CountAsync(i => i.Category!.BusinessId == id);
            itemCount.Should().BeGreaterThan(0);
        }

        // Second: try to re-seed
        var seedReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/businesses/{bizId}/seed-menu");
        seedReq.Headers.Add("X-Admin-Key", bizAdminKey);
        var seedRes = await _client.SendAsync(seedReq);
        seedRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var seedBody = JsonDocument.Parse(await seedRes.Content.ReadAsStringAsync());
        seedBody.RootElement.GetProperty("seeded").GetBoolean().Should().BeFalse();

        // Items count unchanged
        using (var scope = _webApp.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = Guid.Parse(bizId);
            var catCount = await db.MenuCategories.CountAsync(c => c.BusinessId == id);
            catCount.Should().Be(3); // pizza has 3 categories
        }
    }

    // 4. Templates endpoint returns all templates
    [Fact]
    public async Task GetTemplates_ReturnsAllTemplates()
    {
        var res = await _client.GetAsync("/api/admin/businesses/templates");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var arr = body.RootElement;
        arr.GetArrayLength().Should().Be(5);

        var ids = Enumerable.Range(0, arr.GetArrayLength())
            .Select(i => arr[i].GetProperty("id").GetString())
            .ToList();
        ids.Should().Contain("burger");
        ids.Should().Contain("pizza");
        ids.Should().Contain("sushi");
        ids.Should().Contain("arepa");
        ids.Should().Contain("cafe");
    }

    // 4b. Template detail preview returns items
    [Fact]
    public async Task GetTemplatePreview_Burger_ReturnsItemDetails()
    {
        var res = await _client.GetAsync("/api/admin/businesses/templates/burger");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("totalItems").GetInt32().Should().Be(27);
        root.GetProperty("name").GetString().Should().Be("Hamburguesas");

        var cats = root.GetProperty("categories");
        cats.GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task GetTemplatePreview_InvalidType_Returns404()
    {
        var res = await _client.GetAsync("/api/admin/businesses/templates/tacos");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 5. Seed categories match template
    [Fact]
    public async Task List_WithGlobalAdminKey_ReturnsBusinesses()
    {
        // Create a business first
        var createReq = WithAdmin(new HttpRequestMessage(HttpMethod.Post, "/api/admin/businesses")
        {
            Content = JsonContent.Create(new
            {
                name = "List Test Biz",
                phoneNumberId = "list-test-001"
            })
        });
        var createRes = await _client.SendAsync(createReq);
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now GET /api/admin/businesses
        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/businesses");
        listReq.Headers.Add("X-Admin-Key", AdminKey);
        var listRes = await _client.SendAsync(listReq);

        // Dump response for debugging
        var body = await listRes.Content.ReadAsStringAsync();
        listRes.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {body}");

        var arr = JsonDocument.Parse(body).RootElement;
        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("burger", new[] { "Hamburguesas", "Perros Calientes", "Papas", "Bebidas", "Combos", "Salsas" })]
    [InlineData("pizza", new[] { "Pizzas", "Pastas", "Bebidas" })]
    [InlineData("sushi", new[] { "Rolls", "Nigiri", "Bebidas" })]
    [InlineData("arepa", new[] { "Arepas", "Empanadas", "Bebidas" })]
    [InlineData("cafe", new[] { "Cafes", "Postres", "Snacks", "Bebidas Frias" })]
    public async Task Create_WithType_SeedsCorrectCategories(string type, string[] expectedCats)
    {
        var phoneId = $"seed-test-{type}-{Guid.NewGuid():N}"[..20];
        var req = WithAdmin(new HttpRequestMessage(HttpMethod.Post, "/api/admin/businesses")
        {
            Content = JsonContent.Create(new
            {
                name = $"Test {type}",
                phoneNumberId = phoneId,
                restaurantType = type
            })
        });

        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var cats = body.RootElement.GetProperty("defaultCategories");
        var catNames = Enumerable.Range(0, cats.GetArrayLength())
            .Select(i => cats[i].GetString()).ToList();

        catNames.Should().BeEquivalentTo(expectedCats);
    }
}
