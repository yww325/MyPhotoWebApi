using Newtonsoft.Json;
using System.Collections.Generic;

namespace MyPhotoWebApi.Models
{
    public class PhotoDeleteResult
    {
        public List<DeletePhotoResult> Results { get; set; } = new();
    }

    public class DeletePhotoResult
    {
        public const string Deleted = "deleted";
        public const string NotFound = "notFound";
        public const string Failed = "failed";

        public string Id { get; set; } = "";

        /// <summary>deleted (file and record are gone), notFound (no such photo) or failed (nothing was deleted).</summary>
        public string Status { get; set; } = "";

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Path { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FileName { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }
    }
}
