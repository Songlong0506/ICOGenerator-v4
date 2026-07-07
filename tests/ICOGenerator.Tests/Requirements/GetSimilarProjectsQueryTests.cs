using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements.Knowledge;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Endpoint panel "Dự án tương tự": truy vấn từ tên/mô tả dự án + các lượt user gần nhất; project
// không tồn tại hoặc không có gì khớp ⇒ danh sách rỗng (panel giữ trạng thái ẩn).
public class GetSimilarProjectsQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public GetSimilarProjectsQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        // AgentConversation có FK bắt buộc tới Agent ⇒ cần một BA + model để seed lượt chat.
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = _baId, Name = "BA", Temperature = 0.2, AiModelId = model.Id });
        // Dự án đang xét: tên/mô tả KHÔNG chứa từ khóa — nội dung khớp phải đến từ lượt chat.
        db.Projects.Add(new Project { Id = _projectId, Name = "Dự án X", Description = "" });

        // Dự án khác có tài liệu đã duyệt về quản lý kho vật tư.
        var other = new Project { Id = Guid.NewGuid(), Name = "Kho vật tư MFW", Description = "" };
        db.Projects.Add(other);
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = other.Id,
            FileName = "ProductBrief.docx",
            VersionName = "V1",
            IsApproved = true,
            Content = "# Phạm vi\nỨng dụng quản lý kho vật tư giúp thủ kho theo dõi nhập xuất tồn hằng ngày."
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Execute_UsesRecentUserTurnsAsQuery_ReturnsSimilarProject()
    {
        await SeedUserTurnAsync("tôi muốn làm ứng dụng quản lý kho vật tư cho phân xưởng");

        var items = await NewSut().ExecuteAsync(_projectId);

        var item = Assert.Single(items);
        Assert.Equal("Kho vật tư MFW", item.ProjectName);
        Assert.Contains("Product Brief", item.MatchedDocuments);
        Assert.False(string.IsNullOrWhiteSpace(item.Snippet));
    }

    [Fact]
    public async Task Execute_UnknownProjectOrNoMatch_ReturnsEmpty()
    {
        Assert.Empty(await NewSut().ExecuteAsync(Guid.NewGuid()));

        // Có lượt chat nhưng nội dung không giao một token nào với corpus ⇒ rỗng.
        await SeedUserTurnAsync("chấm công nhân sự ca đêm xưởng lắp ráp");
        Assert.Empty(await NewSut().ExecuteAsync(_projectId));
    }

    private async Task SeedUserTurnAsync(string message)
    {
        await using var db = NewDb();
        db.AgentConversations.Add(new AgentConversation { ProjectId = _projectId, AgentId = _baId, Role = "user", Message = message });
        await db.SaveChangesAsync();
    }

    private GetSimilarProjectsQuery NewSut()
    {
        var db = NewDb();
        var knowledge = new ProjectKnowledgeService(db, new StubPrompts(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ProjectKnowledgeService>.Instance);
        return new GetSimilarProjectsQuery(db, knowledge);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "{{EXCERPTS}}";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
