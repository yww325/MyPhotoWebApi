using Microsoft.AspNet.OData.Builder;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNet.OData.Formatter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Microsoft.OData.Edm;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyPhotoWebApi.Helpers;
using MyPhotoWebApi.Models;
using MyPhotoWebApi.Services;
using System.IO;
using System.Linq;
using Microsoft.OpenApi.Models;
using sw= Microsoft.AspNetCore.Builder.SwaggerBuilderExtensions;

namespace MyPhotoWebApi
{
    public class Startup
    {
        public static string HashedUserPass {get;set;}
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            _myPhotoSettings = new MyPhotoSettings();
            Configuration.GetSection(nameof(MyPhotoSettings)).Bind(_myPhotoSettings);
            _fileProvider = new PhysicalFileProvider(_myPhotoSettings.RootFolder);
            var unHashedUserPass = File.ReadAllText(_myPhotoSettings.UserPassLocation);
            HashedUserPass = MD5Helper.MD5Hash(unHashedUserPass); 
        }

        public IConfiguration Configuration { get; }

        private readonly MyPhotoSettings _myPhotoSettings;
        private readonly PhysicalFileProvider _fileProvider;

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConfiguration(Configuration.GetSection("Logging"));
                loggingBuilder.AddConsole();
                loggingBuilder.AddDebug();
            });

            services.Configure<IISServerOptions>(options =>
            {
                options.AutomaticAuthentication = false;
            });

            //services.AddMvc(options => options.EnableEndpointRouting = false)  // we only need controller
            services.AddControllers().AddNewtonsoftJson()
                .SetCompatibilityVersion(CompatibilityVersion.Version_3_0); 

            services.AddApiVersioning(o =>
            {
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.DefaultApiVersion = new ApiVersion(1, 0);
            });
            services.AddVersionedApiExplorer(o =>
            {
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.SubstituteApiVersionInUrl = true;
                o.GroupNameFormat = "'v'V";
            });

       
            services.AddOData().EnableApiVersioning();
            services.AddODataApiExplorer(o =>
            {
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.SubstituteApiVersionInUrl = true;
                o.GroupNameFormat = "'v'V";
            });


            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
            });


            // Add OpenAPI/Swagger document 
            // services.AddOpenApiDocument(); // add OpenAPI v3 document 
            // services.AddSwaggerDocument(); // add Swagger v2 document

            services.AddCors(c =>
            {
                c.AddPolicy("AllowAnyPolicy", options =>
                {
                    options.AllowAnyOrigin(); 
                    options.AllowAnyHeader();
                    options.AllowAnyMethod();
                }); 
            });
            AddMvcCoreWithSetOdataFormatters(services);
            RegisterMyServices(services);  
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
            //app.UseOpenApi();//from NSwag to replace useSwagger()
            //app.UseSwaggerUi3();  //replace UseSwaggerUI()
             sw.UseSwagger(app); 
             app.UseSwaggerUI(c =>
             {
                 c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
             });

            app.UseCors("AllowAnyPolicy");
            //  app.UseMvc(); // disabled for using endpoints routing in 3.x
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                 endpoints.EnableDependencyInjection(); // https://devblogs.microsoft.com/odata/enabling-endpoint-routing-in-odata/
                endpoints.Select().Filter().Expand().MaxTop(10);
                //endpoints.MapODataRoute("odata", "odata/v{version:apiVersion}", GetEdmModel());
            }); 

            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = _fileProvider,
                RequestPath = new PathString(_myPhotoSettings.FileUrl)
            });
        }

        private IEdmModel GetEdmModel()
        {
            var builder = new ODataConventionModelBuilder();
            builder.EntitySet<Photo>("Photos"); // must upper case first here in oData asp.net core, not matching MongoDB collection 'photos'.
            builder.EntityType<Photo>().HasKey(ai => ai.Id); // the call to HasKey is mandatory

            builder.EntitySet<Folder>("Folders");
            builder.EntityType<Folder>().HasKey(ai => ai.Id);
            return builder.GetEdmModel();
        }

        private static void AddMvcCoreWithSetOdataFormatters(IServiceCollection services)
        {
            services.AddMvcCore(options =>
                {
                    foreach (var outputFormatter in options.OutputFormatters.OfType<ODataOutputFormatter>()
                        .Where(_ => _.SupportedMediaTypes.Count == 0))
                    {
                        outputFormatter.SupportedMediaTypes.Add(
                            new MediaTypeHeaderValue("application/prs.odatatestxx-odata"));
                    }

                    foreach (var inputFormatter in options.InputFormatters.OfType<ODataInputFormatter>()
                        .Where(_ => _.SupportedMediaTypes.Count == 0))
                    {
                        inputFormatter.SupportedMediaTypes.Add(
                            new MediaTypeHeaderValue("application/prs.odatatestxx-odata"));
                    }
                }).AddApiExplorer()
                .AddFormatterMappings()
                .AddDataAnnotations()
                .AddNewtonsoftJson()
                .AddCors()
                .SetCompatibilityVersion(CompatibilityVersion.Version_3_0); 
        }

        private void RegisterMyServices(IServiceCollection services)
        {
            services.AddSingleton<MyPhotoSettings, MyPhotoSettings>(sp => _myPhotoSettings);
            services.AddSingleton<IFileProvider, PhysicalFileProvider>(sp => _fileProvider);
            services.AddSingleton<FileIngestionService, FileIngestionService>();
            services.AddSingleton<PhotoService, PhotoService>();
            services.AddSingleton<FolderService, FolderService>();            
            services.AddSingleton<IMongoClient, MongoClient>(sp => 
                new MongoClient(_myPhotoSettings.ConnectionString));
            services.AddTransient<IMongoDatabase, IMongoDatabase>(sp => 
                sp.GetService<IMongoClient>().GetDatabase(_myPhotoSettings.DatabaseName));
            BsonClassMap.RegisterClassMap<Photo>(cm => {
                cm.AutoMap(); 
                cm.SetIgnoreExtraElements(true);
            });
            BsonClassMap.RegisterClassMap<Folder>(cm => {
                cm.AutoMap();
                cm.SetIgnoreExtraElements(true);
            });
        }
    }
 
}
