using DimDock.SketchArchiveLib.Google;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SketchArchiveLib.Google;
using SketchArchiveLib;
using DimDock.SketchArchiveLib;

namespace DimDock.LinuxArchive.Pages
{
    public class IndexModel : PageModel
    {
        public string AboutMe;

        protected readonly IConfiguration _configuration;
        protected readonly GDriveReader _driveReader;
        protected readonly DriveMap _driveMap;
        protected readonly TextFileCache _textFileCache;
        
        public IndexModel(IConfiguration configuration, GDriveReader driveReader, DriveMap driveMap, TextFileCache textFileCache)
        {
            _configuration = configuration;
            _driveReader = driveReader;
            _driveMap = driveMap;
            _textFileCache = textFileCache;
        }

        public void OnGet()
        {
            AboutMe = _textFileCache.Cache["aboutMe.md"].Replace("\r\n","\n").Replace("\n","\n\n");
        }
    }
}
