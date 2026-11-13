using System.Globalization;

namespace Website.Services;

// Mirrors the path loader: filename date, then YYYY-MM path segment, then YYYY segment, else 2000-01-01.
public static class GalleryItemDates
{
    public static DateTime Created(string fileName, string path, DateTime fallback)
    {
        var filenameDate = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"^(\d{4})-?(\d{2})(\d{2})");
        if (filenameDate.Success &&
            int.TryParse(filenameDate.Groups[1].Value, out var fileYear) &&
            int.TryParse(filenameDate.Groups[2].Value, out var fileMonth) &&
            int.TryParse(filenameDate.Groups[3].Value, out var fileDay) &&
            fileYear >= 2000 &&
            fileYear <= fallback.Year &&
            fileMonth is >= 1 and <= 12 &&
            fileDay is >= 1 and <= 31 &&
            DateTime.TryParseExact(
                $"{fileYear:D4}-{fileMonth:D2}-{fileDay:D2}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedFilenameDate))
        {
            return parsedFilenameDate;
        }

        var monthSegment = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment =>
                System.Text.RegularExpressions.Regex.IsMatch(segment, @"^\d{4}-\d{2}$"));
        if (monthSegment is not null &&
            DateTime.TryParseExact(
                $"{monthSegment}-01",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedMonthDate) &&
            parsedMonthDate.Year >= 2000 &&
            parsedMonthDate.Year <= fallback.Year)
        {
            return parsedMonthDate;
        }

        var yearSegment = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment =>
                System.Text.RegularExpressions.Regex.IsMatch(segment, @"^\d{4}$"));
        if (yearSegment is not null &&
            int.TryParse(yearSegment, out var pathYear) &&
            pathYear >= 2000 &&
            pathYear <= fallback.Year)
        {
            return new DateTime(pathYear, 1, 1);
        }

        return new DateTime(2000, 1, 1);
    }
}
