using SentrySMS.Components;
using SentrySMS.Models;
using SentrySMS.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<GsmSettings>(builder.Configuration.GetSection(GsmSettings.SectionName));
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<GsmService>();
builder.Services.AddSingleton<ToastService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
