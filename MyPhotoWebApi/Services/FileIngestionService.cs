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

namespace MyPhotoWebApi.Services
{
    public class FileIngestionService
    {
        private readonly ILogger<FileIngestionService> _logger;
        private readonly IMongoCollection<Photo> _mongoCollection;
        private readonly IFileProvider _fileProvider;

        public FileIngestionService(ILogger<FileIngestionService> logger, IMongoDatabase mongoDatabase, IFileProvider fileProvider)
        {
            _logger = logger;
            _mongoCollection = mongoDatabase.GetCollection<Photo>("photos"); ;
            _fileProvider = fileProvider;
        }

        public async Task<IngestResult> Ingest(string ingestFolder, bool recursive)
        { 
            _logger.LogInformation($"Ingesting folder:{ingestFolder}, recusive={recursive}"); 
            var ingestResult = await IngestOneFolder(ingestFolder, recursive); 
            return ingestResult;
        }

        private async Task<IngestResult> IngestOneFolder(string path, bool recursive)
        {
            var ingestResult = new IngestResult();
            var photos = new List<Photo>();
            var tags = path.Split('\\');
            var contents = _fileProvider.GetDirectoryContents(path);
            foreach (var fileInfo in contents)
            {
                if (fileInfo.IsDirectory)
                {
                    var subFolderResult = await IngestOneFolder(path + "\\" + fileInfo.Name, recursive);
                    ingestResult.Absorb(subFolderResult);
                    continue;
                }

                var photo = new Photo()
                {
                    FileName = fileInfo.Name,
                    Path = path,
                    Tags = tags
                }; 
                ingestResult.TotalFilesFound++;
               
                if (fileInfo.Name.EndsWith(".jpg") || fileInfo.Name.EndsWith(".jpeg") || fileInfo.Name.EndsWith(".png"))
                {
                    photo.MediaType = "photo";
                    var (dateTime, imageBytes) = GetDateTakenAndThumbnailFromImage(fileInfo.PhysicalPath);
                    photo.DateTaken = dateTime;
                    photo.Thumbnail = imageBytes;
                    ingestResult.PhotosFound++;
                } 
                else if (fileInfo.Name.EndsWith(".wav"))
                {
                    photo.MediaType = "sound";
                    ingestResult.SoundsFound++;
                }
                else if (fileInfo.Name.EndsWith(".avi"))
                {
                    photo.MediaType = "video";
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
                await _mongoCollection.InsertManyAsync(photos, new InsertManyOptions() { IsOrdered = false });
            }
           
            return ingestResult;
        }

        #region Image Processing
        private static readonly Regex r = new Regex(":");
        private const int ThumbnailLongSide = 240;
        private static readonly ImageCodecInfo myImageCodecInfo = GetEncoderInfo("image/jpeg");
        private static readonly EncoderParameters myEncoderParameters = GetEncoderParameters();

        private static Tuple<DateTime, byte[]> GetDateTakenAndThumbnailFromImage(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (Image myImage = Image.FromStream(fs, false, false))
            {
                // https://stackoverflow.com/questions/180030/how-can-i-find-out-when-a-picture-was-actually-taken-in-c-sharp-running-on-vista
                PropertyItem propItem = myImage.GetPropertyItem(36867);
                string dateTakenStr = r.Replace(Encoding.UTF8.GetString(propItem.Value), "-", 2);
                DateTime.TryParse(dateTakenStr, out var dateTaken);
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
