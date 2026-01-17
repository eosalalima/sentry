using Microsoft.EntityFrameworkCore;
using SentryApp.Data;
using SentryApp.Services;
using SentryApp.Components;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AccessControlDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AccessControlDb")));

builder.Services.AddSingleton<TurnstileLogState>();
builder.Services.AddSingleton<TurnstilePollingController>();
builder.Services.Configure<PhotoOptions>(builder.Configuration.GetSection("PhotoOptions"));
builder.Services.AddSingleton<IPhotoUrlBuilder, PhotoUrlBuilder>();
builder.Services.AddHostedService<TurnstileLogPollingWorker>();
builder.Services.AddHostedService<DemoDeviceLogGenerator>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/photos/{photoId}", (
    string photoId,
    IOptions<PhotoOptions> options,
    IWebHostEnvironment env) =>
{
    if (string.IsNullOrWhiteSpace(photoId))
    {
        var placeholder = Path.Combine(env.WebRootPath, "img", "avatar-placeholder.svg");
        return Results.File(placeholder, "image/svg+xml");
    }

    var sanitizedPhotoId = Path.GetFileName(photoId);
    var photoDirectory = options.Value.PhotoDirectory;

    if (!string.IsNullOrWhiteSpace(photoDirectory))
    {
        var photoPath = Path.Combine(photoDirectory, $"{sanitizedPhotoId}.jpg");
        if (File.Exists(photoPath))
            return Results.File(photoPath, "image/jpeg");
    }

    var placeholderPath = Path.Combine(env.WebRootPath, "img", "avatar-placeholder.svg");
    return Results.File(placeholderPath, "image/svg+xml");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
