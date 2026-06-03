using ICOGenerator.Application.Abstractions;
using ICOGenerator.Data;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Registry;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Templates;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Workflows;
using ICOGenerator.Services.Workflows.Engine;
using ICOGenerator.Services.Workflows.Steps;
using ICOGenerator.Services.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ICOGenerator.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddIcoGeneratorInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IWorkspacePathResolver, WorkspacePathResolver>();
        services.AddScoped<IBARequirementService, BARequirementService>();

        services.AddPromptServices();
        services.AddLlmServices();
        services.AddArtifactServices();
        services.AddToolServices();
        services.AddAgentRuntime();
        services.AddWorkflowServices();
        services.AddTemplateServices();
        services.AddRequirementServices();

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
        services.AddScoped<ILlmClient, LocalLlmClient>();
        services.AddScoped<LocalLlmClient>();
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
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<AgentActionParser>();
        services.AddScoped<AgentRunService>();
        services.AddScoped<BARequirementService>();
        services.AddHostedService<AgentJobRunner>();
        return services;
    }

    private static IServiceCollection AddWorkflowServices(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowOrchestrator, WorkflowOrchestrator>();
        services.AddScoped<WorkflowTaskDispatcher>();
        services.AddScoped<IWorkflowStepHandler, ImplementationStepHandler>();
        services.AddHostedService<AgentTaskWorker>();
        return services;
    }

    private static IServiceCollection AddTemplateServices(this IServiceCollection services)
    {
        services.AddScoped<RequirementTemplateService>();
        services.AddScoped<DocxTemplateWriter>();
        return services;
    }

    private static IServiceCollection AddRequirementServices(this IServiceCollection services)
    {
        services.AddScoped<RequirementPromptBuilder>();
        services.AddScoped<RequirementResponseParser>();
        services.AddScoped<RequirementDocumentGenerator>();
        return services;
    }
}
