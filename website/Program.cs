using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Website.Data;
using Website.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute());
});
builder.Services.AddScoped<AdminOnlyTagsProvider>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        new MySqlServerVersion(new Version(10, 11)),
        mySqlOptions => mySqlOptions.EnableRetryOnFailure()
    )
    .EnableSensitiveDataLogging(false)
    .EnableDetailedErrors(false)
    .LogTo(_ => { }, LogLevel.None) // 👈 suppress EF Core logging
);

var app = builder.Build();

// Configure middleware
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
