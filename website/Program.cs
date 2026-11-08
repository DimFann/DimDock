using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Website.Data;
using Website.Services;

var builder = WebApplication.CreateBuilder(args);
const long maximumUploadRequestBytes = 512L * 1024 * 1024;

// Add services
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute());
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maximumUploadRequestBytes;
    options.ValueCountLimit = 1024;
});
builder.Services.AddScoped<AdminOnlyTagsProvider>();
builder.Services.AddHttpClient("ThumbnailService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ThumbnailService:BaseAddress"] ??
        "http://thumbnail-service:8080/");
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        new MySqlServerVersion(new Version(10, 11)),
        mySqlOptions => mySqlOptions.EnableRetryOnFailure()
    )
    .EnableSensitiveDataLogging(false)
    .EnableDetailedErrors(false)
    .ConfigureWarnings(warnings => warnings.Log(
        (RelationalEventId.CommandExecuting, LogLevel.Debug),
        (RelationalEventId.CommandExecuted, LogLevel.Debug),
        (RelationalEventId.CommandError, LogLevel.Debug),
        (RelationalEventId.CommandCanceled, LogLevel.Debug)))
);

var app = builder.Build();

// Configure middleware
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        string.Equals(context.Request.Query["handler"], "Upload", StringComparison.OrdinalIgnoreCase) &&
        context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } requestSizeFeature)
    {
        requestSizeFeature.MaxRequestBodySize = maximumUploadRequestBytes;
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Serve static files from wwwroot
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider("/img_src"),
    RequestPath = "/img_src"
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider("/img_db_thumbs"),
    RequestPath = "/img_db_thumbs"
});

app.UseRouting();

// Optional: only if using authentication/authorization
// app.UseAuthentication();
// app.UseAuthorization();

app.MapRazorPages();
app.Run();
