using System.Collections.Generic;

namespace MyPhotoWebApi.Models
{
    /// <summary>One node of the folder tree returned by GET api/v1/Folders/tree.</summary>
    public class FolderNode
    {
        /// <summary>Folder entity id, or null for the root and for paths that only exist on photos.</summary>
        public string? Id { get; set; }

        /// <summary>Path relative to the photo root, using "/" (the root is "").</summary>
        public string Path { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>Number of photos/videos directly in this folder.</summary>
        public int PhotoCount { get; set; }

        /// <summary>Number of photos/videos in this folder and all its subfolders.</summary>
        public int TotalCount { get; set; }

        public List<FolderNode> Children { get; set; } = new();
    }
}
