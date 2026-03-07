using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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

    public AdminUsersController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private Guid? GetBusinessIdFromToken() =>
        Guid.TryParse(User.FindFirstValue("businessId"), out var id) ? id : null;

    private string? GetRoleFromToken() =>
        User.FindFirstValue(ClaimTypes.Role);

    private bool IsOwnerOrManager()
    {
        var role = GetRoleFromToken();
        return role is "Owner" or "Manager";
    }

    private bool IsOwner() => GetRoleFromToken() == "Owner";

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
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
            return BadRequest(new { error = "Password must be at least 6 characters" });

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

        if (!string.IsNullOrWhiteSpace(req.Role))
        {
            var validRoles = new[] { "Owner", "Manager", "Operator" };
            if (!validRoles.Contains(req.Role))
                return BadRequest(new { error = "Invalid role", allowed = validRoles });
            user.Role = req.Role;
        }

        if (!string.IsNullOrWhiteSpace(req.Password))
        {
            if (req.Password.Length < 6)
                return BadRequest(new { error = "Password must be at least 6 characters" });
            user.PasswordHash = AuthController.HashPassword(req.Password);
        }

        if (req.IsActive.HasValue)
            user.IsActive = req.IsActive.Value;

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

    // POST /api/admin/users/seed-owner — Bootstrap: create first Owner for a business (global admin only)
    public sealed class SeedOwnerRequest
    {
        public Guid BusinessId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }

    [HttpPost("seed-owner")]
    [AllowAnonymous]
    public async Task<IActionResult> SeedOwner([FromBody] SeedOwnerRequest req, CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized(new { error = "Global admin key required" });

        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name, email, and password are required" });

        if (req.Password.Length < 6)
            return BadRequest(new { error = "Password must be at least 6 characters" });

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
