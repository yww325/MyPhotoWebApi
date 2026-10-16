using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using System.Globalization;

namespace MyPhotoWebApi.Services
{
    public class FileIngestionService
    {
        private readonly ILogger<FileIngestionService> _logger; 
        private readonly IFileProvider _fileProvider;
        private readonly FolderService _folderService;
        private readonly PhotoService _photoService;

        public FileIngestionService(ILogger<FileIngestionService> logger, IFileProvider fileProvider, FolderService folderService, PhotoService photoService)
        {
            _logger = logger; 
            _fileProvider = fileProvider;
            _folderService = folderService;
            _photoService = photoService;
        }

        public async Task<IngestResult> Ingest(string ingestFolder, bool recursive)
        {
            ingestFolder = ingestFolder.Replace('\\', '/');
            ingestFolder = ingestFolder.TrimStart('/');
            ingestFolder = ingestFolder.TrimEnd('/');

            var folderIndex = ingestFolder.IndexOf('/');
            var folderPath = folderIndex >=0 ? ingestFolder.Substring(0, folderIndex) : "";
            string parentFolderId = await _folderService.FindFolderIdByPath(folderPath);
            _logger.LogInformation($"Start ingesting new folder:{ingestFolder}, recursive={recursive}");
            if (parentFolderId == FolderService.fallbackParentFolderId)
            {
                _logger.LogInformation("No parent folder found, place new folder in root.");
            } 
            else
            {
                _logger.LogInformation($"Found existing parent folder {folderPath}.");
            }

            var ingestResult = await IngestOneFolder(ingestFolder, recursive, parentFolderId);  
            return ingestResult;
        }


        private async Task<IngestResult> IngestOneFolder(string path, bool recursive, string parentFolderId)
        {
            _logger.LogInformation($"Ingesting folder:{path}, recursive={recursive}");
            var ingestResult = new IngestResult(); 
            var contents = _fileProvider.GetDirectoryContents(path);
            if (!contents.Exists) return ingestResult; //something wrong, folder not exists. 

            var photos = new List<Photo>();
            var tags = Util.GenerateTags(path);
            var name = tags.Last();
            var currentFolder = await _folderService.GetOrCreateFolderEntity(path, name, parentFolderId);
            var existingPhotoeNames = _photoService.GetPhotosQueryable(Startup.HashedUserPass)
                .Where(p => p.Path == path).Select(p=>p.FileName).ToHashSet();

            foreach (var fileInfo in contents)
            {
                if (fileInfo.IsDirectory)
                {
                    if (recursive)
                    {
                        var subPath = string.IsNullOrEmpty(path) ? fileInfo.Name : path + "/" + fileInfo.Name;
                        var subFolderResult = await IngestOneFolder(subPath, true, currentFolder.Id);
                        ingestResult.Absorb(subFolderResult);
                    } 
                    continue;
                } 

                if (existingPhotoeNames.Contains(fileInfo.Name))
                {
                    continue;  //no need to add existing file
                }
                ingestResult.TotalFilesFound++;
                var photo = CreatePhoto(fileInfo.PhysicalPath, fileInfo.Name, path, tags);
                switch (photo?.MediaType)
                {
                    case "photo": ingestResult.PhotosFound++; break;
                    case "sound": ingestResult.SoundsFound++; break;
                    case "video": ingestResult.VideosFound++; break;
                    default:
                        _logger.LogWarning($"{fileInfo.PhysicalPath} is not supported file type.");
                        ingestResult.UnknownFilesFound++;
                        continue;
                }

                photos.Add(photo);
            }

            if (photos.Count > 0)
            {
                await _photoService.CreateManyPhotos(photos);
            }
           
            return ingestResult;
        } 

        /// <summary>Media type stored for a file name, or null when the file type is not supported.</summary>
        private static string? GetMediaType(string fileName)
        {
            switch (System.IO.Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": case ".png": case ".bmp": return "photo";
                case ".wav": return "sound";
                case ".avi": case ".mp4": case ".3gp": return "video";
                default: return null;
            }
        }

