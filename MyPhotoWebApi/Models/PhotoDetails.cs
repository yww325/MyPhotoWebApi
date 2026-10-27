using System.Collections.Generic;

namespace MyPhotoWebApi.Models
{
    /// <summary>Technical details of a photo's file, as returned by GET api/v1/Photos/details.</summary>
    public class PhotoDetails
    {
        public string Id { get; set; } = "";

        /// <summary>Labelled values in display order, such as ("相机", "Apple iPhone 15"); only the ones the file has.</summary>
        public List<PhotoDetail> Items { get; set; } = new();
    }

    public class PhotoDetail
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
