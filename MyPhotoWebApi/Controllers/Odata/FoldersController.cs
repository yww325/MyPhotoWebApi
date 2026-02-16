using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System.Linq;

namespace MyPhotoWebApi.Controllers.Odata
{
    public class FoldersController : ODataController
    {
        private readonly IMongoCollection<Folder> _mongoCollection;

        public FoldersController(IMongoDatabase mongoDatabase)
        {
            _mongoCollection = mongoDatabase.GetCollection<Folder>("folders"); ;
        }

        [HttpGet]
        [EnableQuery] 
        public IQueryable<Folder> Get()
        {
            return _mongoCollection.AsQueryable();
        }
    } 
}