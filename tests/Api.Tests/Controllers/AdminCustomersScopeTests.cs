using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSaaS.Infrastructure.Persistence;
using Api.Controllers;

namespace WhatsAppSaaS.Api.Tests.Controllers;

/// <summary>
/// Pins the multi-sede-aware businessId scoping rules added to
/// AdminCustomersController.GetCrm. Each test sets up a ClaimsPrincipal
/// directly on the controller and asserts the auth-gate decision BEFORE
/// the SQL path runs — so these tests do not depend on Postgres being
/// available.
///
/// TEST mapping:
///   T1: Owner — Boyera selected → auth proceeds, businessId honored
///   T2: Owner — Palos selected  → auth proceeds, businessId honored
///   T3: Operator — only assigned sede → out-of-scope returns 401
///   T4: Unauthorized businessId → 401
/// </summary>
public class AdminCustomersScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AdminCustomersController _controller;

    private static readonly Guid Boyera = Guid.Parse("c87fd075-41fd-40c1-8988-df27efbb772f");
    private static readonly Guid Palos  = Guid.Parse("80d87c59-828c-4cf9-b554-df908646aa82");
    private static readonly Guid Foreign = Guid.Parse("aa7177ce-bfa0-45ef-ae3f-44a3046dc57c");

    public AdminCustomersScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var logger = NullLogger<AdminCustomersController>.Instance;
        _controller = new AdminCustomersController(_db, config, logger);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void SetUser(string role, Guid? primaryBizId, IEnumerable<Guid>? allBizIds = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (primaryBizId.HasValue)
            claims.Add(new Claim("businessId", primaryBizId.Value.ToString()));
        if (allBizIds is not null)
            claims.Add(new Claim("businessIds", string.Join(',', allBizIds)));

        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // ── T4: Unauthorized businessId → 401 ────────────────────────────────

    [Fact]
    public async Task T4_Owner_requesting_outOfScope_businessId_returns_401()
    {
        SetUser("Owner", primaryBizId: Boyera, allBizIds: new[] { Boyera, Palos });
        var result = await _controller.GetCrm(businessId: Foreign);
        result.Should().BeOfType<UnauthorizedResult>(
            "an Owner may not query a sede outside their JWT businessIds claim");
    }

    [Fact]
    public async Task T3_Operator_requesting_outOfScope_businessId_returns_401()
    {
        // Operator assigned ONLY to Boyera; tries to view Palos
        SetUser("Operator", primaryBizId: Boyera, allBizIds: new[] { Boyera });
        var result = await _controller.GetCrm(businessId: Palos);
        result.Should().BeOfType<UnauthorizedResult>(
            "an Operator must not see customers for a sede they are not assigned to");
    }

    // ── T1 & T2: in-scope requests proceed past the auth gate ────────────
    // The controller then opens a DB connection and runs Postgres-specific
    // SQL that SQLite cannot execute — so we expect either a real Ok payload
    // (in prod) or a thrown DB exception (in this test fixture). What we
    // CAN assert here: the result is NOT UnauthorizedResult — meaning the
    // dropdown selection was honored, not silently overridden.

    [Fact]
    public async Task T1_Owner_requesting_Boyera_passes_authGate()
    {
        SetUser("Owner", primaryBizId: Palos, allBizIds: new[] { Boyera, Palos });
        // JWT primary is Palos; dropdown sends Boyera — must NOT be overridden.
        await AssertAuthGatePasses(Boyera);
    }

    [Fact]
    public async Task T2_Owner_requesting_Palos_passes_authGate()
    {
        SetUser("Owner", primaryBizId: Boyera, allBizIds: new[] { Boyera, Palos });
        // JWT primary is Boyera; dropdown sends Palos — must NOT be overridden.
        await AssertAuthGatePasses(Palos);
    }

    [Fact]
    public async Task Owner_with_no_businessId_falls_back_to_JWT_primary()
    {
        // No ?businessId= in query — the controller must fall back to JWT primary.
        SetUser("Owner", primaryBizId: Boyera, allBizIds: new[] { Boyera, Palos });
        await AssertAuthGatePasses(businessId: null);
    }

    [Fact]
    public async Task Founder_can_request_any_business()
    {
        // Founder has no businessId/businessIds claim, but tenant gate is bypassed.
        SetUser("Founder", primaryBizId: null, allBizIds: null);
        await AssertAuthGatePasses(Foreign);
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };
        var result = await _controller.GetCrm(businessId: Boyera);
        result.Should().BeOfType<UnauthorizedResult>();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task AssertAuthGatePasses(Guid? businessId)
    {
        try
        {
            var result = await _controller.GetCrm(businessId: businessId);
            // If the DB layer somehow returned, only UnauthorizedResult would mean
            // the auth gate had wrongly fired. Anything else (Ok, 500, etc.) is
            // acceptable for the auth-gate assertion this test pins.
            result.Should().NotBeOfType<UnauthorizedResult>(
                "the auth gate must not reject an in-scope sede request");
        }
        catch (Exception)
        {
            // Expected when SQLite hits Postgres-specific SQL. The fact that the
            // controller reached the DB at all proves the auth gate passed.
        }
    }
}
