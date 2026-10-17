using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Services
{
    public class FolderService
    { 
        public static readonly string fallbackParentFolderId = BsonObjectId.Empty.ToString(); // place in root folder if can't find parent
        public const string RootFolderPath = "";

        private const string Delimeter = "/";
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

        /// <summary>
        /// Turns a client-supplied folder path such as "2026/香港旅行" into the stored form
        /// (segments joined by "/", no leading or trailing slash) and guarantees it stays inside RootFolder.
        /// Returns null when the path is empty or unsafe.
        /// </summary>
        public string? NormalizeFolderPath(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var segments = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) return null;
            // Dot-prefixed names cover "." and "..", and would also be hidden from the file provider.
            if (segments.Any(s => s.StartsWith('.') || s.Any(char.IsControl))) return null;

            var path = string.Join(Delimeter, segments);
            var root = Path.GetFullPath(_myPhotoSettings.RootFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, path));
            return full.StartsWith(root, StringComparison.Ordinal) ? path : null;
        }

        private readonly SemaphoreSlim _ensureFolderLock = new(1, 1);

        /// <summary>
        /// Creates every missing level of a normalized folder path, both on disk and as folder entities
        /// with the correct parent ids, and returns the deepest folder.
        /// </summary>
        public async Task<Folder> EnsureFolderPath(string normalizedPath)
        {
            // Serialized so concurrent uploads into a new folder do not insert duplicate folder entities.
            await _ensureFolderLock.WaitAsync();
            try
            {
                var parentId = fallbackParentFolderId;
                var path = RootFolderPath;
                Folder folder = null!;
                foreach (var name in normalizedPath.Split(Delimeter))
                {
                    path = path == RootFolderPath ? name : path + Delimeter + name;
                    CreatePhyscicalFolder(path);
                    folder = await GetOrCreateFolderEntity(path, name, parentId);
                    parentId = folder.Id;
                }
                return folder;
            }
            finally
            {
                _ensureFolderLock.Release();
            }
        }

        public enum EmptyFolderDeleteResult { Deleted, NotFound, NotEmpty }

        /// <summary>
        /// Deletes a folder that holds nothing: no photos or subfolders in the database and no entries on disk.
        /// Removes both its directory and its database entry. Serialized with folder creation, so an upload
        /// cannot recreate the folder halfway through.
        /// </summary>
        public async Task<EmptyFolderDeleteResult> DeleteEmptyFolder(string normalizedPath)
        {
            await _ensureFolderLock.WaitAsync();
            try
            {
                var branch = await _foldersCollection
                    .Find(f => f.Path == normalizedPath || f.Path.StartsWith(normalizedPath + Delimeter))
                    .Project(f => f.Path).ToListAsync();
                var directory = Path.Combine(_myPhotoSettings.RootFolder, normalizedPath);
                var onDisk = Directory.Exists(directory);
                if (branch.Count == 0 && !onDisk) return EmptyFolderDeleteResult.NotFound;
                if (branch.Any(p => p != normalizedPath)) return EmptyFolderDeleteResult.NotEmpty;
                var hasPhotos = await _photosCollection
                    .Find(p => p.Path == normalizedPath || p.Path.StartsWith(normalizedPath + Delimeter)).AnyAsync();
                if (hasPhotos) return EmptyFolderDeleteResult.NotEmpty;
                if (onDisk)
                {
                    // Files copied in without an ingest are not in the database but still count.
                    if (Directory.EnumerateFileSystemEntries(directory).Any()) return EmptyFolderDeleteResult.NotEmpty;
                    Directory.Delete(directory, recursive: false);
                }
                await _foldersCollection.DeleteManyAsync(f => f.Path == normalizedPath);
                _logger.LogInformation($"Deleted empty folder {normalizedPath}");
                return EmptyFolderDeleteResult.Deleted;
            }
            finally
            {
                _ensureFolderLock.Release();
            }
        }

        /// <summary>
        /// Builds the whole folder tree with photo counts. The tree is derived from the "/"-separated
        /// paths of folders and photos rather than from ParentFolderId, which older ingests did not
        /// always set correctly.
        /// </summary>
        public async Task<FolderNode> GetFolderTree()
        {
            var folders = await _foldersCollection.Find(FilterDefinition<Folder>.Empty)
                .Project(f => new { f.Id, f.Path }).ToListAsync();
            var counts = await _photosCollection.Aggregate()
                .Group(p => p.Path, g => new { Path = g.Key, Count = g.Count() }).ToListAsync();

            var root = new FolderNode { Path = RootFolderPath, Name = RootFolderPath };
            var nodes = new Dictionary<string, FolderNode> { [RootFolderPath] = root };

            FolderNode GetOrAdd(string? rawPath)
            {
                var path = (rawPath ?? RootFolderPath).Trim('/');
                if (nodes.TryGetValue(path, out var node)) return node;
                var cut = path.LastIndexOf('/');
                var parent = GetOrAdd(cut < 0 ? RootFolderPath : path[..cut]);
                node = new FolderNode { Path = path, Name = cut < 0 ? path : path[(cut + 1)..] };
                parent.Children.Add(node);
                nodes[path] = node;
                return node;
            }

            foreach (var f in folders) GetOrAdd(f.Path).Id = f.Id;
            foreach (var c in counts) GetOrAdd(c.Path).PhotoCount += c.Count;

            int Finish(FolderNode node)
            {
                node.Children.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                node.TotalCount = node.PhotoCount + node.Children.Sum(Finish);
                return node.TotalCount;
            }
            Finish(root);
            return root;
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
