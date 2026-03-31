using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WhatsAppSaaS.Api.Services;

public sealed class JwtService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly int _expirationHours;

    public JwtService(IConfiguration config)
    {
        // Resolve secret: prefer config, fall back to env var, reject empty/whitespace
        var configSecret = config["Jwt:Secret"];
        var envSecret = Environment.GetEnvironmentVariable("JWT_SECRET");

        _secret = !string.IsNullOrWhiteSpace(configSecret) ? configSecret.Trim()
                : !string.IsNullOrWhiteSpace(envSecret) ? envSecret.Trim()
                : throw new InvalidOperationException("JWT secret is required. Set Jwt:Secret in config or JWT_SECRET env var.");

        _issuer = config["Jwt:Issuer"] ?? "WhatsAppSaaS";
        _expirationHours = int.TryParse(config["Jwt:ExpirationHours"], out var h) ? h : 24;

        // Safe diagnostic — never log the actual secret
        var source = !string.IsNullOrWhiteSpace(configSecret) ? "Jwt:Secret config" : "JWT_SECRET env";
        Console.WriteLine($"[JwtService] secret source = {source}, length = {_secret.Length}");
    }

    public string GenerateToken(Guid userId, Guid businessId, string role, string email,
        IEnumerable<Guid>? allBusinessIds = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("businessId", businessId.ToString()),
            new(ClaimTypes.Role, role),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        // Multi-business claim: comma-separated list of all assigned business IDs
        if (allBusinessIds is not null)
        {
            var ids = string.Join(",", allBusinessIds.Distinct());
            claims.Add(new Claim("businessIds", ids));
        }

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _issuer,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_expirationHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public SymmetricSecurityKey GetSigningKey() =>
        new(Encoding.UTF8.GetBytes(_secret));

    public string Issuer => _issuer;
}
