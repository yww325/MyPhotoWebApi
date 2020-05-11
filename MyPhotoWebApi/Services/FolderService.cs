using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Services
{
    public class FolderService
    {
        private readonly ILogger<FolderService> _logger;
        private readonly IMongoCollection<Folder> _foldersCollection;

        public FolderService(ILogger<FolderService> logger, IMongoDatabase mongoDatabase)
        {
            _logger = logger;
            _foldersCollection = mongoDatabase.GetCollection<Folder>("folders");
        }


        public async Task<string> FindFolderIdByPath(string folderPath)
        {
            string fallbackParentFolderId = BsonObjectId.Empty.ToString(); // place in root folder if can't find parent
            var currentFolder = await _foldersCollection.Find(f => f.Path == folderPath).FirstOrDefaultAsync();
            if (currentFolder == null)
            {
                return fallbackParentFolderId;
            }
            return currentFolder.Id;
        }


        public async Task<Folder> GetOrCreateFolder(string path, string name, string parentFolderId)
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
            _logger.LogInformation("one new folder created : " + path);
            return currentFolder;
        }
    } 
}
