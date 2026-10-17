using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.Tasks;
 
namespace MyPhotoWebApi.Controllers.API
{
    [ApiVersion("1.0")] // can be removed, default version
    [Route("api/v{version:apiVersion}/Folders")]
    [ApiController]
    public class FoldersController : ControllerBase
    {
        private readonly FolderService _folderService;

        public FoldersController(FolderService folderService)
        {
            _folderService = folderService;
        }
        
        [HttpPut]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CreateFolder([FromHeader, Required, BindRequired] string userPass, string parentFolderId, string folderName)
        {
            if (userPass != Startup.HashedUserPass) return Unauthorized();
            var folder = await _folderService.CreatePhyscicalFolderAndEntity(parentFolderId, folderName);
            if (folder != null)
            {
                return Ok(folder.Path + " is there now");
            }
            else
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, "folder creation failed");
            } 
        }

        /// <summary>The whole folder tree with photo counts, for choosing a folder (e.g. an upload target).</summary>
        [HttpGet("tree")]
        [RequireUserPass]
        [ProducesResponseType(typeof(FolderNode), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<FolderNode>> GetTree([FromHeader, Required, BindRequired] string userPass)
        {
            return Ok(await _folderService.GetFolderTree());
        }

        /// <summary>
        /// Deletes an empty folder, both its directory on disk and its database entry. Returns 409 when the folder
        /// still has photos, subfolders or any files on disk, 404 when it does not exist, and 400 for the root
        /// or an invalid path.
        /// </summary>
        [HttpDelete("empty")]
        [RequireUserPass]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> DeleteEmptyFolder([FromHeader, Required, BindRequired] string userPass, [FromQuery, Required, BindRequired] string folderPath)
        {
            var path = _folderService.NormalizeFolderPath(folderPath);
            if (path == null) return BadRequest("Invalid folder.");
            return await _folderService.DeleteEmptyFolder(path) switch
            {
                FolderService.EmptyFolderDeleteResult.Deleted => Ok(new { folder = path, status = "deleted" }),
                FolderService.EmptyFolderDeleteResult.NotFound => NotFound("No such folder."),
                _ => Conflict("The folder is not empty."),
            };
        }

        /// <summary>Removes the database entries of a folder branch (folders and photos); files on disk stay.</summary>
        [HttpDelete]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> DeleteFolderData([FromHeader, Required, BindRequired] string userPass, [FromQuery, Required, BindRequired] string folderPath)
        {
            if (userPass != Startup.HashedUserPass) return Unauthorized();
            var result = await _folderService.DeleteFolderData(folderPath);
            return Ok(result);
        }
    }
}
