namespace SentryApp.Services;

internal static class PersonnelPhotoResolver
{
    internal static string? FindPhotoPath(
        string? photoDirectory,
        string? photoId,
        string? personnelNo)
    {
        if (string.IsNullOrWhiteSpace(photoDirectory))
            return null;

        var normalizedPersonnelNo = DigitsOnly(personnelNo);
        if (normalizedPersonnelNo.Length > 0)
        {
            // Prefer the canonical, digits-only filename (for example,
            // B26-12345 is stored as 2612345.jpg).
            var canonicalPath = Path.Combine(photoDirectory, $"{normalizedPersonnelNo}.jpg");
            if (File.Exists(canonicalPath))
                return canonicalPath;

            if (Directory.Exists(photoDirectory))
            {
                // Also support legacy filenames whose letters and punctuation differ
                // from the personnel number, such as K26-12345.jpg for 2612345.
                var matchingPath = Directory
                    .EnumerateFiles(photoDirectory, "*.jpg", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(path =>
                        DigitsOnly(Path.GetFileNameWithoutExtension(path)) == normalizedPersonnelNo);

                if (matchingPath is not null)
                    return matchingPath;
            }
        }

        // Retain support for records that explicitly identify a photo filename.
        var sanitizedPhotoId = Path.GetFileName(photoId?.Trim());
        if (string.IsNullOrWhiteSpace(sanitizedPhotoId))
            return null;

        var photoIdPath = Path.Combine(photoDirectory, $"{sanitizedPhotoId}.jpg");
        return File.Exists(photoIdPath) ? photoIdPath : null;
    }

    internal static string DigitsOnly(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : string.Concat(value.Where(char.IsDigit));
}
