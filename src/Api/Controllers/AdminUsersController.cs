using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[EnableRateLimiting("admin")]
[Authorize]
public sealed class AdminUsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminUsersController> _logger;

    public AdminUsersController(AppDbContext db, IConfiguration config, ILogger<AdminUsersController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    private Guid? GetBusinessIdFromToken() =>
        Guid.TryParse(User.FindFirstValue("businessId"), out var id) ? id : null;

    private string? GetRoleFromToken() =>
        User.FindFirstValue(ClaimTypes.Role);

    private bool IsOwnerOrManager()
    {
        var role = GetRoleFromToken();
        return role is "Founder" or "Owner" or "Manager";
    }

    private bool IsOwner() => GetRoleFromToken() is "Founder" or "Owner";
    private bool IsFounderRole() => GetRoleFromToken() == "Founder";

    private bool IsGlobalAdmin()
    {
        var adminKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey)) return false;
        return Request.Headers.TryGetValue("X-Admin-Key", out var headerKey)
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(headerKey.ToString()),
                   System.Text.Encoding.UTF8.GetBytes(adminKey));
    }

    // GET /api/admin/users
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var bizId = GetBusinessIdFromToken();
        if (bizId is null) return Unauthorized();
        if (!IsOwnerOrManager()) return Forbid();

        var users = await _db.BusinessUsers
            .AsNoTracking()
            .Where(u => u.BusinessId == bizId.Value)
            .OrderByDescending(u => u.CreatedAtUtc)
            .Select(u => new
            {
                u.Id,
                u.Name,
                u.Email,
                u.Role,
                u.IsActive,
                u.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    public sealed class CreateUserRequest
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = "Operator";
    }

    // POST /api/admin/users
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        var bizId = GetBusinessIdFromToken();
        if (bizId is null) return Unauthorized();
        if (!IsOwner()) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required" });
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { error = "Email is required" });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters" });

        var validRoles = new[] { "Owner", "Manager", "Operator" };
        if (!validRoles.Contains(req.Role))
            return BadRequest(new { error = "Invalid role", allowed = validRoles });

        var email = req.Email.Trim().ToLowerInvariant();
        var exists = await _db.BusinessUsers
            .AnyAsync(u => u.BusinessId == bizId.Value && u.Email == email, ct);
        if (exists)
            return Conflict(new { error = "A user with that email already exists in this business" });

        var user = new BusinessUser
        {
            BusinessId = bizId.Value,
            Name = req.Name.Trim(),
            Email = email,
            PasswordHash = AuthController.HashPassword(req.Password),
            Role = req.Role,
            IsActive = true
        };

        _db.BusinessUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            user.Id,
            user.Name,
            user.Email,
            user.Role,
            user.IsActive,
            user.CreatedAtUtc
        });
    }

    public sealed class UpdateUserRequest
    {
        public string? Name { get; set; }
        public string? Role { get; set; }
        public string? Password { get; set; }
        public bool? IsActive { get; set; }
    }

    // PATCH /api/admin/users/{id}
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest req, CancellationToken ct)
    {
        var bizId = GetBusinessIdFromToken();
        if (bizId is null) return Unauthorized();
        if (!IsOwner()) return Forbid();

        var user = await _db.BusinessUsers
            .FirstOrDefaultAsync(u => u.Id == id && u.BusinessId == bizId.Value, ct);
        if (user is null) return NotFound(new { error = "User not found" });

        if (!string.IsNullOrWhiteSpace(req.Name))
            user.Name = req.Name.Trim();

        var mustRevoke = false;

        if (!string.IsNullOrWhiteSpace(req.Role))
        {
            var validRoles = new[] { "Owner", "Manager", "Operator" };
            if (!validRoles.Contains(req.Role))
                return BadRequest(new { error = "Invalid role", allowed = validRoles });
            if (user.Role != req.Role) mustRevoke = true;
            user.Role = req.Role;
        }

        if (!string.IsNullOrWhiteSpace(req.Password))
        {
            if (req.Password.Length < 8)
                return BadRequest(new { error = "Password must be at least 8 characters" });
            user.PasswordHash = AuthController.HashPassword(req.Password);
            mustRevoke = true;
        }

        if (req.IsActive.HasValue)
        {
            if (user.IsActive && !req.IsActive.Value) mustRevoke = true;
            user.IsActive = req.IsActive.Value;
        }

        if (mustRevoke) user.TokenVersion++;

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            user.Id,
            user.Name,
            user.Email,
            user.Role,
            user.IsActive,
            user.CreatedAtUtc
        });
    }

    // DELETE /api/admin/users/{id} — permanent delete (Owner or global admin)
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var bizId = GetBusinessIdFromToken();
        var isAdmin = IsGlobalAdmin();
        if (bizId is null && !isAdmin) return Unauthorized();
        if (!IsOwner() && !isAdmin) return Forbid();

        var query = _db.BusinessUsers.Where(u => u.Id == id);
        if (!isAdmin && bizId is not null)
            query = query.Where(u => u.BusinessId == bizId.Value);

        var user = await query.FirstOrDefaultAsync(ct);
        if (user is null) return NotFound(new { error = "User not found" });

        _db.BusinessUsers.Remove(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User permanently deleted: id={UserId} email={Email} business={BusinessId}",
            user.Id, user.Email, user.BusinessId);

        return Ok(new { deleted = true, id = user.Id, email = user.Email });
    }

    // GET /api/admin/users/me
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId)) return Unauthorized();

        var user = await _db.BusinessUsers
            .AsNoTracking()
            .Include(u => u.Business)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return NotFound();

        return Ok(new
        {
            user.Id,
            user.BusinessId,
            businessName = user.Business?.Name,
            user.Name,
            user.Email,
            user.Role,
            user.IsActive,
            user.CreatedAtUtc
        });
    }

    // ── Multi-business user management (global admin) ──

    public sealed class CreateMultiUserRequest
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = "Operator";
        public bool IsActive { get; set; } = true;
        public List<Guid> BusinessIds { get; set; } = new();
    }

    // POST /api/admin/users/multi — Create user with multiple business assignments (global admin only)
    [HttpPost("multi")]
    public async Task<IActionResult> CreateMulti([FromBody] CreateMultiUserRequest req, CancellationToken ct)
    {
        if (!IsGlobalAdmin() && !IsFounderRole())
            return Unauthorized(new { error = "Global admin or Founder role required" });

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required" });
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { error = "Email is required" });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters" });
        if (req.BusinessIds.Count == 0)
            return BadRequest(new { error = "At least one business must be selected" });

        var validRoles = new[] { "Owner", "Manager", "Operator" };
        if (!validRoles.Contains(req.Role))
            return BadRequest(new { error = "Invalid role", allowed = validRoles });

        var email = req.Email.Trim().ToLowerInvariant();
        var passwordHash = AuthController.HashPassword(req.Password);

        // Verify all business IDs exist
        var existingBizIds = await _db.Businesses
            .Where(b => req.BusinessIds.Contains(b.Id))
            .Select(b => b.Id)
            .ToListAsync(ct);

        var missingIds = req.BusinessIds.Except(existingBizIds).ToList();
        if (missingIds.Count > 0)
            return BadRequest(new { error = "Business(es) not found", missingIds });

        // Check for existing users with this email in any of the target businesses
        var existingAssignments = await _db.BusinessUsers
            .Where(u => u.Email == email && req.BusinessIds.Contains(u.BusinessId))
            .Select(u => u.BusinessId)
            .ToListAsync(ct);
        if (existingAssignments.Count > 0)
            return Conflict(new { error = "User already exists in one or more businesses", existingBusinessIds = existingAssignments });

        // Create one BusinessUser row per assigned business
        var created = new List<object>();
        foreach (var bizId in req.BusinessIds)
        {
            var user = new BusinessUser
            {
                BusinessId = bizId,
                Name = req.Name.Trim(),
                Email = email,
                PasswordHash = passwordHash,
                Role = req.Role,
                IsActive = req.IsActive
            };
            _db.BusinessUsers.Add(user);
            created.Add(new { user.Id, businessId = bizId });
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            email,
            name = req.Name.Trim(),
            role = req.Role,
            isActive = req.IsActive,
            assignments = created,
            businessCount = req.BusinessIds.Count
        });
    }

    // GET /api/admin/users/all — List all users across all businesses (global admin only)
    [HttpGet("all")]
    public async Task<IActionResult> ListAll(CancellationToken ct)
    {
        if (!IsGlobalAdmin() && !IsFounderRole())
            return Unauthorized(new { error = "Global admin or Founder role required" });

        // Use raw SQL to avoid legacy DateTime column issues
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        var users = new Dictionary<string, dynamic>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u."Id", u."BusinessId", u."Name", u."Email", u."Role", u."IsActive",
                   b."Name" AS "BizName"
            FROM "BusinessUsers" u
            LEFT JOIN "Businesses" b ON CAST(b."Id" AS TEXT) = CAST(u."BusinessId" AS TEXT)
            ORDER BY u."Email", u."Name"
        """;

        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var emailVal = r.GetString(3);
            var rawActive = r["IsActive"];
            var active = rawActive switch
            {
                bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                string sv => sv is "1" or "true" or "True", _ => true
            };

            if (!users.ContainsKey(emailVal))
            {
                users[emailVal] = new
                {
                    id = r.GetGuid(0),
                    name = r.GetString(2),
                    email = emailVal,
                    role = r.GetString(4),
                    isActive = active,
                    businesses = new List<object>()
                };
            }

            ((List<object>)users[emailVal].businesses).Add(new
            {
                id = r.GetGuid(1),
                name = r["BizName"]?.ToString() ?? ""
            });
        }

        return Ok(users.Values.ToList());
    }

    // PATCH /api/admin/users/multi/{email} — Update user across all assigned businesses (global admin only)
    [HttpPatch("multi/{email}")]
    public async Task<IActionResult> UpdateMulti(string email, [FromBody] UpdateMultiUserRequest req, CancellationToken ct)
    {
        if (!IsGlobalAdmin() && !IsFounderRole())
            return Unauthorized(new { error = "Global admin or Founder role required" });

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var userRows = await _db.BusinessUsers
            .Where(u => u.Email == normalizedEmail)
            .ToListAsync(ct);

        if (userRows.Count == 0)
            return NotFound(new { error = "User not found" });

        var mustRevoke = false;
        foreach (var u in userRows)
        {
            if (!string.IsNullOrWhiteSpace(req.Name)) u.Name = req.Name.Trim();
            if (!string.IsNullOrWhiteSpace(req.Role))
            {
                var validRoles = new[] { "Owner", "Manager", "Operator" };
                if (!validRoles.Contains(req.Role))
                    return BadRequest(new { error = "Invalid role" });
                if (u.Role != req.Role) mustRevoke = true;
                u.Role = req.Role;
            }
            if (req.IsActive.HasValue)
            {
                if (u.IsActive && !req.IsActive.Value) mustRevoke = true;
                u.IsActive = req.IsActive.Value;
            }
            if (!string.IsNullOrWhiteSpace(req.Password))
            {
                if (req.Password.Length < 8)
                    return BadRequest(new { error = "Password must be at least 8 characters" });
                u.PasswordHash = AuthController.HashPassword(req.Password);
                mustRevoke = true;
            }
            if (mustRevoke) u.TokenVersion++;
        }

        // Handle business reassignment
        if (req.BusinessIds is not null)
        {
            var currentBizIds = userRows.Select(u => u.BusinessId).ToList();
            var newBizIds = req.BusinessIds;

            // Remove assignments no longer in the list
            var toRemove = userRows.Where(u => !newBizIds.Contains(u.BusinessId)).ToList();
            _db.BusinessUsers.RemoveRange(toRemove);

            // Add new assignments
            var toAdd = newBizIds.Except(currentBizIds).ToList();
            var template = userRows.First();
            foreach (var bizId in toAdd)
            {
                _db.BusinessUsers.Add(new BusinessUser
                {
                    BusinessId = bizId,
                    Name = template.Name,
                    Email = template.Email,
                    PasswordHash = template.PasswordHash,
                    Role = template.Role,
                    IsActive = template.IsActive
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { updated = true, email = normalizedEmail });
    }

    public sealed class UpdateMultiUserRequest
    {
        public string? Name { get; set; }
        public string? Role { get; set; }
        public string? Password { get; set; }
        public bool? IsActive { get; set; }
        public List<Guid>? BusinessIds { get; set; }
    }

    // POST /api/admin/users/create — Quick onboarding: create user for any business (global admin only)
    public sealed class QuickCreateRequest
    {
        public Guid BusinessId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = "Owner";
    }

    [HttpPost("create")]
    [AllowAnonymous]
    public async Task<IActionResult> QuickCreate([FromBody] QuickCreateRequest req, CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized(new { error = "Global admin key required (X-Admin-Key header)" });

        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { error = "Email is required" });
        if (string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Password is required" });
        if (req.Password.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters" });

        var validRoles = new[] { "Owner", "Manager", "Operator" };
        if (!validRoles.Contains(req.Role))
            return BadRequest(new { error = "Invalid role", allowed = validRoles });

        var biz = await _db.Businesses.FirstOrDefaultAsync(b => b.Id == req.BusinessId && b.IsActive, ct);
        if (biz is null)
            return NotFound(new { error = "Business not found or inactive" });

        var email = req.Email.Trim().ToLowerInvariant();
        var exists = await _db.BusinessUsers.AnyAsync(u => u.BusinessId == req.BusinessId && u.Email == email, ct);
        if (exists)
            return Conflict(new { error = "A user with that email already exists in this business" });

        var name = string.IsNullOrWhiteSpace(req.Name) ? email.Split('@')[0] : req.Name.Trim();
        var user = new BusinessUser
        {
            BusinessId = req.BusinessId,
            Name = name,
            Email = email,
            PasswordHash = AuthController.HashPassword(req.Password),
            Role = req.Role,
            IsActive = true
        };

        _db.BusinessUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User created: email={Email} role={Role} business={BusinessName} ({BusinessId})",
            email, req.Role, biz.Name, req.BusinessId);

        return Ok(new
        {
            success = true,
            id = user.Id,
            email = user.Email,
            name = user.Name,
            role = user.Role,
            businessId = req.BusinessId,
            businessName = biz.Name
        });
    }

    // POST /api/admin/users/seed-owner — Bootstrap: create first Owner for a business (global admin only)
    public sealed class SeedOwnerRequest
    {
        public Guid BusinessId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }

    [HttpPost("seed-owner")]
    public async Task<IActionResult> SeedOwner([FromBody] SeedOwnerRequest req, CancellationToken ct)
    {
        if (!IsGlobalAdmin() && !IsFounderRole())
            return Unauthorized(new { error = "Global admin or Founder role required" });

        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name, email, and password are required" });

        if (req.Password.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters" });

        var biz = await _db.Businesses.FirstOrDefaultAsync(b => b.Id == req.BusinessId && b.IsActive, ct);
        if (biz is null)
            return NotFound(new { error = "Business not found" });

        var email = req.Email.Trim().ToLowerInvariant();
        var exists = await _db.BusinessUsers.AnyAsync(u => u.BusinessId == req.BusinessId && u.Email == email, ct);
        if (exists)
            return Conflict(new { error = "User already exists" });

        var user = new BusinessUser
        {
            BusinessId = req.BusinessId,
            Name = req.Name.Trim(),
            Email = email,
            PasswordHash = AuthController.HashPassword(req.Password),
            Role = "Owner",
            IsActive = true
        };

        _db.BusinessUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            user.Id,
            user.Name,
            user.Email,
            user.Role,
            businessName = biz.Name
        });
    }
}
