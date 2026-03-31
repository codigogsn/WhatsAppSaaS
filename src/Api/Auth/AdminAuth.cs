using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace WhatsAppSaaS.Api.Auth;

/// <summary>
/// Shared JWT-first authorization helpers for admin controllers.
/// Pattern: try JWT claims first → fall back to X-Admin-Key for compatibility.
/// Roles: Owner > Manager > Staff (Operator).
/// </summary>
public static class AdminAuth
{
    // ── JWT claim extraction ──

    public static Guid? GetBusinessId(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue("businessId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// Returns all business IDs from the JWT "businessIds" claim (comma-separated).
    /// Falls back to the single "businessId" claim if "businessIds" is absent.
    /// </summary>
    public static List<Guid> GetBusinessIds(ClaimsPrincipal user)
    {
        var multiClaim = user.FindFirstValue("businessIds");
        if (!string.IsNullOrWhiteSpace(multiClaim))
        {
            return multiClaim.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
        }

        var single = GetBusinessId(user);
        return single.HasValue ? [single.Value] : [];
    }

    public static string? GetRole(ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.Role);

    public static bool IsOwner(ClaimsPrincipal user)
        => GetRole(user) == "Owner";

    public static bool IsOwnerOrManager(ClaimsPrincipal user)
        => GetRole(user) is "Owner" or "Manager";

    // ── JWT business-scoped checks ──

    /// <summary>
    /// Returns true if the JWT user has at least the given role and is scoped
    /// to the requested business. Checks both single businessId and multi businessIds claims.
    /// </summary>
    public static bool IsJwtAuthorizedForBusiness(ClaimsPrincipal user, Guid businessId)
    {
        if (!IsOwnerOrManager(user)) return false;
        var bizIds = GetBusinessIds(user);
        return bizIds.Contains(businessId);
    }

    /// <summary>
    /// Returns true if the JWT user has Owner/Manager role. Used for endpoints
    /// where business scoping is applied separately (e.g., overriding businessId
    /// from JWT claims).
    /// </summary>
    public static bool HasJwtAdminAccess(ClaimsPrincipal user)
        => IsOwnerOrManager(user);

    // ── X-Admin-Key fallback ──

    public static bool IsGlobalAdminKey(HttpRequest request, IConfiguration config)
    {
        if (!request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;

        var key = hk.ToString().Trim();

        // Current global key
        var globalKey = (config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY"))?.Trim();
        if (!string.IsNullOrWhiteSpace(globalKey) && SafeEquals(key, globalKey))
            return true;

        // Legacy global key sources
        string?[] legacySources =
        [
            Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY"),
            config["WhatsApp:AdminKey"],
        ];
        foreach (var src in legacySources)
        {
            if (!string.IsNullOrWhiteSpace(src) && SafeEquals(key, src.Trim()))
                return true;
        }

        return false;
    }

    // ── Unified checks ──

    /// <summary>
    /// JWT-first auth (Owner/Manager with matching businessId) OR X-Admin-Key fallback.
    /// Use for business-scoped endpoints.
    /// </summary>
    public static bool IsAuthorizedForBusiness(ClaimsPrincipal user, HttpRequest request,
        IConfiguration config, Guid businessId)
    {
        // Path 1: JWT with business scope
        if (IsJwtAuthorizedForBusiness(user, businessId))
            return true;

        // Path 2: Global admin key
        if (IsGlobalAdminKey(request, config))
            return true;

        return false;
    }

    /// <summary>
    /// JWT-first auth (Owner/Manager) OR global X-Admin-Key fallback.
    /// Use for global/unscoped admin endpoints.
    /// </summary>
    public static bool IsAuthorized(ClaimsPrincipal user, HttpRequest request, IConfiguration config)
    {
        // Path 1: JWT admin access
        if (HasJwtAdminAccess(user))
            return true;

        // Path 2: Global admin key
        if (IsGlobalAdminKey(request, config))
            return true;

        return false;
    }

    /// <summary>
    /// For JWT users, overrides the businessId with their JWT business scope.
    /// Returns the effective businessId to use for queries.
    /// </summary>
    public static Guid? ScopeBusinessId(ClaimsPrincipal user, Guid? requestedBusinessId)
    {
        var jwtBizId = GetBusinessId(user);
        return jwtBizId ?? requestedBusinessId;
    }

    // ── Crypto ──

    public static bool SafeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
