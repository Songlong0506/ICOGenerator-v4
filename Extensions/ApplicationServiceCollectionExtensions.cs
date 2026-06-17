using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Models;
using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Application.Usage;
using ICOGenerator.Application.Settings;
using ICOGenerator.Data;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Settings;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace ICOGenerator.Extensions;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddIcoGeneratorApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllersWithViews();
        services.AddSingleton<IApiKeyProtector, AesApiKeyProtector>();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));

        services.AddProjectUseCases();
        services.AddRequirementUseCases();
        services.AddAgentUseCases();
        services.AddModelUseCases();
        services.AddUsageUseCases();
        services.AddSettingsUseCases();
        services.AddPromptServices();
        services.AddLlmServices(configuration);
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
        services.AddScoped<GetDocumentPreviewQuery>();
        services.AddScoped<GetAgentManagementPageQuery>();
        services.AddScoped<UpdateAgentUseCase>();
        return services;
    }

    private static IServiceCollection AddModelUseCases(this IServiceCollection services)
    {
        services.AddScoped<ListAiModelsQuery>();
        services.AddScoped<CreateAiModelUseCase>();
        services.AddScoped<UpdateAiModelUseCase>();
        services.AddScoped<DeleteAiModelUseCase>();
        return services;
    }

    private static IServiceCollection AddUsageUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetUsageOverviewQuery>();
        return services;
    }

    private static IServiceCollection AddSettingsUseCases(this IServiceCollection services)
    {
        services.AddSingleton<AppSettingsFileStore>();
        services.AddScoped<GetAppSettingsQuery>();
        services.AddScoped<UpdateAppSettingsUseCase>();
        return services;
    }

    private static IServiceCollection AddPromptServices(this IServiceCollection services)
    {
        services.AddScoped<PromptTemplateService>();
        return services;
    }

    private static IServiceCollection AddLlmServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Proxy is config-driven so the same build works behind the office proxy
        // and at home where it doesn't exist. Toggle via appsettings.json or the
        // environment variables Llm__Proxy__Enabled / Llm__Proxy__Address.
        // Defaults preserve the previous office behaviour (proxy on, port 3128).
        var proxyEnabled = configuration.GetValue("Llm:Proxy:Enabled", true);
        var proxyAddress = configuration.GetValue("Llm:Proxy:Address", "http://127.0.0.1:3128");

        // Two pooled clients so LlmClient never news up a handler per call:
        // one direct (localhost models) and one routed through the local proxy.
        services.AddHttpClient(LlmClient.DirectClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

        services.AddHttpClient(LlmClient.ProxiedClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // When the proxy is disabled (e.g. at home) the "proxied" client
                // falls back to a direct connection instead of failing.
                UseProxy = proxyEnabled,
                Proxy = proxyEnabled ? new WebProxy(proxyAddress) : null,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

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
        // Services/Agents: the autonomous tool-using agent loop used by the workflow worker.
        services.AddScoped<AgentInstructionProvider>();
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<AgentActionParser>();
        services.AddScoped<AgentRunService>();
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
