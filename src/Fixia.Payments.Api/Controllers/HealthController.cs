using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Controllers;

[ApiController]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    [HttpGet("health")]
    [HttpGet("api/payments/health")]
    [HttpGet("actuator/health")]
    public IActionResult GetHealth()
    {
        return Ok(new
        {
            status = "UP",
            service = "ms-payments",
            framework = ".NET 8.0",
            timestamp = DateTime.UtcNow.ToString("o")
        });
    }
}

