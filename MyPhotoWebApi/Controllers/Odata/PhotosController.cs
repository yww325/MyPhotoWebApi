using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;
using System.Linq;
using System.Net;
using System.Threading.Tasks;


namespace MyPhotoWebApi.Controllers.Odata
{
    public class PhotosController : ODataController
    {
        private readonly PhotoService _photoService;

        public PhotosController(PhotoService photoService)
        {
            _photoService = photoService;
        }

        // example Tags/any(s:contains(s, '重固'))
        // support null https://stackoverflow.com/questions/56962714/asp-net-core-odata-on-mongodb-like-filter
        [HttpGet]
        [EnableQuery]
        public IQueryable<Photo> Get([FromHeader] string userPass)
        {
          return _photoService.GetPhotosQueryable(userPass);
        }


        [HttpPatch]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ServiceFilter(typeof(ValidateModelAttribute))]
        public async Task<IActionResult> Patch([FromHeader]string userPass, [FromQuery] string key, Delta<Photo> delta)
        { 
            return await Util.ResponseHelper(async () =>
               await _photoService.Patch(userPass, key, delta)); 
        }
    }
}
