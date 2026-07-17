using ICOGenerator.Application.Account;
using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Audit;
using ICOGenerator.Application.Evals;
using ICOGenerator.Application.Feedback;
using ICOGenerator.Application.Models;
using ICOGenerator.Application.Notifications;
using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Prompts;
using ICOGenerator.Application.Quality;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Application.Roles;
using ICOGenerator.Application.Usage;
using ICOGenerator.Application.Settings;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Feedback;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Notifications.Channels;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Settings;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;
using ICOGenerator.Services.Tools.PullRequests;
using ICOGenerator.Services.Workflows;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Net;
using System.Security.Claims;

namespace ICOGenerator.Extensions;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddIcoGeneratorApplication(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Validate the antiforgery token on every unsafe verb by default, so a new POST action is
        // CSRF-protected even if it forgets [ValidateAntiForgeryToken]. [IgnoreAntiforgeryToken] opts out.
        services.AddControllersWithViews(options =>
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        services.AddAuthServices(configuration, environment);
        services.AddObservabilityServices(configuration, environment);
        // MUST stay Singleton: OnModelCreating captures this instance in the ApiKey value-converter
        // and EF caches that model globally; Scoped/Transient would bind it to a disposed instance.
        services.AddSingleton<IApiKeyProtector, AesApiKeyProtector>();
        // Provider DB chọn được qua "Database:Provider" (mặc định SqlServer cho môi trường thật). Đặt
        // "Sqlite" để chạy app end-to-end ở nơi KHÔNG có SQL Server (Claude Code web / CI / máy dev không
        // cài SQL Server) — model đã provider-agnostic (test cũng chạy trên Sqlite). Sqlite tạo schema
        // bằng EnsureCreated thay vì Migrate vì migration sinh ra là SQL-Server-specific (xem DbInitializer).
        var dbProvider = configuration["Database:Provider"];
        services.AddDbContext<AppDbContext>(options =>
        {
            if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                // Connection string mặc định cho Sqlite nếu chưa cấu hình (hoặc đang trỏ vào SQL Server).
                var connectionString = configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrWhiteSpace(connectionString) ||
                    connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase))
                {
                    connectionString = "Data Source=ICOGenerator.db";
                }
                options.UseSqlite(connectionString);
            }
            else
            {
                // Retry on transient SQL faults (connection blips, deadlocks) so a momentary glitch while
                // saving a task's status doesn't surface as an unhandled exception and strand the task.
                options.UseSqlServer(
                    configuration.GetConnectionString("DefaultConnection"),
                    sql => sql.EnableRetryOnFailure());
            }
        });

        services.AddProjectUseCases();
        services.AddRoleUseCases();
        services.AddRequirementUseCases();
        services.AddAgentUseCases();
        services.AddModelUseCases();
        services.AddUsageUseCases();
        services.AddQualityUseCases();
        services.AddEvalUseCases();
        services.AddPromptStudioUseCases();
        services.AddSettingsUseCases();
        services.AddFeedbackUseCases();
        services.AddAuditUseCases();
        services.AddNotificationServices(configuration);
        services.AddPromptServices();
        services.AddBudgetServices();
        services.AddLlmServices(configuration);
        services.AddArtifactServices();
        services.AddToolServices();
        services.AddRequirementServices();
        services.AddAgentRuntime();
        services.AddWorkflowServices();
        services.AddTemplateServices();

        return services;
    }

    private static IServiceCollection AddAuthServices(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Phân quyền: cache (singleton) chia sẻ giữa các request; PermissionService scoped vì phụ thuộc DbContext.
        services.AddMemoryCache();
        services.AddScoped<IPermissionService, PermissionService>();
        // Phân quyền THEO PROJECT (chặn truy cập chéo bằng GUID đoán/lộ): scoped vì phụ thuộc DbContext.
        services.AddScoped<IProjectAccessGuard, ProjectAccessGuard>();

        // Audit log thay đổi cấu hình: cần actor từ request hiện tại ⇒ đăng ký IHttpContextAccessor; logger
        // scoped vì dùng DbContext. Đặt ở AddAuthServices cùng PermissionService (cross-cutting bảo mật).
        services.AddHttpContextAccessor();
        services.AddScoped<IAuditLogger, AuditLogger>();

        // Cờ chọn kiểu đăng nhập: "Local" (form username/password tự code, mặc định) hoặc "IdentityServer"
        // (SSO Bosch). Đăng ký singleton để AccountController đọc mà không cần IOptions. Tương lai bỏ login
        // tự code ⇒ chỉ đổi cờ này sang IdentityServer, không phải sửa code.
        var authSettings = configuration.GetSection(AuthenticationSettings.SectionName).Get<AuthenticationSettings>()
            ?? new AuthenticationSettings();
        services.AddSingleton(authSettings);

        if (authSettings.Provider == AuthProvider.IdentityServer)
        {
            var identityServer = configuration.GetSection(IdentityServerSettings.SectionName).Get<IdentityServerSettings>()
                ?? new IdentityServerSettings();
            services.AddSingleton(identityServer);
            // Bridge danh tính SSO → AppUser (tra/tự tạo user, đọc DbContext) ⇒ scoped.
            services.AddScoped<SsoUserProvisioner>();
            services.AddIdentityServerAuthentication(identityServer, environment);
        }
        else
        {
            services.AddLocalCookieAuthentication(environment);
        }

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

    // Cấu hình cookie phiên đăng nhập của app — dùng chung cho cả hai provider: Local coi cookie là nơi
    // đăng nhập trực tiếp; IdentityServer coi cookie là SignInScheme lưu phiên SAU khi OIDC xác thực xong.
    private static void ConfigureAppCookie(CookieAuthenticationOptions options, IWebHostEnvironment environment)
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Always send the auth cookie over HTTPS only; relax to SameAsRequest in Development so
        // the cookie still works over the plain-HTTP local profile (http://localhost:55357).
        options.Cookie.SecurePolicy = environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    }

    // Đăng nhập cục bộ (mặc định): chỉ cookie. Không có form mật khẩu — AccountController tự đăng nhập
    // bằng tài khoản Admin seed sẵn khi cookie LoginPath redirect người dùng chưa đăng nhập tới.
    private static void AddLocalCookieAuthentication(this IServiceCollection services, IWebHostEnvironment environment)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => ConfigureAppCookie(options, environment));
    }

    // SSO qua IdentityServer: cookie giữ phiên (SignInScheme), OIDC là scheme thách thức (đẩy user sang IdP).
    // Cấu hình OIDC theo mẫu Bosch (implicit "token id_token", lấy claim từ userinfo). Điểm mấu chốt là
    // OnTokenValidated: bắc cầu danh tính SSO về một AppUser rồi phát lại claim Name (username gắn quyền
    // sở hữu) + Role (lái toàn bộ phân quyền) — nếu không app sẽ không biết vai trò của người đăng nhập.
    private static void AddIdentityServerAuthentication(this IServiceCollection services, IdentityServerSettings ids, IWebHostEnvironment environment)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options => ConfigureAppCookie(options, environment))
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.Authority = ids.BaseURL;
                o.ClientId = ids.Client_Id;
                if (!string.IsNullOrWhiteSpace(ids.ClientSecret))
                    o.ClientSecret = ids.ClientSecret;
                o.ResponseType = string.IsNullOrWhiteSpace(ids.ResponseType) ? "token id_token" : ids.ResponseType;
                o.SaveTokens = true;
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                if (!string.IsNullOrWhiteSpace(ids.APIName))
                    o.Scope.Add(ids.APIName);
                o.Scope.Add("IdentityServerApi");
                o.GetClaimsFromUserInfoEndpoint = true;
                o.RequireHttpsMetadata = ids.RequireHttpsMetadata;
                // Giữ tên claim gốc (preferred_username/email/name/sub/role) thay vì map sang URI dài, để
                // phần bridge đọc claim ổn định; Name/Role của app do ta tự phát lại theo AppUser bên dưới.
                o.MapInboundClaims = false;
                o.TokenValidationParameters.NameClaimType = ClaimTypes.Name;
                o.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                o.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = ctx => BridgeSsoIdentityAsync(ctx, ids),
                    OnRemoteFailure = HandleSsoFailure
                };
            });
    }

    // Bắc cầu danh tính IdentityServer về AppUser rồi phát lại claim Name (username) + Role của app. Chạy
    // trong OnTokenValidated nên claim id_token (preferred_username/email/name/sub) đã sẵn sàng; claim từ
    // userinfo tới sau vẫn được ghép vào cookie nhưng không cần cho bước tra AppUser (mặc định theo NTID).
    private static async Task BridgeSsoIdentityAsync(TokenValidatedContext context, IdentityServerSettings ids)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            context.Fail("IdentityServer không trả về danh tính hợp lệ.");
            return;
        }

        var username = context.Principal.FindFirstValue("username");
        var displayName = context.Principal.FindFirstValue("name");
        var email = context.Principal.FindFirstValue("email");

        // Vai trò do IdentityServer phát (claim "role", có thể nhiều) → UserRole của app. null = không claim
        // nào khớp mapping ⇒ provisioner dùng DefaultRole cho user mới, giữ nguyên vai trò cho user cũ.
        var ssoRoles = context.Principal.FindAll(ids.RoleClaim).Select(c => c.Value);
        var roleFromClaims = ids.MapRole(ssoRoles);

        var provisioner = context.HttpContext.RequestServices.GetRequiredService<SsoUserProvisioner>();
        var appUser = await provisioner.ResolveOrProvisionAsync(
            username, displayName, email, roleFromClaims, ids.DefaultRole, context.HttpContext.RequestAborted);

        // Provisioner trả null = từ chối truy cập (user bị khóa / username rỗng). Fail rõ ràng để rơi vào
        // OnRemoteFailure → trang AccessDenied, thay vì NRE khi phát lại claim bên dưới.
        if (appUser is null)
        {
            context.Fail("Tài khoản SSO bị khóa hoặc không hợp lệ.");
            return;
        }

        // AppUser là nguồn sự thật cho Name (quyền sở hữu) + Role (phân quyền): bỏ mọi claim cùng loại đến
        // từ IdP rồi phát lại theo AppUser để PermissionService/ProjectAccessGuard đọc đúng.
        foreach (var claim in identity.FindAll(ClaimTypes.Name).ToList())
            identity.RemoveClaim(claim);
        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(ClaimTypes.Name, appUser.Username));
        identity.AddClaim(new Claim(ClaimTypes.Role, appUser.Role.ToString()));
    }

    // Đăng nhập SSO thất bại (token bị từ chối ở OnTokenValidated, user hủy ở IdP, lỗi giao thức…): ghi log
    // rồi đưa về trang "không đủ quyền" ([AllowAnonymous]) thay vì ném stack trace. HandleResponse chặn
    // handler chạy tiếp — tránh vòng lặp challenge lại IdP.
    private static Task HandleSsoFailure(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("ICOGenerator.Sso");
        logger.LogWarning(context.Failure, "Đăng nhập SSO IdentityServer thất bại.");
        context.Response.Redirect("/Account/AccessDenied");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    // OpenTelemetry trace + metric — OPT-IN (Otel:Enabled, mặc định TẮT, cùng tinh thần opt-in như
    // Llm:Proxy / StructuredOutput / Budget). Chưa có OTLP collector thì KHÔNG đăng ký gì: tránh sinh
    // lỗi exporter vô nghĩa và không thêm overhead. Khi bật: instrument ASP.NET Core + HttpClient (nên
    // các lời gọi LLM ra ngoài tự thành span — dựng lại được chuỗi agent → model → tool) và metric
    // runtime/HTTP, rồi xuất qua OTLP tới collector (Otel Collector / Jaeger / Tempo / Grafana).
    private static IServiceCollection AddObservabilityServices(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (!configuration.GetValue("Otel:Enabled", false))
            return services;

        var serviceName = configuration.GetValue("Otel:ServiceName", "ICOGenerator")!;
        var otlpEndpoint = configuration.GetValue<string>("Otel:OtlpEndpoint");

        // Endpoint trống ⇒ exporter dùng mặc định OTLP gRPC http://localhost:4317.
        void ConfigureOtlp(OtlpExporterOptions options)
        {
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                options.Endpoint = new Uri(otlpEndpoint);
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: serviceName, serviceInstanceId: Environment.MachineName)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", environment.EnvironmentName)
                }))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(ConfigureOtlp))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(ConfigureOtlp));

        return services;
    }

    private static IServiceCollection AddProjectUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetProjectListQuery>();
        services.AddScoped<CreateProjectUseCase>();
        services.AddScoped<UpdateDeliveryConfigUseCase>();
        services.AddScoped<GetMockupFileQuery>();
        services.AddScoped<GetImplementationSourceQuery>();
        services.AddScoped<GetPocReviewQuery>();
        services.AddScoped<ListPocCommentsQuery>();
        services.AddScoped<AddPocCommentUseCase>();
        services.AddScoped<DeletePocCommentUseCase>();
        return services;
    }

    private static IServiceCollection AddRoleUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetRolePermissionMatrixQuery>();
        services.AddScoped<UpdateRolePermissionsUseCase>();
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
        services.AddScoped<RequestStageRevisionUseCase>();
        services.AddScoped<RetryWorkflowUseCase>();
        services.AddScoped<StartNewChatUseCase>();
        services.AddScoped<UploadProjectSourceUseCase>();
        services.AddScoped<DeleteProjectSourceUseCase>();
        services.AddScoped<GetDocumentRevisionsQuery>();
        services.AddScoped<GetDocumentRevisionDiffQuery>();
        return services;
    }

    private static IServiceCollection AddAgentUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetAgentDashboardQuery>();
        services.AddScoped<GetAgentStatsQuery>();
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

    private static IServiceCollection AddQualityUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetDeliveryQualityQuery>();
        return services;
    }

    private static IServiceCollection AddEvalUseCases(this IServiceCollection services)
    {
        // Trang Prompt Evals: use case/query scoped (DbContext); runner scoped (DbContext) và worker nền
        // poll run Queued (cùng mẫu AgentTaskWorker). PromptFileCatalog nằm ở AddPromptServices.
        services.AddScoped<GetEvalPageQuery>();
        services.AddScoped<CreateEvalScenarioUseCase>();
        services.AddScoped<UpdateEvalScenarioUseCase>();
        services.AddScoped<DeleteEvalScenarioUseCase>();
        services.AddScoped<StartEvalRunUseCase>();
        services.AddScoped<GetEvalRunStatusQuery>();
        services.AddScoped<GetEvalRunDetailQuery>();
        services.AddScoped<CompareEvalRunsQuery>();
        services.AddScoped<EvalRunnerService>();
        services.AddHostedService<EvalRunWorker>();
        return services;
    }

    private static IServiceCollection AddPromptStudioUseCases(this IServiceCollection services)
    {
        // Prompt Studio (Application/Prompts): tất cả scoped vì dùng DbContext. Danh sách prompt nay nằm
        // trong trang Agents (GetAgentManagementPageQuery); controller này chỉ lo chi tiết/lịch sử.
        services.AddScoped<GetPromptDetailQuery>();
        services.AddScoped<GetPromptVersionDiffQuery>();
        services.AddScoped<GetPromptVersionDownloadQuery>();
        services.AddScoped<SavePromptVersionUseCase>();
        services.AddScoped<ActivatePromptVersionUseCase>();
        services.AddScoped<RevertPromptToFileUseCase>();
        return services;
    }

    private static IServiceCollection AddSettingsUseCases(this IServiceCollection services)
    {
        services.AddSingleton<AppSettingsFileStore>();
        services.AddScoped<GetAppSettingsQuery>();
        services.AddScoped<UpdateAppSettingsUseCase>();
        return services;
    }

    private static IServiceCollection AddAuditUseCases(this IServiceCollection services)
    {
        services.AddScoped<GetAuditLogPageQuery>();
        return services;
    }

    private static IServiceCollection AddFeedbackUseCases(this IServiceCollection services)
    {
        // Store stateless + config-bound (như AppSettingsFileStore) ⇒ singleton; các use case/query scoped vì dùng DbContext.
        services.AddSingleton<FeedbackAttachmentStore>();
        services.AddScoped<GetFeedbackPageQuery>();
        services.AddScoped<SubmitFeedbackUseCase>();
        services.AddScoped<UpdateFeedbackStatusUseCase>();
        services.AddScoped<GetFeedbackAttachmentQuery>();
        services.AddScoped<DeleteFeedbackUseCase>();
        return services;
    }

    private static IServiceCollection AddNotificationServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Ghi thông báo (dùng bởi worker) + đọc/đánh dấu (dùng bởi controller). Tất cả scoped vì phụ thuộc DbContext.
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<GetNotificationsQuery>();
        services.AddScoped<MarkNotificationReadUseCase>();
        services.AddScoped<MarkAllNotificationsReadUseCase>();
        services.AddScoped<GetNotificationPreferencesQuery>();
        services.AddScoped<UpdateNotificationPreferencesUseCase>();

        // Kênh NGOÀI (Teams/email) — OPT-IN: bind section "Notifications" (mặc định TẮT nên không gửi gì và
        // không có overhead mạng). Teams cần HttpClient (typed client như GitHubPullRequestPublisher); Email
        // dùng SmtpClient của BCL nên chỉ cần singleton. Cả hai self-disable khi thiếu cấu hình.
        var options = configuration.GetSection("Notifications").Get<NotificationOptions>() ?? new NotificationOptions();
        services.AddSingleton(options);
        services.AddHttpClient<INotificationChannel, TeamsNotificationChannel>();
        services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
        return services;
    }

    private static IServiceCollection AddPromptServices(this IServiceCollection services)
    {
        services.AddScoped<PromptTemplateService>();
        // Bản prompt chỉnh runtime (Prompt Studio) ghi đè nội dung file: provider scoped (DbContext),
        // cache các bản active nằm trong IMemoryCache (singleton) nên vẫn một query mỗi 30s cho cả tiến trình.
        services.AddScoped<IPromptOverrideProvider, DbPromptOverrideProvider>();
        // Danh mục file .md dưới /Prompts (dùng bởi Prompt Studio + scenario eval): quét đĩa một lần ⇒ singleton.
        services.AddSingleton<PromptFileCatalog>();
        return services;
    }

    private static IServiceCollection AddBudgetServices(this IServiceCollection services)
    {
        // Config-bound USD caps (singleton like StructuredOutputPolicy); the guard needs the scoped DbContext
        // to sum spend, so it is scoped. Registered before LLM services since LlmClient/AgentRunService depend on it.
        services.AddSingleton<BudgetPolicy>();
        services.AddScoped<IBudgetGuard, BudgetGuard>();
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
        // Config-bound, opt-in choice of structured output (response_format: json_schema) per model.
        services.AddSingleton<StructuredOutputPolicy>();
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

        // PR publisher: typed HttpClient gọi GitHub REST API để TẠO PR thật khi PullRequest:GitHubToken
        // được cấu hình và remote là github.com; nếu không, GitTools.OpenPullRequest fallback về link compare.
        // GitHub API bắt buộc User-Agent; Accept/version theo khuyến nghị GitHub.
        services.AddHttpClient<IPullRequestPublisher, GitHubPullRequestPublisher>(c =>
        {
            c.BaseAddress = new Uri("https://api.github.com/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ICOGenerator");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        services.AddScoped<ToolDiscoveryService>();
        services.AddScoped<IToolRegistry, ToolRegistry>();
        return services;
    }

    private static IServiceCollection AddAgentRuntime(this IServiceCollection services)
    {
        services.AddScoped<AgentInstructionProvider>();
        services.AddScoped<AgentPromptBuilder>();
        services.AddScoped<AgentRunService>();
        return services;
    }

    private static IServiceCollection AddRequirementServices(this IServiceCollection services)
    {
        // Ba use case của luồng BA (chat / draft Product Brief / tài liệu sau Approve) + các mảnh dùng
        // chung (resolver agent, cổng readiness, ghi lượt hội thoại) — tách từ BARequirementService cũ.
        services.AddScoped<BAChatService>();
        services.AddScoped<ProductBriefDraftService>();
        services.AddScoped<RequirementDocsService>();
        services.AddScoped<BAAgentResolver>();
        services.AddScoped<RequirementReadinessGate>();
        services.AddScoped<BAConversationLog>();
        services.AddScoped<RequirementPromptBuilder>();
        services.AddScoped<RequirementResponseParser>();
        services.AddScoped<BAChatReplyParser>();
        services.AddScoped<RequirementReadinessParser>();
        services.AddScoped<RequirementDocumentGenerator>();
        // Diff thuần in-memory, stateless ⇒ singleton (như các policy/store stateless khác).
        services.AddSingleton<DocumentDiffService>();
        services.AddScoped<ProjectSourceIngestor>();
        services.AddScoped<SourceContextBuilder>();
        services.AddScoped<ConversationMemoryService>();
        services.AddScoped<UserMemoryService>();
        services.AddScoped<ChecklistGapMemoryService>();
        services.AddScoped<RequirementCoverageService>();
        services.AddScoped<ProductBriefReviewParser>();
        // Bối cảnh tổ chức Bosch render từ OrgUnits/Associates cho prompt BA (chat + soạn tài liệu).
        // Scoped vì dùng DbContext; bản render dùng chung nằm trong IMemoryCache (singleton) nên vẫn
        // chỉ tốn một lần dựng mỗi giờ cho cả tiến trình.
        services.AddScoped<OrganizationContextService>();
        return services;
    }

    private static IServiceCollection AddWorkflowServices(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowOrchestrator, WorkflowOrchestrator>();
        services.AddScoped<WorkflowTaskPromptBuilder>();
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
