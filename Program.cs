using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Models;
using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Registry;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Workflows;
using ICOGenerator.Services.Workspace;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Abstractions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));


builder.Services.AddScoped<GetProjectListQuery>();
builder.Services.AddScoped<CreateProjectUseCase>();
builder.Services.AddScoped<GetMockupFileQuery>();
builder.Services.AddScoped<GetRequirementWorkspaceQuery>();
builder.Services.AddScoped<StartRequirementChatUseCase>();
builder.Services.AddScoped<GetRequirementJobStatusQuery>();
builder.Services.AddScoped<GetDocumentDownloadQuery>();
builder.Services.AddScoped<GenerateRequirementDraftUseCase>();
builder.Services.AddScoped<GetAgentDashboardQuery>();
builder.Services.AddScoped<GetAgentCallLogsQuery>();
builder.Services.AddScoped<GetCallLogDetailQuery>();
builder.Services.AddScoped<GetAgentManagementPageQuery>();
builder.Services.AddScoped<UpdateAgentUseCase>();
builder.Services.AddScoped<ListAiModelsQuery>();
builder.Services.AddScoped<CreateAiModelUseCase>();
builder.Services.AddScoped<UpdateAiModelUseCase>();
builder.Services.AddScoped<SetDefaultAiModelUseCase>();
builder.Services.AddScoped<DeleteAiModelUseCase>();

builder.Services.AddScoped<PromptTemplateService>();
builder.Services.AddScoped<IModelCallLogger, ModelCallLogger>();
builder.Services.AddScoped<ILlmClient, LocalLlmClient>();
builder.Services.AddScoped<IProjectArtifactCatalog, ProjectArtifactCatalog>();
builder.Services.AddScoped<IArtifactStorage, LocalArtifactStorage>();
builder.Services.AddScoped<ToolPolicyService>();
builder.Services.AddScoped<IToolExecutionLogger, ToolExecutionLogger>();
builder.Services.AddScoped<WorkspacePathResolver>();
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
builder.Services.AddScoped<RequirementPromptBuilder>();
builder.Services.AddScoped<RequirementResponseParser>();
builder.Services.AddScoped<RequirementDocumentGenerator>();
builder.Services.AddScoped<ApproveRequirementUseCase>();
builder.Services.AddHostedService<AgentJobRunner>();
builder.Services.AddHostedService<AgentTaskWorker>();
builder.Services.AddScoped<RequirementTemplateService>();
builder.Services.AddScoped<DocxTemplateWriter>();

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
