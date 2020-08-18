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
    }
}
