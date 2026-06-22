using ICOGenerator.Application.Account;
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
using ICOGenerator.Services.Workflows.Maf;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace ICOGenerator.Extensions;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddIcoGeneratorApplication(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Validate the antiforgery token on every unsafe verb by default, so a new POST action is
        // CSRF-protected even if it forgets [ValidateAntiForgeryToken]. [IgnoreAntiforgeryToken] opts out.
        services.AddControllersWithViews(options =>
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        services.AddAuthServices(environment);
        // MUST stay Singleton: OnModelCreating captures this instance in the ApiKey value-converter
        // and EF caches that model globally; Scoped/Transient would bind it to a disposed instance.
        services.AddSingleton<IApiKeyProtector, AesApiKeyProtector>();
        services.AddDbContext<AppDbContext>(options =>
            // Retry on transient SQL faults (connection blips, deadlocks) so a momentary glitch while
            // saving a task's status doesn't surface as an unhandled exception and strand the task.
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql => sql.EnableRetryOnFailure()));

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

    private static IServiceCollection AddAuthServices(this IServiceCollection services, IWebHostEnvironment environment)
    {
        services.AddScoped<LoginUseCase>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Account/Login";
                options.LogoutPath = "/Account/Logout";
                options.AccessDeniedPath = "/Account/Login";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Always send the auth cookie over HTTPS only; relax to SameAsRequest in Development so
                // the cookie still works over the plain-HTTP local profile (http://localhost:55357).
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
            });

        // Secure by default: every endpoint requires auth unless it opts out with [AllowAnonymous],
        // so the command-running Settings page stays guarded even if a controller forgets [Authorize].
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    private static IServiceCollection AddProjectUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetProjectListQuery>();
        services.AddScoped<CreateProjectUseCase>();
        services.AddScoped<GetMockupFileQuery>();
        services.AddScoped<GetImplementationSourceQuery>();
        return services;
    }

    private static IServiceCollection AddRequirementUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetRequirementWorkspaceQuery>();
        services.AddScoped<GetDocumentDownloadQuery>();
        services.AddScoped<GenerateRequirementDraftUseCase>();
        services.AddScoped<ChatWithBAUseCase>();
        services.AddScoped<ApproveRequirementUseCase>();
        services.AddScoped<ApproveStageUseCase>();
        services.AddScoped<RejectStageUseCase>();
        services.AddScoped<RetryWorkflowUseCase>();
        services.AddScoped<StartNewChatUseCase>();
        return services;
    }

    private static IServiceCollection AddAgentUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetAgentDashboardQuery>();
        services.AddScoped<GetWorkflowStatusQuery>();
        services.AddScoped<StreamWorkflowProgressQuery>();
        services.AddScoped<GetAgentActivityQuery>();
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
        // Proxy is config-driven (Llm:Proxy:Enabled / :Address) so one build works both behind the
        // office proxy and at home. Defaults preserve the office behaviour (proxy on, port 3128).
        var proxyEnabled = configuration.GetValue("Llm:Proxy:Enabled", true);
        var proxyAddress = configuration.GetValue("Llm:Proxy:Address", "http://127.0.0.1:3128");

        // Re-injects the non-standard "thinking" field that the typed OpenAI SDK can't express.
        services.AddTransient<ThinkingDisabledHandler>();

        // Two pooled clients (direct for localhost, proxied). Timeout is infinite: the per-call deadline
        // is enforced by LlmClient's linked CancellationToken, not by HttpClient/the SDK.
        services.AddHttpClient(OpenAIChatClientFactory.DirectClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            .AddHttpMessageHandler<ThinkingDisabledHandler>();

        services.AddHttpClient(OpenAIChatClientFactory.ProxiedClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // When the proxy is disabled (e.g. at home) this client falls back to a direct connection.
                UseProxy = proxyEnabled,
                Proxy = proxyEnabled ? new WebProxy(proxyAddress) : null,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            .AddHttpMessageHandler<ThinkingDisabledHandler>();

        // Builds a Microsoft.Extensions.AI IChatClient per AiModel; depends only on the singleton
        // IHttpClientFactory, so it is safe to register as a singleton.
        services.AddSingleton<IChatClientFactory, OpenAIChatClientFactory>();
        // Config-bound, immutable choice of native tool-calling vs the prompt-based fallback per model.
        services.AddSingleton<NativeToolCallingPolicy>();
        services.AddScoped<IModelCallLogger, ModelCallLogger>();
        services.AddScoped<ILlmClient, LlmClient>();
        return services;
    }

    private static IServiceCollection AddArtifactServices(this IServiceCollection services)
    {
        services.AddScoped<IProjectArtifactCatalog, ProjectArtifactCatalog>();
        services.AddScoped<IArtifactStorage, LocalArtifactStorage>();
        services.AddScoped<WorkspacePathResolver>();
        services.AddScoped<ImplementationSourcePackager>();
        services.AddScoped<BoschTemplateSeeder>();
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
        services.AddScoped<AgentInstructionProvider>();
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<AgentActionParser>();
        services.AddScoped<AgentRunService>();
        return services;
    }

    private static IServiceCollection AddRequirementServices(this IServiceCollection services)
    {
        services.AddScoped<BARequirementService>();
        services.AddScoped<RequirementPromptBuilder>();
        services.AddScoped<RequirementResponseParser>();
        services.AddScoped<BAChatReplyParser>();
        services.AddScoped<RequirementReadinessParser>();
        services.AddScoped<RequirementDocumentGenerator>();
        return services;
    }

    private static IServiceCollection AddWorkflowServices(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowOrchestrator, WorkflowOrchestrator>();
        services.AddScoped<WorkflowTaskPromptBuilder>();
        services.AddSingleton<IWorkflowProgressReporter, WorkflowProgressReporter>();
        services.AddScoped<PocWorkspaceSeeder>();
        services.AddHostedService<AgentTaskWorker>();

        // Opt-in Microsoft Agent Framework delivery-pipeline engine (Workflows:UseMafEngine). These are
        // safe to register unconditionally — nothing drives the engine unless the flag is on. The runner,
        // factory and checkpoint store are stateless/singletons that open their own DI scopes per call.
        services.AddSingleton<IPipelineStageRunner, PipelineStageRunner>();
        services.AddSingleton<DeliveryWorkflowFactory>();
        services.AddSingleton<EfWorkflowCheckpointStore>();
        return services;
    }

    private static IServiceCollection AddTemplateServices(this IServiceCollection services)
    {
        services.AddScoped<RequirementTemplateService>();
        services.AddScoped<DocxTemplateWriter>();
        return services;
    }
}
