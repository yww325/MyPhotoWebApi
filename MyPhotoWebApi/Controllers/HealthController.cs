using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Controllers
{
    [ApiController]
    public class HealthController : ControllerBase
    {
        private readonly MyPhotoSettings _settings;

        public HealthController(MyPhotoSettings settings)
        {
            _settings = settings;
        }

        // Keep this endpoint out of OData routes.
        [HttpGet("/health")]
        public async Task<IActionResult> Health(CancellationToken ct)
        {
            try
            {
                // Quick MongoDB ping with a small timeout.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));

                var client = new MongoClient(_settings.ConnectionString);
                var db = client.GetDatabase(_settings.DatabaseName);
                await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);

                return Ok(new { status = "ok", mongo = "ok" });
            }
            catch
            {
                // Do not leak connection details.
                return StatusCode(503, new { status = "degraded", mongo = "error" });
            }
        }
    }
}
