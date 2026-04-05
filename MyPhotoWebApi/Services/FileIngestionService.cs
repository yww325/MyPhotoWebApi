using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

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
                var photo = new Photo()
                {
                    FileName = fileInfo.Name, 
                    Path = path,
                    Tags = tags
                }; 
                ingestResult.TotalFilesFound++;
                var fileName = fileInfo.Name.ToLowerInvariant(); 
                if (fileName.EndsWith(".jpg") || fileName.EndsWith(".jpeg") || fileName.EndsWith(".png") || fileName.EndsWith(".bmp"))
                {
                    photo.MediaType = "photo";
                    var (dateTime, imageBytes) = GetDateTakenAndThumbnailFromImage(fileInfo.PhysicalPath);
                    photo.DateTaken = dateTime;
                    photo.Thumbnail = imageBytes;
                    ingestResult.PhotosFound++;
                } 
                else if (fileName.EndsWith(".wav"))
                {
                    photo.MediaType = "sound";
                    photo.DateTaken = DateTime.Now;
                    ingestResult.SoundsFound++;
                }
                else if (fileName.EndsWith(".avi") || fileName.EndsWith(".mp4") || fileName.EndsWith(".3gp"))
                {
                    photo.MediaType = "video";
                    photo.DateTaken = DateTime.Now;
                    ingestResult.VideosFound++;
                }
                else
                {
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

        #region Image Processing
        private const int ThumbnailLongSide = 240;

        private Tuple<DateTime, byte[]> GetDateTakenAndThumbnailFromImage(string path)
        {
            DateTime dateTaken = DateTime.Now;
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
                    return new Tuple<DateTime, byte[]>(dateTaken, ms.ToArray());
                }
            }
        }
        #endregion 
    }
}
