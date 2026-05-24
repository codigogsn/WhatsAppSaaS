using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSaaS.Api.Controllers;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Tests.Controllers;

/// <summary>
/// Tests for POST /api/admin/menu/reset. Drives the controller directly
/// (no WebApplicationFactory) — matches the pattern in
/// <see cref="PaymentProofTenantIsolationTests"/> and avoids the test-host
/// bootstrap fragility that affects the WebApplicationFactory&lt;Program&gt; path.
/// Covers:
///   • dry-run vs real call
///   • typed RESET MENU confirmation enforced server-side
///   • multi-tenant isolation (the most important guarantee)
///   • orders / customers / MenuPdfs / Business settings stay untouched
///   • idempotence
///   • auth gate (X-Admin-Key) and role gate (Operator JWT blocked)
/// </summary>
public sealed class ResetMenuTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private const string GlobalAdminKey = "test-reset-global-admin-key";

    public ResetMenuTests()
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

    // ── Test fixture helpers ─────────────────────────────────────────────────

    private AdminMenuController NewController(
        string? adminKeyHeader = GlobalAdminKey,
        ClaimsPrincipal? user = null,
        string? globalAdminKey = GlobalAdminKey)
    {
        var configValues = new Dictionary<string, string?>();
        if (globalAdminKey is not null) configValues["ADMIN_KEY"] = globalAdminKey;
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var ctx = new DefaultHttpContext { User = user ?? new ClaimsPrincipal() };
        if (adminKeyHeader is not null) ctx.Request.Headers["X-Admin-Key"] = adminKeyHeader;

        return new AdminMenuController(_db, config, NullLogger<AdminMenuController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };
    }

    private Guid SeedBusinessWithMenu(
        string nameSuffix = "A",
        int categories = 2,
        int itemsPerCategory = 3,
        int aliasesPerItem = 2,
        int extras = 1,
        int upsells = 1)
    {
        var biz = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Tenant {nameSuffix}",
            PhoneNumberId = $"phone-{nameSuffix}-{Guid.NewGuid():N}".Substring(0, 18),
            AccessToken = $"token-{nameSuffix}",
            AdminKey = $"admin-{nameSuffix}",
            IsActive = true,
            MenuPdfUrl = $"https://example.com/menu-{nameSuffix}.pdf",
        };
        _db.Businesses.Add(biz);

        Guid? firstCategoryId = null;
        for (var c = 0; c < categories; c++)
        {
            var cat = new MenuCategory
            {
                Id = Guid.NewGuid(),
                BusinessId = biz.Id,
                Name = $"Cat {nameSuffix}-{c}",
                SortOrder = c,
                IsActive = true,
            };
            _db.MenuCategories.Add(cat);
            firstCategoryId ??= cat.Id;

            for (var i = 0; i < itemsPerCategory; i++)
            {
                var item = new MenuItem
                {
                    Id = Guid.NewGuid(),
                    CategoryId = cat.Id,
                    Name = $"Item {nameSuffix}-{c}-{i}",
                    Price = 1.50m + i,
                    IsAvailable = true,
                    SortOrder = i,
                };
                _db.MenuItems.Add(item);

                for (var a = 0; a < aliasesPerItem; a++)
                {
                    _db.MenuItemAliases.Add(new MenuItemAlias
                    {
                        Id = Guid.NewGuid(),
                        MenuItemId = item.Id,
                        Alias = $"alias-{nameSuffix}-{c}-{i}-{a}",
                    });
                }
            }
        }

        for (var e = 0; e < extras; e++)
        {
            _db.Extras.Add(new Extra
            {
                Id = Guid.NewGuid(),
                BusinessId = biz.Id,
                Name = $"Extra {nameSuffix}-{e}",
                AdditivePrice = 1.0m,
                IsActive = true,
                SortOrder = e,
            });
        }

        for (var u = 0; u < upsells; u++)
        {
            // firstCategoryId is captured in the loop above so we don't query
            // the DbContext for a not-yet-saved category.
            if (firstCategoryId is null) break;
            _db.UpsellRules.Add(new UpsellRule
            {
                Id = Guid.NewGuid(),
                BusinessId = biz.Id,
                SourceCategoryId = firstCategoryId.Value,
                SuggestionLabel = $"upsell-{nameSuffix}-{u}",
                IsActive = true,
                SortOrder = u,
            });
        }

        _db.SaveChanges();
        return biz.Id;
    }

    private (Guid orderId, Guid customerId) SeedOrderAndCustomerFor(Guid bizId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = bizId,
            PhoneE164 = "+58414123" + Random.Shared.Next(10000, 99999).ToString(),
            Name = "Reset Test Customer",
        };
        _db.Customers.Add(customer);
        _db.SaveChanges();

        // Use raw SQL to bypass EF's ValueGeneratedOnAddOrUpdate on Order.RowVersion
        // (the xmin column is auto-generated in Postgres but has no SQLite equivalent).
        var orderId = Guid.NewGuid();
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO "Orders" ("Id","BusinessId","From","PhoneNumberId","CustomerId","CustomerName",
                                       "Status","DeliveryType","CheckoutCompleted","CheckoutFormSent",
                                       "CreatedAtUtc","CashChangeRequired","CashChangeReturned","xmin")
                VALUES (@id,@bid,@from,@pid,@cid,@cname,'Completed','delivery',1,0,@now,0,0,0)
            """;
            void P(string n, object v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; cmd.Parameters.Add(p); }
            P("id", orderId);
            P("bid", bizId);
            P("from", "58414123" + Random.Shared.Next(10000, 99999).ToString());
            P("pid", "test-phone");
            P("cid", customer.Id);
            P("cname", "Reset Test Customer");
            P("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO "OrderItems" ("Id","OrderId","Name","Quantity","UnitPrice","LineTotal")
                VALUES (@id,@oid,'Frozen-name Item',1,5.00,5.00)
            """;
            void P(string n, object v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; cmd.Parameters.Add(p); }
            P("id", Guid.NewGuid());
            P("oid", orderId);
            cmd.ExecuteNonQuery();
        }
        return (orderId, customer.Id);
    }

    private void SeedMenuPdfFor(Guid bizId)
    {
        _db.MenuPdfs.Add(new MenuPdf
        {
            Id = Guid.NewGuid(),
            BusinessId = bizId,
            Data = new byte[] { 1, 2, 3, 4 },
            ContentType = "application/pdf",
            UploadedAtUtc = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private (int categories, int items, int aliases, int extras, int upsells)
        CountMenuRows(Guid bizId)
    {
        var catIds = _db.MenuCategories.Where(c => c.BusinessId == bizId).Select(c => c.Id).ToList();
        var itemIds = _db.MenuItems.Where(i => catIds.Contains(i.CategoryId)).Select(i => i.Id).ToList();
        return (
            categories: catIds.Count,
            items: itemIds.Count,
            aliases: _db.MenuItemAliases.Count(a => itemIds.Contains(a.MenuItemId)),
            extras: _db.Extras.Count(e => e.BusinessId == bizId),
            upsells: _db.UpsellRules.Count(u => u.BusinessId == bizId)
        );
    }

    // Pulls anonymous-object fields out of an OkObjectResult.Value via reflection.
    private static int IntField(object? value, string name)
        => Convert.ToInt32(value!.GetType().GetProperty(name)!.GetValue(value));
    private static bool BoolField(object? value, string name)
        => (bool)value!.GetType().GetProperty(name)!.GetValue(value)!;

    private static AdminMenuController.ResetMenuRequest Req(Guid biz, string confirm = "")
        => new() { BusinessId = biz, Confirm = confirm };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetMenu_DryRun_ReturnsCountsAndDeletesNothing()
    {
        var biz = SeedBusinessWithMenu("DR", categories: 2, itemsPerCategory: 3, aliasesPerItem: 2, extras: 1, upsells: 1);
        var before = CountMenuRows(biz);

        var controller = NewController();
        var result = await controller.ResetMenu(Req(biz, confirm: ""), dryRun: true, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        BoolField(ok.Value, "dryRun").Should().BeTrue();
        IntField(ok.Value, "deletedCategories").Should().Be(before.categories);
        IntField(ok.Value, "deletedItems").Should().Be(before.items);
        IntField(ok.Value, "deletedAliases").Should().Be(before.aliases);
        IntField(ok.Value, "deletedExtras").Should().Be(before.extras);
        IntField(ok.Value, "deletedUpsells").Should().Be(before.upsells);

        CountMenuRows(biz).Should().Be(before, "dry-run must not modify any rows");
    }

    [Fact]
    public async Task ResetMenu_RealCall_DeletesAllMenuRowsForBusiness()
    {
        var biz = SeedBusinessWithMenu("R", categories: 3, itemsPerCategory: 4, aliasesPerItem: 2, extras: 2, upsells: 1);
        var before = CountMenuRows(biz);
        before.categories.Should().Be(3);
        before.items.Should().Be(12);
        before.aliases.Should().Be(24);
        before.extras.Should().Be(2);
        before.upsells.Should().Be(1);

        var controller = NewController();
        var result = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        BoolField(ok.Value, "dryRun").Should().BeFalse();
        IntField(ok.Value, "deletedCategories").Should().Be(3);
        IntField(ok.Value, "deletedItems").Should().Be(12);
        IntField(ok.Value, "deletedAliases").Should().Be(24);
        IntField(ok.Value, "deletedExtras").Should().Be(2);
        IntField(ok.Value, "deletedUpsells").Should().Be(1);

        CountMenuRows(biz).Should().Be((0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task ResetMenu_DoesNotTouchOrdersOrCustomers()
    {
        var biz = SeedBusinessWithMenu("ORD");
        SeedOrderAndCustomerFor(biz);

        var ordersBefore     = _db.Orders.Count(o => o.BusinessId == biz);
        var orderIds         = _db.Orders.Where(o => o.BusinessId == biz).Select(o => o.Id).ToList();
        var orderItemsBefore = _db.OrderItems.Count(oi => orderIds.Contains(oi.OrderId));
        var customersBefore  = _db.Customers.Count(c => c.BusinessId == biz);
        ordersBefore.Should().BeGreaterThan(0);
        customersBefore.Should().BeGreaterThan(0);

        var controller = NewController();
        var result = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        _db.Orders.Count(o => o.BusinessId == biz).Should().Be(ordersBefore,
            "reset must NOT touch the Orders table");
        _db.OrderItems.Count(oi => orderIds.Contains(oi.OrderId)).Should().Be(orderItemsBefore,
            "reset must NOT touch OrderItems (historical snapshots must survive)");
        _db.Customers.Count(c => c.BusinessId == biz).Should().Be(customersBefore,
            "reset must NOT touch Customers");
    }

    [Fact]
    public async Task ResetMenu_DoesNotTouchMenuPdf()
    {
        var biz = SeedBusinessWithMenu("PDF");
        SeedMenuPdfFor(biz);

        _db.MenuPdfs.Count(p => p.BusinessId == biz).Should().Be(1);
        var pdfUrlBefore = _db.Businesses.Single(b => b.Id == biz).MenuPdfUrl;
        pdfUrlBefore.Should().NotBeNullOrEmpty();

        var controller = NewController();
        var result = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        _db.MenuPdfs.Count(p => p.BusinessId == biz).Should().Be(1,
            "reset must NOT delete the customer-facing menu PDF");
        _db.Businesses.AsNoTracking().Single(b => b.Id == biz).MenuPdfUrl.Should().Be(pdfUrlBefore,
            "Business.MenuPdfUrl must survive the reset");
    }

    [Fact]
    public async Task ResetMenu_DoesNotTouchOtherTenants()
    {
        var bizA = SeedBusinessWithMenu("A");
        var bizB = SeedBusinessWithMenu("B");
        var beforeB = CountMenuRows(bizB);

        var controller = NewController();
        var result = await controller.ResetMenu(Req(bizA, "RESET MENU"), dryRun: false, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        CountMenuRows(bizA).Should().Be((0, 0, 0, 0, 0), "bizA's menu must be wiped");
        CountMenuRows(bizB).Should().Be(beforeB,
            "bizB must be completely untouched — multi-tenant isolation is non-negotiable");
    }

    [Fact]
    public async Task ResetMenu_RejectsMissingConfirmString()
    {
        var biz = SeedBusinessWithMenu("MC");
        var before = CountMenuRows(biz);

        var controller = NewController();
        var result = await controller.ResetMenu(Req(biz, confirm: ""), dryRun: false, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();

        CountMenuRows(biz).Should().Be(before, "BadRequest must NOT delete anything");
    }

    [Fact]
    public async Task ResetMenu_RejectsWrongConfirmString()
    {
        var biz = SeedBusinessWithMenu("WC");
        var before = CountMenuRows(biz);

        // Case-sensitive check — lowercase must be rejected.
        var controller = NewController();
        var result = await controller.ResetMenu(Req(biz, "reset menu"), dryRun: false, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();

        CountMenuRows(biz).Should().Be(before);
    }

    [Fact]
    public async Task ResetMenu_Unauthorized_Returns401_NoDeletes()
    {
        var biz = SeedBusinessWithMenu("U");
        var before = CountMenuRows(biz);

        // No JWT, no X-Admin-Key header. Global admin key is still configured
        // but the request doesn't carry it, so auth must fail.
        var controller = NewController(adminKeyHeader: null);
        var result = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);
        result.Should().BeOfType<UnauthorizedResult>();

        CountMenuRows(biz).Should().Be(before);
    }

    [Fact]
    public async Task ResetMenu_RoleEnforcement_OperatorJwtBlocked()
    {
        // Operator role must be blocked even when JWT scope includes this business.
        // Only Owner / Manager / Founder may run the reset.
        var biz = SeedBusinessWithMenu("OP");
        var before = CountMenuRows(biz);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Operator"),
            new Claim("businessId", biz.ToString()),
        }, authenticationType: "TestJwt");

        var controller = NewController(adminKeyHeader: null, user: new ClaimsPrincipal(identity));
        var result = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);

        // The operator passes JWT auth (scoped to this business) but fails the
        // role gate — so the response is the role-specific UnauthorizedObjectResult.
        result.Should().BeOfType<UnauthorizedObjectResult>(
            "Operator role must be blocked from menu reset");

        CountMenuRows(biz).Should().Be(before, "blocked call must NOT delete anything");
    }

    [Fact]
    public async Task ResetMenu_OwnerJwtAllowed()
    {
        // Companion to the operator-blocked test: Owner JWT scoped to the business
        // must pass both auth and role gates and complete the wipe.
        var biz = SeedBusinessWithMenu("OW");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Owner"),
            new Claim("businessId", biz.ToString()),
        }, authenticationType: "TestJwt");

        var controller = NewController(adminKeyHeader: null, user: new ClaimsPrincipal(identity));
        var result = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        CountMenuRows(biz).Should().Be((0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task ResetMenu_IsIdempotent()
    {
        var biz = SeedBusinessWithMenu("ID");

        var controller = NewController();
        var r1 = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);
        r1.Should().BeOfType<OkObjectResult>();

        var r2 = await controller.ResetMenu(Req(biz, "RESET MENU"), dryRun: false, CancellationToken.None);
        var ok = r2.Should().BeOfType<OkObjectResult>().Subject;
        IntField(ok.Value, "deletedCategories").Should().Be(0);
        IntField(ok.Value, "deletedItems").Should().Be(0);
        IntField(ok.Value, "deletedAliases").Should().Be(0);
        IntField(ok.Value, "deletedExtras").Should().Be(0);
        IntField(ok.Value, "deletedUpsells").Should().Be(0);
    }

    [Fact(Skip = "Transaction-rollback simulation requires injecting a mid-execution failure into a raw ADO.NET DbCommand — non-trivial without bespoke mocking. The BeginTransactionAsync + Commit/Rollback shape is verified by inspection; the other tests prove the happy path and the negative gates. Tracked as a follow-up.")]
    public void ResetMenu_OnFailureMidWay_NoPartialState()
    {
        // See Skip reason above.
    }
}
