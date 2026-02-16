using Microsoft.AspNetCore.OData;
using Microsoft.OData.ModelBuilder;
using Microsoft.AspNetCore.Mvc.Versioning;
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
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Serialization;
using System;
using System.Text;
using Swashbuckle.AspNetCore.SwaggerGen;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.ApiExplorer;

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
            var unHashedUserPass = File.ReadAllText(_myPhotoSettings.UserPassLocation).Trim();
            HashedUserPass = MD5Helper.MD5Hash(unHashedUserPass); 
        }

        public IConfiguration Configuration { get; }

        private readonly MyPhotoSettings _myPhotoSettings;
        private readonly PhysicalFileProvider _fileProvider;

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            Console.OutputEncoding = Encoding.UTF8;
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
                .AddOData(options => options.Select().Filter().OrderBy().Expand().Count().SetMaxTop(null)
                    .AddRouteComponents("odata/v1", GetEdmModel()));

            services.AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
            });
            services.AddVersionedApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'VVV"; // 这样就会生成 v1, v1.1 等格式的 GroupName
                o.SubstituteApiVersionInUrl = true;
            });

       
            // services.AddODataApiExplorer(o =>
            // {
            // });


            services.AddOpenApiDocument();


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
            // AddMvcCoreWithSetOdataFormatters(services); // Removed for OData 8.x compatibility
            RegisterMyServices(services);
            services.AddScoped<ValidateModelAttribute>();
        } 

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IApiVersionDescriptionProvider provider)
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
            
            // app.UseHttpsRedirection();

            // Enable middleware to serve generated Swagger as a JSON endpoint.
            //https://github.com/RicoSuter/NSwag/wiki/AspNetCore-Middleware
            //app.UseOpenApi();//from NSwag to replace useSwagger()
            //app.UseSwaggerUi3();  //replace UseSwaggerUI()
             app.UseOpenApi();
             app.UseSwaggerUi(c =>
             {
                 c.Path = "/swagger";
             });

            app.UseCors("AllowAnyPolicy");
            //  app.UseMvc(); // disabled for using endpoints routing in 3.x
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                // OData 8.x configuration simplified
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
            builder.EnableLowerCamelCase();
            var photos = builder.EntitySet<Photo>("Photos");
            photos.EntityType.Property(p => p.Thumbnail).IsNullable();
            builder.EntitySet<Folder>("Folders");
            return builder.GetEdmModel();
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
