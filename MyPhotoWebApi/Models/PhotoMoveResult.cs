using Newtonsoft.Json;
using System.Collections.Generic;

namespace MyPhotoWebApi.Models
{
    public class PhotoMoveRequest
    {
        /// <summary>Target folder such as "2026/Trip"; "" is the root. Created if missing.</summary>
        public string? Folder { get; set; }

        public string[]? Ids { get; set; }
    }

    public class PhotoMoveResult
    {
        /// <summary>The normalized target folder.</summary>
        public string Folder { get; set; } = "";

        public List<MovePhotoResult> Results { get; set; } = new();
    }

    public class MovePhotoResult
    {
        public const string Moved = "moved";
        public const string NotFound = "notFound";
        public const string Failed = "failed";

        public string Id { get; set; } = "";

        /// <summary>moved (file and record are in the target folder), notFound (no such photo) or failed (the photo stayed where it was).</summary>
        public string Status { get; set; } = "";

        /// <summary>The folder the photo was in.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? From { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FileName { get; set; }

        /// <summary>The name in the target folder; differs from FileName when renamed to avoid a clash.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SavedAs { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }
    }
}
