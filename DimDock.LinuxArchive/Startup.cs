using DimDock.SketchArchiveLib.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SketchArchiveLib.Google;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DimDock.LinuxArchive
{
    public class Startup
    {
        public Startup()
        {
            string webRootPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                webRootPath = @"C:\web\";
            else
                webRootPath = "/web/";

            if (!Directory.Exists(webRootPath))
                throw new Exception($"Config root path does not exist, {webRootPath}");

            string globalSettings = Path.Combine(webRootPath, "globalSettings.json");
            if (!File.Exists(globalSettings))
                throw new Exception($"Global settings file was not found, {globalSettings}");

            string logPath = Path.Combine(webRootPath, "log");
            if (!Directory.Exists(logPath))
                Directory.CreateDirectory(logPath);

            Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile(globalSettings)
                .Build();

            Logger = LoggerFactory.Create(builder =>
            {
                if (Configuration.GetSection("LogToConsole").Get<bool>())
                    builder.AddConsole();
                if (Configuration.GetSection("LogToFile").Get<bool>())
                    builder.AddFile(Path.Combine(logPath, "dimdock.{Date}.log"));
            }).CreateLogger("DimDock");

            // Set this to a known value if not provided.
            if(!Configuration.GetSection("DimDock:HiddenFeatures:DriveMap:CacheFile").Exists())
                Configuration["DimDock:HiddenFeatures:DriveMap:CacheFile"] = Path.Combine(webRootPath, "dimdock.drivemap.cache.json");
        }

        public ILogger Logger { get; }
        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRazorPages();
            services.AddSingleton(Configuration);

            GDriveReader driveReader = new(
                Configuration["DimDock:ApiKey"], 
                Configuration["DimDock:ApiUrlGetFiles"]
            );
            DriveMap driveMap = new(
                Configuration["DimDock:HiddenFeatures:DriveMap:CacheFile"],
                driveReader,
                Configuration["DimDock:RootId"],
                Configuration["DimDock:ResourceKey"],
                Configuration.GetSection("DimDock:HiddenFeatures:DriveMap:EnableRefresh").Get<bool>(),
                Logger,
                Configuration.GetSection("DimDock:HiddenFeatures:DriveMap:ApiDelayMs").Get<int>(),
                Configuration.GetSection("DimDock:HiddenFeatures:DriveMap:RefreshIntervalMinutes").Get<int>()
            );

            services.AddSingleton(driveReader);
            services.AddSingleton(driveMap);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRewriter(new RewriteOptions().Add(SketchTraverse));

            app.UseStatusCodePagesWithReExecute("/Error");
            app.UseExceptionHandler("/Error");
            app.UseHsts();

            app.UseRouting();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapRazorPages());
        }

        public void SketchTraverse(RewriteContext context)
        {
            var request = context.HttpContext.Request;
            var path = request.Path.ToString();

            // TODO:    This is kind of hacky, but I personally will never make a folder
            //          Named "Viewer" at the root of my archive.
            //          If you intend to do that feel free to make the change.
            //          I'm avoiding it now because it'll break links people have already copied.
            if (path.StartsWith("/Sketch/") && !path.StartsWith("/Sketch/Viewer"))
            {
                path = path[8..];
                if (!string.IsNullOrWhiteSpace(path))
                {
                    context.HttpContext.Request.Path = $"/Sketch?url={path}";
                }
            }
        }
    }
}
