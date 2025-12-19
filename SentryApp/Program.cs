using Microsoft.EntityFrameworkCore;
using SentryApp.Data;
using SentryApp.Services;
using SentryApp.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AccessControlDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AccessControlDb")));

builder.Services.AddSingleton<TurnstileLogState>();
builder.Services.AddSingleton<IPhotoUrlBuilder, PhotoUrlBuilder>();
builder.Services.AddHostedService<TurnstileLogPollingWorker>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
