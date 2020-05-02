using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyPhotoWebApi.Models
{
    public class Folder
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("path")]
        public string Path { get; set; }

        [BsonElement("name")]
        public string Name { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        [BsonElement("parent")]
        public string ParentFolderId { get; set; }
    }
}