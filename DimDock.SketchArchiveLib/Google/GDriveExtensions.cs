using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SketchArchiveLib.Google
{
    public static class GDriveExtensions
    {
        public static IEnumerable<GDriveItem> SortDirectoryItems(this IEnumerable<GDriveItem> items)
        {
            return items.OrderBy(x =>
            {
                StringBuilder sb = new StringBuilder();
                for(int i = 0; i < x.Name.Length; i++)
                {
                    if (char.IsDigit(x.Name[i]))
                        sb.Append(x.Name[i]);
                    else
                        break;
                }

                if (long.TryParse(sb.ToString(), out long lName))
                    return lName;
                else
                    return long.MaxValue;
            }).ThenBy(x => x.Name);
        }

        public static IEnumerable<GDriveItem> Folders(this IEnumerable<GDriveItem> items)
        {
            return items.Where(x => x.Folder);
        }

        public static IEnumerable<GDriveItem> Files(this IEnumerable<GDriveItem> items)
        {
            return items.Where(x => !x.Folder);
        }

        public static IEnumerable<GDriveItem> SortGalleryItems(this IEnumerable<GDriveItem> items)
        {
            return items.OrderBy(x =>
            {
                int index = x.Name.IndexOf('.');
                if (index == -1)
                    return int.MaxValue;
                if (int.TryParse(x.Name.Substring(0, index), out index))
                    return index;
                return int.MaxValue;
            }).ThenBy(x => x.Name);
        }

        public static IEnumerable<(VType Value, int Index)> ToIndexed<VType>(this IEnumerable<VType> items)
        {
            return items.Select((value, index)=>(value,index));
        }
    }
}
