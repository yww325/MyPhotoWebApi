using Microsoft.AspNet.OData;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Services
{
    public class PhotoService
    {
        private readonly ILogger<PhotoService> _logger;
        private readonly MyPhotoSettings _myPhotoSettings;
        private readonly FolderService _folderService;
        private readonly IMongoCollection<Photo> _photosCollection;

        public PhotoService(ILogger<PhotoService> logger, IMongoDatabase mongoDatabase, MyPhotoSettings myPhotoSettings, FolderService folderService)
        {
            _logger = logger;
            _myPhotoSettings = myPhotoSettings;
            _folderService = folderService;
            _photosCollection = mongoDatabase.GetCollection<Photo>("photos");
        }

        public async Task<bool> MarkPrivate(string path, bool toPrivate)
        {
            _logger.LogInformation($"Changing Private attribute to {toPrivate} for path: {path}");
            var update = Builders<Photo>.Update.Set(x => x.IsPrivate, toPrivate);
            var result = await _photosCollection.UpdateManyAsync(p=>p.Path == path, update);
            _logger.LogInformation("updated records: " + result.ModifiedCount);
            return true;
        }

        internal IQueryable<Photo> GetPhotosQueryable(string userPass)
        {
            IQueryable<Photo> queryable = _photosCollection.AsQueryable();
            if (userPass != Startup.HashedUserPass)
            {
                queryable = queryable.Where(f => f.IsPrivate == false);
            }
            return queryable;
        }

        internal async Task<string> Patch(string userPass, string key, Delta<Photo> delta)
        {
            if (userPass != Startup.HashedUserPass) throw new MyPhotoException("You need a correct password to patch.", MyErrorCode.BadRequest);

            var entity = await _photosCollection.Find(p => p.Id == key).FirstOrDefaultAsync();
            if (entity == null) throw new MyPhotoException($"Photo {key} is not found.", MyErrorCode.NotFound);

            delta.Patch(entity);
            var replaceResult = _photosCollection.ReplaceOne(p => p.Id == key, entity);
            if (!replaceResult.IsAcknowledged)
            {
                throw new MyPhotoException(replaceResult.ToString(), MyErrorCode.General); 
            }

            return $"Photo {key} is patched successully.";
        }

        public async Task<bool> MarkPrivateById(string photoId, bool toPrivate)
        {
            _logger.LogInformation($"Changing Private attribute to {toPrivate} for photo with id: {photoId}");
            var update = Builders<Photo>.Update.Set(x => x.IsPrivate, toPrivate);
            var result = await _photosCollection.UpdateOneAsync(p => p.Id == photoId, update);
            _logger.LogInformation("updated records: " + result.ModifiedCount);
            return true;
        }

        public async Task CreateManyPhotos(IList<Photo> photos)
        {
            await _photosCollection.InsertManyAsync(photos, new InsertManyOptions() { IsOrdered = false });
        }

        public async Task<bool> MovePhotos(string[] ids, string folderId)
        {
            try
            {
                var folder = await _folderService.FindFolderById(folderId);
                if (folder == null)
                {
                    _logger.LogError($"unknown folderId: {folderId}");
                    return false;
                }

                foreach (var id in ids)
                {
                    var photo = await _photosCollection.Find(p => p.Id == id).FirstOrDefaultAsync();
                    if (photo == null)
                    {
                        _logger.LogError($"unknown photo id: {id}");
                        return false;
                    }

                    var sourceFile = Path.Combine(_myPhotoSettings.RootFolder, photo.Path, photo.FileName);
                    var destinationFile = Path.Combine(_myPhotoSettings.RootFolder, folder.Path, photo.FileName);
                    // Move the file.
                    _logger.LogInformation($"moving file {photo.FileName} from {photo.Path} to {folder.Path}");
                    File.Move(sourceFile, destinationFile);

                    photo.Path = folder.Path;
                    photo.Tags = Util.GenerateTags(photo.Path);
                    var replaceResult = await _photosCollection.ReplaceOneAsync(p => p.Id == photo.Id, photo);
                    if (replaceResult.IsAcknowledged)
                    {
                        _logger.LogInformation($"photo entity {photo.Id} has its path changed.");
                    }
                    else
                    {
                        _logger.LogError($"photo entity {photo.Id} failed to modify, try move file back");
                        File.Move(destinationFile, sourceFile);
                        return false;
                    }
                } 

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "moving photo throwing exception");
                return false;
            } 
        }
    }
}
