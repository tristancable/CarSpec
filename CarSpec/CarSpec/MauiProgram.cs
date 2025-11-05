using CarSpec.Interfaces;
using CarSpec.Services.Obd;
using CarSpec.Services.Profiles;
using CarSpec.Services.Storage;
using Microsoft.Extensions.Logging;
using Syncfusion.Blazor;

namespace CarSpec
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
            //builder.Services.AddSingleton<ObdService>();
            builder.Services.AddSingleton<ObdConnectionService>();
            builder.Services.AddScoped<IVehicleProfileService, VehicleProfileService>();
            builder.Services.AddScoped<IAppStorage, PreferencesStorage>();
            builder.Services.AddSyncfusionBlazor();


#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}