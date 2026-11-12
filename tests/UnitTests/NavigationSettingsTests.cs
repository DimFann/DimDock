using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Website.Data;
using Website.Models;
using Website.Pages;
using Website.Services;

namespace UnitTests;

public sealed partial class NavigationSettingsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dimdock-navigation-tests-{Guid.NewGuid():N}");
    private readonly WebApplication _app;
    private readonly IServiceScope _scope;
    private IServiceProvider Services => _scope.ServiceProvider;
    [Theory]
    [InlineData("upload.example.com", true)]
    [InlineData("dimdock.com", false)]
    public async Task HeaderNavigationUsesMainSiteOnlyOnUploadHost(string host, bool external)
    {
        Configuration["Uploads:Mode"] = "Bypass";
        Configuration["Uploads:BypassHost"] = "upload.example.com";
        Configuration["Uploads:MainSiteUrl"] = "https://dimdock.com";
        var model = new LoginModel(Configuration, Services.GetRequiredService<IWebHostEnvironment>())
        {
            PageContext = CreatePageContext("/Login", admin: true)
        };
        model.Request.Host = new HostString(host);
        var html = await RenderAsync("/Pages/Login.cshtml", model);
        var header = html[..html.IndexOf("</header>", StringComparison.Ordinal)];
        var links = Regex.Matches(header, "<a\\b[^>]*href=\"([^\"]+)\"");
        Assert.Equal(8, links.Count);
        foreach (Match link in links)
        {
            Assert.Equal(external, link.Groups[1].Value.StartsWith("https://dimdock.com/", StringComparison.Ordinal));
        }
        Assert.Contains("handler=Logout", header);
    }

    [Theory]
    [InlineData("upload.example.com")]
    [InlineData("dimdock.com")]
    public async Task HeaderLoginStaysOnCurrentHostAndPreservesReturnUrl(string host)
    {
        Configuration["Uploads:Mode"] = "Bypass";
        Configuration["Uploads:BypassHost"] = "upload.example.com";
        Configuration["Uploads:MainSiteUrl"] = "https://dimdock.com";
        var model = new LoginModel(Configuration, Services.GetRequiredService<IWebHostEnvironment>())
        {
            PageContext = CreatePageContext("/Login", admin: false)
        };
        model.Request.Host = new HostString(host);
        model.Request.QueryString = new QueryString("?ReturnUrl=%2FGallery%2FAlbum%3Fsort%3Ddate");
        var html = await RenderAsync("/Pages/Login.cshtml", model);
        var header = html[..html.IndexOf("</header>", StringComparison.Ordinal)];
        var login = System.Net.WebUtility.HtmlDecode(
            Regex.Match(header, "href=\"([^\"]+)\"[^>]*>Login</a>").Groups[1].Value);
        Assert.Equal("/Login?ReturnUrl=%2FGallery%2FAlbum%3Fsort%3Ddate", login);
        Assert.Contains("<form method=\"post\">", html);
    }

    private IConfiguration Configuration => Services.GetRequiredService<IConfiguration>();
    private IFileStore Store => Services.GetRequiredService<IFileStore>();
    private NavigationSettingsProvider Provider => Services.GetRequiredService<NavigationSettingsProvider>();

    public NavigationSettingsTests()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AdminModel).Assembly.GetName().Name,
            ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "website")),
            EnvironmentName = "Production"
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileStorage:RootPath"] = _root,
            ["AdminAuth:Username"] = "test-admin",
            ["AdminAuth:Password"] = "test-password-only"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "DimDock.Admin";
            options.Cookie.Domain = AdminAccess.GetCookieDomain(builder.Configuration);
        });
        builder.Services.AddRateLimiter(options =>
            options.AddFixedWindowLimiter("admin-login", limiter =>
            {
                limiter.PermitLimit = 100;
                limiter.Window = TimeSpan.FromMinutes(1);
            }));
        builder.Services.AddRazorPages();
        builder.Services.AddHttpClient();
        builder.Services.AddDbContext<AppDbContext>();
        builder.Services.AddSingleton<IFileStore, LocalFileStore>();
        builder.Services.AddHostedService<FileStoreInitializationService>();
        builder.Services.AddScoped<NavigationSettingsProvider>();
        builder.Services.AddSingleton<GalleryFilterPreferences>();
        builder.Services.AddScoped<AdminOnlyTagsProvider>();
        builder.Services.AddScoped<FilesystemPersistenceService>();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseMiddleware<NavigationAccessMiddleware>();
        _app.UseRateLimiter();
        _app.MapRazorPages();
        _scope = _app.Services.CreateScope();
        _app.MapGet("/test/navigation-form", async (HttpContext http) =>
        {
            var model = CreateAdmin();
            model.PageContext.HttpContext = http;
            model.NavigationInput = await Provider.GetAsync();
            return Results.Content(await RenderAsync("/Pages/Admin.cshtml", model), "text/html");
        });
    }

    public Task InitializeAsync() => _app.StartAsync();

    public async Task DisposeAsync()
    {
        _scope.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFileUsesConfigurationDefaultsWithoutWriting()
    {
        Configuration["Navigation:Gallery"] = "false";
        Configuration["Navigation:Blog"] = "false";
        Configuration["Navigation:Commissions"] = "false";

        var settings = await Provider.GetAsync();

        Assert.False(settings.Gallery);
        Assert.True(settings.Search);
        Assert.False(settings.Blog);
        Assert.False(settings.Commissions);
        Assert.False(await Store.FileExistsAsync(FileStoreArea.Admin, "navigation.json"));
    }

    [Fact]
    public async Task SavedSettingsOverrideConfigurationAndSurviveNewProvider()
    {
        await Provider.SaveAsync(new NavigationSettings { Gallery = false, Search = false, Blog = false, Commissions = false });
        Configuration["Navigation:Gallery"] = "true";
        Configuration["Navigation:Search"] = "true";
        Configuration["Navigation:Blog"] = "true";
        Configuration["Navigation:Commissions"] = "true";
        var freshProvider = new NavigationSettingsProvider(Configuration, Store);

        var settings = await freshProvider.GetAsync();

        Assert.False(settings.Gallery);
        Assert.False(settings.Search);
        Assert.False(settings.Blog);
        Assert.False(settings.Commissions);
        using var document = JsonDocument.Parse(await Store.ReadAllTextAsync(FileStoreArea.Admin, "navigation.json"));
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
        Assert.False(document.RootElement.GetProperty("Gallery").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_root, "admin", "navigation.json")));

        await freshProvider.SaveAsync(new NavigationSettings());
        Assert.True((await Provider.GetAsync()).Gallery);
        Assert.True((await Provider.GetAsync()).Search);
        Assert.True((await Provider.GetAsync()).Blog);
        Assert.True((await Provider.GetAsync()).Commissions);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"Gallery":"false","Search":true,"Blog":true}""")]
    public async Task InvalidStoredSettingsAreNotSilentlyReplaced(string json)
    {
        await Store.WriteAllTextAsync(FileStoreArea.Admin, "navigation.json", json);

        await Assert.ThrowsAsync<JsonException>(() => Provider.GetAsync());
        Assert.Equal(json, await Store.ReadAllTextAsync(FileStoreArea.Admin, "navigation.json"));
    }

    [Fact]
    public async Task NullStoredSettingsAreRejected()
    {
        await Store.WriteAllTextAsync(FileStoreArea.Admin, "navigation.json", "null");
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider.GetAsync());
    }

    [Fact]
    public async Task SaveHandlerPersistsAndRedirectsWithConfirmation()
    {
        var model = CreateAdmin();
        AddAntiforgeryToken(model.HttpContext);
        model.NavigationInput = new NavigationSettings { Gallery = false, Search = true, Blog = false };

        var result = Assert.IsType<RedirectToPageResult>(await model.OnPostSaveNavigationAsync());

        Assert.Equal("/Admin", result.PageName);
        Assert.Equal("Header links saved.", model.NavigationStatus);
        var settings = await Provider.GetAsync();
        Assert.False(settings.Gallery);
        Assert.True(settings.Search);
        Assert.False(settings.Blog);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task UnauthorizedOrInvalidSavesDoNotWrite(bool admin, bool enabled, bool configured, bool validToken)
    {
        Configuration["Features:AdminEnabled"] = enabled.ToString();
        if (!configured)
        {
            Configuration["AdminAuth:Password"] = "";
        }
        var model = CreateAdmin(admin);
        if (validToken)
        {
            AddAntiforgeryToken(model.HttpContext);
        }

        var result = await model.OnPostSaveNavigationAsync();

        if (admin && enabled && configured)
        {
            Assert.IsType<BadRequestObjectResult>(result);
        }
        else
        {
            Assert.IsType<NotFoundResult>(result);
        }
        Assert.False(await Store.FileExistsAsync(FileStoreArea.Admin, "navigation.json"));
    }

    [Fact]
    public async Task InvalidBindingDoesNotWrite()
    {
        var model = CreateAdmin();
        AddAntiforgeryToken(model.HttpContext);
        model.ModelState.AddModelError("NavigationInput.Gallery", "Invalid boolean.");

        Assert.IsType<BadRequestObjectResult>(await model.OnPostSaveNavigationAsync());
        Assert.False(await Store.FileExistsAsync(FileStoreArea.Admin, "navigation.json"));
    }

    [Fact]
    public async Task ActualFormSubmissionBindsUncheckedBoxesWithoutRequiringTagsInput()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_app.Urls.Single()) };
        var login = await client.GetStringAsync("/Login");
        using var loginResponse = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "test-admin",
            ["Password"] = "test-password-only",
            ["__RequestVerificationToken"] = Token(login)
        }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, loginResponse.StatusCode);

        var form = await client.GetStringAsync("/test/navigation-form");
        using var response = await client.PostAsync("/Admin?handler=SaveNavigation",
            new FormUrlEncodedContent(new[]
            {
                KeyValuePair.Create("__RequestVerificationToken", Token(form)),
                KeyValuePair.Create("NavigationInput.Gallery", "false"),
                KeyValuePair.Create("NavigationInput.Search", "true"),
                KeyValuePair.Create("NavigationInput.Search", "false"),
                KeyValuePair.Create("NavigationInput.Blog", "false"),
                KeyValuePair.Create("NavigationInput.Commissions", "false")
            }));

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var settings = await Provider.GetAsync();
        Assert.False(settings.Gallery);
        Assert.True(settings.Search);
        Assert.False(settings.Blog);
        Assert.False(settings.Commissions);

        static string Token(string html) => System.Net.WebUtility.HtmlDecode(
            Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RenderedHeaderReflectsEveryCombinationAndKeepsFixedLinks(bool gallery, bool search, bool blog)
    {
        Configuration["Navigation:Home"] = "false";
        Configuration["Navigation:Admin"] = "false";
        await Provider.SaveAsync(new NavigationSettings { Gallery = gallery, Search = search, Blog = blog });

        foreach (var admin in new[] { false, true })
        {
            var model = new LoginModel(Configuration, Services.GetRequiredService<IWebHostEnvironment>())
            {
                PageContext = CreatePageContext("/Login", admin)
            };
            var html = await RenderAsync("/Pages/Login.cshtml", model);
            Assert.Contains(">Home</a>", html);
            Assert.Equal(gallery || admin, html.Contains(">Gallery</a>"));
            Assert.Equal(search || admin, html.Contains(">Search</a>"));
            Assert.Equal(blog || admin, html.Contains(">Blog</a>"));
            foreach (var (name, visible) in new[] { ("Gallery", gallery), ("Search", search), ("Blog", blog) })
            {
                Assert.Equal(admin && !visible,
                    Regex.IsMatch(html, $"<a\\b[^>]*class=\"[^\"]*admin-only-tagged[^\"]*\"[^>]*>{name}</a>"));
            }
            Assert.Equal(admin, html.Contains(">Admin</a>"));
            Assert.Equal(!admin, html.Contains(">Login</a>"));
        }
    }

    [Fact]
    public async Task AdminFormRendersSavedCheckboxesAndCannotEditFixedLinks()
    {
        var model = CreateAdmin();
        model.NavigationInput = new NavigationSettings { Gallery = false, Search = true, Blog = false };

        var html = await RenderAsync("/Pages/Admin.cshtml", model);

        Assert.Contains("handler=SaveNavigation", html);
        Assert.Matches("""<input[^>]*checked="checked"[^>]*name="NavigationInput.Search"[^>]*>""", html);
        Assert.DoesNotContain("NavigationInput.Home", html);
        Assert.DoesNotContain("NavigationInput.Admin", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.Matches("""<input[^>]*name="NavigationInput.Gallery"[^>]*type="hidden"[^>]*value="false"[^>]*>""", html);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task AccessGateProtectsOnlyHiddenSectionsForAllMethods(bool gallery, bool search, bool blog)
    {
        await Provider.SaveAsync(new NavigationSettings { Gallery = gallery, Search = search, Blog = blog });
        foreach (var (page, path, isPublic) in new (string?, string, bool)[]
        {
            ("/Gallery", "/Gallery/Album", gallery),
            ("/Image", "/image/1", gallery),
            (null, "/IMG_SRC/Album/photo.png", gallery),
            (null, "/img_db_thumbs/1.webp", gallery),
            ("/Search", "/Search?tag=art", search),
            ("/Blog", "/blog", blog),
            ("/BlogPost", "/blog/post", blog),
            (null, "/BLOGS/assets/post/photo.png", blog),
            ("/Index", "/", true),
            ("/Login", "/Login", true),
            ("/Admin", "/Admin", true),
            (null, "/about/assets/photo.png", true),
            (null, "/css/site.css", true)
        })
        foreach (var role in new string?[] { null, "Viewer", "Admin" })
        foreach (var method in new[] { "GET", "HEAD", "POST" })
        {
            using var requestScope = _app.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
            http.Request.Path = path.Split('?')[0];
            http.Request.QueryString = path.Contains('?') ? new QueryString("?tag=art") : QueryString.Empty;
            http.Request.Method = method;
            if (page is not null)
            {
                http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                    new EndpointMetadataCollection(new PageActionDescriptor { ViewEnginePath = page }), "test"));
            }
            if (role is not null)
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
            }
            var allowed = false;
            var middleware = new NavigationAccessMiddleware(_ => { allowed = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(http, Provider, Configuration, Services.GetRequiredService<IWebHostEnvironment>());

            Assert.Equal(isPublic || role == "Admin", allowed);
            if (!allowed)
            {
                Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
                Assert.Contains("/Login?ReturnUrl=", http.Response.Headers.Location.ToString());
            }
            Assert.Equal(!isPublic, http.Response.Headers.CacheControl.ToString() == "private, no-store");
        }
    }

    [Theory]
    [InlineData("/Gallery/Album?sort=date")]
    [InlineData("/image/1")]
    [InlineData("/Search?tag=art")]
    [InlineData("/blog")]
    [InlineData("/blog/example-post")]
    [InlineData("/img_src/photo.png")]
    [InlineData("/img_db_thumbs/photo.webp")]
    [InlineData("/blogs/assets/photo.png")]
    public async Task RealHiddenRoutesChallengeBeforeLoadingContent(string path)
    {
        await Provider.SaveAsync(new NavigationSettings { Gallery = false, Search = false, Blog = false });
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_app.Urls.Single()) };

        using var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/Login?ReturnUrl=" + Uri.EscapeDataString(path), response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrUnconfiguredAdminCannotBypassHiddenPages(bool enabled, bool configured)
    {
        await Provider.SaveAsync(new NavigationSettings { Gallery = false });
        Configuration["Features:AdminEnabled"] = enabled.ToString();
        if (!configured)
        {
            Configuration["AdminAuth:Password"] = "";
        }
        var http = CreatePageContext("/Gallery", admin: true).HttpContext;
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new PageActionDescriptor { ViewEnginePath = "/Gallery" }), "test"));
        var allowed = false;
        var middleware = new NavigationAccessMiddleware(_ => { allowed = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(http, Provider, Configuration, Services.GetRequiredService<IWebHostEnvironment>());

        Assert.False(allowed);
        Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
    }

    private AdminModel CreateAdmin(bool admin = true) => new(
        Configuration, Services.GetRequiredService<IWebHostEnvironment>(),
        Services.GetRequiredService<AppDbContext>(), Services.GetRequiredService<IAntiforgery>(),
        Services.GetRequiredService<AdminOnlyTagsProvider>(),
        Services.GetRequiredService<FilesystemPersistenceService>(), Store, Provider)
    {
        PageContext = CreatePageContext("/Admin", admin)
    };

    private PageContext CreatePageContext(string page, bool admin)
    {
        var http = new DefaultHttpContext { RequestServices = Services };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("localhost");
        http.Request.Path = page;
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "test"));
        if (admin)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"));
        }
        var routes = new RouteData();
        routes.Values["page"] = page;
        return new PageContext(new ActionContext(http, routes, new PageActionDescriptor { ViewEnginePath = page }));
    }

    private void AddAntiforgeryToken(HttpContext http)
    {
        var issuingContext = new DefaultHttpContext { RequestServices = Services, User = http.User };
        issuingContext.Request.Scheme = "https";
        var tokens = Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(issuingContext);
        http.Request.Method = "POST";
        http.Request.Headers.Cookie = issuingContext.Response.Headers.SetCookie.ToString().Split(';')[0];
        http.Request.Headers[tokens.HeaderName!] = tokens.RequestToken;
    }

    private async Task<string> RenderAsync<TModel>(string path, TModel model) where TModel : PageModel
    {
        var view = Services.GetRequiredService<IRazorViewEngine>().GetView(null, path, isMainPage: true);
        Assert.True(view.Success, string.Join(", ", view.SearchedLocations ?? []));
        using var output = new StringWriter();
        var viewData = new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), model.ModelState) { Model = model };
        model.PageContext.ViewData = viewData;
        var razorView = Assert.IsType<RazorView>(view.View);
        var page = Assert.IsAssignableFrom<Page>(razorView.RazorPage);
        page.PageContext = model.PageContext;
        await view.View.RenderAsync(new ViewContext(model.PageContext, view.View, viewData,
            new TempDataDictionary(model.HttpContext, Services.GetRequiredService<ITempDataProvider>()),
            output, new HtmlHelperOptions()));
        return output.ToString();
    }
}
