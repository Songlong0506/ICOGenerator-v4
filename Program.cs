using ICOGenerator.Data;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Registry;
using ICOGenerator.Services.Tools;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<WorkspaceTools>();
builder.Services.AddScoped<CommandTools>();
builder.Services.AddScoped<GitTools>();
builder.Services.AddScoped<DiffTools>();
builder.Services.AddScoped<ToolDiscoveryService>();
builder.Services.AddScoped<IToolRegistry, ToolRegistry>();
builder.Services.AddScoped<DynamicToolInvoker>();
builder.Services.AddScoped<LocalLlmClient>();
builder.Services.AddScoped<AgentPromptBuilder>();
builder.Services.AddScoped<AgentRunService>();
builder.Services.AddScoped<BARequirementService>();

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
