using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Sau "＋ New Chat", timeline trang Requirements chỉ còn hội thoại mới: các workflow run tạo trước mốc New
// Chat (vd thẻ "✓ POC đã tạo xong" đang chờ ở cổng POC) bị ẩn — POC vào qua POC Review. Run còn đang chạy
// thì vẫn hiện để người dùng thấy tiến độ và cổng "đang soạn" vẫn khoá.
public class RequirementWorkspaceRunFilterTests : IDisposable
{
    private static readonly DateTime NewChatAt = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public RequirementWorkspaceRunFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_HidesFinishedRunsCreatedBeforeNewChat()
    {
        Seed(NewChatAt,
            ("POC cũ chờ duyệt", WorkflowRunStatus.WaitingForHuman, NewChatAt.AddHours(-2)),
            ("Brief cũ", WorkflowRunStatus.Completed, NewChatAt.AddHours(-3)),
            ("Run cũ lỗi", WorkflowRunStatus.Failed, NewChatAt.AddHours(-1)),
            ("Run cũ đang chạy", WorkflowRunStatus.Running, NewChatAt.AddHours(-1)),
            ("Run mới", WorkflowRunStatus.Completed, NewChatAt.AddMinutes(5)));

        var names = await LoadRunNamesAsync();

        Assert.Equal(["Run cũ đang chạy", "Run mới"], names);
    }

    [Fact]
    public async Task ExecuteAsync_NeverStartedNewChat_ShowsAllRuns()
    {
        Seed(null,
            ("A", WorkflowRunStatus.WaitingForHuman, NewChatAt.AddHours(-2)),
            ("B", WorkflowRunStatus.Completed, NewChatAt));

        Assert.Equal(["A", "B"], await LoadRunNamesAsync());
    }

    private void Seed(DateTime? conversationStartedAt, params (string Name, WorkflowRunStatus Status, DateTime CreatedAt)[] runs)
    {
        using var db = NewDb();
        db.Projects.Add(new Project { Id = _projectId, Name = "P", ConversationStartedAtUtc = conversationStartedAt });
        foreach (var (name, status, createdAt) in runs)
        {
            db.WorkflowRuns.Add(new WorkflowRun
            {
                ProjectId = _projectId,
                Name = name,
                Status = status,
                CreatedAt = createdAt
            });
        }
        db.SaveChanges();
    }

    private async Task<string[]> LoadRunNamesAsync()
    {
        await using var db = NewDb();
        var result = await new GetRequirementWorkspaceQuery(db, new CoverageChecklist(new StubPrompts()))
            .ExecuteAsync(_projectId);
        Assert.NotNull(result);
        return result!.Project.WorkflowRuns.OrderBy(r => r.CreatedAt).Select(r => r.Name).ToArray();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) => string.Empty;
    }
}
