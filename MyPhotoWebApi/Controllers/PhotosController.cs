using Microsoft.AspNet.OData;
using Microsoft.AspNet.OData.Query;
using Microsoft.AspNet.OData.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Controllers
{
    [ApiVersion("1.0")] // can be removed, default version 
    public class PhotosController : ODataController    
    {
        private readonly ILogger<PhotosController> _logger;
        private readonly IMongoCollection<Photo> _mongoCollection;

        public PhotosController(ILogger<PhotosController> logger, IMongoDatabase mongoDatabase)
        {
            _logger = logger;
            _mongoCollection = mongoDatabase.GetCollection<Photo>("photos"); ;
        }
        // example Tags/any(s:contains(s, '重固'))
        // support null https://stackoverflow.com/questions/56962714/asp-net-core-odata-on-mongodb-like-filter
        [EnableQuery(HandleNullPropagation = HandleNullPropagationOption.False)]
        public IQueryable<Photo> Get()
        { 
            return _mongoCollection.AsQueryable();
        }


    }
}
