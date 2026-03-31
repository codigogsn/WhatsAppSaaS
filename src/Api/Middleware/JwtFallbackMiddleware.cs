using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using WhatsAppSaaS.Api.Services;

namespace WhatsAppSaaS.Api.Middleware;

/// <summary>
/// Fallback JWT validation middleware. If the standard ASP.NET JWT Bearer middleware
/// did not authenticate the request but an Authorization: Bearer header is present,
/// this middleware manually validates the token and sets the ClaimsPrincipal.
/// This handles edge cases where the middleware silently fails (key size, config issues).
/// </summary>
public sealed class JwtFallbackMiddleware
{
    private readonly RequestDelegate _next;

    public JwtFallbackMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only act if standard middleware didn't authenticate AND we have a Bearer token
        if (context.User.Identity?.IsAuthenticated != true
            && context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var headerValue = authHeader.ToString();
            if (headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var rawToken = headerValue["Bearer ".Length..].Trim();
                if (!string.IsNullOrEmpty(rawToken) && rawToken != "null" && rawToken != "undefined")
                {
                    var jwtService = context.RequestServices.GetService<JwtService>();
                    if (jwtService != null)
                    {
                        try
                        {
                            var handler = new JwtSecurityTokenHandler();
                            var principal = handler.ValidateToken(rawToken, new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidateAudience = true,
                                ValidateLifetime = true,
                                ValidateIssuerSigningKey = true,
                                ValidIssuer = jwtService.Issuer,
                                ValidAudience = jwtService.Issuer,
                                IssuerSigningKey = jwtService.GetSigningKey()
                            }, out _);

                            context.User = principal;
                            Console.WriteLine($"[JWT FALLBACK] Manual validation succeeded for {principal.FindFirstValue(JwtRegisteredClaimNames.Email)} role={principal.FindFirstValue(ClaimTypes.Role)}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[JWT FALLBACK] Manual validation failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
        }

        await _next(context);
    }
}
