using DimDock.SketchArchiveLib;
using DimDock.SketchArchiveLib.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Net;

namespace DimDock.LinuxArchive.Pages.ControlPanel
{
    public class IndexModel : PageModel
    {
        private readonly DriveMap _driveMap;
        private readonly TextFileCache _textFileCache;
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;

        public string DriveMapJson;

        public IndexModel(IConfiguration configuration, DriveMap driveMap, TextFileCache textFileCache, ILogger logger)
        {
            _configuration = configuration;
            _driveMap = driveMap;
            _textFileCache = textFileCache;
            _logger = logger;
        }

        public IActionResult OnGet()
        {
            var configPassword = _configuration.GetSection("DimDock:MaintenancePassword").Get<string>();
            if (string.IsNullOrWhiteSpace(configPassword))
            {
                // No maintenance password is set.
                //HttpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return Redirect($"/Error?statusCode={HttpContext.Response.StatusCode}");
            }

            var password = HttpContext.Request.Query["password"];
            if (!password.Equals(configPassword))
            {
                // Bad password.
                //HttpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return Redirect($"/Error?statusCode={HttpContext.Response.StatusCode}");
            }

            DriveMapJson = JsonSerializer.Serialize(_driveMap.Map);

            return null;
        }
    }
}
