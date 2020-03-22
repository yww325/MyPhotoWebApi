using MongoDB.Bson;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Models
{
    public class Photo
    {
        public ObjectId Id { get; set; }
        public string FileName { get; set; }
        public string Path { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
        public string[] Tags { get; set; }
    }
}
