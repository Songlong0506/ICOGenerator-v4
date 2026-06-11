using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Models;
using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Extensions;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddIcoGeneratorApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllersWithViews();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));

        services.AddProjectUseCases();
        services.AddRequirementUseCases();
        services.AddAgentUseCases();
        services.AddModelUseCases();
        services.AddPromptServices();
        services.AddLlmServices();
        services.AddArtifactServices();
        services.AddToolServices();
        services.AddRequirementServices();
        services.AddAgentRuntime();
        services.AddWorkflowServices();
        services.AddTemplateServices();

        return services;
    }

    private static IServiceCollection AddProjectUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetProjectListQuery>();
        services.AddScoped<CreateProjectUseCase>();
        services.AddScoped<GetMockupFileQuery>();
        return services;
    }

    private static IServiceCollection AddRequirementUseCases(this IServiceCollection services)
    {
        // Application layer: use cases & queries driven by RequirementsController.
        services.AddScoped<GetRequirementWorkspaceQuery>();
        services.AddScoped<StartRequirementChatUseCase>();
        services.AddScoped<GetRequirementJobStatusQuery>();
        services.AddScoped<GetDocumentDownloadQuery>();
        services.AddScoped<GenerateRequirementDraftUseCase>();
        services.AddScoped<ChatWithBAUseCase>();
        services.AddScoped<ApproveRequirementUseCase>();
        return services;
    }

    private static IServiceCollection AddAgentUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetAgentDashboardQuery>();
        services.AddScoped<GetWorkflowStatusQuery>();
        services.AddScoped<GetAgentCallLogsQuery>();
        services.AddScoped<GetCallLogDetailQuery>();
        services.AddScoped<GetAgentManagementPageQuery>();
        services.AddScoped<UpdateAgentUseCase>();
        return services;
    }

    private static IServiceCollection AddModelUseCases(this IServiceCollection services)
    {
        services.AddScoped<ListAiModelsQuery>();
        services.AddScoped<CreateAiModelUseCase>();
        services.AddScoped<UpdateAiModelUseCase>();
        services.AddScoped<SetDefaultAiModelUseCase>();
        services.AddScoped<DeleteAiModelUseCase>();
        return services;
    }

    private static IServiceCollection AddPromptServices(this IServiceCollection services)
    {
        services.AddScoped<PromptTemplateService>();
        return services;
    }

    private static IServiceCollection AddLlmServices(this IServiceCollection services)
    {
        services.AddScoped<IModelCallLogger, ModelCallLogger>();
        services.AddScoped<ILlmClient, LlmClient>();
        return services;
    }

    private static IServiceCollection AddArtifactServices(this IServiceCollection services)
    {
        services.AddScoped<IProjectArtifactCatalog, ProjectArtifactCatalog>();
        services.AddScoped<IArtifactStorage, LocalArtifactStorage>();
        services.AddScoped<WorkspacePathResolver>();
        return services;
    }

    private static IServiceCollection AddToolServices(this IServiceCollection services)
    {
        services.AddScoped<ToolPolicyService>();
        services.AddScoped<IToolExecutionLogger, ToolExecutionLogger>();
        services.AddScoped<WorkspaceTools>();
        services.AddScoped<CommandTools>();
        services.AddScoped<GitTools>();
        services.AddScoped<DiffTools>();
        services.AddScoped<ToolDiscoveryService>();
        services.AddScoped<IToolRegistry, ToolRegistry>();
        services.AddScoped<DynamicToolInvoker>();
        return services;
    }

    private static IServiceCollection AddAgentRuntime(this IServiceCollection services)
    {
        // Services/Agents: the autonomous tool-using agent loop + its background job runner.
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<AgentActionParser>();
        services.AddScoped<AgentRunService>();
        services.AddHostedService<AgentJobRunner>();
        return services;
    }

    private static IServiceCollection AddRequirementServices(this IServiceCollection services)
    {
        // Services/Requirements: domain services that turn a BA conversation into requirement documents.
        services.AddScoped<BARequirementService>();
        services.AddScoped<RequirementPromptBuilder>();
        services.AddScoped<RequirementResponseParser>();
        services.AddScoped<RequirementDocumentGenerator>();
        return services;
    }

    private static IServiceCollection AddWorkflowServices(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowOrchestrator, WorkflowOrchestrator>();
        services.AddSingleton<IWorkflowProgressReporter, WorkflowProgressReporter>();
        services.AddHostedService<AgentTaskWorker>();
        return services;
    }

    private static IServiceCollection AddTemplateServices(this IServiceCollection services)
    {
        services.AddScoped<RequirementTemplateService>();
        services.AddScoped<DocxTemplateWriter>();
        return services;
    }
}
