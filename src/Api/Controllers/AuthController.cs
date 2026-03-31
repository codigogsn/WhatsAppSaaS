using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Services;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("login")]
public sealed class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly JwtService _jwt;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext db, JwtService jwt, ILogger<AuthController> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public sealed class LoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Email and password are required" });

        var email = req.Email.Trim().ToLowerInvariant();

        // Raw SQL to avoid EF materialization crashes on legacy PostgreSQL schema
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // Fetch ALL business assignments for this email (multi-sede support)
        var assignments = new List<(Guid UserId, Guid BusinessId, string PasswordHash, string Name, string Email, string Role, bool UserActive, string BizName, bool BizActive)>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT u."Id", u."BusinessId", u."PasswordHash", u."Name", u."Email", u."Role", u."IsActive",
                       b."Name" AS "BizName", b."IsActive" AS "BizActive"
                FROM "BusinessUsers" u
                INNER JOIN "Businesses" b ON CAST(b."Id" AS TEXT) = CAST(u."BusinessId" AS TEXT)
                WHERE u."Email" = @email
                ORDER BY u."CreatedAtUtc" ASC
            """;
            var p = cmd.CreateParameter();
            p.ParameterName = "email";
            p.Value = email;
            cmd.Parameters.Add(p);

            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var rawActive = r["IsActive"];
                var userActive = rawActive switch
                {
                    bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                    string sv => sv is "1" or "true" or "True", _ => true
                };
                var rawBizActive = r["BizActive"];
                var bizActive = rawBizActive switch
                {
                    bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                    string sv => sv is "1" or "true" or "True", _ => true
                };

                assignments.Add((
                    r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5), userActive, r["BizName"]?.ToString() ?? "", bizActive
                ));
            }
        }

        if (assignments.Count == 0)
        {
            _logger.LogWarning("Login failed: no user found for {Email}", email);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        // Use the first active assignment for password verification
        var primary = assignments.FirstOrDefault(a => a.UserActive);
        if (primary.UserId == Guid.Empty)
        {
            _logger.LogWarning("Login failed: user {Email} is inactive", email);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        if (!VerifyPassword(req.Password, primary.PasswordHash))
        {
            _logger.LogWarning("Login failed: wrong password for {Email}", email);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        // Filter to only active user assignments with active businesses
        var activeBizIds = assignments
            .Where(a => a.UserActive && a.BizActive)
            .Select(a => a.BusinessId)
            .Distinct()
            .ToList();

        if (activeBizIds.Count == 0)
        {
            _logger.LogWarning("Login failed: no active businesses for {Email}", email);
            return Unauthorized(new { error = "No active businesses assigned" });
        }

        // Build business names list for UI
        var bizNames = assignments
            .Where(a => activeBizIds.Contains(a.BusinessId))
            .Select(a => new { id = a.BusinessId, name = a.BizName })
            .DistinctBy(x => x.id)
            .ToList();

        var token = _jwt.GenerateToken(primary.UserId, activeBizIds[0], primary.Role, primary.Email, activeBizIds);

        _logger.LogInformation("Login success: {Email} role={Role} businesses={Count}",
            primary.Email, primary.Role, activeBizIds.Count);

        return Ok(new
        {
            token,
            user = new
            {
                id = primary.UserId,
                businessId = activeBizIds[0],
                businessIds = activeBizIds,
                businessName = bizNames.FirstOrDefault()?.name ?? "",
                businesses = bizNames,
                name = primary.Name,
                email = primary.Email,
                role = primary.Role
            }
        });
    }

    internal static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var combined = new byte[16 + 32];
        Buffer.BlockCopy(salt, 0, combined, 0, 16);
        Buffer.BlockCopy(hash, 0, combined, 16, 32);
        return Convert.ToBase64String(combined);
    }

    internal static bool VerifyPassword(string password, string storedHash)
    {
        var combined = Convert.FromBase64String(storedHash);
        if (combined.Length != 48) return false;
        var salt = combined[..16];
        var storedKey = combined[16..];
        var testKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(testKey, storedKey);
    }
}
