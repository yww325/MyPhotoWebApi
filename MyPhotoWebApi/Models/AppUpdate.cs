using Newtonsoft.Json;
using System;

namespace MyPhotoWebApi.Models
{
    /// <summary>The release the server publishes, as returned by GET api/v1/Update/latest.</summary>
    public class AppUpdate
    {
        /// <summary>Version name, such as "1.0.0".</summary>
        [JsonProperty("version")]
        public string Version { get; set; } = "";

        /// <summary>Android versionCode; the client updates when this is larger than its own.</summary>
        [JsonProperty("versionCode")]
        public int VersionCode { get; set; }

        /// <summary>Name of the APK file.</summary>
        [JsonProperty("fileName")]
        public string FileName { get; set; } = "";

        /// <summary>Size of the APK in bytes.</summary>
        [JsonProperty("size")]
        public long Size { get; set; }

        /// <summary>What this release changes.</summary>
        [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
        public string? Notes { get; set; }

        /// <summary>When this release was published.</summary>
        [JsonProperty("releasedAt", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? ReleasedAt { get; set; }

        /// <summary>Where to download the APK, relative to the API root.</summary>
        [JsonProperty("downloadUrl")]
        public string DownloadUrl { get; set; } = "";

        /// <summary>Where the APK is on the server; not sent to the client.</summary>
        [JsonIgnore]
        public string ApkPath { get; set; } = "";
    }
}
