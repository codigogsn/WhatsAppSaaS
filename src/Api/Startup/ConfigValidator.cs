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

        var criticalVars = new Dictionary<string, string>
        {
            ["WHATSAPP_VERIFY_TOKEN"] = "webhook verification will reject all requests",
            ["WHATSAPP_PHONE_NUMBER_ID"] = "outbound messages will not work",
            ["ADMIN_KEY"] = "admin endpoints will reject all requests",
            ["JWT_SECRET"] = "founder login will not work",
        };

        // WhatsApp access token can be either name
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("META_ACCESS_TOKEN")))
            Log.Error("CONFIG MISSING: No WhatsApp access token set (WHATSAPP_ACCESS_TOKEN / META_ACCESS_TOKEN)");

        foreach (var (key, impact) in criticalVars)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                Log.Error("CONFIG MISSING: {Variable} not set — {Impact}", key, impact);
        }

        // SMTP (non-critical but warn)
        var smtpVars = new[] { "SMTP_USERNAME", "SMTP_PASSWORD", "SMTP_PORT", "SMTP_USE_SSL" };
        foreach (var v in smtpVars)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                Log.Warning("CONFIG: {Variable} not set — password reset emails may not work", v);
        }

        // Data Protection keys path (required for persistence)
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH")))
            Log.Error("CONFIG MISSING: DATA_PROTECTION_KEYS_PATH not set — keys will NOT persist across deploys");

        Log.Information("CONFIG CHECK: WhatsApp + SMTP configuration loaded");
    }
}
