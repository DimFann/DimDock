﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace SketchArchiveLib.Google
{
    public class ShortcutDetails
    {
        [JsonProperty("targetId")]
        public string TargetId;

        [JsonProperty("targetMimeType")]
        public string TargetMimeType;

        [JsonProperty("targetResourceKey")]
        public string TargetResourceKey;
    }

    public class GDriveItem
    {
        [JsonProperty("shortcutDetails")]
        public ShortcutDetails ShortcutDetails;

        [JsonProperty("parents")]
        public List<string> Parents;

        [JsonProperty("id")]
        public string ID;

        [JsonProperty("name")]
        public string Name;

        [JsonProperty("mimeType")]
        public string MimeType;

        [JsonProperty("description")]
        public string Description;

        [JsonProperty("resourceKey")]
        public string ResourceKey;

        [JsonIgnore]
        public bool Folder { get { return !string.IsNullOrEmpty(MimeType) && MimeType.IndexOf("folder", System.StringComparison.OrdinalIgnoreCase) >= 0; } }

        public string ToLink(string rootID, Display? displayOverride = null)
        {
            if (Folder)
            {
                string url = $".?folderID={ShortcutDetails?.TargetId ?? ID}";

                if(!string.IsNullOrEmpty(ResourceKey))
                    url += $"&resourceKey={ResourceKey}";

                string parent = Parents?.DefaultIfEmpty(string.Empty).FirstOrDefault()?.Trim();

                if (!string.IsNullOrEmpty(parent))
                    url += $"&parentFolderID={parent}";

                if(displayOverride != null)
                {
                    url += $"&display={displayOverride.Value.ToString().ToLower()}";
                }
                else if(!string.IsNullOrEmpty(Name))
                {
                    if (Name.StartsWith("comic_", StringComparison.OrdinalIgnoreCase))
                        url += "&display=comic";
                    else if (Name.StartsWith("files_", StringComparison.OrdinalIgnoreCase))
                        url += "&display=files";
                    else if (Name.StartsWith("info", StringComparison.OrdinalIgnoreCase))
                        url += "&display=info";
                    else
                        url += "&display=gallery";
                }
                else
                {
                    url += "&display=gallery";
                }

                if(!string.IsNullOrWhiteSpace(rootID))
                {
                    url += $"&rootID={rootID}";
                }

                return url;
            }
            else if(MimeType.StartsWith("video",StringComparison.OrdinalIgnoreCase))
            {
                return $"https://drive.google.com/file/d/{ShortcutDetails?.TargetId ?? ID}/view?resourceKey={ShortcutDetails?.TargetResourceKey ?? ResourceKey}";
            }
            else
            {
                return $"./Viewer/?imageId={ShortcutDetails?.TargetId ?? ID}";
            }
        }

        public string ToThumb(int width)
        {
            if (!Folder)
            {
                if (ShortcutDetails?.TargetResourceKey != null)
                    Console.WriteLine("hello");

                string resourceKeyString = string.IsNullOrWhiteSpace(ShortcutDetails?.TargetResourceKey ?? ResourceKey) ? "" : $"&resourceKey={ResourceKey}";
                return $"https://drive.google.com/thumbnail?sz=w{width}&id={ShortcutDetails?.TargetId ?? ID}{resourceKeyString}";
            }
            return null;
        }
    }

    public class GDriveFiles
    {
        [JsonProperty("files")]
        public List<GDriveItem> Files;
    }
}