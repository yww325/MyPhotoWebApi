using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Models
{
    public class IngestResult
    {
        public int TotalFilesFound { get; set; }
        public int PhotosFound { get; set; }
        public int SoundsFound { get; set; }
        public int VideosFound { get; set; }
        public int UnknownFilesFound { get; set; }

        internal void Absorb(IngestResult subFolderResult)
        {
            TotalFilesFound += subFolderResult.TotalFilesFound;
            PhotosFound += subFolderResult.PhotosFound;
            SoundsFound += subFolderResult.SoundsFound;
            VideosFound += subFolderResult.VideosFound;
            UnknownFilesFound += subFolderResult.UnknownFilesFound;
        }
    }
}
