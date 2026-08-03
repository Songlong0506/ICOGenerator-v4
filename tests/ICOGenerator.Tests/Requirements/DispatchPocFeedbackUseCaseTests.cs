using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
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
    public async Task BothGroups_RunsRequirementPath_AndHoldsDemoFixCommentsOpenForTheNextRound()
    {
        // POC sẽ được dựng lại từ tài liệu đã sửa, nên vá HTML lúc này vừa phí một lượt trong trần
        // MaxRevisionRounds vừa cho ra bản vá bị bỏ đi ngay. Giữ Open ⇒ không mất gì, chỉ hoãn lại.
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
        Assert.Equal(PocCommentStatus.Open, verify.PocComments.Single(c => c.Id == cosmetic).Status);
        // Không tốn vòng chỉnh sửa nào.
        Assert.False(await verify.AgentTasks.AnyAsync(t => t.RevisionFeedback != null));
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

        return new DispatchPocFeedbackUseCase(db, route, new RequestStageRevisionUseCase(db));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // compose == null ⇒ giả lập lời gọi model hỏng.
    private sealed class FakeLlm : ILlmClient
    {
        private readonly string? _compose;

        public FakeLlm(string? compose) => _compose = compose;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
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

        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false)
        {
            StartedProjectId = projectId;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string v, string s) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string v) => Task.FromResult(Guid.NewGuid());
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
