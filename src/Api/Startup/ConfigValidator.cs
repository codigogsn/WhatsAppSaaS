using Serilog;

namespace WhatsAppSaaS.Api.Startup;

/// <summary>
/// Logs warnings for missing critical production configuration.
/// </summary>
public static class ConfigValidator
{
    public static void WarnIfMissing(string environmentName)
    {
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN")))
            Log.Warning("CONFIG: WHATSAPP_VERIFY_TOKEN not set — webhook verification will reject all requests");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("META_ACCESS_TOKEN")))
            Log.Warning("CONFIG: No WhatsApp access token set (WHATSAPP_ACCESS_TOKEN / META_ACCESS_TOKEN)");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ADMIN_KEY")))
            Log.Warning("CONFIG: ADMIN_KEY not set — admin endpoints will reject all requests");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JWT_SECRET")))
            Log.Warning("CONFIG: JWT_SECRET not set — founder login will not work");
    }
}
