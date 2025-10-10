using CarSpec.Shared.Services;
using CarSpec.Web.Client.Services;
using CarSpec.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Shared services
builder.Services.AddSingleton<IFormFactor, FormFactor>();

var app = builder.Build();

// Middleware pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// Important order for Blazor Hosted
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();

// New for .NET 8+ Hosted WASM: maps static assets before render mode registration
app.MapStaticAssets();

// Optional if you have Web APIs
app.MapControllers();

// Razor Components: only add necessary assemblies once
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(CarSpec.Web.Client._Imports).Assembly);

// Fallback to client-side index.html
app.MapFallbackToFile("index.html");

app.Run();
