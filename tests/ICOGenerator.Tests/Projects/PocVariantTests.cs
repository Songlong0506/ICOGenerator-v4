using System.Runtime.CompilerServices;
using System.Text.Json;
using ICOGenerator.Application.Models;
using ICOGenerator.Application.Projects;
using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Execution;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// "Thử model khác" trên trang POC Review (PocVariant). Bất biến quan trọng nhất: một lượt thử là THÍ NGHIỆM —
// nó không được đụng bản demo đang review, kết quả tự kiểm của bản đó, lịch sử hội thoại của dự án, hay
// cấu hình model của agent (dùng chung mọi dự án). Phần còn lại: cùng đầu vào với bản chính, giới hạn
// tiêu tiền (một lượt đang chạy / trần số biến thể), và bảng so sánh chấm mọi bản bằng cùng một thước.
public class PocVariantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly WorkspacePathResolver _resolver;

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _agentModelId = Guid.NewGuid();
    private readonly Guid _candidateModelId = Guid.NewGuid();
    private readonly Guid _developerId = Guid.NewGuid();
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _sourceTaskId = Guid.NewGuid();
    private const string Spec = "# AI Design Spec\n## Screens To Generate\n- Dashboard\n";

    public PocVariantTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        _root = Path.Combine(Path.GetTempPath(), "poc-variant-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();
        _resolver = new WorkspacePathResolver(_configuration);

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "Nghỉ phép" });
        db.AiModels.Add(new AiModel { Id = _agentModelId, ModelId = "agent-model" });
        db.AiModels.Add(new AiModel { Id = _candidateModelId, ModelId = "candidate-model", InputPricePerMillionTokens = 1_000_000m });
        db.Agents.Add(new Agent { Id = _developerId, RoleKey = AgentRoleKey.Developer, AiModelId = _agentModelId });
        db.WorkflowRuns.Add(new WorkflowRun { Id = _runId, ProjectId = _projectId, CurrentStage = WorkflowStageKey.PocPreview });
        db.AgentTasks.Add(new AgentTask
        {
            Id = _sourceTaskId, WorkflowRunId = _runId, ProjectId = _projectId, AgentId = _developerId,
            Type = AgentTaskType.PocPreview, Status = AgentTaskStatus.Completed, Input = Spec,
            CreatedAt = DateTime.UtcNow.AddHours(-2), StartedAt = DateTime.UtcNow.AddHours(-2), FinishedAt = DateTime.UtcNow.AddHours(-2).AddMinutes(5)
        });
        db.SaveChanges();
    }

    // ── Start ───────────────────────────────────────────────────────────────────────────────────────

    // Đầu vào chung là lượt DỰNG MỚI, không phải vòng chỉnh sửa (vòng đó vá trên bản có sẵn), và tên model
    // được chụp lại để bảng còn đọc được sau khi model bị sửa/xoá.
    [Fact]
    public async Task Start_QueuesVariant_FromLatestFreshBuild_NotFromARevisionRound()
    {
        await using (var seed = NewDb())
        {
            seed.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = _runId, ProjectId = _projectId, AgentId = _developerId, Type = AgentTaskType.PocPreview,
                Status = AgentTaskStatus.Completed, Input = Spec, RevisionFeedback = "sửa màu nút",
                CreatedAt = DateTime.UtcNow, FinishedAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var result = await new StartPocVariantUseCase(db, new PocOpencodeSettings()).ExecuteAsync(_projectId, _candidateModelId, "admin");

        Assert.Equal(StartPocVariantResult.Ok, result);
        var variant = await NewDb().PocVariants.SingleAsync();
        Assert.Equal(PocVariantStatus.Queued, variant.Status);
        Assert.Equal(_sourceTaskId, variant.SourceTaskId);
        Assert.Equal("candidate-model", variant.ModelLabel);
        Assert.Equal("admin", variant.RequestedBy);
    }

    // Bấm đúp / bấm liền nhiều model không được thành nhiều lượt chồng nhau — mỗi lượt tiêu tiền thật.
    [Fact]
    public async Task Start_RefusesWhileAnotherVariantIsQueuedOrRunning()
    {
        await using var db = NewDb();
        var sut = new StartPocVariantUseCase(db, new PocOpencodeSettings());
        Assert.Equal(StartPocVariantResult.Ok, await sut.ExecuteAsync(_projectId, _candidateModelId, "admin"));

        Assert.Equal(StartPocVariantResult.AlreadyRunning, await sut.ExecuteAsync(_projectId, _agentModelId, "admin"));
        Assert.Equal(1, await NewDb().PocVariants.CountAsync());
    }

    [Fact]
    public async Task Start_RefusesWhenTheKeptVariantLimitIsReached()
    {
        await using (var seed = NewDb())
        {
            for (var i = 0; i < StartPocVariantUseCase.MaxVariantsPerProject; i++)
                seed.PocVariants.Add(NewVariant(PocVariantStatus.Completed));
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        Assert.Equal(StartPocVariantResult.LimitReached,
            await new StartPocVariantUseCase(db, new PocOpencodeSettings()).ExecuteAsync(_projectId, _candidateModelId, "admin"));
    }

    [Fact]
    public async Task Start_RefusesWithoutAFreshPocBuild_OrWithAnInactiveModel()
    {
        await using (var seed = NewDb())
        {
            seed.AgentTasks.Remove(await seed.AgentTasks.SingleAsync(t => t.Id == _sourceTaskId));
            (await seed.AiModels.SingleAsync(m => m.Id == _agentModelId)).IsActive = false;
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var sut = new StartPocVariantUseCase(db, new PocOpencodeSettings());
        Assert.Equal(StartPocVariantResult.NoSourcePoc, await sut.ExecuteAsync(_projectId, _candidateModelId, "admin"));
        Assert.Equal(StartPocVariantResult.ModelNotFound, await sut.ExecuteAsync(_projectId, _agentModelId, "admin"));
    }

    // ── Delete / Demo ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RefusesARunningVariant_AndRemovesRowAndFolderOtherwise()
    {
        var running = NewVariant(PocVariantStatus.Running);
        var done = NewVariant(PocVariantStatus.Completed);
        await using (var seed = NewDb())
        {
            seed.PocVariants.AddRange(running, done);
            await seed.SaveChangesAsync();
        }
        var folder = _resolver.GetPocVariantPath(_projectId, done.Id);
        WriteFile(Path.Combine(folder, PocTemplate.MockupRelativePath), "<html>variant</html>");

        await using var db = NewDb();
        var sut = new DeletePocVariantUseCase(db, _resolver);

        Assert.Equal(DeletePocVariantResult.Running, await sut.ExecuteAsync(_projectId, running.Id));
        Assert.Equal(DeletePocVariantResult.Ok, await sut.ExecuteAsync(_projectId, done.Id));
        Assert.False(Directory.Exists(folder));
        Assert.Equal(running.Id, (await NewDb().PocVariants.SingleAsync()).Id);
    }

    // Guard project chỉ xác nhận quyền với projectId trên URL — biến thể của dự án KHÁC không được mở qua đó.
    [Fact]
    public async Task DemoFile_IsOnlyServedForAVariantOfTheSameProject()
    {
        var otherProject = Guid.NewGuid();
        var variant = NewVariant(PocVariantStatus.Completed);
        await using (var seed = NewDb())
        {
            seed.Projects.Add(new Project { Id = otherProject, Name = "Khác" });
            seed.PocVariants.Add(variant);
            await seed.SaveChangesAsync();
        }
        WriteFile(Path.Combine(_resolver.GetPocVariantPath(_projectId, variant.Id), PocTemplate.MockupRelativePath), "<html/>");

        await using var db = NewDb();
        var sut = new GetPocVariantFileQuery(db, _resolver);

        Assert.NotNull(await sut.ExecuteAsync(_projectId, variant.Id));
        Assert.Null(await sut.ExecuteAsync(otherProject, variant.Id));
    }

    // Thư mục biến thể nằm NGOÀI workspace dự án: bên trong thì các bước sau của pipeline thấy thêm hàng
    // loạt poc-demo.html lạ, và bản nhân bản/gói rà soát chép theo.
    [Fact]
    public void VariantFolder_LivesUnderRootButOutsideTheProjectWorkspace()
    {
        var variantPath = _resolver.GetPocVariantPath(_projectId, Guid.NewGuid());
        var workspace = _resolver.GetProjectWorkspacePath(WorkspacePathResolver.GetWorkspaceFolder(_projectId, "Nghỉ phép"));

        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, variantPath);
        Assert.False(variantPath.StartsWith(workspace, StringComparison.Ordinal));
    }

    // ── Bảng so sánh ────────────────────────────────────────────────────────────────────────────────

    // Bản chính và biến thể chấm bằng cùng một thước (poc-verification.json của mỗi bên); model của bản
    // chính là model THẬT đã trả lời các lời gọi dựng nó, không phải model đang gán cho Developer.
    [Fact]
    public async Task List_ScoresBaselineAndVariantsWithTheSameYardstick()
    {
        var variant = NewVariant(PocVariantStatus.Completed);
        variant.ModelCalls = 3;
        variant.CostUsd = 1.5m;
        await using (var seed = NewDb())
        {
            seed.PocVariants.Add(variant);
            seed.AgentModelCallLogs.Add(new AgentModelCallLog
            {
                ProjectId = _projectId, AgentId = _developerId, WorkflowRunId = _runId, Purpose = "AgentRun",
                ModelId = "old-model", PromptTokens = 100, CompletionTokens = 50,
                CreatedAt = DateTime.UtcNow.AddHours(-2).AddMinutes(1)
            });
            await seed.SaveChangesAsync();
        }

        var workspace = _resolver.GetProjectWorkspacePath(WorkspacePathResolver.GetWorkspaceFolder(_projectId, "Nghỉ phép"));
        await PocVerification.SaveAsync(workspace, new PocVerificationSummary
        {
            SpecScreens = 4, CoveredScreens = 3, OpenIssues = { "a", "b" },
            SelfTestResults = { "PASS BR-1 — ok", "FAIL BR-2 — sai" }
        });
        await PocVerification.SaveAsync(_resolver.GetPocVariantPath(_projectId, variant.Id), new PocVerificationSummary
        {
            SpecScreens = 4, CoveredScreens = 4,
            SelfTestResults = { "PASS BR-1 — ok", "PASS BR-2 — ok" }
        });

        await using var db = NewDb();
        var page = await new ListPocVariantsQuery(db, _resolver, new PocVariantProgress(), new PocOpencodeSettings()).ExecuteAsync(_projectId);

        Assert.NotNull(page);
        Assert.True(page!.HasSource);
        var baseline = Assert.Single(page.Rows, r => r.IsBaseline);
        Assert.Equal("old-model", baseline.ModelLabel);
        Assert.Equal(1, baseline.ModelCalls);
        Assert.Equal(3, baseline.Metrics!.CoveredScreens);
        Assert.Equal(2, baseline.Metrics.OpenIssues);
        Assert.Equal(1, baseline.Metrics.SelfTestPass);

        var row = Assert.Single(page.Rows, r => !r.IsBaseline);
        Assert.Equal(4, row.Metrics!.CoveredScreens);
        Assert.Equal(2, row.Metrics.SelfTestPass);
        Assert.Equal(1.5m, row.CostUsd);
        Assert.Equal("Builtin", row.Harness);
        Assert.False(page.OpencodeEnabled);

        Assert.Contains(page.Models, m => m.Id == _agentModelId && m.IsCurrent);
    }

    // ── Runner (chạy thật AgentRunService với model giả) ────────────────────────────────────────────

    // Lượt agent dựng POC MỚI chỉ thấy bộ tool POC: còn ReadFile/SearchInFiles/RunCommand thì model đi săn lỗi
    // bằng các lượt lẻ (ca thật: 5/13 lượt, một lần đọc cả file làm prompt +53k cho mọi lượt sau).
    [Fact]
    public async Task Runner_AgentPath_AdvertisesOnlyThePocBuildTools()
    {
        var variant = NewVariant(PocVariantStatus.Queued);
        await using (var seed = NewDb())
        {
            seed.PocVariants.Add(variant);
            await seed.SaveChangesAsync();
        }

        var factory = new ToolRecordingFactory();
        await using var db = NewDb();
        await NewRunner(db, factory, new DeveloperTools()).RunAsync(variant.Id, CancellationToken.None);

        Assert.NotEmpty(factory.AdvertisedTools);
        Assert.All(factory.AdvertisedTools, t => Assert.Contains(t, WorkspaceTools.PocBuildToolNames));
        Assert.Contains("FixPocAndAudit", factory.AdvertisedTools);
        Assert.DoesNotContain("ReadFile", factory.AdvertisedTools);
    }

    // Lượt thử chạy bằng model ĐƯỢC CHỌN, trong thư mục riêng, và không để lại dấu vết nào trên dự án:
    // bản demo chính, kết quả tự kiểm của nó, hội thoại dự án và model của agent đều nguyên vẹn.
    [Fact]
    public async Task Runner_BuildsWithTheChosenModel_InItsOwnFolder_WithoutTouchingTheProject()
    {
        var variant = NewVariant(PocVariantStatus.Queued);
        await using (var seed = NewDb())
        {
            seed.PocVariants.Add(variant);
            await seed.SaveChangesAsync();
        }

        var workspace = _resolver.GetProjectWorkspacePath(WorkspacePathResolver.GetWorkspaceFolder(_projectId, "Nghỉ phép"));
        var mainDemo = Path.Combine(workspace, PocTemplate.MockupRelativePath);
        WriteFile(mainDemo, "<html>BẢN CHÍNH</html>");
        await PocVerification.SaveAsync(workspace, new PocVerificationSummary { CoveredScreens = 7 });

        var factory = new RecordingChatClientFactory("Đã dựng xong bản demo.");
        await using var db = NewDb();
        await NewRunner(db, factory).RunAsync(variant.Id, CancellationToken.None);

        var saved = await NewDb().PocVariants.SingleAsync();
        Assert.Equal(PocVariantStatus.Completed, saved.Status);
        // Model giả trả chữ thường (không theo khung ===POC_…===) ⇒ đi agent nhiều bước, và Output ghi lại vì sao.
        Assert.StartsWith("Đã dựng xong bản demo.", saved.Output);
        Assert.Contains("Cách dựng: agent nhiều bước", saved.Output);
        Assert.Equal(new[] { "candidate-model" }, factory.ModelIds.Distinct());

        // Chi phí chốt từ chính các lời gọi của lượt thử (Purpose = PocVariant).
        Assert.True(saved.ModelCalls >= 1);
        Assert.True(saved.CostUsd > 0);
        Assert.All(await NewDb().AgentModelCallLogs.ToListAsync(), l => Assert.Equal(PocVariantRunner.CallPurpose, l.Purpose));

        // Thư mục riêng được seed từ template thật; bản chính và kết quả tự kiểm của nó nguyên vẹn.
        Assert.True(File.Exists(Path.Combine(_resolver.GetPocVariantPath(_projectId, variant.Id), PocTemplate.MockupRelativePath)));
        Assert.Equal("<html>BẢN CHÍNH</html>", await File.ReadAllTextAsync(mainDemo));
        Assert.Equal(7, PocVerification.TryLoad(workspace)!.CoveredScreens);

        Assert.Empty(await NewDb().AgentConversations.ToListAsync());
        Assert.Equal(_agentModelId, (await NewDb().Agents.SingleAsync()).AiModelId);
    }

    // Đường dựng một lần cũng chạy cho lượt thử (cùng cách bản chính được dựng): MỘT lời gọi, ghi vào thư
    // mục riêng, Purpose = PocVariant để cột chi phí bắt được nó. Test trên kia đi nhánh còn lại — câu trả
    // lời không có dòng đánh dấu ⇒ rơi về agent nhiều bước.
    [Fact]
    public async Task Runner_BuildsInOneCall_WhenTheModelAnswersInTheOneShotFormat()
    {
        var variant = NewVariant(PocVariantStatus.Queued);
        await using (var seed = NewDb())
        {
            seed.PocVariants.Add(variant);
            await seed.SaveChangesAsync();
        }

        var factory = new RecordingChatClientFactory(
            "===POC_META===\n{\"appName\":\"Nghỉ phép\",\"navItems\":[{\"label\":\"Dashboard\"}]}\n" +
            "===POC_CONTENT===\n<section class=\"page-view active\" data-view=\"Dashboard\">x</section>\n" +
            "===POC_HANDOFF===\n- Dựng một lần.\n===POC_END===");
        await using var db = NewDb();
        await NewRunner(db, factory).RunAsync(variant.Id, CancellationToken.None);

        var saved = await NewDb().PocVariants.SingleAsync();
        Assert.Equal(PocVariantStatus.Completed, saved.Status);
        Assert.Contains("Dựng một lần.", saved.Output);
        Assert.Single(factory.ModelIds);
        Assert.All(await NewDb().AgentModelCallLogs.ToListAsync(), l => Assert.Equal(PocVariantRunner.CallPurpose, l.Purpose));
        var demo = await File.ReadAllTextAsync(Path.Combine(_resolver.GetPocVariantPath(_projectId, variant.Id), PocTemplate.MockupRelativePath));
        Assert.Contains("data-view=\"Dashboard\"", demo);
    }

    // Model bị xoá giữa lúc chờ: lượt thử Failed với lý do đọc được, không ném ra worker.
    [Fact]
    public async Task Runner_FailsReadably_WhenTheModelWasDeleted()
    {
        var variant = NewVariant(PocVariantStatus.Queued);
        variant.AiModelId = null;
        await using (var seed = NewDb())
        {
            seed.PocVariants.Add(variant);
            await seed.SaveChangesAsync();
        }

        var factory = new RecordingChatClientFactory("không được gọi");
        await using var db = NewDb();
        await NewRunner(db, factory).RunAsync(variant.Id, CancellationToken.None);

        var saved = await NewDb().PocVariants.SingleAsync();
        Assert.Equal(PocVariantStatus.Failed, saved.Status);
        Assert.Contains("candidate-model", saved.Error);
        Assert.NotNull(saved.FinishedAt);
        Assert.Empty(factory.ModelIds);
    }

    // ── Vỏ opencode ─────────────────────────────────────────────────────────────────────────────────

    // Vỏ opencode cần opencode cài trên máy chủ và cho một tiến trình ngoài app chạy — người vận hành phải bật.
    [Fact]
    public async Task Start_Opencode_RequiresTheSwitch_AndRecordsTheHarness()
    {
        await using var db = NewDb();
        Assert.Equal(StartPocVariantResult.HarnessDisabled,
            await new StartPocVariantUseCase(db, new PocOpencodeSettings()).ExecuteAsync(_projectId, _candidateModelId, "admin", PocVariantHarness.Opencode));
        Assert.Empty(await NewDb().PocVariants.ToListAsync());

        Assert.Equal(StartPocVariantResult.Ok,
            await new StartPocVariantUseCase(db, new PocOpencodeSettings { Enabled = true }).ExecuteAsync(_projectId, _candidateModelId, "admin", PocVariantHarness.Opencode));
        Assert.Equal(PocVariantHarness.Opencode, (await NewDb().PocVariants.SingleAsync()).Harness);
    }

    // opencode gọi model QUA RELAY của app bằng token tạm: lời gọi vào call log với Purpose của lượt thử nên
    // chi phí được chốt như vỏ của app; bốn file nguồn được ghép vào bản demo qua đúng các tool POC; token
    // chết khi lượt chạy xong; và không có lời gọi nào của đường dựng một lượt (không rơi về vỏ của app).
    [Fact]
    public async Task Runner_Opencode_CallsTheModelThroughTheRelay_AndAssemblesTheSourceFiles()
    {
        var variant = NewVariant(PocVariantStatus.Queued);
        variant.Harness = PocVariantHarness.Opencode;
        await using (var seed = NewDb())
        {
            seed.PocVariants.Add(variant);
            await seed.SaveChangesAsync();
        }

        var sessions = new LlmRelaySessions();
        var factory = new RecordingChatClientFactory("viết xong rồi");
        var cli = new RelayCallingOpencode(this, sessions, factory, writeFiles: true);
        await using var db = NewDb();
        await NewRunner(db, factory, opencode: cli, relaySessions: sessions).RunAsync(variant.Id, CancellationToken.None);

        var saved = await NewDb().PocVariants.SingleAsync();
        Assert.True(saved.Status == PocVariantStatus.Completed, saved.Error);
        Assert.Contains("Bàn giao từ opencode.", saved.Output);
        Assert.Contains("Cách dựng: opencode", saved.Output);

        // Mọi lời gọi model là lời gọi relay của opencode (một lần dựng — tự kiểm không chạy trong test nên không có lượt sửa).
        Assert.Equal(1, cli.Runs);
        Assert.Equal(new[] { "candidate-model" }, factory.ModelIds);
        Assert.Equal(1, saved.ModelCalls);
        Assert.True(saved.CostUsd > 0);
        Assert.All(await NewDb().AgentModelCallLogs.ToListAsync(), l => Assert.Equal(PocVariantRunner.CallPurpose, l.Purpose));
        Assert.Null(sessions.Resolve(cli.SeenToken));

        var sandbox = _resolver.GetPocVariantPath(_projectId, variant.Id);
        var demo = await File.ReadAllTextAsync(Path.Combine(sandbox, PocTemplate.MockupRelativePath));
        Assert.Contains("data-view=\"Dashboard\"", demo);
        Assert.Contains("function tinhNgayNghi", PocTemplate.GetScriptBody(demo));
        Assert.Contains("Leave Desk", demo);

        // Token chỉ đi qua biến môi trường, không bao giờ nằm trong cấu hình trên đĩa; prompt bước POC nằm ở instructions.
        var config = await File.ReadAllTextAsync(Path.Combine(sandbox, PocOpencodeBuilder.ConfigFile));
        Assert.DoesNotContain(cli.SeenToken!, config);
        Assert.Contains("{env:" + PocOpencodeBuilder.TokenVariable + "}", config);
        Assert.Contains("http://127.0.0.1:5999/opencode-relay/v1", config);
        var task = await File.ReadAllTextAsync(Path.Combine(sandbox, PocOpencodeBuilder.TaskFile));
        Assert.Contains("Dựng POC demo", task);
        Assert.Contains(Spec.Trim(), task);
    }

    // opencode chạy xong mà không giao file: chạy lại MỘT lần (như lượt sinh lại của đường một lượt), vẫn không
    // có thì lượt thử Failed với lý do đọc được — tuyệt đối không âm thầm dựng bằng vỏ của app.
    [Fact]
    public async Task Runner_Opencode_FailsReadably_WhenNoSourceFilesAreDelivered()
    {
        var variant = NewVariant(PocVariantStatus.Queued);
        variant.Harness = PocVariantHarness.Opencode;
        await using (var seed = NewDb())
        {
            seed.PocVariants.Add(variant);
            await seed.SaveChangesAsync();
        }

        var sessions = new LlmRelaySessions();
        var factory = new RecordingChatClientFactory("không làm gì");
        var cli = new RelayCallingOpencode(this, sessions, factory, writeFiles: false);
        await using var db = NewDb();
        await NewRunner(db, factory, opencode: cli, relaySessions: sessions).RunAsync(variant.Id, CancellationToken.None);

        var saved = await NewDb().PocVariants.SingleAsync();
        Assert.Equal(PocVariantStatus.Failed, saved.Status);
        Assert.Contains("meta.json", saved.Error);
        Assert.Equal(PocOpencodeBuilder.MaxBuildAttempts, cli.Runs);
        Assert.Contains("CHƯA GIAO ĐỦ", await File.ReadAllTextAsync(Path.Combine(_resolver.GetPocVariantPath(_projectId, variant.Id), PocOpencodeBuilder.RunFile)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private PocVariant NewVariant(PocVariantStatus status) => new()
    {
        ProjectId = _projectId,
        AiModelId = _candidateModelId,
        ModelLabel = "candidate-model",
        SourceTaskId = _sourceTaskId,
        Status = status,
        StartedAt = status == PocVariantStatus.Queued ? null : DateTime.UtcNow.AddMinutes(-1)
    };

    private PocVariantRunner NewRunner(AppDbContext db, IChatClientFactory factory, IToolRegistry? tools = null,
        IPocOpencodeCli? opencode = null, LlmRelaySessions? relaySessions = null)
    {
        var env = new FakeWebHostEnvironment { ContentRootPath = RepoRoot() };
        var prompts = new PromptTemplateService(env);
        var workspaceTools = new WorkspaceTools(_configuration, _resolver, null!, null!);
        var callLogger = new ModelCallLogger(db, new ModelCallImageStore(_resolver, _configuration, NullLogger<ModelCallImageStore>.Instance));
        var agentRun = new AgentRunService(
            db, tools ?? new NoTools(), new ToolPolicyService(), null!, new AgentPromptBuilder(prompts, new AgentInstructionProvider(prompts)),
            workspaceTools, callLogger,
            factory, NullLoggerFactory.Instance, new NoBudgetLimit(), new LlmSettings(),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)));
        var oneShotBuilder = new PocOneShotBuilder(
            new LlmClient(factory, callLogger, new NoBudgetLimit(), new LlmSettings(), NullLogger<LlmClient>.Instance),
            prompts, workspaceTools, NullLogger<PocOneShotBuilder>.Instance, maxRepairRounds: 0);
        var opencodeSettings = new PocOpencodeSettings { Enabled = true, RelayBaseUrl = "http://127.0.0.1:5999" };
        var opencodeBuilder = new PocOpencodeBuilder(workspaceTools, prompts, relaySessions ?? new LlmRelaySessions(),
            opencode ?? new UnusedOpencode(), new PocOpencodeRelayAddress(opencodeSettings), opencodeSettings,
            NullLogger<PocOpencodeBuilder>.Instance);

        return new PocVariantRunner(db, agentRun, workspaceTools, _resolver, new WorkflowTaskPromptBuilder(prompts),
            new UatScenarioService(db, null!, prompts, _resolver, null!, NullLogger<UatScenarioService>.Instance),
            new PocUiConventionService(db, null!, prompts, _resolver, null!, NullLogger<PocUiConventionService>.Instance),
            new PocVariantProgress(), env, oneShotBuilder, opencodeBuilder);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy repo root từ " + AppContext.BaseDirectory);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Agent được gán cả tool đọc/tìm/chạy lệnh lẫn tool POC — như Developer thật.
    private sealed class DeveloperTools : IToolRegistry
    {
        public Task<IReadOnlyList<ToolRuntimeDescriptor>> GetToolsForAgentAsync(Guid agentId)
        {
            var instance = new WorkspaceTools(new ConfigurationBuilder().Build(), null!, null!, null!);
            var names = new[] { "ReadFile", "SearchInFiles", "ReplaceInFile", "SetPocContent", "FixPocAndAudit", "AuditPocContent" };
            return Task.FromResult<IReadOnlyList<ToolRuntimeDescriptor>>(names
                .Select(n => new ToolRuntimeDescriptor(new ToolDefinition { Name = n }, instance, typeof(WorkspaceTools).GetMethod(n)!))
                .ToList());
        }
    }

    private sealed class ToolRecordingFactory : IChatClientFactory
    {
        public List<string> AdvertisedTools { get; } = new();

        public IChatClient Create(AiModel model) => new Client(this);

        private sealed class Client(ToolRecordingFactory owner) : IChatClient
        {
            private void Record(ChatOptions? options)
            {
                if (options?.Tools is { } tools)
                    lock (owner.AdvertisedTools) owner.AdvertisedTools.AddRange(tools.Select(t => t.Name));
            }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                Record(options);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Đã dựng xong.")));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Record(options);
                await Task.CompletedTask;
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Đã dựng xong.");
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }

    private sealed class UnusedOpencode : IPocOpencodeCli
    {
        public Task<PocOpencodeRunResult> RunAsync(PocOpencodeRun run, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Vỏ opencode không được dùng trong test này.");
    }

    // opencode giả: gọi model qua CHÍNH relay của app bằng token nhận từ biến môi trường (như provider
    // OpenAI-compatible của opencode thật), rồi — nếu được bảo — giao bốn file nguồn.
    private sealed class RelayCallingOpencode(PocVariantTests owner, LlmRelaySessions sessions, IChatClientFactory factory, bool writeFiles) : IPocOpencodeCli
    {
        public int Runs { get; private set; }
        public string? SeenToken { get; private set; }

        public async Task<PocOpencodeRunResult> RunAsync(PocOpencodeRun run, CancellationToken cancellationToken)
        {
            Runs++;
            SeenToken = run.Environment[PocOpencodeBuilder.TokenVariable];
            Assert.Contains(run.Arguments, a => a.Contains(PocOpencodeBuilder.RunFile));

            if (Runs == 1)
            {
                await using var db = owner.NewDb();
                var callLogger = new ModelCallLogger(db, new ModelCallImageStore(owner._resolver, owner._configuration, NullLogger<ModelCallImageStore>.Instance));
                var relay = new RelayModelCallUseCase(db, sessions, factory, callLogger, new NoBudgetLimit(), new LlmSettings());
                using var body = JsonDocument.Parse("""{"model":"poc","stream":true,"messages":[{"role":"user","content":"dựng POC"}]}""");
                var call = await relay.ExecuteAsync(SeenToken, body.RootElement, cancellationToken);
                Assert.Equal(RelayModelCallStatus.Ok, call.Status);
                await foreach (var _ in call.StreamChunksAsync(cancellationToken)) { }
            }

            if (writeFiles)
            {
                var src = Path.Combine(run.WorkingDirectory, PocOpencodeBuilder.SourceFolder);
                WriteFile(Path.Combine(src, PocOpencodeBuilder.MetaFile), """{"appName":"Leave Desk","breadcrumb":"Home > Dashboard","navItems":[{"label":"Dashboard"}]}""");
                WriteFile(Path.Combine(src, PocOpencodeBuilder.ContentFile), "<section class=\"page-view active\" data-view=\"Dashboard\">x</section>");
                WriteFile(Path.Combine(src, PocOpencodeBuilder.ScriptFile), "function tinhNgayNghi(a, b) { return b - a + 1; }");
                WriteFile(Path.Combine(src, PocOpencodeBuilder.HandoffFile), "- Bàn giao từ opencode.");
            }

            return new PocOpencodeRunResult(0, false, string.Empty);
        }
    }

    private sealed class NoTools : IToolRegistry
    {
        public Task<IReadOnlyList<ToolRuntimeDescriptor>> GetToolsForAgentAsync(Guid agentId) =>
            Task.FromResult<IReadOnlyList<ToolRuntimeDescriptor>>(Array.Empty<ToolRuntimeDescriptor>());
    }

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Ghi lại model của MỌI client được dựng — cách để khẳng định lượt thử dùng model được chọn chứ không
    // phải model của agent. Trả lời bằng văn xuôi (không gọi tool) nên agent kết thúc ngay lượt đầu.
    private sealed class RecordingChatClientFactory : IChatClientFactory
    {
        private readonly string _reply;
        public List<string> ModelIds { get; } = new();

        public RecordingChatClientFactory(string reply) => _reply = reply;

        public IChatClient Create(AiModel model)
        {
            ModelIds.Add(model.ModelId);
            return new FixedReplyClient(_reply);
        }
    }

    private sealed class FixedReplyClient : IChatClient
    {
        private readonly string _reply;
        public FixedReplyClient(string reply) => _reply = reply;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
