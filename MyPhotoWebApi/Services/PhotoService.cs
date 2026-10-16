using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        public async Task<Photo?> FindByPathAndName(string path, string fileName)
        {
            return await _photosCollection.Find(p => p.Path == path && p.FileName == fileName)
                .Project<Photo>(Builders<Photo>.Projection.Exclude(x => x.Thumbnail))
                .FirstOrDefaultAsync();
        }

        public async Task CreateManyPhotos(IList<Photo> photos)
        {
            // Root-cause fix for duplicate rows:
            // ingestion can run concurrently, and the pre-check in FileIngestionService is not atomic with inserts.
            // Use upserts keyed by (Path, FileName) so repeated ingests do not create duplicates.
            var models = new List<WriteModel<Photo>>(photos.Count);
            foreach (var p in photos)
            {
                var path = p.Path ?? string.Empty;
                var fileName = p.FileName ?? string.Empty;

                var filter = Builders<Photo>.Filter.Eq(x => x.Path, path) &
                             Builders<Photo>.Filter.Eq(x => x.FileName, fileName);

                // If the doc already exists, keep it (no-op). If it doesn't, insert.
                // We intentionally avoid overwriting existing docs here.
                var update = Builders<Photo>.Update
                    .SetOnInsert(x => x.Path, path)
                    .SetOnInsert(x => x.FileName, fileName)
                    .SetOnInsert(x => x.MediaType, p.MediaType)
                    .SetOnInsert(x => x.DateTaken, p.DateTaken)
                    .SetOnInsert(x => x.Tags, p.Tags)
                    .SetOnInsert(x => x.Thumbnail, p.Thumbnail)
                    .SetOnInsert(x => x.IsPrivate, p.IsPrivate);

                models.Add(new UpdateOneModel<Photo>(filter, update) { IsUpsert = true });
            }

            if (models.Count == 0) return;
            await _photosCollection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false });
        }

        /// <summary>Sort keys accepted by <see cref="GetThumbPage"/>.</summary>
        public static readonly string[] ThumbSortKeys = { "date", "name", "size" };

        /// <summary>
        /// One page of the files in a folder (and, when <paramref name="recursive"/>, in all its subfolders) in the
        /// requested order, plus the total number of files, so a client can page through them and size its list.
        /// </summary>
        public async Task<(List<PhotoThumb> Page, int Total)> GetThumbPage(string userPass, string pathPrefix, int top, int skip, string orderBy = "date", bool descending = true, bool recursive = false)
        {
            // Dedicated endpoint path: avoids OData $select projection, which doesn't translate through Mongo LINQ.
            if (top <= 0) top = 100;
            if (top > 500) top = 500; // safety cap
            if (skip < 0) skip = 0;
            pathPrefix = (pathPrefix ?? string.Empty).Trim('/');

            var filter = recursive
                ? (pathPrefix.Length == 0
                    ? Builders<Photo>.Filter.Empty
                    // The folder itself or anything below it; "Trip" must not match a sibling such as "Trip2".
                    : Builders<Photo>.Filter.Eq(x => x.Path, pathPrefix) |
                      Builders<Photo>.Filter.Regex(x => x.Path, new BsonRegularExpression("^" + Regex.Escape(pathPrefix + "/"))))
                : Builders<Photo>.Filter.Eq(x => x.Path, pathPrefix);
            if (userPass != Startup.HashedUserPass)
            {
                filter &= Builders<Photo>.Filter.Eq(x => x.IsPrivate, false);
            }

            // File sizes live on disk, not in the database, so rank all matching files in memory from a light
            // projection and only load thumbnails for the requested page.
            var entries = (await _photosCollection.Find(filter)
                    .Project(p => new { p.Id, p.Path, p.FileName, p.DateTaken })
                    .ToListAsync())
                .Select(p => new { p.Id, p.Path, p.FileName, p.DateTaken, Size = orderBy == "size" ? GetFileSize(p.Path, p.FileName) : null })
                .ToList();

            var names = StringComparer.InvariantCultureIgnoreCase;
            var ordered = orderBy switch
            {
                "name" => descending ? entries.OrderByDescending(e => e.FileName, names) : entries.OrderBy(e => e.FileName, names),
                "size" => descending ? entries.OrderByDescending(e => e.Size ?? -1) : entries.OrderBy(e => e.Size ?? -1),
                _ => descending ? entries.OrderByDescending(e => e.DateTaken) : entries.OrderBy(e => e.DateTaken),
            };
            // Ties (same date, same size, same name in different folders) keep a stable order so paging never repeats or skips a file.
            var pageIds = ordered.ThenBy(e => e.FileName, names).ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)
                .Skip(skip).Take(top).Select(e => e.Id).ToList();
            if (pageIds.Count == 0) return (new List<PhotoThumb>(), entries.Count);

            // Only return fields needed for the grid.
            var projection = Builders<Photo>.Projection
                .Include(x => x.Id)
                .Include(x => x.FileName)
                .Include(x => x.MediaType)
                .Include(x => x.Path)
                .Include(x => x.DateTaken)
                .Include(x => x.Thumbnail)
                .Include(x => x.IsPrivate);
            var docs = (await _photosCollection.Find(Builders<Photo>.Filter.In(x => x.Id, pageIds))
                    .Project<Photo>(projection).ToListAsync())
                .ToDictionary(d => d.Id);

            var page = pageIds.Where(docs.ContainsKey)
                .Select(id => PhotoThumb.From(docs[id], GetFileSize(docs[id].Path, docs[id].FileName)))
                .ToList();
            return (page, entries.Count);
        }

        /// <summary>Size in bytes of a photo's file on disk, or null when the file is missing.</summary>
        private long? GetFileSize(string path, string fileName)
        {
            var file = new FileInfo(Path.Combine(_myPhotoSettings.RootFolder, path, fileName));
            return file.Exists ? file.Length : null;
        }

        /// <summary>
        /// Deletes each photo's file from disk and then its database record. The record is only removed once the
        /// file is gone (or was already missing), so a failure never leaves a file on disk that the database forgot.
        /// </summary>
        public async Task<PhotoDeleteResult> DeletePhotos(IEnumerable<string> ids)
        {
            var root = Path.GetFullPath(_myPhotoSettings.RootFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var result = new PhotoDeleteResult();
            foreach (var id in ids.Distinct())
            {
                var item = new DeletePhotoResult { Id = id };
                result.Results.Add(item);

                var photo = ObjectId.TryParse(id, out _)
                    ? await _photosCollection.Find(p => p.Id == id)
                        .Project<Photo>(Builders<Photo>.Projection.Exclude(x => x.Thumbnail)).FirstOrDefaultAsync()
                    : null;
                if (photo == null)
                {
                    item.Status = DeletePhotoResult.NotFound;
                    continue;
                }
                item.Path = photo.Path;
                item.FileName = photo.FileName;

                var file = Path.GetFullPath(Path.Combine(root, photo.Path ?? "", photo.FileName ?? ""));
                if (!file.StartsWith(root, StringComparison.Ordinal) || string.IsNullOrEmpty(photo.FileName))
                {
                    item.Status = DeletePhotoResult.Failed;
                    item.Error = "The photo's file is outside the photo root.";
                    continue;
                }

                try
                {
                    // File.Delete does nothing when the file is already missing.
                    File.Delete(file);
                    await _photosCollection.DeleteOneAsync(p => p.Id == id);
                    item.Status = DeletePhotoResult.Deleted;
                    _logger.LogInformation($"Deleted photo {id}: {file}");
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to delete photo {id}: {file}");
                    item.Status = DeletePhotoResult.Failed;
                    item.Error = e.Message;
                }
            }
            return result;
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
