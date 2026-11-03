using System;

namespace MyPhotoWebApi.Models
{
    /// <summary>
    /// The release the server publishes, read from version.json in the APK folder.
    /// Publishing a release means putting the APK next to that file and describing it here.
    /// </summary>
    public class UpdateManifest
    {
        /// <summary>Version name shown to the user, such as "1.0.0".</summary>
        public string Version { get; set; } = "";

        /// <summary>Android versionCode; the client updates when this is larger than its own.</summary>
        public int VersionCode { get; set; }

        /// <summary>Name of the APK file in the folder; defaults to MyPhotoAdmin-{version}.apk.</summary>
        public string? FileName { get; set; }

        /// <summary>What this release changes, shown in the client's update dialog.</summary>
        public string? Notes { get; set; }

        /// <summary>When this release was published.</summary>
        public DateTimeOffset? ReleasedAt { get; set; }
    }
}
