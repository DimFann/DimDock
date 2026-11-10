using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Website.Services;

public sealed class NavigationAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        NavigationSettingsProvider provider,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var page = context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath;
        var path = context.Request.Path;
        var isGallery = page is "/Gallery" or "/Image" ||
            path.StartsWithSegments("/img_src", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/img_db_thumbs", StringComparison.OrdinalIgnoreCase);
        var isBlog = page is "/Blog" or "/BlogPost" ||
            path.StartsWithSegments("/blogs/assets", StringComparison.OrdinalIgnoreCase);
        var isCommissions = page == "/Commissions" ||
            path.StartsWithSegments("/commissions/assets", StringComparison.OrdinalIgnoreCase);
        if (!isGallery && !isBlog && !isCommissions && page != "/Search")
        {
            await next(context);
            return;
        }

        var navigation = await provider.GetAsync(context.RequestAborted);
        var isPublic = isGallery ? navigation.Gallery :
            isBlog ? navigation.Blog :
            isCommissions ? navigation.Commissions : navigation.Search;
        if (!isPublic)
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!AdminAccess.IsAdmin(configuration, context.User,
                AdminAccess.IsDevelopmentPasswordBypassEnabled(configuration, environment)))
            {
                await context.ChallengeAsync();
                return;
            }
        }

        await next(context);
    }
}
