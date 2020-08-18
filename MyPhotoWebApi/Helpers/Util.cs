using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Helpers
{
    public static class Util
    {
        public static string[] GenerateTags(string path)
        {
            return path.Split('\\').Select(s => s.ToLowerInvariant()).ToArray();
        }
    }
}
