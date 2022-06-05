using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SketchArchiveLib;
using SketchArchiveLib.Google;
using System.IO;
using System.Runtime.InteropServices;

namespace DimDock.LinuxArchive
{
    public class Startup
    {
        public Startup()
        {
            string globalSettings = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                globalSettings = @"C:\web\globalSettings.json";
            else
                globalSettings = "/web/globalSettings.json";

            if (string.IsNullOrWhiteSpace(globalSettings) || !File.Exists(globalSettings))
                throw new System.Exception("Missing globalSettings");

            Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile(globalSettings)
                .Build();
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRazorPages();
            services.AddSingleton(Configuration);
            services.AddSingleton(new GDriveReader(
                Configuration["DimDock:ApiKey"],
                Configuration["DimDock:ApiUrlGetFile"],
                Configuration["DimDock:ApiUrlGetFiles"]
                ));
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapRazorPages());
	    app.UsePathBase(Configuration.GetValue<string>("Subdirectory"));
        }
    }
}
