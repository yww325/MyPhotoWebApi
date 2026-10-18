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

        public const int MaxExtraTags = 20;
        public const int MaxTagLength = 20;

        /// <summary>
        /// Replaces a photo's tags other than its folder names (AI or hand-added) with <paramref name="tags"/>, trimmed,
        /// lower-cased and without repeats or folder names; the folder-name tags always stay.
        /// </summary>
        public async Task<PhotoTags> SetExtraTags(string id, IEnumerable<string?> tags)
        {
            var photo = ObjectId.TryParse(id, out _)
                ? await _photosCollection.Find(p => p.Id == id).Project<Photo>(Builders<Photo>.Projection.Exclude(x => x.Thumbnail)).FirstOrDefaultAsync()
                : null;
            if (photo == null) throw new MyPhotoException($"Photo {id} is not found.", MyErrorCode.NotFound);

            var folderTags = Util.GenerateTags(photo.Path ?? "");
            var extra = tags.Select(t => Regex.Replace(t ?? "", @"\s+", " ").Trim().ToLowerInvariant())
                .Where(t => t.Length > 0 && !folderTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Distinct().ToArray();
            if (extra.Length > MaxExtraTags) throw new MyPhotoException($"At most {MaxExtraTags} tags besides the folder names.", MyErrorCode.BadRequest);
            if (extra.Any(t => t.Length > MaxTagLength)) throw new MyPhotoException($"A tag can have at most {MaxTagLength} characters.", MyErrorCode.BadRequest);

            var all = folderTags.Concat(extra).ToArray();
            // Matches nothing if the photo moved meanwhile, so the move's folder tags are not overwritten.
            var update = await _photosCollection.UpdateOneAsync(p => p.Id == id && p.Path == photo.Path, Builders<Photo>.Update.Set(p => p.Tags, all));
            if (update.MatchedCount == 0) throw new MyPhotoException("The photo moved while its tags were being saved; try again.", MyErrorCode.General);
            _logger.LogInformation($"Set the tags of photo {id}: {string.Join(", ", extra)}");
            return new PhotoTags { Id = id, Tags = all };
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
        /// With <paramref name="tags"/> (words separated by spaces) only files whose tags contain every word are listed.
        /// </summary>
        public async Task<(List<PhotoThumb> Page, int Total)> GetThumbPage(string userPass, string pathPrefix, int top, int skip, string orderBy = "date", bool descending = true, bool recursive = false, string? tags = null)
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
            // Each word must be part of at least one tag, ignoring case, so "猫" finds "暹罗猫" too.
            foreach (var word in (tags ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct())
            {
                filter &= Builders<Photo>.Filter.Regex("tags", new BsonRegularExpression(Regex.Escape(word), "i"));
            }
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
                .Include(x => x.Tags)
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

        /// <summary>
        /// Moves photos into <paramref name="folder"/> ("" is the root; other folders are created when needed):
        /// each file on disk and then its record's path, tags and, if renamed, file name. Nothing else in the record
        /// changes, so dateTaken and the order by date stay as they were. A name already used in the target
        /// folder, by a file or a record, gets " (n)" added like an upload. Every id gets its own result, and a
        /// photo that fails stays where it was.
        /// </summary>
        public async Task<PhotoMoveResult> MovePhotosTo(string folder, IEnumerable<string> ids)
        {
            var target = string.IsNullOrWhiteSpace(folder)
                ? FolderService.RootFolderPath
                : _folderService.NormalizeFolderPath(folder) ?? throw new MyPhotoException($"Invalid folder '{folder}'.", MyErrorCode.BadRequest);
            // Created only once a photo is about to move, so a request where every photo fails leaves no new folder.
            var folderReady = target == FolderService.RootFolderPath;
            var root = Path.GetFullPath(_myPhotoSettings.RootFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var directory = Path.Combine(root, target);

            var result = new PhotoMoveResult { Folder = target };
            foreach (var id in ids.Distinct())
            {
                var item = new MovePhotoResult { Id = id };
                result.Results.Add(item);

                var photo = ObjectId.TryParse(id, out _)
                    ? await _photosCollection.Find(p => p.Id == id)
                        .Project<Photo>(Builders<Photo>.Projection.Exclude(x => x.Thumbnail)).FirstOrDefaultAsync()
                    : null;
                if (photo == null)
                {
                    item.Status = MovePhotoResult.NotFound;
                    continue;
                }
                item.From = photo.Path;
                item.FileName = photo.FileName;
                if ((photo.Path ?? "") == target)
                {
                    item.Status = MovePhotoResult.Moved;
                    item.SavedAs = photo.FileName;
                    continue;
                }

                var source = Path.GetFullPath(Path.Combine(root, photo.Path ?? "", photo.FileName ?? ""));
                if (!source.StartsWith(root, StringComparison.Ordinal) || string.IsNullOrEmpty(photo.FileName))
                {
                    item.Status = MovePhotoResult.Failed;
                    item.Error = "The photo's file is outside the photo root.";
                    continue;
                }
                if (!File.Exists(source))
                {
                    item.Status = MovePhotoResult.Failed;
                    item.Error = "The photo's file is missing on disk.";
                    continue;
                }

                string? destination = null;
                try
                {
                    if (!folderReady)
                    {
                        await _folderService.EnsureFolderPath(target);
                        folderReady = true;
                    }
                    var name = await FreeName(directory, target, photo.FileName);
                    destination = Path.Combine(directory, name);
                    // Fails instead of overwriting if another request took the name meanwhile.
                    File.Move(source, destination);
                    var update = Builders<Photo>.Update
                        .Set(p => p.Path, target)
                        .Set(p => p.FileName, name)
                        .Set(p => p.Tags, Util.MoveTags(photo.Tags, photo.Path, target));
                    await _photosCollection.UpdateOneAsync(p => p.Id == id, update);
                    item.Status = MovePhotoResult.Moved;
                    item.SavedAs = name;
                    _logger.LogInformation($"Moved photo {id} from {source} to {destination}");
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Failed to move photo {id} from {source} to {target}");
                    // Put the file back if it already moved but the record could not follow.
                    if (destination != null && File.Exists(destination) && !File.Exists(source))
                    {
                        try { File.Move(destination, source); }
                        catch (Exception back) { _logger.LogError(back, $"Could not move {destination} back to {source}"); }
                    }
                    item.Status = MovePhotoResult.Failed;
                    item.Error = e.Message;
                }
            }
            return result;
        }

        /// <summary>The file name itself if neither a file nor a record in the folder uses it, else the first free "name (n).ext".</summary>
        private async Task<string> FreeName(string directory, string folder, string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var n = 0; ; n++)
            {
                var candidate = n == 0 ? fileName : $"{stem} ({n}){extension}";
                if (!File.Exists(Path.Combine(directory, candidate)) && await FindByPathAndName(folder, candidate) == null) return candidate;
            }
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

                    photo.Tags = Util.MoveTags(photo.Tags, photo.Path, folder.Path);
                    photo.Path = folder.Path;
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
