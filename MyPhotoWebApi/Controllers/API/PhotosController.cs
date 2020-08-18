using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MyPhotoWebApi.Services;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Controllers.API
{
    [ApiVersion("1.0")] // can be removed, default version
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiController]
    public class PhotosController : ControllerBase
    {
        private readonly PhotoService _photoService;

        public PhotosController(PhotoService photoService)
        {
            _photoService = photoService; 
        }

        [HttpPatch("private")]
        [ProducesResponseType(StatusCodes.Status200OK)] 
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> MarkPrivate([FromHeader, Required, BindRequired] string userPass, [Required, BindRequired] string path, bool toPrivate = true)
        {
            if (userPass != Startup.HashedUserPass) return Unauthorized(); 
            var ret = await _photoService.MarkPrivate(path, toPrivate);
            if (ret)
            {
                return Ok(path);
            }
            else
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, "MarkPrivate failed");
            }
        }

        [HttpPatch("privateById")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> MarkPrivateById([FromHeader, Required, BindRequired] string userPass, [Required, BindRequired] string id, bool toPrivate = true)
        {
            if (userPass != Startup.HashedUserPass) return Unauthorized(); 
            var ret = await _photoService.MarkPrivateById(id, toPrivate);
            if (ret)
            {
                return Ok(id);
            }
            else
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, "MarkPrivateById failed");
            }
        }

        [HttpPatch("move")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> Move([FromHeader, Required, BindRequired] string userPass, [Required, FromBody, BindRequired] string[] ids, string folderId)
        {
            if (userPass != Startup.HashedUserPass) return Unauthorized();
            if (ids == null || ids.Length == 0)
            {
                return BadRequest("need at least one photo id");
            }

            var ret = await _photoService.MovePhotos(ids, folderId);
            if (ret)
            {
                return Ok("Move photo success");
            }
            else
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, "Move photo failed");
            }
        }

    }
}
