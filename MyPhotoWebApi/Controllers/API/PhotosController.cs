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
using System.Linq;
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
        private readonly AiTagService _aiTagService;

        // Nginx allows 1 GB per request (client_max_body_size 1G); match it here.
        private const long MaxUploadBytes = 1L << 30;

        public PhotosController(PhotoService photoService, FileIngestionService fileIngestionService, AiTagService aiTagService)
        {
            _photoService = photoService; 
            _fileIngestionService = fileIngestionService;
            _aiTagService = aiTagService;
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

        /// <summary>
        /// Permanently deletes photos: each file is removed from disk, then its database record.
        /// Send the photo ids as a JSON array. Every id gets its own result: deleted, notFound or failed.
        /// </summary>
        [HttpDelete]
        [RequireUserPass]
        [ProducesResponseType(typeof(PhotoDeleteResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Delete([FromHeader, Required, BindRequired] string userPass, [FromBody] string[] ids)
        {
            if (ids == null || ids.Length == 0) return BadRequest("need at least one photo id");
            return await Util.ResponseHelper(() => _photoService.DeletePhotos(ids));
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

        /// <summary>
        /// Moves photos into a folder given by path ("" is the root; others are created if missing), both the files
        /// and their records; dateTaken is kept. A name already used there gets " (n)" added. Send
        /// {"folder": "...", "ids": [...]}. Every id gets its own result: moved, notFound or failed.
        /// </summary>
        [HttpPost("move-to")]
        [RequireUserPass]
        [ProducesResponseType(typeof(PhotoMoveResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> MoveTo([FromHeader, Required, BindRequired] string userPass, [FromBody] PhotoMoveRequest request)
        {
            if (request?.Ids == null || request.Ids.Length == 0) return BadRequest("need at least one photo id");
            return await Util.ResponseHelper(() => _photoService.MovePhotosTo(request.Folder ?? "", request.Ids));
        }

        /// <summary>
        /// Asks the vision model for up to 5 tags per photo and stores them after the folder-name tags. Photos that already
        /// have AI tags are skipped unless regenerate is true, which replaces them. Send {"ids": [...], "regenerate": false},
        /// at most 50 ids; calls to the model are paced, so a request can take a while. Every id gets its own result:
        /// tagged, skipped, notFound or failed. Returns 503 when no API key is configured.
        /// </summary>
        [HttpPost("ai-tags")]
        [RequireUserPass]
        [ProducesResponseType(typeof(AiTagResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> AiTags([FromHeader, Required, BindRequired] string userPass, [FromBody] AiTagRequest request, CancellationToken ct)
        {
            if (request?.Ids == null || request.Ids.Length == 0) return BadRequest("need at least one photo id");
            if (request.Ids.Length > AiTagService.MaxIdsPerRequest) return BadRequest($"at most {AiTagService.MaxIdsPerRequest} photo ids per request");
            if (!_aiTagService.IsConfigured) return StatusCode(StatusCodes.Status503ServiceUnavailable, "AI tagging is not configured on this server");
            return await Util.ResponseHelper(() => _aiTagService.TagPhotos(request.Ids, request.Regenerate, ct));
        }

        /// <summary>
        /// Replaces a photo's tags other than its folder names (AI or hand-added) with the given ones; the folder-name tags
        /// always stay. Send {"id": "...", "tags": [...]}, at most 20 tags of up to 20 characters; an empty array removes
        /// them all. Returns the photo's id and all its tags.
        /// </summary>
        [HttpPut("tags")]
        [RequireUserPass]
        [ProducesResponseType(typeof(PhotoTags), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> SetTags([FromHeader, Required, BindRequired] string userPass, [FromBody] PhotoTags request)
        {
            if (string.IsNullOrEmpty(request?.Id) || request.Tags == null) return BadRequest("need a photo id and a tags array");
            return await Util.ResponseHelper(() => _photoService.SetExtraTags(request.Id, request.Tags));
        }

        /// <summary>
        /// Older move by target folder id; stops at the first failure and reports only overall success.
        /// Kept unchanged for existing clients; new clients should use move-to.
        /// </summary>
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

        /// <summary>
        /// Lists one page of the files in a folder, with thumbnails and file sizes; the X-Total-Count response header
        /// holds the number of matching files. orderBy is "date" (default), "name" or "size"; descending defaults to true;
        /// recursive also lists the files of all subfolders and orders them together.
        /// pathPrefix is the folder path; empty or missing means the root folder.
        /// tags filters by tag: words separated by spaces, each of which must be part of one of the file's tags (ignoring case).
        /// </summary>
        [HttpGet("thumbs")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<List<PhotoThumb>>> GetThumbs(
            [FromHeader, Required, BindRequired] string userPass,
            string? pathPrefix,
            int top = 100,
            int skip = 0,
            string orderBy = "date",
            bool descending = true,
            bool recursive = false,
            string? tags = null)
        {
            if (!PhotoService.ThumbSortKeys.Contains(orderBy)) return BadRequest($"orderBy must be one of: {string.Join(", ", PhotoService.ThumbSortKeys)}");
            // We do NOT use OData $select here because it triggers Mongo LINQ translation errors.
            var (page, total) = await _photoService.GetThumbPage(userPass, pathPrefix, top, skip, orderBy, descending, recursive, tags);
            Response.Headers["X-Total-Count"] = total.ToString();
            return Ok(page);
        }

    }
}
