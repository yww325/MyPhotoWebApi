using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver.Core.Misc;

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
            _logger.LogInformation($"Ingesting folder:{ingestFolder}, recursive={recursive}");
            var folderIndex = ingestFolder.LastIndexOf("\\");
            var folderPath = folderIndex >=0 ? ingestFolder.Substring(0, folderIndex) : "";
            string parentFolderId = await _folderService.FindFolderIdByPath(folderPath);
            var ingestResult = await IngestOneFolder(ingestFolder, recursive, parentFolderId);  
            return ingestResult;
        }


        private async Task<IngestResult> IngestOneFolder(string path, bool recursive, string parentFolderId)
        {
            var ingestResult = new IngestResult(); 
            var contents = _fileProvider.GetDirectoryContents(path);
            if (!contents.Exists) return ingestResult; //something wrong, folder not exists. 

            var photos = new List<Photo>();
            var tags = path.Split('\\').Select(s => s.ToLowerInvariant()).ToArray();
            var name = tags.Last();
            var currentFolder = await _folderService.GetOrCreateFolder(path, name, parentFolderId);

            foreach (var fileInfo in contents)
            {
                if (fileInfo.IsDirectory)
                {
                    if (recursive)
                    {
                        var subFolderResult = await IngestOneFolder(path + "\\" + fileInfo.Name, true, currentFolder.Id);
                        ingestResult.Absorb(subFolderResult);
                    } 
                    continue;
                } 
                var photo = new Photo()
                {
                    FileName = fileInfo.Name, 
                    Path = path,
                    Tags = tags
                }; 
                ingestResult.TotalFilesFound++;
                var fileName = fileInfo.Name.ToLowerInvariant(); 
                if (fileName.EndsWith(".jpg") || fileName.EndsWith(".jpeg") || fileName.EndsWith(".png"))
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
        private static readonly Regex r = new Regex(":");
        private const int ThumbnailLongSide = 240;
        private static readonly ImageCodecInfo myImageCodecInfo = GetEncoderInfo("image/jpeg");
        private static readonly EncoderParameters myEncoderParameters = GetEncoderParameters();

        private Tuple<DateTime, byte[]> GetDateTakenAndThumbnailFromImage(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (Image myImage = Image.FromStream(fs, false, false))
            {
                // https://stackoverflow.com/questions/180030/how-can-i-find-out-when-a-picture-was-actually-taken-in-c-sharp-running-on-vista
                DateTime dateTaken = DateTime.Now;
                try
                {
                    PropertyItem propItem = myImage.GetPropertyItem(36867);
                    string dateTakenStr = r.Replace(Encoding.UTF8.GetString(propItem.Value), "-", 2);
                    DateTime.TryParse(dateTakenStr, out dateTaken);
                }
                catch (Exception)
                {
                    _logger.LogWarning($"image file {path} can't load dateTaken ");
                }
              
                var ratio = (double)myImage.Width / myImage.Height;
                var width = ratio > 1 ? ThumbnailLongSide : ThumbnailLongSide * ratio;
                var height = ratio < 1 ? ThumbnailLongSide : ThumbnailLongSide / ratio;
                var thumb = myImage.GetThumbnailImage((int)width, (int)height, () => false, IntPtr.Zero);
                using (MemoryStream m = new MemoryStream())
                {
                    // https://docs.microsoft.com/en-us/dotnet/api/system.drawing.image.save?view=netframework-4.8
                    // https://www.c-sharpcorner.com/blogs/convert-an-image-to-base64-string-and-base64-string-to-image 
                    thumb.Save(m, myImageCodecInfo, myEncoderParameters);
                    byte[] imageBytes = m.ToArray();
                    return new Tuple<DateTime, byte[]>(dateTaken, imageBytes);
                }
            }
        }

        private static EncoderParameters GetEncoderParameters()
        {
            var myEncoder = System.Drawing.Imaging.Encoder.Quality;
            var myEncoderParameters = new EncoderParameters(1);
            var myEncoderParameter = new EncoderParameter(myEncoder, 50L);
            myEncoderParameters.Param[0] = myEncoderParameter;
            return myEncoderParameters;
        }

        private static ImageCodecInfo GetEncoderInfo(String mimeType)
        {
            int j;
            ImageCodecInfo[] encoders;
            encoders = ImageCodecInfo.GetImageEncoders();
            for (j = 0; j < encoders.Length; ++j)
            {
                if (encoders[j].MimeType == mimeType)
                    return encoders[j];
            }
            return null;
        }
        #endregion 
    }
}
