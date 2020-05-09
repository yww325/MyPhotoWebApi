using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace MyPhotoWebApi.Models
{
    public class Photo
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("fileName")]
        public string FileName { get; set; }

        [BsonElement("mediaType")]
        public string MediaType { get; set; }

        [BsonElement("path")]
        public string Path { get; set; }

        [BsonElement("dateTaken")]
        public DateTime DateTaken { get; set; }  
        
        [BsonElement("tags")]
        public string[] Tags { get; set; }

        [BsonElement("thumbnail")]
        public Byte[] Thumbnail { get; set; } 
        
        [BsonElement("private")]
        public bool IsPrivate { get; set; }
    }
}
