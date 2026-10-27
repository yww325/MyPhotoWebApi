using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Helpers
{
    public static class Util
    {
        public static string[] GenerateTags(string path)
        {
            return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToLowerInvariant()).ToArray();
        }

        /// <summary>
        /// The tags that did not come from <paramref name="path"/>'s folder names, such as AI tags, in their stored order.
        /// </summary>
        public static string[] ExtraTags(string[]? tags, string? path)
        {
            var pathTags = GenerateTags(path ?? "");
            return (tags ?? Array.Empty<string>()).Where(t => !pathTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
        }

        /// <summary>
        /// The tags of a photo after it moves from <paramref name="oldPath"/> to <paramref name="newPath"/>: the new
        /// folder names first, then the photo's other tags (such as AI tags), which a move keeps.
        /// </summary>
        public static string[] MoveTags(string[]? tags, string? oldPath, string? newPath)
        {
            return GenerateTags(newPath ?? "").Concat(ExtraTags(tags, oldPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static async Task<IActionResult> ResponseHelper<T>(Func<Task<T>> serviceCall)
        { 
            try
            {
                T response = await serviceCall();
                return new OkObjectResult(response);
            }
            catch (MyPhotoException e)
            {
                switch (e.ErrorCode)
                {
                    case MyErrorCode.NotFound:
                        return new JsonResult(e) { StatusCode = (int)HttpStatusCode.NotFound };
                    case MyErrorCode.BadRequest:
                        return new JsonResult(e) { StatusCode = (int)HttpStatusCode.BadRequest };
                    case MyErrorCode.Unauthorized:
                        return new JsonResult(e) { StatusCode = (int)HttpStatusCode.Unauthorized };
                    case MyErrorCode.General:
                    default:
                        return new JsonResult(e) { StatusCode = (int)HttpStatusCode.InternalServerError }; 
                } 
            }
            catch (Exception e)
            {
                return new JsonResult(e) { StatusCode = (int)HttpStatusCode.InternalServerError };
            } 
        }
    }
}
