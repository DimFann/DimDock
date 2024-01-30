using DimDock.SketchArchiveLib;
using DimDock.SketchArchiveLib.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
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
            if(!Configuration.GetSection("DimDock:MaintenanceFeatures:DriveMap:CacheFile").Exists())
                Configuration["DimDock:MaintenanceFeatures:DriveMap:CacheFile"] = Path.Combine(webRootPath, "dimdock.drivemap.cache.json");
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
                Configuration["DimDock:MaintenanceFeatures:DriveMap:CacheFile"],
                driveReader,
                Configuration["DimDock:RootId"],
                Configuration["DimDock:ResourceKey"],
                Configuration.GetSection("DimDock:MaintenanceFeatures:DriveMap:EnableRefresh").Get<bool>(),
                Logger,
                Configuration.GetSection("DimDock:MaintenanceFeatures:DriveMap:ApiDelayMs").Get<int>(),
                Configuration.GetSection("DimDock:MaintenanceFeatures:DriveMap:RefreshIntervalMinutes").Get<int>()
            );
            TextFileCache textFileCache = new(
                Path.Combine("wwwroot", "md"),
                "*.md"
            );

            services.AddSingleton(driveReader);
            services.AddSingleton(driveMap);
            services.AddSingleton(textFileCache);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseStatusCodePagesWithRedirects("/Error?statusCode={0}");
            app.UseExceptionHandler("/Error");
            app.UseHsts();

            app.UseRouting();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapRazorPages());

            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"node_modules", "marked")),
                RequestPath = "/npm/marked"
            });

            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"node_modules", "dompurify","dist")),
                RequestPath = "/npm/dompurify"
            });
        }
    }
}
