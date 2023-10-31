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
        public string Path { get; set; } = string.Empty;
        public string EasyUrl { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public GDriveFiles FolderItems = null;

        public async Task<IActionResult> OnGetAsync()
        {
            var url = HttpUtility.UrlDecode(Request.Query["url"]);
            if(!string.IsNullOrWhiteSpace(url))
            {
                // This is a legacy url, these are out there but don't create these anymore.
                if(url.StartsWith("Sketches__"))
                {
                    string[] split = url.Split("__").Skip(1).ToArray(); // Remove "Sketches"
                    url = string.Join("/", split);
                }

                (string Path, DriveMapItem Item) = _driveMap.GetFolderId(url);
                if(Item != null)
                {
                    string newUrl = $"/Sketch?folderID={Item.FolderId}";
                    if (!string.IsNullOrWhiteSpace(Item.ResourceKey))
                        newUrl += $"&resourceKey={Item.ResourceKey}";
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
                Path = easyUrlEntry.Name;
                var split = easyUrlEntry.Name.Split('/').Select(x=>Uri.EscapeDataString(x));
                EasyUrl = $"{request.Scheme}://{request.Host}/Sketch?url={string.Join("/",split)}";
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

            // See if DriveMap has it.
            if (string.IsNullOrWhiteSpace(ParentFolderId) && FolderId != RootId && !string.IsNullOrWhiteSpace(Path))
                ParentFolderId = GetParentId(Path);

            // Default to the Root ID.
            if(string.IsNullOrWhiteSpace(ParentFolderId))
                ParentFolderId = RootId;

            FolderItems = await _driveReader.GetFolderContentsAsync(FolderId, ResourceKey);

            Description = FolderItems.Description;

            return null;
        }

        private string GetParentId(string path)
        {
            int idx = path.LastIndexOf('/');
            if(idx >= 0)
            {
                path = path[..idx].TrimEnd('/');
                (_, DriveMapItem Item) = _driveMap.GetFolderId(path);
                return Item?.FolderId;
            }
            else
                return null;
        }
    }
}
