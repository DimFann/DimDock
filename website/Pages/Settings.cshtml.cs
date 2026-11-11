using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Website.Services;

namespace Website.Pages;

public sealed class SettingsModel(GalleryFilterPreferences preferences) : PageModel
{
    [BindProperty]
    public GalleryFilter Filter { get; set; } = GalleryFilter.BlurNsfw;

    public bool AdultConfirmed { get; private set; }
    public bool NeedsConfirmation { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public void OnGet()
    {
        Response.Headers.CacheControl = "private, no-store";
        var current = preferences.Read(Request);
        Filter = current.Filter;
        AdultConfirmed = current.AdultConfirmed;
    }

    public IActionResult OnPost(bool confirmAdult = false)
    {
        Response.Headers.CacheControl = "private, no-store";
        var current = preferences.Read(Request);
        AdultConfirmed = current.AdultConfirmed;
        if (!ModelState.IsValid || !Enum.IsDefined(Filter))
        {
            ModelState.AddModelError(nameof(Filter), "Choose a valid filter.");
            return Page();
        }
        if (Filter == GalleryFilter.ShowAll && !AdultConfirmed && !confirmAdult)
        {
            NeedsConfirmation = true;
            return Page();
        }

        preferences.Save(HttpContext, new GalleryFilterPreference(Filter,
            AdultConfirmed || (Filter == GalleryFilter.ShowAll && confirmAdult)));
        StatusMessage = "Filters saved for this browser.";
        return RedirectToPage("/Settings");
    }
}
