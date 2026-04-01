using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("login")]
public class PasswordResetController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PasswordResetController> _logger;

    public PasswordResetController(AppDbContext db, IConfiguration config, ILogger<PasswordResetController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public sealed class ForgotPasswordRequest { public string Email { get; set; } = ""; }
    public sealed class ResetPasswordRequest
    {
        public string Token { get; set; } = "";
        public string NewPassword { get; set; } = "";
    }

    // POST /api/auth/forgot-password
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req, CancellationToken ct)
    {
        // Always return generic response regardless of whether email exists
        var genericResponse = new { message = "If the account exists, a reset link has been sent." };

        if (string.IsNullOrWhiteSpace(req.Email))
            return Ok(genericResponse);

        var email = req.Email.Trim().ToLowerInvariant();
        _logger.LogInformation("Password reset requested for {Email}", email);

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // Find user by email
            Guid? userId = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """SELECT "Id" FROM "BusinessUsers" WHERE "Email" = @email AND "IsActive"::boolean = true LIMIT 1""";
                var p = cmd.CreateParameter(); p.ParameterName = "email"; p.Value = email; cmd.Parameters.Add(p);
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result is Guid g) userId = g;
                else if (result is not null && result is not DBNull && Guid.TryParse(result.ToString(), out var parsed))
                    userId = parsed;
            }

            if (!userId.HasValue)
            {
                _logger.LogInformation("Password reset: no active user found for email (not revealing to client)");
                return Ok(genericResponse);
            }

            // Generate secure token
            var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var tokenHash = HashToken(rawToken);
            var expiresAt = DateTime.UtcNow.AddHours(1);

            // Invalidate any existing unused tokens for this user
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """UPDATE "PasswordResetTokens" SET "UsedAtUtc" = @now WHERE "UserId" = @uid AND "UsedAtUtc" IS NULL""";
                var p1 = cmd.CreateParameter(); p1.ParameterName = "now"; p1.Value = DateTime.UtcNow; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "uid"; p2.Value = userId.Value; cmd.Parameters.Add(p2);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Store hashed token
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO "PasswordResetTokens" ("Id", "UserId", "TokenHash", "ExpiresAtUtc", "CreatedAtUtc")
                    VALUES (@id, @uid, @hash, @expires, @created)
                """;
                var p1 = cmd.CreateParameter(); p1.ParameterName = "id"; p1.Value = Guid.NewGuid(); cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "uid"; p2.Value = userId.Value; cmd.Parameters.Add(p2);
                var p3 = cmd.CreateParameter(); p3.ParameterName = "hash"; p3.Value = tokenHash; cmd.Parameters.Add(p3);
                var p4 = cmd.CreateParameter(); p4.ParameterName = "expires"; p4.Value = expiresAt; cmd.Parameters.Add(p4);
                var p5 = cmd.CreateParameter(); p5.ParameterName = "created"; p5.Value = DateTime.UtcNow; cmd.Parameters.Add(p5);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Send email
            var baseUrl = Environment.GetEnvironmentVariable("APP_BASE_URL")?.TrimEnd('/')
                          ?? _config["APP_BASE_URL"]?.TrimEnd('/')
                          ?? $"{Request.Scheme}://{Request.Host}";
            var resetUrl = $"{baseUrl}/reset-password.html?token={Uri.EscapeDataString(rawToken)}";

            await SendResetEmailAsync(email, resetUrl);
            _logger.LogInformation("Password reset email sent for user {UserId}", userId.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password reset flow failed for {Email}", email);
            // Still return generic response — do not reveal internal errors
        }

        return Ok(genericResponse);
    }

    // POST /api/auth/reset-password
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { error = "Token is required" });

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest(new { error = "Password must be at least 6 characters" });

        var tokenHash = HashToken(req.Token);

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // Find valid token
            Guid? tokenId = null;
            Guid? userId = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT "Id", "UserId" FROM "PasswordResetTokens"
                    WHERE "TokenHash" = @hash AND "UsedAtUtc" IS NULL AND "ExpiresAtUtc" > @now
                    LIMIT 1
                """;
                var p1 = cmd.CreateParameter(); p1.ParameterName = "hash"; p1.Value = tokenHash; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "now"; p2.Value = DateTime.UtcNow; cmd.Parameters.Add(p2);
                using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    tokenId = r.GetGuid(0);
                    userId = r.GetGuid(1);
                }
            }

            if (!tokenId.HasValue || !userId.HasValue)
            {
                _logger.LogWarning("Invalid or expired reset token submitted");
                return BadRequest(new { error = "Invalid or expired reset link. Please request a new one." });
            }

            // Hash new password
            var newHash = WhatsAppSaaS.Api.Controllers.AuthController.HashPassword(req.NewPassword);

            // Update password for ALL business assignments of this user (same email)
            string? userEmail = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """SELECT "Email" FROM "BusinessUsers" WHERE "Id" = @uid LIMIT 1""";
                var p = cmd.CreateParameter(); p.ParameterName = "uid"; p.Value = userId.Value; cmd.Parameters.Add(p);
                userEmail = (await cmd.ExecuteScalarAsync(ct))?.ToString();
            }

            if (userEmail != null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """UPDATE "BusinessUsers" SET "PasswordHash" = @hash WHERE "Email" = @email""";
                var p1 = cmd.CreateParameter(); p1.ParameterName = "hash"; p1.Value = newHash; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "email"; p2.Value = userEmail; cmd.Parameters.Add(p2);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Mark token as used
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """UPDATE "PasswordResetTokens" SET "UsedAtUtc" = @now WHERE "Id" = @id""";
                var p1 = cmd.CreateParameter(); p1.ParameterName = "now"; p1.Value = DateTime.UtcNow; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "id"; p2.Value = tokenId.Value; cmd.Parameters.Add(p2);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            _logger.LogInformation("Password reset completed for user {UserId}", userId.Value);
            return Ok(new { message = "Password has been reset successfully. You can now log in." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password reset submission failed");
            return StatusCode(500, new { error = "An error occurred. Please try again." });
        }
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    private async Task SendResetEmailAsync(string toEmail, string resetUrl)
    {
        var host = Environment.GetEnvironmentVariable("SMTP_HOST") ?? _config["SMTP_HOST"] ?? "";
        var port = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT") ?? _config["SMTP_PORT"], out var p) ? p : 587;
        var username = Environment.GetEnvironmentVariable("SMTP_USERNAME") ?? _config["SMTP_USERNAME"] ?? "";
        var password = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? _config["SMTP_PASSWORD"] ?? "";
        var fromEmail = Environment.GetEnvironmentVariable("SMTP_FROM_EMAIL") ?? _config["SMTP_FROM_EMAIL"] ?? username;
        var fromName = Environment.GetEnvironmentVariable("SMTP_FROM_NAME") ?? _config["SMTP_FROM_NAME"] ?? "CODIGO";
        var useSsl = (Environment.GetEnvironmentVariable("SMTP_USE_SSL") ?? _config["SMTP_USE_SSL"] ?? "true")
                     .Equals("true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username))
        {
            _logger.LogWarning("SMTP not configured — password reset email not sent");
            return;
        }

        var subject = "Reset your CODIGO password";
        var htmlBody = $"""
            <div style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:480px;margin:0 auto;padding:32px 24px;">
              <div style="font-size:18px;font-weight:700;letter-spacing:3px;margin-bottom:24px;color:#1D1D1F;">CODIGO</div>
              <p style="font-size:15px;color:#333;line-height:1.6;">You requested a password reset for your account.</p>
              <p style="font-size:15px;color:#333;line-height:1.6;">Click the button below to set a new password. This link expires in 1 hour.</p>
              <div style="text-align:center;margin:32px 0;">
                <a href="{resetUrl}" style="display:inline-block;padding:12px 32px;background:#007AFF;color:#fff;text-decoration:none;border-radius:10px;font-size:14px;font-weight:600;">Reset Password</a>
              </div>
              <p style="font-size:12px;color:#999;line-height:1.5;">If you didn't request this, you can safely ignore this email.</p>
              <p style="font-size:12px;color:#999;line-height:1.5;">Or copy this link: {resetUrl}</p>
            </div>
            """;
        var textBody = $"Reset your CODIGO password.\n\nClick this link to set a new password (expires in 1 hour):\n{resetUrl}\n\nIf you didn't request this, ignore this email.";

        using var client = new SmtpClient(host, port)
        {
            Credentials = new NetworkCredential(username, password),
            EnableSsl = useSsl
        };

        var msg = new MailMessage
        {
            From = new MailAddress(fromEmail, fromName),
            Subject = subject,
            IsBodyHtml = true,
            Body = htmlBody
        };
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));
        msg.To.Add(toEmail);

        await client.SendMailAsync(msg);
    }
}
