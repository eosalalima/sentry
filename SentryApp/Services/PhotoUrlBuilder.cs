using System;

namespace SentryApp.Services;

public interface IPhotoUrlBuilder
{
    string Build(string? photoId, string? personnelNo = null);
}

public sealed class PhotoUrlBuilder : IPhotoUrlBuilder
{
    public string Build(string? photoId, string? personnelNo = null)
    {
        var path = string.IsNullOrWhiteSpace(photoId)
            ? "/photos"
            : $"/photos/{Uri.EscapeDataString(photoId)}";

        // Include the personnel number so the photo endpoint can audit which
        // personnel record caused each image retrieval.
        return string.IsNullOrWhiteSpace(personnelNo)
            ? path
            : $"{path}?personnelNo={Uri.EscapeDataString(personnelNo)}";
    }
}
