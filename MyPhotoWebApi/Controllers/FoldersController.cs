using Microsoft.AspNet.OData;
using Microsoft.AspNet.OData.Query;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System.Linq;

namespace MyPhotoWebApi.Controllers
{
    [ApiVersion("1.0")] // can be removed, default version 
    public class FoldersController : ODataController
    {
        private readonly ILogger<FoldersController> _logger;
        private readonly IMongoCollection<Folder> _mongoCollection;

        public FoldersController(ILogger<FoldersController> logger, IMongoDatabase mongoDatabase)
        {
            _logger = logger;
            _mongoCollection = mongoDatabase.GetCollection<Folder>("folders"); ;
        }
       
        [EnableQuery(HandleNullPropagation = HandleNullPropagationOption.False)]
        [EnableCors("AllowAnyPolicy")]
        public IQueryable<Folder> Get()
        {
            return _mongoCollection.AsQueryable();
        }
    } 
}