using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// MỘT lượt gửi ghi chú POC, hai đường, mỗi đường nhận ĐÚNG tập con của nó.
//
// Trước đây trang POC Review có hai nút mà CẢ HAI đều nuốt trọn mọi ghi chú Open. Buổi review lẫn hai
// loại (lỗi trình bày + hiểu sai yêu cầu) vì thế không có nút nào đúng, và đường "gửi về Requirement"
// còn đánh dấu RoutedToRequirement cả những ghi chú thẩm mỹ mà BA đã loại khỏi tin nhắn — mất trắng vì
// ReopenPocCommentUseCase chỉ mở lại được Addressed/Sent. Các test dưới đây khoá đúng những hành vi ấy.
public class DispatchPocFeedbackUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _runId = Guid.NewGuid();

    public DispatchPocFeedbackUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();

        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        var agentId = Guid.NewGuid();
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = agentId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });

        // Cổng POC đang mở: run chờ duyệt ĐÚNG ở bước PocPreview + task POC đã xong để vòng chỉnh sửa
        // có Input gốc mà kế thừa.
        db.WorkflowRuns.Add(new WorkflowRun
        {
            Id = _runId,
            ProjectId = _projectId,
            Status = WorkflowRunStatus.WaitingForHuman,
            CurrentStage = WorkflowStageKey.PocPreview
        });
        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = _runId,
            ProjectId = _projectId,
            AgentId = agentId,
            Type = AgentTaskType.PocPreview,
            Status = AgentTaskStatus.Completed,
            Title = "Dựng POC",
            Input = "spec gốc",
            Output = "poc v1",
            FinishedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task OnlyFixGroup_SendsExactlySelectedCommentsToDev_AndLeavesTheRestOpen()
    {
        var chosen = AddComment("nhãn nút sai");
        var untouched = AddComment("ghi chú của người khác, chưa gửi");

        await using var db = NewDb();
        var report = await NewSut(db).ExecuteAsync(_projectId, new[] { chosen }, Array.Empty<Guid>());

        Assert.Equal(PocFeedbackDispatchStatus.Ok, report.Status);
        Assert.Equal(1, report.FixSentCount);

        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.Sent, verify.PocComments.Single(c => c.Id == chosen).Status);
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == untouched).Status);

        // Nhận xét gửi Developer chỉ chứa ghi chú đã chọn.
        var revision = await verify.AgentTasks.SingleAsync(t => t.RevisionFeedback != null);
        Assert.Contains("nhãn nút sai", revision.RevisionFeedback!);
        Assert.DoesNotContain("người khác", revision.RevisionFeedback!);
    }

    [Fact]
    public async Task OnlyRequirementGroup_RoutesOnlySelected_AndDoesNotBurnOtherOpenComments()
    {
        // ĐÂY là lỗi cũ: đường Requirement đánh dấu MỌI ghi chú Open là RoutedToRequirement, kể cả ghi
        // chú thẩm mỹ mà BA đã bỏ qua — và trạng thái đó không mở lại được, tức là mất trắng.
        var misunderstanding = AddComment("thiếu bước trưởng phòng duyệt");
        var cosmetic = AddComment("nút nên đổi thành màu xanh");

        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();
        var report = await NewSut(db, orchestrator).ExecuteAsync(_projectId, Array.Empty<Guid>(), new[] { misunderstanding });

        Assert.Equal(PocFeedbackDispatchStatus.Ok, report.Status);
        Assert.Equal(1, report.RoutedCount);
        Assert.Equal(_projectId, orchestrator.StartedProjectId);

        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.RoutedToRequirement, verify.PocComments.Single(c => c.Id == misunderstanding).Status);
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == cosmetic).Status);
    }

    [Fact]
    public async Task BothGroups_RunsRequirementPath_AndDefersDemoFixCommentsToTheNextBuild()
    {
        // POC sẽ được dựng lại từ tài liệu đã sửa, nên vá HTML lúc này vừa phí một lượt trong trần
        // MaxRevisionRounds vừa cho ra bản vá bị bỏ đi ngay. Hoãn (Deferred) ⇒ vòng dựng mới tự mang
        // chúng, và chúng không quay lại hộp phân loại như ghi chú chưa ai quyết.
        var misunderstanding = AddComment("sai công thức tính ngày phép tồn");
        var cosmetic = AddComment("bảng bị canh lệch");

        await using var db = NewDb();
        var report = await NewSut(db).ExecuteAsync(_projectId, new[] { cosmetic }, new[] { misunderstanding });

        Assert.Equal(PocFeedbackDispatchStatus.Ok, report.Status);
        Assert.Equal(1, report.RoutedCount);
        Assert.Equal(1, report.HeldCount);
        Assert.Equal(0, report.FixSentCount);

        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.RoutedToRequirement, verify.PocComments.Single(c => c.Id == misunderstanding).Status);
        var deferred = verify.PocComments.Single(c => c.Id == cosmetic);
        Assert.Equal(PocCommentStatus.Deferred, deferred.Status);
        Assert.Equal(PocCommentRoute.FixPoc, deferred.Route);
        // Không tốn vòng chỉnh sửa nào.
        Assert.False(await verify.AgentTasks.AnyAsync(t => t.RevisionFeedback != null));
    }

    [Fact]
    public async Task BothGroups_WhenRequirementPathFails_DoesNotDeferAnything()
    {
        // Hoãn chỉ có nghĩa khi bản demo THẬT SỰ sắp dựng lại. Đường tài liệu hỏng ⇒ không có vòng dựng
        // mới nào, nên ghi chú chỉnh demo phải còn Open để người dùng gửi lại được.
        var misunderstanding = AddComment("thiếu bước duyệt");
        var cosmetic = AddComment("chữ quá nhỏ");

        await using var db = NewDb();
        var report = await NewSut(db, compose: null).ExecuteAsync(_projectId, new[] { cosmetic }, new[] { misunderstanding });

        Assert.Equal(PocFeedbackDispatchStatus.RequirementFailed, report.Status);

        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == cosmetic).Status);
        Assert.Null(verify.PocComments.Single(c => c.Id == cosmetic).Route);
    }

    [Fact]
    public async Task StaleSelection_IsRejectedWholesale_WithoutTouchingAnything()
    {
        // Ghi chú đã rời Open trong lúc hộp xác nhận còn mở (người khác vừa gửi) ⇒ bảng phân loại đang
        // cầm đã cũ. Từ chối cả lượt thay vì gửi một phần theo bản không còn đúng.
        var open = AddComment("còn mở");
        var alreadySent = AddComment("đã gửi rồi", PocCommentStatus.Sent);

        await using var db = NewDb();
        var report = await NewSut(db).ExecuteAsync(_projectId, new[] { open, alreadySent }, Array.Empty<Guid>());

        Assert.Equal(PocFeedbackDispatchStatus.InvalidSelection, report.Status);

        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == open).Status);
        Assert.False(await verify.AgentTasks.AnyAsync(t => t.RevisionFeedback != null));
    }

    [Fact]
    public async Task ComposeFailure_LeavesEveryCommentUntouched()
    {
        // Soạn hụt tin nhắn gửi BA ⇒ dừng hẳn. Nếu ở đây đã "tiêu" ghi chú thì người dùng bấm lại là
        // mất trắng phản hồi của họ.
        var id = AddComment("thiếu màn hình báo cáo");

        await using var db = NewDb();
        var sut = NewSut(db, compose: null);
        var report = await sut.ExecuteAsync(_projectId, Array.Empty<Guid>(), new[] { id });

        Assert.Equal(PocFeedbackDispatchStatus.RequirementFailed, report.Status);
        Assert.Equal(RoutePocFeedbackResult.ComposeFailed, report.RequirementError);

        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == id).Status);
        Assert.Equal(0, await verify.AgentConversations.CountAsync());
    }

    [Fact]
    public async Task SameCommentInBothGroups_IsRejected()
    {
        var id = AddComment("mơ hồ");

        await using var db = NewDb();
        var report = await NewSut(db).ExecuteAsync(_projectId, new[] { id }, new[] { id });

        Assert.Equal(PocFeedbackDispatchStatus.InvalidSelection, report.Status);
    }

    private Guid AddComment(string text, PocCommentStatus status = PocCommentStatus.Open)
    {
        using var db = NewDb();
        var comment = new PocComment
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            PageView = "Danh sách đơn",
            ElementLabel = "Nút Lưu",
            ElementPath = "#save",
            Comment = text,
            Status = status,
            CreatedAt = DateTime.UtcNow.AddSeconds(db.PocComments.Count())
        };
        db.PocComments.Add(comment);
        db.SaveChanges();
        return comment.Id;
    }

    // ── Nhánh thứ ba: góp ý THIẾT KẾ màn hình ──────────────────────────────────────────────────────

    private void SeedBlueprint()
    {
        using var db = NewDb();
        var project = db.Projects.Single(p => p.Id == _projectId);
        project.UxBlueprint = UxBlueprintBuilder.Serialize(new UxBlueprint
        {
            Version = 1,
            Tasks = { new UxTask { Id = "T-01", Name = "Thu hồi tủ", Object = "Locker", Kind = UxTaskKind.Transition, Placed = true } },
            Screens = { new UxScreen { Id = "S-01", Name = "Locker Recovery", Pattern = UxPattern.Form, Actions = { new UxAction { Task = "T-01", Label = "Thu hồi" } } } }
        });
        db.SaveChanges();
    }

    // Góp ý "gộp màn này vào danh sách tủ" đi vào bản thiết kế (ghi thành DesignNotes, thiết kế lại ở chế độ
    // feedback) chứ không vào hội thoại, rồi Brief soạn lại ĐÚNG MỘT lần.
    [Fact]
    public async Task DesignGroup_RedesignsWithTheNote_ThenRedraftsTheBriefOnce()
    {
        SeedBlueprint();
        var note = AddComment("gộp màn Locker Recovery vào danh sách tủ");
        var orchestrator = new FakeOrchestrator();
        var llm = new DesignLlm();

        await using var db = NewDb();
        var report = await NewDesignSut(db, orchestrator, llm).ExecuteAsync(_projectId, Array.Empty<Guid>(), Array.Empty<Guid>(), new[] { note });

        Assert.Equal(PocFeedbackDispatchStatus.Ok, report.Status);
        Assert.Equal(1, report.RoutedCount);
        Assert.Equal(1, orchestrator.Starts);
        Assert.Contains("Chế độ: FEEDBACK", llm.DesignInput);
        Assert.Contains("gộp màn Locker Recovery vào danh sách tủ", llm.DesignInput);

        await using var verify = NewDb();
        var bp = UxBlueprintBuilder.Parse(verify.Projects.Single(p => p.Id == _projectId).UxBlueprint);
        Assert.Equal(2, bp.Version);
        Assert.Equal(new[] { "Locker List" }, bp.Screens.Select(s => s.Name));
        Assert.Contains(bp.DesignNotes, n => n.Text.Contains("gộp màn", StringComparison.Ordinal));
        var comment = verify.PocComments.Single(c => c.Id == note);
        Assert.Equal((PocCommentStatus.RoutedToRequirement, PocCommentRoute.Design), (comment.Status, comment.Route));
    }

    // Lượt thiết kế lại hỏng ⇒ không ghi chú nào đổi trạng thái — bấm gửi lại được.
    [Fact]
    public async Task DesignGroup_LeavesEveryCommentOpen_WhenTheRedesignFails()
    {
        SeedBlueprint();
        var note = AddComment("gộp màn");
        var orchestrator = new FakeOrchestrator();

        await using var db = NewDb();
        var report = await NewDesignSut(db, orchestrator, new DesignLlm { Fails = true })
            .ExecuteAsync(_projectId, Array.Empty<Guid>(), Array.Empty<Guid>(), new[] { note });

        Assert.Equal(PocFeedbackDispatchStatus.RequirementFailed, report.Status);
        Assert.Equal(RoutePocFeedbackResult.DesignFailed, report.RequirementError);
        Assert.Null(orchestrator.StartedProjectId);
        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == note).Status);
    }

    // Cùng lượt có cả góp ý thiết kế lẫn yêu cầu hiểu sai ⇒ thiết kế chạy trước, Brief soạn lại một lần (do
    // đường Requirement), ghi chú chỉnh demo bị hoãn như cũ.
    [Fact]
    public async Task DesignAndRequirementGroups_ShareOneBriefRedraft()
    {
        SeedBlueprint();
        var design = AddComment("nút thu hồi nên ở danh sách tủ");
        var requirement = AddComment("thiếu bước FCM kiểm tra tủ");
        var cosmetic = AddComment("chữ hơi nhỏ");
        var orchestrator = new FakeOrchestrator();

        await using var db = NewDb();
        var report = await NewDesignSut(db, orchestrator, new DesignLlm())
            .ExecuteAsync(_projectId, new[] { cosmetic }, new[] { requirement }, new[] { design });

        Assert.Equal(PocFeedbackDispatchStatus.Ok, report.Status);
        Assert.Equal(2, report.RoutedCount);
        Assert.Equal(1, report.HeldCount);
        Assert.Equal(1, orchestrator.Starts);
        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.Deferred, verify.PocComments.Single(c => c.Id == cosmetic).Status);
    }

    // Thiết kế đã áp rồi đường tài liệu mới hỏng (soạn tin nhắn lỗi) ⇒ Brief VẪN được soạn lại cho bản thiết kế
    // mới — nếu không, ghi chú thiết kế kẹt Waiting mà không vòng dựng nào đóng chúng. Ghi chú tài liệu còn Open.
    [Fact]
    public async Task DesignAndRequirementGroups_StillRedraftForTheDesign_WhenTheRequirementPathFails()
    {
        SeedBlueprint();
        var design = AddComment("nút thu hồi nên ở danh sách tủ");
        var requirement = AddComment("thiếu bước FCM kiểm tra tủ");
        var orchestrator = new FakeOrchestrator();

        await using var db = NewDb();
        var report = await NewDesignSut(db, orchestrator, new DesignLlm { ComposeFails = true })
            .ExecuteAsync(_projectId, Array.Empty<Guid>(), new[] { requirement }, new[] { design });

        Assert.Equal(PocFeedbackDispatchStatus.RequirementFailed, report.Status);
        Assert.Equal(1, orchestrator.Starts);
        await using var verify = NewDb();
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == requirement).Status);
        Assert.Equal(PocCommentRoute.Design, verify.PocComments.Single(c => c.Id == design).Route);
    }

    private DispatchPocFeedbackUseCase NewDesignSut(AppDbContext db, FakeOrchestrator orchestrator, DesignLlm llm)
    {
        var draft = new GenerateRequirementDraftUseCase(orchestrator);
        var route = new RoutePocFeedbackToRequirementUseCase(
            db, llm, new StubPrompts(), new BAAgentResolver(db), new BAConversationLog(db), draft);
        var design = new RoutePocFeedbackToDesignUseCase(
            db, new BAAgentResolver(db), new UxDesignService(db, llm, new StubPrompts()), draft);
        return new DispatchPocFeedbackUseCase(db, route, new RequestStageRevisionUseCase(db),
            new ICOGenerator.Application.Projects.PocAcceptanceGate(db), design, draft);
    }

    // Đủ cho cả ba lời gọi của đường thiết kế (danh mục rỗng ⇒ giữ tác vụ cũ; bản thiết kế mới gộp màn) và lời
    // gọi soạn tin nhắn của đường Requirement.
    private sealed class DesignLlm : ILlmClient
    {
        public bool Fails;
        public bool ComposeFails;
        public string DesignInput = "";

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            var ok = new LlmCallResult { IsSuccess = true, Content = "{}" };
            object? value = logContext.Purpose switch
            {
                "BAUxInventory" => new UxInventoryDraft(),
                "UiUxBlueprint" when !Fails => Capture(messages, new UxBlueprintDraft
                {
                    Screens =
                    {
                        new UxScreenDraft
                        {
                            Name = "Locker List", Pattern = "list", Object = "Locker",
                            Actions = { new UxAction { Task = "T-01", Label = "Thu hồi tủ", Placement = "row" } }
                        }
                    }
                }),
                "BAPocFeedbackCompose" when !ComposeFails => new PocFeedbackComposeResult { Message = "phản hồi đã soạn" },
                _ => null
            };
            return Task.FromResult(value == null
                ? (new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" }, (T?)null)
                : (ok, (T?)value));
        }

        private UxBlueprintDraft Capture(List<ChatMessage> messages, UxBlueprintDraft draft)
        {
            DesignInput = messages.Last(m => m.Role == ChatRole.User).Text ?? string.Empty;
            return draft;
        }
    }

    private DispatchPocFeedbackUseCase NewSut(
        AppDbContext db, FakeOrchestrator? orchestrator = null, string? compose = "phản hồi đã soạn")
    {
        var route = new RoutePocFeedbackToRequirementUseCase(
            db,
            new FakeLlm(compose),
            new StubPrompts(),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new GenerateRequirementDraftUseCase(orchestrator ?? new FakeOrchestrator()));

        return new DispatchPocFeedbackUseCase(db, route, new RequestStageRevisionUseCase(db), new ICOGenerator.Application.Projects.PocAcceptanceGate(db));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // compose == null ⇒ giả lập lời gọi model hỏng.
    private sealed class FakeLlm : ILlmClient
    {
        private readonly string? _compose;

        public FakeLlm(string? compose) => _compose = compose;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (_compose == null)
                return Task.FromResult((new LlmCallResult { IsSuccess = false }, (T?)null));

            var value = new PocFeedbackComposeResult { Message = _compose } as T;
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class FakeOrchestrator : IWorkflowOrchestrator
    {
        public Guid? StartedProjectId;
        public int Starts;

        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false)
        {
            StartedProjectId = projectId;
            Starts++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string v, string s) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string v) => Task.FromResult(Guid.NewGuid());
    }
}
