using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SketchArchiveLib;

namespace DimDock.LinuxArchive.Pages.Sketch
{
    public class ViewerModel : PageModel
    {
        public string Error;
        public string ImageUrl;

        [BindProperty(SupportsGet = true)] public string ImageId { get; set; }

        public void OnGet(string imageId)
        {
            ImageId = imageId ?? ImageId;
            if(!string.IsNullOrWhiteSpace(ImageId))
            {
                ImageUrl = $"https://drive.google.com/uc?export=view&id={ImageId}";
            }
            else
            {
                Error = "Where's your ImageId, did you delete it?\n" +
                    "If you didn't delete it but you got here somehow send me a message on Twitter (@DimFann)";
            }
        }
    }
}
