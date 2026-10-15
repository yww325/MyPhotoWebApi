using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Services;
using System.Threading;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using MyPhotoWebApi.Models;

namespace MyPhotoWebApi.Controllers.API
{
    [ApiVersion("1.0")] // can be removed, default version
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiController]
    public class PhotosController : ControllerBase
    {
        private readonly PhotoService _photoService;
        private readonly FileIngestionService _fileIngestionService;

        // Nginx allows 1 GB per request (client_max_body_size 1G); match it here.
        private const long MaxUploadBytes = 1L << 30;

        public PhotosController(PhotoService photoService, FileIngestionService fileIngestionService)
        {
            _photoService = photoService; 
            _fileIngestionService = fileIngestionService;
        }

        /// <summary>
        /// Uploads one or more files into a folder (created if missing). Send multipart/form-data with a
        /// "folder" field such as "2026/Trip" and one or more "files" parts. Every file gets its own result.
        /// </summary>
        [HttpPost("upload")]
        [RequireUserPass]
        [RequestSizeLimit(MaxUploadBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(UploadResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Upload(
            [FromHeader, Required, BindRequired] string userPass,
            [FromForm, Required, BindRequired] string folder,
            [FromForm] List<IFormFile> files,
            CancellationToken ct)
        {
            if (files == null || files.Count == 0) return BadRequest("need at least one file in the \"files\" field");
            return await Util.ResponseHelper(() => _fileIngestionService.Upload(folder, files, ct));
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

        [HttpGet("thumbs")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<List<Photo>>> GetThumbs(
            [FromHeader, Required, BindRequired] string userPass,
            [Required, BindRequired] string pathPrefix,
            int top = 100,
            int skip = 0)
        {
            // We do NOT use OData $select here because it triggers Mongo LINQ translation errors.
            var photos = await _photoService.GetThumbPage(userPass, pathPrefix, top, skip);
            return Ok(photos);
        }

    }
}
