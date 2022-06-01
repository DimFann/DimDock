using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Microsoft.Extensions.Configuration;
using SketchArchiveLib.Google;
using SketchArchiveLib;
using System;

namespace DimDock.LinuxArchive.Pages
{
    public class SketchModel : PageModel
    {
        public Display Display = Display.Gallery;
        private readonly IConfiguration _configuration;
        private readonly GDriveReader _driveReader;
        private readonly string _resourceKey;

        public SketchModel(IConfiguration configuration, GDriveReader driveReader)
        {
            _configuration = configuration;
            _resourceKey = _configuration["DimDock:ResourceKey"];
            _driveReader = driveReader;
        }

        public string RootId { get; set; } = string.Empty;
        public string FolderId { get; set; } = string.Empty;
        public string ParentFolderId { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string ResourceKey { get; set; } = string.Empty;

        public GDriveFiles FolderItems = null;

        public async Task OnGetAsync()
        {
            if(string.IsNullOrWhiteSpace(RootId))
                RootId = _configuration["DimDock:RootId"];

            FolderId = Request.Query["FolderId"];
            ParentFolderId = Request.Query["ParentFolderId"];
            ResourceKey = Request.Query["ResourceKey"];

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
        }
    }
}
