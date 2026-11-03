using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;
using System.ComponentModel.DataAnnotations;

namespace MyPhotoWebApi.Controllers.API
{
    /// <summary>Publishes the app's newest release and serves its APK.</summary>
    [ApiVersion("1.0")] // can be removed, default version
    [Route("api/v{version:apiVersion}/Update")]
    [ApiController]
    public class UpdateController : ControllerBase
    {
        private readonly UpdateService _updateService;

        public UpdateController(UpdateService updateService)
        {
            _updateService = updateService;
        }

        /// <summary>
        /// The release the server publishes. The client compares versionCode with its own and updates when the
        /// server's is larger; 404 when the server publishes nothing yet.
        /// </summary>
        [HttpGet("latest")]
        [ProducesResponseType(typeof(AppUpdate), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<AppUpdate> Latest()
        {
            var release = _updateService.CurrentRelease();
            if (release == null) return NotFound(new { message = "The server has not published an update yet." });
            return Ok(release);
        }

        /// <summary>The APK of the published release; the client installs it after downloading it.</summary>
        [HttpGet("apk/{fileName}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Apk([FromRoute, Required, BindRequired] string fileName)
        {
            var path = _updateService.ResolveApkPath(fileName);
            if (path == null) return NotFound(new { message = "No such update file." });
            return PhysicalFile(path, "application/vnd.android.package-archive", fileName, enableRangeProcessing: true);
        }
    }
}
