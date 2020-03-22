using Microsoft.AspNet.OData;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Controllers
{
    [ApiVersion("1.0")] // can be removed 
    [ApiExplorerSettings(IgnoreApi = false)]
    public class PhotosController : ODataController    
    { 
        [EnableQuery]
        public IQueryable<Photo> Get()
        {
            var connectionString = "mongodb://localhost:27017";
            var client = new MongoClient(connectionString);
            IMongoDatabase db = client.GetDatabase("testdb");
            IMongoCollection<Photo> collection = db.GetCollection<Photo>("photos");
            return collection.AsQueryable();
        }
    }
}
