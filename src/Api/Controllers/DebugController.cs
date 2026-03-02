using Microsoft.AspNetCore.Mvc;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("debug")]
public class DebugController : ControllerBase
{
    [HttpGet("db")]
    public IActionResult Db([FromServices] AppDbContext db)
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

        return Ok(new
        {
            env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            databaseUrlPresent = !string.IsNullOrWhiteSpace(databaseUrl),
            databaseUrlHost = TryGetHost(databaseUrl),
            provider = db.Database.ProviderName
        });
    }

    private static string? TryGetHost(string? url)
    {
        try { return string.IsNullOrWhiteSpace(url) ? null : new Uri(url).Host; }
        catch { return "invalid DATABASE_URL"; }
    }
}

