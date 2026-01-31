using Microsoft.EntityFrameworkCore;
using SentryApp.Data;
using SentryApp.Services;
using SentryApp.Components;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
}

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

var isLiveMode = builder.Configuration.GetValue<bool>("IsLiveMode");
if (!isLiveMode)
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

app.MapPost("/sms/send", HandleSmsSendAsync)
    .DisableAntiforgery();
app.MapPost("/send-sms", HandleSmsSendAsync)
    .DisableAntiforgery();

app.Run();

static async Task<IResult> HandleSmsSendAsync(
    HttpRequest request,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var smsRequest = await SmsRequestParser.ParseAsync(request, cancellationToken);

    if (smsRequest is null)
    {
        return Results.BadRequest(new
        {
            error = "Missing SMS recipient or message content. Provide `to` and `message` (or `body`)."
        });
    }

    logger.LogInformation("SMS send requested for {Recipient}.", smsRequest.To);

    return Results.Ok(new { status = "queued", to = smsRequest.To });
}

static class SmsRequestParser
{
    public static async Task<SmsSendRequest?> ParseAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var to = FirstNonEmpty(form["to"], form["phone"], form["phoneNumber"], form["recipient"]);
            var message = FirstNonEmpty(form["message"], form["body"], form["text"]);
            return Build(to, message);
        }

        if (request.ContentLength is null or 0)
        {
            return null;
        }

        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var to = GetPropertyValue(root, "to", "phone", "phoneNumber", "recipient");
        var message = GetPropertyValue(root, "message", "body", "text");
        return Build(to, message);
    }

    private static SmsSendRequest? Build(string? to, string? message)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return new SmsSendRequest(to.Trim(), message.Trim());
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? GetPropertyValue(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }
}

record SmsSendRequest(string To, string Message);
