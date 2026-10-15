using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;

namespace MyPhotoWebApi.Helpers
{
    /// <summary>
    /// Rejects the request with 401 unless the userPass header holds the logged-in token.
    /// Runs as an authorization filter, i.e. before model binding, so an unauthenticated
    /// upload is refused before its body is read.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class RequireUserPassAttribute : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (context.HttpContext.Request.Headers["userPass"] != Startup.HashedUserPass)
            {
                context.Result = new UnauthorizedResult();
            }
        }
    }
}
