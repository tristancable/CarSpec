var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles(); // serve Web.Client/_framework/
app.UseStaticFiles();

app.MapFallbackToFile("index.html");

app.Run();