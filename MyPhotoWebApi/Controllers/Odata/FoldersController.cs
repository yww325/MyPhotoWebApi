using Microsoft.AspNet.OData;
using Microsoft.AspNet.OData.Query;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System.Linq;

namespace MyPhotoWebApi.Controllers.Odata
{
    [ApiVersion("1.0")] // can be removed, default version 
    public class FoldersController : ODataController
    {
        private readonly IMongoCollection<Folder> _mongoCollection;

        public FoldersController(IMongoDatabase mongoDatabase)
        {
            _mongoCollection = mongoDatabase.GetCollection<Folder>("folders"); ;
        }
       
        [EnableQuery(HandleNullPropagation = HandleNullPropagationOption.False)] 
        public IQueryable<Folder> Get()
        {
            return _mongoCollection.AsQueryable();
        }
    } 
}