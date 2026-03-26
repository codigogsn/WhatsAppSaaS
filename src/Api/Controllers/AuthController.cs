using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Services;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
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

        Guid userId = Guid.Empty;
        Guid businessId = Guid.Empty;
        string passwordHash = "";
        string userName = "";
        string userEmail = "";
        string userRole = "";
        bool userActive = false;
        string businessName = "";
        bool businessActive = false;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT u."Id", u."BusinessId", u."PasswordHash", u."Name", u."Email", u."Role", u."IsActive",
                       b."Name" AS "BizName", b."IsActive" AS "BizActive"
                FROM "BusinessUsers" u
                INNER JOIN "Businesses" b ON b."Id" = u."BusinessId"
                WHERE u."Email" = @email
                LIMIT 1
            """;
            var p = cmd.CreateParameter();
            p.ParameterName = "email";
            p.Value = email;
            cmd.Parameters.Add(p);

            using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                _logger.LogWarning("Login failed: no user found for {Email}", email);
                return Unauthorized(new { error = "Invalid credentials" });
            }

            userId = r.GetGuid(0);
            businessId = r.GetGuid(1);
            passwordHash = r.GetString(2);
            userName = r.GetString(3);
            userEmail = r.GetString(4);
            userRole = r.GetString(5);

            var rawActive = r["IsActive"];
            userActive = rawActive switch
            {
                bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                string sv => sv is "1" or "true" or "True", _ => true
            };

            businessName = r["BizName"]?.ToString() ?? "";
            var rawBizActive = r["BizActive"];
            businessActive = rawBizActive switch
            {
                bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                string sv => sv is "1" or "true" or "True", _ => true
            };
        }

        if (!userActive)
        {
            _logger.LogWarning("Login failed: user {Email} is inactive", email);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        if (!VerifyPassword(req.Password, passwordHash))
        {
            _logger.LogWarning("Login failed: wrong password for {Email}", email);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        if (!businessActive)
        {
            _logger.LogWarning("Login failed: business {BizId} is inactive for {Email}", businessId, email);
            return Unauthorized(new { error = "Business is inactive" });
        }

        var token = _jwt.GenerateToken(userId, businessId, userRole, userEmail);

        _logger.LogInformation("Login success: {Email} role={Role} bizId={BizId}", userEmail, userRole, businessId);

        return Ok(new
        {
            token,
            user = new
            {
                id = userId,
                businessId,
                businessName,
                name = userName,
                email = userEmail,
                role = userRole
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
