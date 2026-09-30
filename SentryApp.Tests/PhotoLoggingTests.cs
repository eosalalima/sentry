using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SentryApp.Services;

namespace SentryApp.Tests;

public sealed class PhotoLoggingTests
{
    [Theory]
    [InlineData("photo 1", "person/1", "/photos/photo%201?personnelNo=person%2F1")]
    [InlineData(null, "12345", "/photos?personnelNo=12345")]
    [InlineData("photo-2", null, "/photos/photo-2")]
    public void PhotoUrlBuilder_IncludesPersonnelNumber(
        string? photoId,
        string? personnelNo,
        string expected)
    {
        var builder = new PhotoUrlBuilder();

        Assert.Equal(expected, builder.Build(photoId, personnelNo));
    }

    [Theory]
    [InlineData("B26-12345", "2612345")]
    [InlineData(" K26 / 12.345 ", "2612345")]
    [InlineData("ABC-!", "")]
    [InlineData(null, "")]
    public void PersonnelPhotoResolver_StripsLettersAndSpecialCharacters(
        string? value,
        string expected)
    {
        Assert.Equal(expected, PersonnelPhotoResolver.DigitsOnly(value));
    }

    [Theory]
    [InlineData("2612345.jpg")]
    [InlineData("K26-12345.jpg")]
    public void PersonnelPhotoResolver_MatchesPersonnelNumberToNormalizedFilename(string fileName)
    {
        var photoDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(photoDirectory);

        try
        {
            var expectedPath = Path.Combine(photoDirectory, fileName);
            File.WriteAllText(expectedPath, "photo");

            var actualPath = PersonnelPhotoResolver.FindPhotoPath(
                photoDirectory,
                photoId: null,
                personnelNo: "B26-12345");

            Assert.Equal(expectedPath, actualPath);
        }
        finally
        {
            Directory.Delete(photoDirectory, recursive: true);
        }
    }

    [Fact]
    public void PersonnelPhotoResolver_FallsBackToExplicitPhotoId()
    {
        var photoDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(photoDirectory);

        try
        {
            var expectedPath = Path.Combine(photoDirectory, "existing-photo.jpg");
            File.WriteAllText(expectedPath, "photo");

            var actualPath = PersonnelPhotoResolver.FindPhotoPath(
                photoDirectory,
                photoId: "existing-photo",
                personnelNo: null);

            Assert.Equal(expectedPath, actualPath);
        }
        finally
        {
            Directory.Delete(photoDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PictureRetrievalLogWriter_RecordsPersonnelNumberAndFile()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        try
        {
            var environment = new TestWebHostEnvironment { ContentRootPath = contentRoot };
            var writer = new PictureRetrievalLogWriter(
                environment,
                NullLogger<PictureRetrievalLogWriter>.Instance);

            await writer.LogAsync("P-123", "portrait.jpg", true, CancellationToken.None);

            var logPath = Path.Combine(contentRoot, PictureRetrievalLogWriter.FileName);
            var line = Assert.Single(await File.ReadAllLinesAsync(logPath));
            using var entry = JsonDocument.Parse(line);
            Assert.Equal("P-123", entry.RootElement.GetProperty("PersonnelNo").GetString());
            Assert.Equal("portrait.jpg", entry.RootElement.GetProperty("FileName").GetString());
            Assert.True(entry.RootElement.GetProperty("PhotoFound").GetBoolean());
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SentryApp.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
