using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

/// <summary>
/// TEMPORARY one-time repair endpoint — safe to delete once Tony's sedes are fixed.
/// Reassigns toninader7@gmail.com to exactly the two La Mina sedes (Owner role),
/// preserving the existing password hash / name / active flag. It only ever touches
/// BusinessUser rows for that one email — never any Business row, the Founder
/// account, the CODIGO Founder account, or any other user.
/// </summary>
[ApiController]
[Route("api/admin/repairs")]
[EnableRateLimiting("admin")]
[Authorize]
public sealed class AdminRepairsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminRepairsController> _logger;

    public AdminRepairsController(AppDbContext db, IConfiguration config, ILogger<AdminRepairsController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    private const string TargetEmail  = "toninader7@gmail.com";
    private const string LosPalosName = "La mina del shawarma - Sede Los Palos Grandes";
    private const string LaBoyeraName = "La mina del shawarma - Sede La Boyera";

    // POST /api/admin/repairs/tony-la-mina-sedes
    [HttpPost("tony-la-mina-sedes")]
    [AllowAnonymous] // gated internally — Founder JWT OR global X-Admin-Key, like AdminUsersController.QuickCreate
    public async Task<IActionResult> RepairTonySedes(CancellationToken ct)
    {
        if (!AdminAuth.IsFounder(User) && !AdminAuth.IsGlobalAdminKey(Request, _config))
            return Unauthorized(new { error = "Founder role or global admin key required" });

        // 1. Find the user's assignment rows (one row per business).
        var userRows = await _db.BusinessUsers
            .Where(u => u.Email == TargetEmail)
            .ToListAsync(ct);
        if (userRows.Count == 0)
            return NotFound(new { error = $"User not found: {TargetEmail}" });

        // 9. Never touch a Founder account.
        if (userRows.Any(u => u.Role == "Founder"))
            return BadRequest(new { error = "Refusing: target email holds a Founder account." });

        // 2 & 3. Resolve the two target businesses by exact name.
        var losPalosMatches = await _db.Businesses
            .Where(b => b.Name == LosPalosName)
            .Select(b => new { b.Id, b.Name })
            .Take(2).ToListAsync(ct);
        var laBoyeraMatches = await _db.Businesses
            .Where(b => b.Name == LaBoyeraName)
            .Select(b => new { b.Id, b.Name })
            .Take(2).ToListAsync(ct);

        // 8. Refuse if either business is missing — or ambiguous (multiple same-name rows).
        if (losPalosMatches.Count == 0) return BadRequest(new { error = $"Business not found: '{LosPalosName}'" });
        if (laBoyeraMatches.Count == 0) return BadRequest(new { error = $"Business not found: '{LaBoyeraName}'" });
        if (losPalosMatches.Count > 1)  return BadRequest(new { error = $"Ambiguous: multiple businesses named '{LosPalosName}'" });
        if (laBoyeraMatches.Count > 1)  return BadRequest(new { error = $"Ambiguous: multiple businesses named '{LaBoyeraName}'" });

        var losPalos = losPalosMatches[0];
        var laBoyera = laBoyeraMatches[0];
        var targetIds = new[] { losPalos.Id, laBoyera.Id };

        // 7. Snapshot BEFORE — materialized copy, taken before any mutation.
        var before = userRows
            .Select(u => new { u.Id, businessId = u.BusinessId, u.Role, u.IsActive })
            .ToList();

        // 4, 5, 6. Reassign to EXACTLY the two sedes as Owner, preserving hash/name/active.
        var template = userRows.First();
        var existingBizIds = userRows.Select(u => u.BusinessId).ToHashSet();

        // Remove this user's rows that are not one of the two target sedes.
        var toRemove = userRows.Where(u => !targetIds.Contains(u.BusinessId)).ToList();
        _db.BusinessUsers.RemoveRange(toRemove);

        // Keep rows already on a target sede; ensure role Owner.
        foreach (var u in userRows.Where(u => targetIds.Contains(u.BusinessId)))
        {
            u.Role = "Owner";
            u.TokenVersion++;
        }

        // Add any target sede the user does not yet have (reusing the existing credentials).
        foreach (var bid in targetIds)
        {
            if (!existingBizIds.Contains(bid))
            {
                _db.BusinessUsers.Add(new BusinessUser
                {
                    BusinessId   = bid,
                    Name         = template.Name,
                    Email        = template.Email,
                    PasswordHash = template.PasswordHash,
                    Role         = "Owner",
                    IsActive     = template.IsActive
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        // 7. Snapshot AFTER.
        var after = await _db.BusinessUsers
            .Where(u => u.Email == TargetEmail)
            .Select(u => new { u.Id, businessId = u.BusinessId, u.Role, u.IsActive })
            .ToListAsync(ct);

        _logger.LogWarning("REPAIR tony-la-mina-sedes: {Email} -> [{LP}, {LB}]; removed={Removed} before={Before} after={After}",
            TargetEmail, losPalos.Id, laBoyera.Id, toRemove.Count, before.Count, after.Count);

        return Ok(new
        {
            repaired = true,
            email = TargetEmail,
            assignedSedes = new[]
            {
                new { id = losPalos.Id, name = losPalos.Name },
                new { id = laBoyera.Id, name = laBoyera.Name }
            },
            removedAssignments = toRemove.Count,
            before,
            after
        });
    }
}