        /// <summary>Builds the photo entity for a file on disk, or returns null when its type is not supported.</summary>
        private Photo? CreatePhoto(string physicalPath, string fileName, string path, string[] tags)
        {
            var mediaType = GetMediaType(fileName);
            if (mediaType == null) return null;
            var photo = new Photo
            {
                FileName = fileName,
                Path = path,
                Tags = tags,
                MediaType = mediaType,
            };
            DateTime? dateTaken;
            if (mediaType == "photo")
            {
                var (dateTime, imageBytes) = GetDateTakenAndThumbnailFromImage(physicalPath);
                dateTaken = dateTime ?? DateFromFileName(fileName);
                photo.Thumbnail = imageBytes;
            }
            else
            {
                // A camera's file name holds local time like EXIF does, so it keeps a trip's videos on the same
                // days as its photos; the movie header's UTC time is the fallback.
                dateTaken = DateFromFileName(fileName) ?? GetMovieCreated(physicalPath);
            }
            // Ingest time is the last resort; it groups unrelated files under the day they were ingested.
            photo.DateTaken = dateTaken ?? DateTime.Now;
            return photo;
        }

        /// <summary>
        /// The recording time of an MP4 or QuickTime movie, which its header stores in UTC; null when the header
        /// is missing or unset (an unset time reads as 1904, the format's epoch).
        /// </summary>
        private DateTime? GetMovieCreated(string path)
        {
            try
            {
                var header = ImageMetadataReader.ReadMetadata(path).OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
                if (header != null && header.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && created.Year >= 1971)
                {
                    return DateTime.SpecifyKind(created, DateTimeKind.Utc);
                }
            }
            catch (Exception)
            {
                _logger.LogWarning($"movie file {path} can't load its creation time");
            }
            return null;
        }

        private static readonly Regex FileNameDate = new(@"(?<!\d)(\d{8})[_-](\d{6})(?!\d)");

        /// <summary>
        /// The time in a camera-style file name such as 20210710_115044.mp4, VID_20210710_115044.mp4 or
        /// Screenshot_20210710-115044.jpg. Like EXIF times it is the local time where the file was made.
        /// </summary>
        internal static DateTime? DateFromFileName(string fileName)
        {
            var match = FileNameDate.Match(fileName);
            if (match.Success
                && DateTime.TryParseExact(match.Groups[1].Value + match.Groups[2].Value, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date.Year >= 1990 && date <= DateTime.Now.AddDays(1))
            {
                return date;
            }
            return null;
        }

        #region Upload
        // Serializes choosing a final file name and moving the upload into place, so two
        // concurrent uploads of different files with the same name cannot claim the same name.
        private readonly SemaphoreSlim _placeFileLock = new(1, 1);

        /// <summary>
        /// Saves uploaded files into the given folder (created if missing) and adds them to the database,
        /// so they show up immediately without a separate ingest. Each file gets its own result.
        /// </summary>
        public async Task<UploadResult> Upload(string folder, IReadOnlyList<IFormFile> files, CancellationToken ct)
        {
            var path = _folderService.NormalizeFolderPath(folder)
                ?? throw new MyPhotoException($"Invalid folder '{folder}'.", MyErrorCode.BadRequest);
            await _folderService.EnsureFolderPath(path);
            var directory = _fileProvider.GetFileInfo(path).PhysicalPath!;
            var tags = Util.GenerateTags(path);

            var result = new UploadResult { Folder = path };
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                result.Results.Add(await UploadOne(file, path, directory, tags, ct));
            }
            return result;
        }

