using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lịch sử revision của tài liệu sinh ra: mỗi lần UpsertDocument GHI nội dung (lần đầu hoặc ghi đè có
// thay đổi) phải chụp một ProjectDocumentRevision tăng số thứ tự; ghi lại cùng nội dung thì KHÔNG
// snapshot (tránh lịch sử toàn bản trùng). Diff query đối chiếu một revision với bản liền trước.
public class DocumentRevisionHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _workspaceRoot;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public DocumentRevisionHistoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ico-rev-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);

        using var db = NewDb();
        db.Database.EnsureCreated();

        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = _baId, Name = "BA", AiModelId = model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task RegeneratingDraft_SnapshotsOneRevisionPerContentChange()
    {
        await GenerateDraftAsync("bản đầu tiên\ndòng chung");
        await GenerateDraftAsync("bản thứ hai\ndòng chung");

        await using var db = NewDb();
        var doc = await db.ProjectDocuments.SingleAsync(x => x.ProjectId == _projectId);
        var revisions = await db.ProjectDocumentRevisions
            .Where(x => x.ProjectDocumentId == doc.Id)
            .OrderBy(x => x.RevisionNumber)
            .ToListAsync();

        Assert.Equal(2, revisions.Count);
        Assert.Equal(new[] { 1, 2 }, revisions.Select(r => r.RevisionNumber));
        Assert.Equal("bản đầu tiên\ndòng chung", revisions[0].Content);
        Assert.Equal("bản thứ hai\ndòng chung", revisions[1].Content);
        // Bản mới nhất luôn trùng nội dung hiện hành của document.
        Assert.Equal(doc.Content, revisions[1].Content);
        Assert.All(revisions, r => Assert.Equal("draft", r.VersionName));
        Assert.All(revisions, r => Assert.False(string.IsNullOrWhiteSpace(r.ChangeNote)));
    }

    [Fact]
    public async Task RegeneratingSameContent_DoesNotSnapshotDuplicate()
    {
        await GenerateDraftAsync("nội dung y hệt");
        await GenerateDraftAsync("nội dung y hệt");

        await using var db = NewDb();
        Assert.Equal(1, await db.ProjectDocumentRevisions.CountAsync());
    }

    [Fact]
    public async Task DiffQuery_ComparesRevisionWithPrevious()
    {
        await GenerateDraftAsync("dòng chung\ndòng cũ");
        await GenerateDraftAsync("dòng chung\ndòng mới");

        await using var db = NewDb();
        var latest = await db.ProjectDocumentRevisions.SingleAsync(x => x.RevisionNumber == 2);

        var diff = await new GetDocumentRevisionDiffQuery(db, new DocumentDiffService()).ExecuteAsync(latest.Id);

        Assert.NotNull(diff);
        Assert.Equal(2, diff!.RevisionNumber);
        Assert.Equal(1, diff.PreviousRevisionNumber);
        Assert.Contains(diff.Lines, l => l.Type == "same" && l.Text == "dòng chung");
        Assert.Contains(diff.Lines, l => l.Type == "removed" && l.Text == "dòng cũ");
        Assert.Contains(diff.Lines, l => l.Type == "added" && l.Text == "dòng mới");
    }

    [Fact]
    public async Task RevisionsQuery_ListsNewestFirst()
    {
        await GenerateDraftAsync("v1");
        await GenerateDraftAsync("v2");

        await using var db = NewDb();
        var doc = await db.ProjectDocuments.SingleAsync();

        var result = await new GetDocumentRevisionsQuery(db).ExecuteAsync(doc.Id);

        Assert.NotNull(result);
        Assert.Equal(new[] { 2, 1 }, result!.Revisions.Select(r => r.RevisionNumber));
    }

    private async Task GenerateDraftAsync(string content)
    {
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(x => x.Id == _projectId);
        var generator = NewGenerator(db);

        await generator.GenerateProductBriefDraftFiles(project, _baId, new BAProductBriefResult
        {
            ProductBrief = new ProductBriefDto { Content = content }
        });
        await db.SaveChangesAsync();
    }

    private RequirementDocumentGenerator NewGenerator(AppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _workspaceRoot })
            .Build();
        var resolver = new WorkspacePathResolver(config);

        return new RequirementDocumentGenerator(
            db,
            new RequirementTemplateService(new FakeWebHostEnvironment()),
            new DocxTemplateWriter(),
            resolver,
            new ProjectArtifactCatalog(),
            new LocalArtifactStorage(resolver));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_workspaceRoot, true); } catch { /* dọn tạm, lỗi bỏ qua */ }
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

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
