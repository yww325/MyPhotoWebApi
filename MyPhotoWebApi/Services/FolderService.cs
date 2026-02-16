using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Services
{
    public class FolderService
    { 
        public static readonly string fallbackParentFolderId = BsonObjectId.Empty.ToString(); // place in root folder if can't find parent
        public const string RootFolderPath = "";

        private const string Delimeter = "\\";
        private readonly ILogger<FolderService> _logger;
        private readonly MyPhotoSettings _myPhotoSettings;
        private readonly IMongoCollection<Folder> _foldersCollection;

        private readonly IMongoCollection<Photo> _photosCollection;

        public FolderService(ILogger<FolderService> logger, IMongoDatabase mongoDatabase, MyPhotoSettings myPhotoSettings)
        {
            _logger = logger;
            _myPhotoSettings = myPhotoSettings;
            _foldersCollection = mongoDatabase.GetCollection<Folder>("folders");
            _photosCollection = mongoDatabase.GetCollection<Photo>("photos");
        }

        public async Task<object> DeleteFolderData(string folderPath)
        {
            folderPath = folderPath.Replace('\\', '/').Trim('/');
            _logger.LogInformation($"Deleting all data for folder branch: {folderPath}");

            // Find all folders in this branch (self + subfolders)
            // Path should be equal to folderPath or start with folderPath + "/"
            var folderFilter = Builders<Folder>.Filter.Where(f => f.Path == folderPath || f.Path.StartsWith(folderPath + "/"));
            var deletedFolders = await _foldersCollection.Find(folderFilter).ToListAsync();

            // Find all photos in this branch
            var photoFilter = Builders<Photo>.Filter.Where(p => p.Path == folderPath || p.Path.StartsWith(folderPath + "/"));
            var photos = await _photosCollection.Find(photoFilter).ToListAsync();

            // Redact thumbnails for the return value
            var deletedPhotos = photos.Select(p => new {
                p.Id,
                p.FileName,
                p.MediaType,
                p.Path,
                p.DateTaken,
                p.Tags,
                Thumbnail = p.Thumbnail != null ? "..." : null,
                p.IsPrivate
            }).ToList();

            // Perform deletion
            var folderDeleteResult = await _foldersCollection.DeleteManyAsync(folderFilter);
            var photoDeleteResult = await _photosCollection.DeleteManyAsync(photoFilter);

            _logger.LogInformation($"Deleted {folderDeleteResult.DeletedCount} folders and {photoDeleteResult.DeletedCount} photos.");

            return new
            {
                folderPath,
                deletedFolders,
                deletedPhotos,
                summary = $"Deleted {folderDeleteResult.DeletedCount} folders and {photoDeleteResult.DeletedCount} photos."
            };
        }


        public async Task<string> FindFolderIdByPath(string folderPath)
        { 
            var currentFolder = await _foldersCollection.Find(f => f.Path == folderPath).FirstOrDefaultAsync();
            if (currentFolder == null)
            {
                return fallbackParentFolderId;
            }
            return currentFolder.Id;
        }

        public async Task<Folder> CreatePhyscicalFolderAndEntity(string parentFolderId, string folderName)
        {
            var parentFolder = await FindFolderById(parentFolderId);
            var parentPath = parentFolder == null ? RootFolderPath : parentFolder.Path; 
            if (parentPath == RootFolderPath)
            {
                parentFolderId = fallbackParentFolderId;
            }
            else
            {
                parentPath += Delimeter;
            }

            string folderPath = parentPath + folderName;
            CreatePhyscicalFolder(folderPath);
            return await GetOrCreateFolderEntity(folderPath, folderName, parentFolderId);
        } 

        public async Task<Folder> GetOrCreateFolderEntity(string path, string name, string parentFolderId)
        {
            var currentFolder = await _foldersCollection.Find(f => f.Path == path).FirstOrDefaultAsync();
            if (currentFolder != null) return currentFolder;

            currentFolder = new Folder()
            {
                Path = path,
                Name = name,
                ParentFolderId = parentFolderId
            };
            await _foldersCollection.InsertOneAsync(currentFolder);
            _logger.LogInformation("one new folder created: " + path);
            return currentFolder;
        }

        public async Task<Folder> FindFolderById(string id)
        {
            return await _foldersCollection.Find(f => f.Id == id).FirstOrDefaultAsync(); 
        }

        #region private methods
        private void CreatePhyscicalFolder(string path)
        {
            var fullPath = Path.Combine(_myPhotoSettings.RootFolder, path);
            Directory.CreateDirectory(fullPath);
        } 
        #endregion

    } 
}
