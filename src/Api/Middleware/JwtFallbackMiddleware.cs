using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using WhatsAppSaaS.Api.Services;

namespace WhatsAppSaaS.Api.Middleware;

/// <summary>
/// Fallback JWT validation middleware. If the standard ASP.NET JWT Bearer middleware
/// did not authenticate the request but an Authorization: Bearer header is present,
/// this middleware manually validates the token and sets the ClaimsPrincipal.
/// Uses identical validation parameters as the primary JWT Bearer middleware.
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
                if (string.IsNullOrEmpty(rawToken) || rawToken is "null" or "undefined")
                {
                    // Bogus token from frontend — do not attempt validation
                    await _next(context);
                    return;
                }

                var jwtService = context.RequestServices.GetService<JwtService>();
                if (jwtService is null)
                {
                    await _next(context);
                    return;
                }

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
                        IssuerSigningKey = jwtService.GetSigningKey(),
                        ClockSkew = TimeSpan.FromSeconds(30)
                    }, out var validatedToken);

                    // Extra guard: ensure the token was signed with the expected algorithm
                    if (validatedToken is JwtSecurityToken jwt
                        && !jwt.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("JWT FALLBACK: rejected token with unexpected algorithm {Algorithm}", jwt.Header.Alg);
                        await _next(context);
                        return;
                    }

                    context.User = principal;
                    Log.Debug("JWT FALLBACK: validation succeeded for role={Role}",
                        principal.FindFirstValue(ClaimTypes.Role));
                }
                catch (SecurityTokenExpiredException)
                {
                    Log.Debug("JWT FALLBACK: token expired");
                    // Do not authenticate — let [Authorize] reject
                }
                catch (SecurityTokenException ex)
                {
                    Log.Debug("JWT FALLBACK: token rejected — {Reason}", ex.Message);
                    // Do not authenticate — let [Authorize] reject
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "JWT FALLBACK: unexpected validation error");
                    // Do not authenticate — let [Authorize] reject
                }
            }
        }

        await _next(context);
    }
}
