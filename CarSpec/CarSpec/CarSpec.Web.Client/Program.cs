using CarSpec.Shared.Services;
using CarSpec.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<CarSpec.Web.Client.App>("#app");

// Register shared and web-specific services
builder.Services.AddSingleton<IFormFactor, FormFactor>();
builder.Services.AddSingleton<IObdService, WebObdService>();

await builder.Build().RunAsync();
