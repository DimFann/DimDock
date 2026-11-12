using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Website.Models;
using Website.Pages;
using Website.Services;

namespace UnitTests;

public sealed class GalleryFilterTests
{
    private readonly GalleryFilterPreferences _preferences =
        new(new EphemeralDataProtectionProvider(), NullLogger<GalleryFilterPreferences>.Instance);

    [Fact]
    public void MissingOrInvalidCookieDefaultsToBlur()
    {
        Assert.Equal(new GalleryFilterPreference(), _preferences.Read(new DefaultHttpContext().Request));
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = "DimDock.GalleryFilter=invalid";
        Assert.Equal(new GalleryFilterPreference(), _preferences.Read(http.Request));
    }

    [Theory]
    [InlineData(GalleryFilter.BlurNsfw, false)]
    [InlineData(GalleryFilter.BlurNsfw, true)]
    [InlineData(GalleryFilter.HideNsfw, false)]
    [InlineData(GalleryFilter.ShowAll, true)]
    [InlineData(GalleryFilter.HideNsfw, true)]
    public void CookieRemembersFilterAndConfirmation(GalleryFilter filter, bool confirmed)
    {
        var request = SaveAndReadRequest(new(filter, confirmed));

        Assert.Equal(new GalleryFilterPreference(filter, confirmed), _preferences.Read(request));
    }

    [Fact]
    public void CookieIsHttpOnlySecureSameSiteAndYearLong()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        _preferences.Save(http, new());

        var cookie = http.Response.Headers.SetCookie.ToString();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("max-age=31536000", cookie);
    }

    [Fact]
    public void CookieCannotBeReadAfterTampering()
    {
        var request = SaveAndReadRequest(new(GalleryFilter.ShowAll, true));
        request.Headers.Cookie = request.Headers.Cookie.ToString().Replace("DimDock.GalleryFilter=", "DimDock.GalleryFilter=x");

        Assert.Equal(new GalleryFilterPreference(), _preferences.Read(request));
    }

    [Fact]
    public void UnconfirmedShowAllOrUnknownFilterCannotBeSaved()
    {
        Assert.Throws<ArgumentException>(() => _preferences.Save(new DefaultHttpContext(), new(GalleryFilter.ShowAll)));
        Assert.Throws<ArgumentException>(() => _preferences.Save(new DefaultHttpContext(), new((GalleryFilter)123)));
    }

    [Theory]
    [InlineData(GalleryFilter.BlurNsfw, false, false, true)]
    [InlineData(GalleryFilter.HideNsfw, false, true, false)]
    [InlineData(GalleryFilter.ShowAll, false, false, false)]
    [InlineData(GalleryFilter.BlurNsfw, true, false, false)]
    [InlineData(GalleryFilter.HideNsfw, true, false, false)]
    [InlineData(GalleryFilter.ShowAll, true, false, false)]
    public void NsfwTagFilteringAndAdminBypass(GalleryFilter filter, bool admin, bool hide, bool blur)
    {
        var item = new Item { TagLinks = [new ItemTag { Tag = new Tag { Name = "NSFW" } }] };
        Assert.True(GalleryFilterPreferences.IsNsfw(item));
        Assert.Equal(hide, GalleryFilterPreferences.ShouldHide(item, filter, admin));
        Assert.Equal(blur, GalleryFilterPreferences.ShouldBlur(item, filter, admin));

        var safe = new Item { TagLinks = [new ItemTag { Tag = new Tag { Name = "not-nsfw" } }] };
        Assert.False(GalleryFilterPreferences.ShouldHide(safe, filter, admin));
        Assert.False(GalleryFilterPreferences.ShouldBlur(safe, filter, admin));
    }

    [Fact]
    public void SettingsDefaultAndShowAllRequireConfirmationWithoutChangingCookie()
    {
        var model = Model(new DefaultHttpContext());
        model.OnGet();
        Assert.Equal(GalleryFilter.BlurNsfw, model.Filter);
        model.Filter = GalleryFilter.ShowAll;

        Assert.IsType<PageResult>(model.OnPost());
        Assert.True(model.NeedsConfirmation);
        Assert.Equal(0, model.Response.Headers.SetCookie.Count);
    }

    [Fact]
    public void SettingsRejectInvalidSelection()
    {
        var model = Model(new DefaultHttpContext());
        model.Filter = (GalleryFilter)123;

        Assert.IsType<PageResult>(model.OnPost());
        Assert.False(model.ModelState.IsValid);
        Assert.Equal(0, model.Response.Headers.SetCookie.Count);
    }

    [Theory]
    [InlineData(GalleryFilter.BlurNsfw)]
    [InlineData(GalleryFilter.HideNsfw)]
    [InlineData(GalleryFilter.ShowAll)]
    public void ConfirmingImagePreservesExistingFilter(GalleryFilter filter)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = SaveAndReadRequest(new(filter, filter == GalleryFilter.ShowAll)).Headers.Cookie;
        var model = Model(http);
        Assert.IsType<JsonResult>(model.OnPostConfirmAdult(confirmAdult: true));
        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = http.Response.Headers.SetCookie.ToString().Split(';')[0];
        Assert.Equal(new GalleryFilterPreference(filter, true), _preferences.Read(next.Request));
    }

    [Fact]
    public void SettingsConfirmationPersistsThroughOtherSelections()
    {
        var http = new DefaultHttpContext();
        var model = Model(http);
        model.Filter = GalleryFilter.ShowAll;
        model.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http,
            new MemoryTempDataProvider());
        Assert.IsType<RedirectToPageResult>(model.OnPost(confirmAdult: true));

        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = http.Response.Headers.SetCookie.ToString().Split(';')[0];
        var remembered = Model(next);
        remembered.TempData = model.TempData;
        remembered.Filter = GalleryFilter.HideNsfw;
        Assert.IsType<RedirectToPageResult>(remembered.OnPost());
        var last = new DefaultHttpContext();
        last.Request.Headers.Cookie = next.Response.Headers.SetCookie.ToString().Split(';')[0];
        Assert.Equal(new GalleryFilterPreference(GalleryFilter.HideNsfw, true), _preferences.Read(last.Request));
        var showAgain = Model(last);
        showAgain.TempData = model.TempData;
        showAgain.Filter = GalleryFilter.ShowAll;
        Assert.IsType<RedirectToPageResult>(showAgain.OnPost());
        Assert.False(showAgain.NeedsConfirmation);
    }

    private HttpRequest SaveAndReadRequest(GalleryFilterPreference preference)
    {
        var http = new DefaultHttpContext();
        _preferences.Save(http, preference);
        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = http.Response.Headers.SetCookie.ToString().Split(';')[0];
        return next.Request;
    }

    private SettingsModel Model(HttpContext http) => new(_preferences)
    {
        PageContext = new PageContext { HttpContext = http }
    };

    private sealed class MemoryTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
