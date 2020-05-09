using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;

namespace MyPhotoWebApi.Controllers
{ 
    [ApiVersion("1.0")] // can be removed, default version
    [Route("api/v{version:apiVersion}/[controller]")]  
    [ApiController]
    public class DefaultController : ControllerBase
    {
        private readonly FileIngestionService _fileIngestionService;
        private readonly PhotoService _photoService;

        public DefaultController(FileIngestionService fileIngestionService, PhotoService photoService)
        {
            _fileIngestionService = fileIngestionService;
            _photoService = photoService;
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

        [HttpPatch("private")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult> MarkPrivate([FromHeader, Required, BindRequired]string userPass, string path)
        {
            if (userPass != Startup.HashedUserPass) return Unauthorized();
            var ret = await _photoService.MarkPrivate(path);
            if (ret)
            {
                return Ok();
            }
            else
            {
                return BadRequest();
            }
        }

    }
}
