using Microsoft.AspNetCore.OData;
using Microsoft.OData.ModelBuilder;
using Microsoft.AspNetCore.Mvc;
using MyPhotoWebApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
 

namespace MyPhotoWebApi
{
    public class OdataModelConfigurations
    { 
        public void Apply(ODataModelBuilder builder)
        {
            builder.EntitySet<Photo>("Photos");
            builder.EntityType<Photo>().HasKey(ai => ai.Id);

            builder.EntitySet<Folder>("Folders");
            builder.EntityType<Folder>().HasKey(ai => ai.Id);
        }
    }
}
