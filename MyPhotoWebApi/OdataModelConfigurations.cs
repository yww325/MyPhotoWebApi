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
        public void Apply(ODataModelBuilder builder, ApiVersion apiVersion)
        { 
            builder.EntitySet<Photo>("Photos");

            builder.EntityType<Photo>().HasKey(ai => ai.FileName); // the call to HasKey is mandatory
        }
    }
}
