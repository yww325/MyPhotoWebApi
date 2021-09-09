using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System;
using System.Collections.Generic;

namespace MyPhotoWebApi.Helpers
{
    public class MySwaggerConfigOptions : IConfigureOptions<SwaggerGenOptions>
    {
        private readonly IApiVersionDescriptionProvider _apiVersionDescriptionProvider;

        public MySwaggerConfigOptions(IApiVersionDescriptionProvider apiVersionDescriptionProvider)
        {
            this._apiVersionDescriptionProvider = apiVersionDescriptionProvider;
        }
        public void Configure(SwaggerGenOptions options)
        {
            foreach (var description in _apiVersionDescriptionProvider.ApiVersionDescriptions)
            {
                var info = new OpenApiInfo
                {
                    Title ="My Photo",
                    Version = description.ApiVersion.ToString(),
                    Contact = new OpenApiContact
                    {
                        Email ="yww325@gmail.com",
                        Name ="Jason Yan",
                        Url =new Uri("https://github.com/yww325/MyPhotoWebApi"),
                        Extensions = new Dictionary<string, IOpenApiExtension>
                        {
                            { "extra-key", new OpenApiString("extra-info")}
                        }
                    }
                };
                if (description.IsDeprecated)
                {
                    info.Description = "API deprecated";
                }
                options.SwaggerDoc(description.GroupName, info);

            } 
        }
    }
}
