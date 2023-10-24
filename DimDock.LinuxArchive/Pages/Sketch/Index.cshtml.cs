using DimDock.SketchArchiveLib.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using SketchArchiveLib;
using SketchArchiveLib.Google;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace DimDock.LinuxArchive.Pages
{
    public class SketchModel : PageModel
    {
        public Display Display = Display.Gallery;
        private readonly IConfiguration _configuration;
        private readonly GDriveReader _driveReader;
        private readonly DriveMap _driveMap;
        private readonly string _resourceKey;

        public SketchModel(IConfiguration configuration, GDriveReader driveReader, DriveMap driveMap)
        {
            _configuration = configuration;
            _resourceKey = _configuration["DimDock:ResourceKey"];
            _driveReader = driveReader;
            _driveMap = driveMap;
        }

        public string RootId { get; set; } = string.Empty;
        public string FolderId { get; set; } = string.Empty;
        public string ParentFolderId { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string ResourceKey { get; set; } = string.Empty;
        public string EasyUrl { get; set; } = string.Empty;

        public GDriveFiles FolderItems = null;

        public async Task<IActionResult> OnGetAsync()
        {
            var url = HttpUtility.UrlDecode(Request.Query["url"]);
            if(!string.IsNullOrWhiteSpace(url))
            {
                var item = _driveMap.GetFolderId(url);
                if(item != null)
                {
                    string newUrl = $"/Sketch?folderID={item.FolderId}";
                    if (!string.IsNullOrWhiteSpace(item.ResourceKey))
                        newUrl += $"&resourceKey={item.ResourceKey}";
                    return Redirect(newUrl);
                }
                else
                {
                    return Redirect("/Sketch");
                }
            }

            if(string.IsNullOrWhiteSpace(RootId))
                RootId = _configuration["DimDock:RootId"];

            FolderId = Request.Query["FolderId"];
            ParentFolderId = Request.Query["ParentFolderId"];
            ResourceKey = Request.Query["ResourceKey"];

            var easyUrlEntry = _driveMap.Map
                .Where(x => x.Value.FolderId == FolderId)
                .Select(x => (Name: x.Key, x.Value.FolderId,x.Value.ResourceKey))
                .FirstOrDefault();

            var request = HttpContext.Request;
            if (easyUrlEntry != default)
            {
                var split = easyUrlEntry.Name.Split('/').Select(x=>Uri.EscapeDataString(x));
                EasyUrl = $"{request.Scheme}://{request.Host}/Sketch/{string.Join("/",split)}";
            }
            else
                EasyUrl = $"{request.Scheme}://{request.Host}/Sketch";

            if (string.IsNullOrWhiteSpace(ResourceKey))
                ResourceKey = _resourceKey;

            string strDisplay = Request.Query["Display"];

            if(!string.IsNullOrWhiteSpace(strDisplay))
            {
                if (!Enum.TryParse(strDisplay, true, out Display))
                    Display = Display.Gallery;
            }

            if (string.IsNullOrWhiteSpace(FolderId))
                FolderId = RootId;

            if (string.IsNullOrWhiteSpace(ParentFolderId))
                ParentFolderId = RootId;

            FolderItems = await _driveReader.GetFolderContentsAsync(FolderId, ResourceKey);

            return null;
        }
    }
}
