using ICOGenerator.Data;
using ICOGenerator.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIcoGeneratorApplication(builder.Configuration, builder.Environment);

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Baseline security headers on every response (defense-in-depth against MIME sniffing and clickjacking).
// No global CSP here so existing inline scripts/styles keep working; the LLM-generated mockup is sandboxed
// separately on its own endpoint (ProjectsController.Mockup).
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute(name: "default", pattern: "{controller=Projects}/{action=Index}/{id?}");
app.Run();
