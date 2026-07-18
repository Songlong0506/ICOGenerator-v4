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

// Ghi chú trên Product Brief → gom thành MỘT lượt user trong hội thoại + chạy lại workflow soạn draft.
// Đi qua transcript (không sửa thẳng file) để Brief luôn sinh từ nguồn sự thật là hội thoại.
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
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), Name = "BA", RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
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

        await using var verify = NewDb();
        var turn = await verify.AgentConversations.SingleAsync(c => c.ProjectId == _projectId);
        Assert.Equal("user", turn.Role);
        Assert.Contains("đổi thành đơn xin nghỉ", turn.Message);
        Assert.Contains("thêm mục báo cáo", turn.Message);
        Assert.Contains("đơn nghỉ phép", turn.Message);
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
        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId)
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
