using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Middleware;

/// <summary>
/// Validates the token version claim (tokVer) against the database.
/// If the user's TokenVersion has been incremented (password change, role change,
/// deactivation), any token with an older version is rejected with 401.
/// </summary>
public sealed class TokenVersionMiddleware
{
    private readonly RequestDelegate _next;

    public TokenVersionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var sub = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var tokVerClaim = context.User.FindFirstValue("tokVer");

            if (Guid.TryParse(sub, out var userId) && int.TryParse(tokVerClaim, out var tokVer))
            {
                var db = context.RequestServices.GetRequiredService<AppDbContext>();
                var current = await db.BusinessUsers
                    .AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.TokenVersion, u.IsActive })
                    .FirstOrDefaultAsync();

                if (current is null || !current.IsActive || current.TokenVersion != tokVer)
                {
                    Log.Warning("Token revoked: user={UserId} tokenVer={TokenVer} currentVer={CurrentVer} active={Active}",
                        userId, tokVer, current?.TokenVersion, current?.IsActive);

                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("""{"error":"Token revoked. Please log in again."}""");
                    return;
                }
            }
            else
            {
                // Fail closed: authenticated tokens without a valid tokVer claim must not bypass revocation
                Log.Warning("Token rejected: missing or invalid tokVer claim user={Sub}", sub);

                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"error":"Invalid token. Please log in again."}""");
                return;
            }
        }

        await _next(context);
    }
}
