using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Ghi chú trên Product Brief → gom thành MỘT lượt user trong hội thoại + chạy lại workflow soạn draft,
// và LƯU thành dòng lịch sử (PocComment, Target=Brief). Đi qua transcript (không sửa thẳng file) để Brief
// luôn sinh từ nguồn sự thật là hội thoại; lưu dòng riêng vì transcript không phân biệt được lượt nào là
// ghi chú review, và sau khi Brief lên version mới thì không còn cách nào truy lại bản cũ bị chê gì.
public class ReviseBriefFromNotesUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ReviseBriefFromNotesUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_AppendsUserTurn_AndTriggersDraft()
    {
        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();
        var sut = NewSut(db, orchestrator);

        var result = await sut.ExecuteAsync(_projectId, new List<BriefNote>
        {
            new() { Quote = "đơn nghỉ phép", Note = "đổi thành đơn xin nghỉ" },
            new() { Quote = "", Note = "thêm mục báo cáo" }
        });

        Assert.Equal(ReviseBriefResult.Ok, result);
        Assert.Equal(_projectId, orchestrator.StartedProjectId);
        // KHÔNG gộp vào run đang bay: lượt này vừa ghi thêm một lượt user (các ghi chú), mà run đang chạy
        // đã đọc transcript từ trước — gộp là nuốt mất đúng phản hồi người dùng vừa gửi.
        Assert.False(orchestrator.StartedWithCoalesce);

        await using var verify = NewDb();
        var turn = await verify.AgentConversations.SingleAsync(c => c.ProjectId == _projectId);
        Assert.Equal("user", turn.Role);
        Assert.Contains("đổi thành đơn xin nghỉ", turn.Message);
        Assert.Contains("thêm mục báo cáo", turn.Message);
        Assert.Contains("đơn nghỉ phép", turn.Message);
    }

    [Fact]
    public async Task ExecuteAsync_SavesEachNote_AsDraftStampedHistoryRow()
    {
        await using var db = NewDb();

        await NewSut(db, new FakeOrchestrator()).ExecuteAsync(_projectId, new List<BriefNote>
        {
            new() { Quote = "đơn nghỉ phép", Note = "đổi thành đơn xin nghỉ" },
            new() { Quote = "", Note = "thêm mục báo cáo" }
        }, createdByUsername: "user");

        await using var verify = NewDb();
        var notes = await verify.PocComments.OrderBy(c => c.Comment).ToListAsync();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n =>
        {
            Assert.Equal(PocCommentTarget.Brief, n.Target);
            // "draft" chứ không phải V{n}: bản đang xem chưa được duyệt. ApproveRequirementUseCase nâng
            // dấu này lên V{n} cùng lúc với file draft.
            Assert.Equal("draft", n.BriefVersion);
            Assert.Equal(PocCommentRoute.Requirement, n.Route);
            Assert.Equal("user", n.CreatedByUsername);
        });
        // Ghi chú chung (không bôi đen đoạn nào) vẫn là một dòng — Quote rỗng, không mất.
        Assert.Equal("", notes.Single(n => n.Comment == "thêm mục báo cáo").Quote);
        Assert.Equal("đơn nghỉ phép", notes.Single(n => n.Comment == "đổi thành đơn xin nghỉ").Quote);
    }

    [Fact]
    public async Task ExecuteAsync_NoNotes_ReturnsNoNotes_AndDoesNotTrigger()
    {
        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();

        var result = await NewSut(db, orchestrator).ExecuteAsync(_projectId, new List<BriefNote>
        {
            new() { Quote = "x", Note = "   " } // ghi chú rỗng bị loại
        });

        Assert.Equal(ReviseBriefResult.NoNotes, result);
        Assert.Null(orchestrator.StartedProjectId);
        Assert.Equal(0, await NewDb().AgentConversations.CountAsync());
    }

    private static ReviseBriefFromNotesUseCase NewSut(AppDbContext db, IWorkflowOrchestrator orchestrator) =>
        new(db, new BAConversationLog(db), new BAAgentResolver(db), new GenerateRequirementDraftUseCase(orchestrator));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeOrchestrator : IWorkflowOrchestrator
    {
        public Guid? StartedProjectId;
        public bool StartedWithCoalesce;
        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false)
        {
            StartedProjectId = projectId;
            StartedWithCoalesce = coalesceWithActiveRun;
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string v, string s) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string v) => Task.FromResult(Guid.NewGuid());
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
