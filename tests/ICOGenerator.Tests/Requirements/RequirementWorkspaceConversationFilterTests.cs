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

// Khung chat trang Requirements là hội thoại người dùng ↔ BA. Các dự án cũ còn những dòng bàn giao cuối
// của agent delivery (vd Developer sau khi dựng POC) đã lỡ ghi vào AgentConversations — chúng là báo cáo
// kỹ thuật cho đội Dev, hiện thành bubble chat thì người dùng đọc không hiểu. Query của trang phải bỏ chúng.
public class RequirementWorkspaceConversationFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public RequirementWorkspaceConversationFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        var ba = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id };
        var developer = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.Developer, AiModelId = model.Id };
        db.AiModels.Add(model);
        db.Agents.AddRange(ba, developer);
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });

        AddTurn(db, ba.Id, "user", "Tôi cần quản lý tủ locker", 0);
        AddTurn(db, ba.Id, "assistant", "Ai là người dùng chính?", 1);
        AddTurn(db, developer.Id, "assistant", "Bản POC đã dựng xong trong `04_Implementation/poc-demo.html`.", 2);
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_LoadsOnlyBusinessAnalystTurns()
    {
        await using var db = NewDb();

        var result = await new GetRequirementWorkspaceQuery(db, new CoverageChecklist(new StubPrompts()))
            .ExecuteAsync(_projectId);

        Assert.NotNull(result);
        Assert.Equal(
            ["Tôi cần quản lý tủ locker", "Ai là người dùng chính?"],
            result!.Project.Conversations.Select(c => c.Message).ToArray());
    }

    private void AddTurn(AppDbContext db, Guid agentId, string role, string message, int second) =>
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = agentId,
            Role = role,
            Message = message,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, second, DateTimeKind.Utc)
        });

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) => string.Empty;
    }
}
