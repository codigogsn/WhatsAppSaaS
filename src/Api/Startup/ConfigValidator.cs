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
            ["WHATSAPP_APP_SECRET"] = "webhook signature validation will reject ALL inbound messages (required in production)",
            ["ADMIN_KEY"] = "admin endpoints will reject all requests",
            ["JWT_SECRET"] = "founder login will not work",
        };

        var missing = new List<string>();

        // WhatsApp access token can be either name
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("META_ACCESS_TOKEN")))
        {
            Log.Error("CONFIG MISSING: No WhatsApp access token set (WHATSAPP_ACCESS_TOKEN / META_ACCESS_TOKEN)");
            missing.Add("WHATSAPP_ACCESS_TOKEN");
        }

        foreach (var (key, impact) in criticalVars)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                Log.Error("CONFIG MISSING: {Variable} not set — {Impact}", key, impact);
                missing.Add(key);
            }
        }

        // SMTP (non-critical but warn)
        var smtpVars = new[] { "SMTP_USERNAME", "SMTP_PASSWORD", "SMTP_PORT", "SMTP_USE_SSL" };
        foreach (var v in smtpVars)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                Log.Warning("CONFIG: {Variable} not set — password reset emails may not work", v);
        }

        // AI parser (non-critical — fallback regex parser will be used)
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            Log.Warning("CONFIG: OPENAI_API_KEY not set — AI parsing disabled, fallback regex parser will handle orders");

        // Data Protection keys path (required for persistence)
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH")))
            Log.Error("CONFIG MISSING: DATA_PROTECTION_KEYS_PATH not set — keys will NOT persist across deploys");

        Log.Information("CONFIG CHECK: WhatsApp + SMTP configuration loaded");

        // Fail fast in production if critical vars are missing
        if (missing.Count > 0)
        {
            var msg = $"FATAL: cannot start in Production — missing critical env vars: {string.Join(", ", missing)}";
            Log.Fatal(msg);
            throw new InvalidOperationException(msg);
        }
    }
}
