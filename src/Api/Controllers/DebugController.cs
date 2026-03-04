using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("debug")]
public class DebugController : ControllerBase
{
    [HttpGet("version")]
    public IActionResult Version()
    {
        var sha = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
                  ?? Environment.GetEnvironmentVariable("GIT_COMMIT")
                  ?? "unknown";

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? "unknown";

        return Ok(new
        {
            commit = sha,
            aspnetcore_environment = env,
            utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        });
    }
}
