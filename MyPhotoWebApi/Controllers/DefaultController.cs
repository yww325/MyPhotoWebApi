using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;

namespace MyPhotoWebApi.Controllers
{ 
    [ApiVersion("1.0")] // can be removed, default version
    //[Route("api/v{version:apiVersion}/[controller]")] // will use if we have future version
    [Route("api/[controller]")]
    [ApiController]
    public class DefaultController : ControllerBase
    {
        private readonly FileIngestionService _fileIngestionService;

        public DefaultController(FileIngestionService fileIngestionService)
        {
            _fileIngestionService = fileIngestionService;
        }
   
        // POST: api/Default
        [HttpPost("ingest")]
        [ProducesResponseType(typeof(IngestResult), StatusCodes.Status200OK)]
        public async Task<ActionResult> Ingest([FromBody] IngestBody body)
        {
            var result = await _fileIngestionService.Ingest(body.IngestFolder, body.Recursive);
            return Ok(result); 
        }

    }
}
