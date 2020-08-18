using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Controllers.API
{
    [ApiVersion("1.0")] // can be removed, default version
    [Route("api/v{version:apiVersion}/[controller]")]  
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
        public async Task<ActionResult> Ingest([FromHeader, Required, BindRequired]string userPass,[FromBody] IngestBody body)
        {
            if (userPass != Startup.HashedUserPass) return Unauthorized();
            var result = await _fileIngestionService.Ingest(body.IngestFolder, body.Recursive);
            return Ok(result); 
        }

        [HttpGet("validate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult Validate([FromQuery, Required, BindRequired]string userPass)
        {
            if (MD5Helper.MD5Hash(userPass) != Startup.HashedUserPass) return Unauthorized();
            return Ok(Startup.HashedUserPass);
        } 
    }
}
