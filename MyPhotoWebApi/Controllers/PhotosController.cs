using Microsoft.AspNet.OData;
using Microsoft.AspNet.OData.Query;
using Microsoft.AspNet.OData.Routing;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Controllers
{
    [ApiVersion("1.0")] // can be removed, default version  
    public class PhotosController : ODataController    
    {
        private readonly ILogger<PhotosController> _logger; 
        private readonly IMongoCollection<Photo> _photosCollection;

        public PhotosController(ILogger<PhotosController> logger, IMongoDatabase mongoDatabase)
        {
            _logger = logger; 
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
    }
}
