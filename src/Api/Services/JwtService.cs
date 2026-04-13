using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WhatsAppSaaS.Api.Services;

public sealed class JwtService
{
    private readonly string[] _secrets;
    private readonly string _issuer;
    private readonly int _expirationHours;

    public JwtService(IConfiguration config)
    {
        // Resolve secret(s): prefer config, fall back to env var, reject empty/whitespace.
        // Supports comma-separated secrets for rotation: sign with first, validate against all.
        var configSecret = config["Jwt:Secret"];
        var envSecret = Environment.GetEnvironmentVariable("JWT_SECRET");

        var raw = !string.IsNullOrWhiteSpace(configSecret) ? configSecret.Trim()
                : !string.IsNullOrWhiteSpace(envSecret) ? envSecret.Trim()
                : throw new InvalidOperationException("JWT secret is required. Set Jwt:Secret in config or JWT_SECRET env var.");

        _secrets = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(s => s.Length > 0)
                      .ToArray();

        if (_secrets.Length == 0)
            throw new InvalidOperationException("JWT secret is required. Set Jwt:Secret in config or JWT_SECRET env var.");

        _issuer = config["Jwt:Issuer"] ?? "WhatsAppSaaS";
        _expirationHours = int.TryParse(config["Jwt:ExpirationHours"], out var h) ? h : 24;

        // Safe diagnostic — never log the actual secret
        var source = !string.IsNullOrWhiteSpace(configSecret) ? "Jwt:Secret config" : "JWT_SECRET env";
        Serilog.Log.Debug("JwtService: secret source={Source} keyCount={Count} primaryLength={Length}",
            source, _secrets.Length, _secrets[0].Length);
    }

    public string GenerateToken(Guid userId, Guid businessId, string role, string email,
        IEnumerable<Guid>? allBusinessIds = null, int tokenVersion = 0)
    {
        // Always sign with the first (primary) secret
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secrets[0]));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("businessId", businessId.ToString()),
            new(ClaimTypes.Role, role),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("tokVer", tokenVersion.ToString()),
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

    /// <summary>
    /// Returns the primary signing key (first secret).
    /// </summary>
    public SymmetricSecurityKey GetSigningKey() =>
        new(Encoding.UTF8.GetBytes(_secrets[0]));

    /// <summary>
    /// Returns all configured signing keys for token validation.
    /// Enables graceful secret rotation: sign with first, validate against all.
    /// </summary>
    public IEnumerable<SecurityKey> GetAllSigningKeys() =>
        _secrets.Select(s => new SymmetricSecurityKey(Encoding.UTF8.GetBytes(s)));

    public string Issuer => _issuer;
}
