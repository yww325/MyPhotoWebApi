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

        public FolderService(ILogger<FolderService> logger, IMongoDatabase mongoDatabase, MyPhotoSettings myPhotoSettings)
        {
            _logger = logger;
            _myPhotoSettings = myPhotoSettings;
            _foldersCollection = mongoDatabase.GetCollection<Folder>("folders");
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
