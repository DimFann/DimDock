using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Website.Services;

namespace Website.Pages;

[EnableRateLimiting("admin-login")]
public class LoginModel(IConfiguration configuration) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }
    public bool AdminFeatureEnabled => AdminAccess.IsFeatureEnabled(configuration);
    public bool CredentialsConfigured => AdminAccess.IsConfigured(configuration);

    public IActionResult OnGet()
    {
        if (AdminAccess.IsAdmin(configuration, User))
        {
            return RedirectToPage("/Admin");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!AdminAccess.IsFeatureEnabled(configuration))
        {
            ErrorMessage = "Admin access is disabled for this site.";
            return Page();
        }

        if (!AdminAccess.IsConfigured(configuration))
        {
            ErrorMessage = "Admin access is not configured. Set ADMIN_USERNAME and ADMIN_PASSWORD (at least 12 characters) in your environment.";
            return Page();
        }

        if (!AdminAccess.ValidateCredentials(configuration, Username, Password))
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, Username),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });

        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Admin");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Index");
    }
}
