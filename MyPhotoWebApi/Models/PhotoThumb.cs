namespace MyPhotoWebApi.Models
{
    /// <summary>A photo as returned by GET api/v1/Photos/thumbs: the grid fields plus the file size.</summary>
    public class PhotoThumb : Photo
    {
        /// <summary>Size of the file on disk in bytes, or null when the file is missing.</summary>
        public long? Size { get; set; }

        public static PhotoThumb From(Photo photo, long? size) => new PhotoThumb
        {
            Id = photo.Id,
            FileName = photo.FileName,
            MediaType = photo.MediaType,
            Path = photo.Path,
            DateTaken = photo.DateTaken,
            Tags = photo.Tags,
            Thumbnail = photo.Thumbnail,
            IsPrivate = photo.IsPrivate,
            Size = size,
        };
    }
}
