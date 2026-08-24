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

// Ghi chú trên Product Brief → MỘT lượt user trong hội thoại (để ghi chú không nằm ngoài transcript) +
// một run "Write Requirement" MANG THEO chính các ghi chú đó. Payload là thứ khiến worker rẽ sang vòng
// SỬA CÓ PHẠM VI thay vì soạn lại cả tài liệu — mất nó là quay về đúng ca người dùng ghi chú một dòng và
// nhận về một bản Brief đổi hàng chục dòng.
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

        // Ghi chú phải đi theo run dưới dạng DỮ LIỆU CÓ CẤU TRÚC, không chỉ nằm trong câu chữ của lượt
        // chat: worker cần biết đúng đoạn nào được chú để sửa mỗi chỗ đó.
        var payload = BriefNotePayload.TryParse(orchestrator.StartedWithNotesPayload);
        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Count);
        Assert.Equal("đơn nghỉ phép", payload[0].Quote);
        Assert.Equal("đổi thành đơn xin nghỉ", payload[0].Note);
        Assert.Equal("thêm mục báo cáo", payload[1].Note);
    }

    // Ghi chú rỗng bị loại từ use case; payload chỉ mang những ghi chú thật, và một payload rỗng phải
    // được đọc lại thành null (worker rơi về đường soạn bình thường thay vì chạy vòng sửa không có gì để sửa).
    [Fact]
    public async Task ExecuteAsync_PayloadCarriesOnlyRealNotes()
    {
        var orchestrator = new FakeOrchestrator();
        await using var db = NewDb();

        await NewSut(db, orchestrator).ExecuteAsync(_projectId, new List<BriefNote>
        {
            new() { Quote = "a", Note = "sửa chỗ này" },
            new() { Quote = "b", Note = "  " }
        });

        var payload = BriefNotePayload.TryParse(orchestrator.StartedWithNotesPayload);
        Assert.Single(payload!);
        Assert.Equal("sửa chỗ này", payload![0].Note);

        Assert.Null(BriefNotePayload.TryParse("[]"));
        Assert.Null(BriefNotePayload.TryParse(""));
        Assert.Null(BriefNotePayload.TryParse("không phải json"));
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
        new(new BAConversationLog(db), new BAAgentResolver(db), new GenerateRequirementDraftUseCase(orchestrator));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeOrchestrator : IWorkflowOrchestrator
    {
        public Guid? StartedProjectId;
        public bool StartedWithCoalesce;
        public string? StartedWithNotesPayload;
        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false, string? briefNotesPayload = null)
        {
            StartedProjectId = projectId;
            StartedWithCoalesce = coalesceWithActiveRun;
            StartedWithNotesPayload = briefNotesPayload;
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
