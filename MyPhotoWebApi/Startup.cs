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
using System;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNet.OData.Formatter;
using Microsoft.OpenApi.Models;
using System.Reflection;
using System.IO;
using Swashbuckle.AspNetCore.Swagger;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using NSwag.AspNetCore;

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
            }).AddApiExplorer()
            .AddFormatterMappings()
            .AddDataAnnotations()
            .AddJsonFormatters()
            .AddCors()
            .SetCompatibilityVersion(CompatibilityVersion.Version_2_2);

            // Register the Swagger generator, defining 1 or more Swagger documents, obselete, this is replaced by OpenAPIDocument from NSwag
            //services.AddSwaggerGen(c =>
            //{ 
            //    c.SwaggerDoc("v1", new OpenApiInfo { Title = "My photo API", Version = "v1" }); 

            //    // Set the comments path for the Swagger JSON and UI.
            //    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            //    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            //    c.IncludeXmlComments(xmlPath);
            //});
            services.AddOpenApiDocument();
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);
            services.AddApiVersioning(o =>
            {
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.DefaultApiVersion = new ApiVersion(1, 0);
            }).AddVersionedApiExplorer(o=>
            {
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.SubstituteApiVersionInUrl = true;
                o.GroupNameFormat = "'v'V";
            });

          

            services.AddOData().EnableApiVersioning(); 
            services.AddODataApiExplorer(); 

          
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

            // Enable middleware to serve generated Swagger as a JSON endpoint.
            //https://github.com/RicoSuter/NSwag/wiki/AspNetCore-Middleware
            app.UseOpenApi();//from NSwag to replace useSwagger()
            app.UseSwaggerUi3();  //replace UseSwaggerUI()

            app.UseMvc();             
            app.UseWhen(
                    context => context.Request.Path.StartsWithSegments("/odata", StringComparison.InvariantCultureIgnoreCase),

                    app1 => app1

                        .UseMvc(builder =>

                        {
                          
                            var mvcOptions = builder.ApplicationBuilder.ApplicationServices

                                .GetService<Microsoft.Extensions.Options.IOptions<MvcOptions>>();



                            foreach (var outputFormatter in mvcOptions.Value.OutputFormatters.OfType<ODataOutputFormatter>().Where(_ => _.SupportedMediaTypes.Count == 0))

                            {

                                outputFormatter.SupportedMediaTypes.Add(new MediaTypeHeaderValue("application/prs.odatatestxx-odata"));

                            }

                            foreach (var inputFormatter in mvcOptions.Value.InputFormatters.OfType<ODataInputFormatter>().Where(_ => _.SupportedMediaTypes.Count == 0))

                            {

                                inputFormatter.SupportedMediaTypes.Add(new MediaTypeHeaderValue("application/prs.odatatestxx-odata"));

                            }



                            builder.Select().Expand().Filter().OrderBy().Count().MaxTop(100);

                            builder.MapVersionedODataRoutes("odataRoutes", "odata", modelBuilder.GetEdmModels());

                        }));

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
