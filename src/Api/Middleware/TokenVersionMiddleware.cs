using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Middleware;

/// <summary>
/// Validates token version and business membership on every authenticated request.
/// 1. Rejects tokens with missing/invalid tokVer (fail-closed).
/// 2. Rejects tokens where the primary assignment is revoked/inactive.
/// 3. Rejects tokens whose claimed businessIds include memberships the user no longer holds.
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
                    .Select(u => new { u.TokenVersion, u.IsActive, u.PasswordHash })
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

                // Validate all claimed business memberships are still active.
                // The JWT may list multiple businessIds; each must have a live,
                // active assignment with the same password hash (same identity).
                var bizIdsClaim = context.User.FindFirstValue("businessIds");
                if (!string.IsNullOrWhiteSpace(bizIdsClaim))
                {
                    var claimedIds = bizIdsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : Guid.Empty)
                        .Where(g => g != Guid.Empty)
                        .Distinct()
                        .ToList();

                    if (claimedIds.Count > 0)
                    {
                        var activeCount = await db.BusinessUsers
                            .AsNoTracking()
                            .CountAsync(u => claimedIds.Contains(u.BusinessId)
                                && u.IsActive
                                && u.PasswordHash == current.PasswordHash
                                && u.Business != null && u.Business.IsActive);

                        if (activeCount < claimedIds.Count)
                        {
                            Log.Warning("Business membership stale: user={UserId} claimed={Claimed} active={Active}",
                                userId, claimedIds.Count, activeCount);

                            context.Response.StatusCode = 401;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync("""{"error":"Business access changed. Please log in again."}""");
                            return;
                        }
                    }
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
