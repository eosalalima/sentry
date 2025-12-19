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

        // Example convention: wwwroot/photos/{photoId}.jpg
        // return $"/photos/{Uri.EscapeDataString(photoId)}.jpg";
        return "/img/avatar-placeholder.svg";
    }
}