        private async Task<UploadFileResult> UploadOne(IFormFile file, string path, string directory, string[] tags, CancellationToken ct)
        {
            // Browsers and some clients send a full client-side path; keep only the name.
            var fileName = System.IO.Path.GetFileName((file.FileName ?? "").Replace('\\', '/')).Trim();
            var result = new UploadFileResult { FileName = file.FileName ?? "" };
            if (fileName.Length == 0 || fileName.StartsWith('.') || fileName.Any(char.IsControl))
            {
                result.Status = UploadFileResult.Rejected;
                result.Error = "Invalid file name.";
                return result;
            }
            if (GetMediaType(fileName) == null)
            {
                result.Status = UploadFileResult.Rejected;
                result.Error = "Unsupported file type.";
                return result;
            }
            if (file.Length == 0)
            {
                result.Status = UploadFileResult.Rejected;
                result.Error = "Empty file.";
                return result;
            }

            // Dot-prefixed, so ingest and the file provider ignore it while it is incomplete.
            var tempPath = System.IO.Path.Combine(directory, $".upload-{Guid.NewGuid():N}.tmp");
            string? savedPath = null;
            try
            {
                byte[] hash;
                await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using var source = file.OpenReadStream();
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    hash = sha.GetHashAndReset();
                }

                string savedAs;
                bool duplicate;
                await _placeFileLock.WaitAsync(ct);
                try
                {
                    (savedAs, duplicate) = await ChooseName(directory, fileName, hash, new FileInfo(tempPath).Length, ct);
                    if (!duplicate)
                    {
                        savedPath = System.IO.Path.Combine(directory, savedAs);
                        File.Move(tempPath, savedPath);
                    }
                }
                finally
                {
                    _placeFileLock.Release();
                }

                result.SavedAs = savedAs;
                var existing = await _photoService.FindByPathAndName(path, savedAs);
                if (duplicate && existing != null)
                {
                    result.Status = UploadFileResult.Duplicate;
                    result.Id = existing.Id;
                    return result;
                }

                // New file, or an identical file that is on disk but was never ingested.
                var photo = CreatePhoto(System.IO.Path.Combine(directory, savedAs), savedAs, path, tags)!;
                await _photoService.CreateManyPhotos(new[] { photo });
                result.Id = (await _photoService.FindByPathAndName(path, savedAs))?.Id;
                result.Status = duplicate ? UploadFileResult.Duplicate : UploadFileResult.Created;
                savedPath = null; // keep the file
                _logger.LogInformation($"Uploaded {path}/{savedAs} ({result.Status})");
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // E.g. an image that cannot be decoded; remove what we saved so disk and database stay in sync.
                _logger.LogWarning(e, $"Upload of {fileName} into {path} failed");
                if (savedPath != null) TryDelete(savedPath);
                result.SavedAs = null;
                result.Status = UploadFileResult.Failed;
                result.Error = e is SixLabors.ImageSharp.ImageFormatException or SixLabors.ImageSharp.UnknownImageFormatException
                    ? "The image could not be read."
                    : "The file could not be saved.";
                return result;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        /// <summary>
        /// Picks the name an upload is stored under: the original name if free, otherwise the first free
        /// "name (n).ext". Returns duplicate = true when a file with that name already has identical content.
        /// </summary>
        private static async Task<(string Name, bool Duplicate)> ChooseName(string directory, string fileName, byte[] hash, long length, CancellationToken ct)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var extension = System.IO.Path.GetExtension(fileName);
            for (var n = 0; ; n++)
            {
                var candidate = n == 0 ? fileName : $"{stem} ({n}){extension}";
                var candidatePath = System.IO.Path.Combine(directory, candidate);
                if (!File.Exists(candidatePath)) return (candidate, false);
                if (new FileInfo(candidatePath).Length == length && (await HashFile(candidatePath, ct)).AsSpan().SequenceEqual(hash))
                    return (candidate, true);
            }
        }

        private static async Task<byte[]> HashFile(string physicalPath, CancellationToken ct)
        {
            await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return await SHA256.HashDataAsync(stream, ct);
        }

        private void TryDelete(string physicalPath)
        {
            try
            {
                if (File.Exists(physicalPath)) File.Delete(physicalPath);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Could not delete {physicalPath}");
            }
        }
        #endregion

        #region Image Processing
        private const int ThumbnailLongSide = 240;

        /// <summary>The EXIF capture time, or null when the image has none, and a thumbnail.</summary>
        private Tuple<DateTime?, byte[]> GetDateTakenAndThumbnailFromImage(string path)
        {
            DateTime? dateTaken = null;
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(path);
                var subIfdDirectory = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfdDirectory != null && subIfdDirectory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateTime))
                {
                    dateTaken = dateTime;
                }
            }
            catch (Exception)
            {
                _logger.LogWarning($"image file {path} can't load dateTaken ");
            }

            using (var image = SixLabors.ImageSharp.Image.Load(path))
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(ThumbnailLongSide, ThumbnailLongSide),
                    Mode = ResizeMode.Max
                }));

                using (var ms = new MemoryStream())
                {
                    image.SaveAsJpeg(ms);
                    return new Tuple<DateTime?, byte[]>(dateTaken, ms.ToArray());
                }
            }
        }
        #endregion 
    }
}
