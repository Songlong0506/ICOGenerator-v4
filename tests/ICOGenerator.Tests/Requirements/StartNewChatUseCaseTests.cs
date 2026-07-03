using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Nút "＋ New Chat": xoá lịch sử hội thoại VÀ reset toàn bộ bộ nhớ per-project gắn với hội thoại
// (summary + các con trỏ đếm-lượt + bản đồ bao phủ). Nếu chỉ xoá lượt chat mà giữ con trỏ, Skip(summarized)
// sẽ nuốt các lượt MỚI của chat sau (BA không thấy tin nhắn nào) và summary/bản đồ cũ vẫn được nạp lại.
// Không đụng dữ liệu của project khác.
public class StartNewChatUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public StartNewChatUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_DeletesConversations_AndResetsChatMemoryState()
    {
        var (project, other) = await SeedAsync();

        await using var db = NewDb();
        await new StartNewChatUseCase(db).ExecuteAsync(project.Id);

        await using var verify = NewDb();
        Assert.Equal(0, await verify.AgentConversations.CountAsync(c => c.ProjectId == project.Id));

        var reloaded = await verify.Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Null(reloaded.ConversationSummary);
        Assert.Equal(0, reloaded.SummarizedTurnCount);
        Assert.Equal(0, reloaded.UserMemoryHarvestedTurnCount);
        Assert.Null(reloaded.RequirementCoverageMap);
        Assert.Equal(0, reloaded.CoverageHarvestedTurnCount);

        // Project khác không bị ảnh hưởng.
        Assert.Equal(1, await verify.AgentConversations.CountAsync(c => c.ProjectId == other.Id));
        var otherReloaded = await verify.Projects.FirstAsync(p => p.Id == other.Id);
        Assert.Equal("summary khác", otherReloaded.ConversationSummary);
        Assert.Equal(3, otherReloaded.SummarizedTurnCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProject_DoesNotThrow()
    {
        await using var db = NewDb();
        await new StartNewChatUseCase(db).ExecuteAsync(Guid.NewGuid());
    }

    private async Task<(Project Project, Project Other)> SeedAsync()
    {
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        var ba = new Agent { Id = Guid.NewGuid(), Name = "BA", AiModelId = model.Id };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            ConversationSummary = "summary cũ",
            SummarizedTurnCount = 15,
            UserMemoryHarvestedTurnCount = 10,
            RequirementCoverageMap = "- ★ Mục tiêu / bài toán: [RÕ] app kho",
            CoverageHarvestedTurnCount = 12
        };
        var other = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Khác",
            ConversationSummary = "summary khác",
            SummarizedTurnCount = 3
        };

        await using var db = NewDb();
        db.AiModels.Add(model);
        db.Agents.Add(ba);
        db.Projects.AddRange(project, other);
        for (var i = 0; i < 4; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"turn-{i}"
            });
        }
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = other.Id,
            AgentId = ba.Id,
            Role = "user",
            Message = "giữ nguyên"
        });
        await db.SaveChangesAsync();
        return (project, other);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
