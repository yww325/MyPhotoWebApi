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
            return path.Split('\\').Select(s => s.ToLowerInvariant()).ToArray();
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
