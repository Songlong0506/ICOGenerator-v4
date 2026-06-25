using ICOGenerator.Data;
using ICOGenerator.Extensions;
using Serilog;

// Bootstrap logger: bắt cả lỗi xảy ra TRƯỚC khi host dựng xong (đọc config, build DI, migrate DB ở
// DbInitializer). Sau khi host build xong, UseSerilog thay nó bằng logger cấu hình đầy đủ từ appsettings.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Thay logging Console mặc định của ASP.NET bằng Serilog. MinimumLevel/sink/enrich đọc từ section
    // "Serilog" trong appsettings (đổi mức log không cần build lại); ReadFrom.Services nạp enricher/sink
    // đăng ký qua DI; FromLogContext cho phép gắn thuộc tính theo phạm vi (vd ProjectId) vào log.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddIcoGeneratorApplication(builder.Configuration, builder.Environment);

    var app = builder.Build();

    await DbInitializer.InitializeAsync(app.Services);

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    // Một dòng log tóm tắt có cấu trúc cho mỗi HTTP request (method, path, status, thời lượng) thay cho
    // log mặc định dài dòng của ASP.NET. Đặt sớm để bao trùm toàn bộ pipeline phía sau.
    app.UseSerilogRequestLogging();

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
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // Đưa lỗi khởi động vào log Fatal thay vì để stack trace trần ra stderr. HostAbortedException là
    // bình thường khi chạy công cụ EF design-time (dotnet ef migrations/database) nên không coi là Fatal.
    Log.Fatal(ex, "ICOGenerator dừng đột ngột khi khởi động");
}
finally
{
    // Flush mọi log còn nằm trong buffer (sink File) trước khi tiến trình thoát.
    Log.CloseAndFlush();
}
