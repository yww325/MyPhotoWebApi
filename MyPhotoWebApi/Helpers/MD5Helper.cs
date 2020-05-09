using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Helpers
{
    public static class MD5Helper
    {
        public static string MD5Hash(string unHashed)
        {
            using (var md5 = MD5.Create())
            {
                var result = md5.ComputeHash(Encoding.UTF8.GetBytes(unHashed));
                return BitConverter.ToString(result).Replace("-", "").ToLowerInvariant();
            }
        } 
    }
}
