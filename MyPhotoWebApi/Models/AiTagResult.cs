using Newtonsoft.Json;
using System.Collections.Generic;

namespace MyPhotoWebApi.Models
{
    public class AiTagRequest
    {
        public string[]? Ids { get; set; }

        /// <summary>Also tag photos that already have AI tags, replacing them; otherwise those photos are skipped.</summary>
        public bool Regenerate { get; set; }
    }

    public class AiTagResult
    {
        public List<AiTagPhotoResult> Results { get; set; } = new();
    }

    public class AiTagPhotoResult
    {
        public const string Tagged = "tagged";
        public const string Skipped = "skipped";
        public const string NotFound = "notFound";
        public const string Failed = "failed";

        public const string AlreadyTagged = "alreadyTagged";
        public const string NoThumbnail = "noThumbnail";

        public string Id { get; set; } = "";

        /// <summary>tagged, skipped (see Reason), notFound (no such photo) or failed (the record was not changed; see Error).</summary>
        public string Status { get; set; } = "";

        /// <summary>Why a photo was skipped: alreadyTagged (it has AI tags and Regenerate is false) or noThumbnail.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Reason { get; set; }

        /// <summary>All the photo's tags afterwards: its folder names, then its AI tags.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string[]? Tags { get; set; }

        /// <summary>The AI tags this request stored.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string[]? Added { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }
    }
}
