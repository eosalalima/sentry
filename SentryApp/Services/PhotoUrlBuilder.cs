using System;

namespace SentryApp.Services;

public interface IPhotoUrlBuilder
{
    string Build(string? photoId);
}

public sealed class PhotoUrlBuilder : IPhotoUrlBuilder
{
    public string Build(string? photoId)
    {
        if (string.IsNullOrWhiteSpace(photoId))
            return "/img/avatar-placeholder.svg";

        // Serve via the /photos endpoint so the server can resolve files from the configured directory.
        return $"/photos/{Uri.EscapeDataString(photoId)}";
    }
}
