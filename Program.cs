using ICOGenerator.Data;
using ICOGenerator.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIcoGeneratorApplication(builder.Configuration);

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapControllerRoute(name: "default", pattern: "{controller=Projects}/{action=Index}/{id?}");
app.Run();
