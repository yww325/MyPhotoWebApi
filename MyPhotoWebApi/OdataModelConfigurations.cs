using Microsoft.AspNet.OData.Builder;
using Microsoft.AspNetCore.Mvc;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
 

namespace MyPhotoWebApi
{
    public class OdataModelConfigurations : IModelConfiguration
    { 
        public void Apply(ODataModelBuilder builder, ApiVersion apiVersion, string? routePrefix)
        {
            builder.EntitySet<Photo>("Photos"); // must upper case first here in oData asp.net core, not matching MongoDB collection 'photos'.
            builder.EntityType<Photo>().HasKey(ai => ai.Id); // the call to HasKey is mandatory

            builder.EntitySet<Folder>("Folders");
            builder.EntityType<Folder>().HasKey(ai => ai.Id);
        }
    }
}
