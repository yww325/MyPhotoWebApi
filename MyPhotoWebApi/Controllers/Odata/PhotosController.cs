using Microsoft.AspNet.OData;
using Microsoft.AspNet.OData.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Controllers.Odata
{
    [ApiVersion("1.0")] // can be removed, default version  
    public class PhotosController : ODataController
    { 
        private readonly IMongoCollection<Photo> _photosCollection;

        public PhotosController(IMongoDatabase mongoDatabase)
        {
            _photosCollection = mongoDatabase.GetCollection<Photo>("photos");
        }
        // example Tags/any(s:contains(s, '重固'))
        // support null https://stackoverflow.com/questions/56962714/asp-net-core-odata-on-mongodb-like-filter
        [EnableQuery(HandleNullPropagation = HandleNullPropagationOption.False)]
        public IQueryable<Photo> Get([FromHeader]string userPass)
        {
            IQueryable<Photo> queryable = _photosCollection.AsQueryable();
            if (userPass != Startup.HashedUserPass)
            {
                queryable = queryable.Where(f => f.IsPrivate == false);
            }
            return queryable;
        }

        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Patch([FromHeader]string userPass, [FromODataUri] string key, Delta<Photo> delta)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (userPass != Startup.HashedUserPass) return Unauthorized();
            var entity = await _photosCollection.Find(p => p.Id == key).FirstOrDefaultAsync();
            if (entity == null) return NotFound();
            delta.Patch(entity);
            var replaceResult = _photosCollection.ReplaceOne(p => p.Id == key, entity);
            if (replaceResult.IsAcknowledged)
            {
                return Ok();
            }

            return StatusCode((int)HttpStatusCode.InternalServerError, replaceResult.ToString());
        }
    }
}
