using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Website.Models;

namespace Website.Services;

public enum GalleryFilter
{
    ShowAll,
    HideNsfw,
    BlurNsfw
}

public sealed record GalleryFilterPreference(GalleryFilter Filter = GalleryFilter.BlurNsfw, bool AdultConfirmed = false);

public sealed class GalleryFilterPreferences(
    IDataProtectionProvider protectionProvider,
    ILogger<GalleryFilterPreferences> logger)
{
    private const string CookieName = "DimDock.GalleryFilter";
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("DimDock.GalleryFilter.v1");

    public GalleryFilterPreference Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            return new();
        }

        try
        {
            var preference = JsonSerializer.Deserialize<GalleryFilterPreference>(_protector.Unprotect(cookie));
            if (preference is null || !Enum.IsDefined(preference.Filter) ||
                (preference.Filter == GalleryFilter.ShowAll && !preference.AdultConfirmed))
            {
                logger.LogWarning("Invalid gallery filter preference cookie.");
                return new();
            }
            return preference;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Could not read gallery filter preference cookie.");
            return new();
        }
    }

    public void Save(HttpContext context, GalleryFilterPreference preference)
    {
        if (!Enum.IsDefined(preference.Filter) ||
            (preference.Filter == GalleryFilter.ShowAll && !preference.AdultConfirmed))
        {
            throw new ArgumentException("Show All requires adult confirmation and a valid filter.", nameof(preference));
        }
        context.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(preference)),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
                MaxAge = TimeSpan.FromDays(365),
                IsEssential = true
            });
    }

    public static bool IsNsfw(Item item) =>
        item.TagLinks.Any(link => string.Equals(link.Tag.Name, "nsfw", StringComparison.OrdinalIgnoreCase));

    public static bool ShouldHide(Item item, GalleryFilter filter, bool admin) =>
        !admin && filter == GalleryFilter.HideNsfw && IsNsfw(item);

    public static bool ShouldBlur(Item item, GalleryFilter filter, bool admin) =>
        !admin && filter == GalleryFilter.BlurNsfw && IsNsfw(item);
}
