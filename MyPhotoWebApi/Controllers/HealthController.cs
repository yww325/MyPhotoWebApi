using Microsoft.AspNetCore.Mvc;

namespace MyPhotoWebApi.Controllers
{
    [ApiController]
    [ApiVersion("1.0")]
    public class HealthController : ControllerBase
    {
        // Keep this endpoint out of OData routes and make it version-explicit.
        [HttpGet("/health")]
        public IActionResult Health()
        {
            return Ok(new { status = "ok" });
        }
    }
}
