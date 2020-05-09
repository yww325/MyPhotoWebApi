using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Services
{
    public class PhotoService
    {
        private readonly ILogger<PhotoService> _logger;
        private readonly IMongoCollection<Photo> _photosCollection;

        public PhotoService(ILogger<PhotoService> logger, IMongoDatabase mongoDatabase)
        {
            _logger = logger;
            _photosCollection = mongoDatabase.GetCollection<Photo>("photos");
        }
        public async Task<bool> MarkPrivate(string path)
        { 
            var update = Builders<Photo>.Update.Set(x => x.IsPrivate, true);
            var result = await _photosCollection.UpdateManyAsync(p=>p.Path.StartsWith(path), update);
            _logger.LogInformation("updated records: " + result.ModifiedCount);
            return true;
        }
    }
}
