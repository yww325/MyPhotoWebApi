using Newtonsoft.Json;
using System.Collections.Generic;

namespace MyPhotoWebApi.Models
{
    public class UploadResult
    {
        /// <summary>The normalized target folder the files were uploaded into.</summary>
        public string Folder { get; set; } = "";

        public List<UploadFileResult> Results { get; set; } = new();
    }

    public class UploadFileResult
    {
        public const string Created = "created";
        public const string Duplicate = "duplicate";
        public const string Rejected = "rejected";
        public const string Failed = "failed";

        /// <summary>The file name sent by the client.</summary>
        public string FileName { get; set; } = "";

        /// <summary>created, duplicate (identical file already there), rejected (invalid input) or failed.</summary>
        public string Status { get; set; } = "";

        /// <summary>The name the file has on the server; differs from FileName when renamed to avoid a clash.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SavedAs { get; set; }

        /// <summary>Photo id of the saved (or already existing) file.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Id { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }
    }
}
