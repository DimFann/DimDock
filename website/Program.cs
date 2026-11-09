using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using MySqlConnector;
using System.Threading.RateLimiting;
using Website.Data;
using Website.Services;
using Amazon.Runtime;
using Amazon.S3;

var builder = WebApplication.CreateBuilder(args);
const long maximumUploadRequestBytes = 512L * 1024 * 1024;
const long maximumBlogImageRequestBytes = 21L * 1024 * 1024;
var fileStorageProvider = builder.Configuration["FileStorage:Provider"] ?? "Local";
if (string.Equals(fileStorageProvider, "Local", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IFileStore, LocalFileStore>();
}
else if (string.Equals(fileStorageProvider, "Linode", StringComparison.OrdinalIgnoreCase))
{
    var storageOptions = builder.Configuration.GetSection("LinodeStorage").Get<LinodeStorageOptions>()
        ?? throw new InvalidOperationException("LinodeStorage settings are required.");
    storageOptions.Validate();
    builder.Services.AddSingleton(storageOptions);
    builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
        new BasicAWSCredentials(storageOptions.AccessKey, storageOptions.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = storageOptions.Endpoint,
            AuthenticationRegion = storageOptions.Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        }));
    builder.Services.AddSingleton<IFileStore, LinodeFileStore>();
}
else
{
    throw new InvalidOperationException($"Unsupported FileStorage:Provider '{fileStorageProvider}'. Use Local or Linode.");
}

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Add services
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "DimDock.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/Login";
    });
builder.Services.AddRazorPages();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 10,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maximumUploadRequestBytes;
    options.ValueCountLimit = 1024;
});
builder.Services.AddScoped<AdminOnlyTagsProvider>();
builder.Services.AddScoped<FilesystemPersistenceService>();
builder.Services.AddHttpClient("ThumbnailService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ThumbnailService:BaseAddress"] ??
        "http://thumbnail-service:8080/");
    client.Timeout = TimeSpan.FromMinutes(2);
});

var databasePassword = builder.Configuration["Database:Password"];
var databaseConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(databasePassword))
{
    databaseConnectionString = new MySqlConnectionStringBuilder
    {
        Server = builder.Configuration["Database:Host"] ?? "db",
        Port = builder.Configuration.GetValue<uint?>("Database:Port") ?? 3306,
        Database = builder.Configuration["Database:Name"]
            ?? throw new InvalidOperationException("Database:Name is not configured."),
        UserID = builder.Configuration["Database:User"]
            ?? throw new InvalidOperationException("Database:User is not configured."),
        Password = databasePassword
    }.ConnectionString;
}
else if (string.IsNullOrWhiteSpace(databaseConnectionString))
{
    throw new InvalidOperationException(
        "Configure the database with Database:Password and its connection settings, or ConnectionStrings:DefaultConnection.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        databaseConnectionString,
        new MySqlServerVersion(new Version(10, 11)),
        mySqlOptions => mySqlOptions.EnableRetryOnFailure()
    )
    .EnableSensitiveDataLogging(false)
    .EnableDetailedErrors(false)
);

builder.Services.AddLegacyRedirects(builder.Configuration);

var app = builder.Build();

// Configure middleware
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } requestSizeFeature)
    {
        if (string.Equals(context.Request.Query["handler"], "UploadImage", StringComparison.OrdinalIgnoreCase))
        {
            requestSizeFeature.MaxRequestBodySize = maximumBlogImageRequestBytes;
        }
        else if (string.Equals(context.Request.Query["handler"], "Upload", StringComparison.OrdinalIgnoreCase))
        {
            requestSizeFeature.MaxRequestBodySize = maximumUploadRequestBytes;
        }
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
app.UseLegacyRedirects();

// Serve application assets from wwwroot.
app.UseStaticFiles();

var fileContentTypes = new FileExtensionContentTypeProvider();
app.MapMethods("/img_src/{**key}", ["GET", "HEAD"], ServeStoredFileAsync(FileStoreArea.Gallery, addNosniff: false));
app.MapMethods("/img_db_thumbs/{**key}", ["GET", "HEAD"], ServeStoredFileAsync(FileStoreArea.Thumbnails, addNosniff: false));
app.MapMethods("/blogs/assets/{**key}", ["GET", "HEAD"], ServeStoredFileAsync(FileStoreArea.Blogs, addNosniff: true, keyPrefix: "assets/"));
app.MapMethods("/about/assets/{**key}", ["GET", "HEAD"], ServeStoredFileAsync(FileStoreArea.About, addNosniff: true, keyPrefix: "assets/"));

RequestDelegate ServeStoredFileAsync(FileStoreArea area, bool addNosniff, string keyPrefix = "") =>
    async context =>
    {
        var key = context.Request.RouteValues["key"]?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        key = keyPrefix + key;

        try
        {
            var fileStore = context.RequestServices.GetRequiredService<IFileStore>();
            var stream = await fileStore.OpenReadAsync(area, key, context.RequestAborted);
            context.Response.ContentType = fileContentTypes.TryGetContentType(key, out var contentType)
                ? contentType
                : "application/octet-stream";
            if (addNosniff)
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            }

            await Results.Stream(
                stream,
                context.Response.ContentType,
                enableRangeProcessing: true).ExecuteAsync(context);
        }
        catch (FileNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (DirectoryNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    };

app.UseRouting();

app.UseAuthentication();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var isLoginRequest = context.Request.Path.Equals("/Login", StringComparison.OrdinalIgnoreCase);
    if (HttpMethods.IsPost(context.Request.Method) &&
        !isLoginRequest &&
        !context.User.IsInRole("Admin"))
    {
        await context.ChallengeAsync();
        return;
    }

    await next();
});

app.MapRazorPages();
app.Run();
