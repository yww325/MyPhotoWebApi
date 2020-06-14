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

        public async Task<bool> MarkPrivate(string path, bool toPrivate)
        { 
            var update = Builders<Photo>.Update.Set(x => x.IsPrivate, toPrivate);
            var result = await _photosCollection.UpdateManyAsync(p=>p.Path.StartsWith(path), update);
            _logger.LogInformation("updated records: " + result.ModifiedCount);
            return true;
        }

        public async Task<bool> MarkPrivateById(string photoId, bool toPrivate)
        {
            var update = Builders<Photo>.Update.Set(x => x.IsPrivate, toPrivate);
            var result = await _photosCollection.UpdateOneAsync(p => p.Id == photoId, update);
            _logger.LogInformation("updated records: " + result.ModifiedCount);
            return true;
        }

        public async Task CreateManyPhotos(IList<Photo> photos)
        {
            await _photosCollection.InsertManyAsync(photos, new InsertManyOptions() { IsOrdered = false });
        }
    }
}
