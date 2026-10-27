using System.Collections.Generic;

namespace MyPhotoWebApi.Models
{
    /// <summary>A photo's tags: the request and response of PUT api/v1/Photos/tags.</summary>
    public class PhotoTags
    {
        public string? Id { get; set; }

        /// <summary>
        /// In a request, the tags other than folder names, which replace the photo's current ones; in a response,
        /// all the photo's tags: its folder names first, then the others.
        /// </summary>
        public string[]? Tags { get; set; }
    }
}
