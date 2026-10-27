using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MyPhotoWebApi.Services
{
    /// <summary>Reads the technical details a photo's or video's file carries: size, dimensions, camera and EXIF exposure, place.</summary>
    public static class PhotoDetailsReader
    {
        /// <summary>The file's details in display order; a file whose metadata cannot be read still gets its size.</summary>
        public static List<PhotoDetail> Read(string file)
        {
            var items = new List<PhotoDetail>();
            void Add(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) items.Add(new PhotoDetail { Name = name, Value = value.Trim() });
            }

            Add("文件大小", FormatSize(new FileInfo(file).Length));

            IReadOnlyList<MetadataExtractor.Directory> directories;
            try
            {
                directories = ImageMetadataReader.ReadMetadata(file);
            }
            catch (Exception)
            {
                return items;
            }
            T? Dir<T>() where T : MetadataExtractor.Directory => directories.OfType<T>().FirstOrDefault();
            string? Text<T>(int tag) where T : MetadataExtractor.Directory => directories.OfType<T>().Select(d => d.GetDescription(tag)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var ifd0 = Dir<ExifIfd0Directory>();
            var exif = Dir<ExifSubIfdDirectory>();
            var (width, height) = Dimensions(directories);
            if (width > 0 && height > 0) Add("尺寸", $"{width} × {height}");
            Add("时长", Text<QuickTimeMovieHeaderDirectory>(QuickTimeMovieHeaderDirectory.TagDuration));

            var make = ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            var model = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
            // Models usually repeat the maker ("Canon EOS R5"), so the maker is only added when they do not.
            Add("相机", string.IsNullOrEmpty(make) || (model ?? "").StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}");
            Add("镜头", exif?.GetDescription(ExifDirectoryBase.TagLensModel));
            Add("光圈", exif?.GetDescription(ExifDirectoryBase.TagFNumber));
            Add("快门", exif?.GetDescription(ExifDirectoryBase.TagExposureTime));
            Add("ISO", exif?.GetDescription(ExifDirectoryBase.TagIsoEquivalent));
            Add("焦距", exif?.GetDescription(ExifDirectoryBase.TagFocalLength));
            Add("等效焦距", exif?.GetDescription(ExifDirectoryBase.Tag35MMFilmEquivFocalLength));
            Add("闪光灯", exif?.GetDescription(ExifDirectoryBase.TagFlash));
            Add("白平衡", exif?.GetDescription(ExifDirectoryBase.TagWhiteBalanceMode));
            Add("EXIF 拍摄时间", exif?.GetDescription(ExifDirectoryBase.TagDateTimeOriginal));
            Add("方向", ifd0?.GetDescription(ExifDirectoryBase.TagOrientation));
            Add("软件", ifd0?.GetDescription(ExifDirectoryBase.TagSoftware));
            GeoLocation place = default;
            if (Dir<GpsDirectory>()?.TryGetGeoLocation(out place) == true && !place.IsZero)
            {
                Add("GPS", string.Format(CultureInfo.InvariantCulture, "{0:0.00000}, {1:0.00000}", place.Latitude, place.Longitude));
            }
            return items;
        }

        private static (int Width, int Height) Dimensions(IReadOnlyList<MetadataExtractor.Directory> directories)
        {
            foreach (var d in directories)
            {
                var (w, h) = d switch
                {
                    JpegDirectory => (JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight),
                    PngDirectory => (PngDirectory.TagImageWidth, PngDirectory.TagImageHeight),
                    ExifSubIfdDirectory => (ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight),
                    QuickTimeTrackHeaderDirectory => (QuickTimeTrackHeaderDirectory.TagWidth, QuickTimeTrackHeaderDirectory.TagHeight),
                    _ => (-1, -1),
                };
                if (w >= 0 && d.TryGetInt32(w, out var width) && d.TryGetInt32(h, out var height) && width > 0 && height > 0) return (width, height);
            }
            return (0, 0);
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
            >= 1L << 10 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} B",
        };
    }
}
