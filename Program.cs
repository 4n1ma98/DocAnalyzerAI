using DocAnalyzerAI.Components;
using DocAnalyzerAI.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configuración de SignalR para permitir transferencia de archivos grandes en Blazor Server
builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 32 * 1024 * 1024; // 32 MB
});

// Registrar HttpClient para Gemini con timeout adecuado
builder.Services.AddHttpClient(GeminiAuditService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(180);
});

// Registrar servicios de la aplicación
builder.Services.AddScoped<IDocumentReaderService, DocumentReaderService>();
builder.Services.AddScoped<IGeminiAuditService, GeminiAuditService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
