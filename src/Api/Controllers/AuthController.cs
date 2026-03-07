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

    public AuthController(AppDbContext db, JwtService jwt)
    {
        _db = db;
        _jwt = jwt;
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

        var user = await _db.BusinessUsers
            .Include(u => u.Business)
            .FirstOrDefaultAsync(u => u.Email == req.Email.Trim().ToLowerInvariant() && u.IsActive, ct);

        if (user is null || !VerifyPassword(req.Password, user.PasswordHash))
            return Unauthorized(new { error = "Invalid credentials" });

        if (user.Business is null || !user.Business.IsActive)
            return Unauthorized(new { error = "Business is inactive" });

        var token = _jwt.GenerateToken(user.Id, user.BusinessId, user.Role, user.Email);

        return Ok(new
        {
            token,
            user = new
            {
                user.Id,
                user.BusinessId,
                businessName = user.Business.Name,
                user.Name,
                user.Email,
                user.Role
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
