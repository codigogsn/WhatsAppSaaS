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
        _secret = config["Jwt:Secret"]
                   ?? Environment.GetEnvironmentVariable("JWT_SECRET")
                   ?? throw new InvalidOperationException("JWT_SECRET or Jwt:Secret is required");
        _issuer = config["Jwt:Issuer"] ?? "WhatsAppSaaS";
        _expirationHours = int.TryParse(config["Jwt:ExpirationHours"], out var h) ? h : 24;
    }

    public string GenerateToken(Guid userId, Guid businessId, string role, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("businessId", businessId.ToString()),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

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
