using DaxemondStockMarkerApp.Components;
using DaxemondStockMarkerApp.Services;
using FunctionAppMovers.Services;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddSingleton<IMoverService, MoverService>();
builder.Services.AddSingleton<IYahooService, YahooService>();
builder.Services.AddSingleton<IHkexLoader, HkexLoader>();
builder.Services.AddSingleton<IGraphMailer, GraphMailer>();
builder.Services.AddHostedService<TaskWorkerService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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
