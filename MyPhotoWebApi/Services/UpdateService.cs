using Microsoft.Extensions.Logging;
using MyPhotoWebApi.Models;
using System;
using System.IO;
using System.Text.Json;

namespace MyPhotoWebApi.Services
{
    /// <summary>
    /// The app release the server publishes: version.json in the APK folder describes the release, and the
    /// APK file itself sits next to it. The folder is configured with MyPhotoSettings.ApkFolder.
    /// </summary>
    public class UpdateService
    {
        /// <summary>The manifest in the APK folder that describes the published release.</summary>
        public const string ManifestFileName = "version.json";

        /// <summary>Name of the APK when the manifest does not name one.</summary>
        public const string ApkPrefix = "MyPhotoAdmin-";

        /// <summary>Where the APK is downloaded from, relative to the API root.</summary>
        public const string DownloadUrlPrefix = "api/v1/Update/apk/";

        private static readonly JsonSerializerOptions ManifestOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly ILogger<UpdateService> _logger;
        private readonly MyPhotoSettings _myPhotoSettings;

        public UpdateService(ILogger<UpdateService> logger, MyPhotoSettings myPhotoSettings)
        {
            _logger = logger;
            _myPhotoSettings = myPhotoSettings;
        }

        /// <summary>The folder the APKs live in, or null when it is not configured.</summary>
        public string? ApkFolder => string.IsNullOrWhiteSpace(_myPhotoSettings.ApkFolder) ? null : _myPhotoSettings.ApkFolder;

        /// <summary>
        /// The release the server publishes, or null when there is none: the folder is not configured, the
        /// manifest is missing or unreadable, or the APK it names is not in the folder.
        /// </summary>
        public AppUpdate? CurrentRelease()
        {
            var folder = ApkFolder;
            if (folder == null) return null;

            var manifestPath = Path.Combine(folder, ManifestFileName);
            if (!File.Exists(manifestPath)) return null;

            UpdateManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), ManifestOptions);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                _logger.LogError(e, "Cannot read the update manifest {ManifestPath}.", manifestPath);
                return null;
            }

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version)) return null;

            var fileName = FileNameOf(manifest);
            var apkPath = ResolveApkPath(fileName);
            if (apkPath == null)
            {
                _logger.LogWarning("The update manifest {ManifestPath} names {FileName}, which is not in the APK folder.", manifestPath, fileName);
                return null;
            }

            return new AppUpdate
            {
                Version = manifest.Version,
                VersionCode = manifest.VersionCode,
                FileName = fileName,
                Size = new FileInfo(apkPath).Length,
                Notes = manifest.Notes,
                ReleasedAt = manifest.ReleasedAt,
                DownloadUrl = DownloadUrlPrefix + Uri.EscapeDataString(fileName),
                ApkPath = apkPath,
            };
        }

        /// <summary>
        /// The full path of the APK named <paramref name="fileName"/> in the APK folder, or null when the
        /// name is not a plain APK file in that folder or the file is not there.
        /// </summary>
        public string? ResolveApkPath(string? fileName)
        {
            var folder = ApkFolder;
            if (folder == null || string.IsNullOrWhiteSpace(fileName)) return null;

            // Only a plain file name: no directory of its own, and an APK.
            if (fileName.Contains('/') || fileName.Contains('\\')) return null;
            if (!fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return null;

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var path = Path.GetFullPath(Path.Combine(root, fileName));
            // The file must sit directly in the APK folder.
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal)) return null;

            return File.Exists(path) ? path : null;
        }

        /// <summary>The name of the APK of a release: the one the manifest names, or one made from its version.</summary>
        public static string FileNameOf(UpdateManifest manifest) =>
            string.IsNullOrWhiteSpace(manifest.FileName) ? $"{ApkPrefix}{manifest.Version}.apk" : manifest.FileName!.Trim();
    }
}
