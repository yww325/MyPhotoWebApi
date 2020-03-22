using Microsoft.AspNet.OData.Builder;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using MongoDB.Bson.Serialization;
using MyPhotoWebApi.Models;
using System.Collections.Generic;
using System.Linq;
using Microsoft.OData;
using Microsoft.OData.UriParser;

namespace MyPhotoWebApi
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvcCore(options =>
            {
                options.EnableEndpointRouting = false;
            });
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);
            services.AddApiVersioning(o =>
            {
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.DefaultApiVersion = new ApiVersion(1, 0);
            });
            services.AddOData().EnableApiVersioning(); 
          //  services.AddSingleton(sp => new ODataUriResolver() { EnableCaseInsensitive = true });  // this doesn't work, why?
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, VersionedODataModelBuilder modelBuilder)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            
            app.UseHttpsRedirection();
            app.UseMvc(builder =>
            {
                //builder.EnableDependencyInjection(containerBuilder => containerBuilder.AddService(
                //  Microsoft.OData.ServiceLifetime.Singleton,
                //  typeof(ODataUriResolver),
                //      _ => app.ApplicationServices.GetRequiredService<     var resolver = app.ApplicationServices.GetRequiredService<Microsoft.OData.UriParser.ODataUriResolver>();>())  // this doesn't work, why?
                //);
              //  builder.EnableDependencyInjection();
                builder.Select().Expand().Filter().OrderBy().Count().MaxTop(100);
                  builder.MapVersionedODataRoutes("odata", "odata", modelBuilder.GetEdmModels()
              //       , b => {  b.AddService<ODataUriResolver>(Microsoft.OData.ServiceLifetime.Singleton, sp => new CaseInsensitiveResolver());}
                      );
             
                //builder.MapODataServiceRoute("ODataRoute", "odata",
                //     b => b.AddService(Microsoft.OData.ServiceLifetime.Singleton, sp => modelBuilder.GetEdmModels())
                //.AddService<ODataUriResolver>(Microsoft.OData.ServiceLifetime.Singleton, sp => new CaseInsensitiveResolver()));

            }); 

            BsonClassMap.RegisterClassMap<Photo>(cm => {
                cm.AutoMap();
                cm.GetMemberMap(c => c.FileName).SetElementName("fileName");
                cm.GetMemberMap(c => c.Path).SetElementName("path");
                cm.GetMemberMap(c => c.Year).SetElementName("year");
                cm.GetMemberMap(c => c.Month).SetElementName("month");
                cm.GetMemberMap(c => c.Tags).SetElementName("tags");
                cm.SetIgnoreExtraElements(true);
            });
        }
    }
}
