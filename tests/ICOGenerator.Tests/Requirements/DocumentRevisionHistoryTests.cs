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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lịch sử revision của tài liệu sinh ra: mỗi lần UpsertDocument GHI nội dung (lần đầu hoặc ghi đè có
// thay đổi) phải chụp một ProjectDocumentRevision tăng số thứ tự; ghi lại cùng nội dung thì KHÔNG
// snapshot (tránh lịch sử toàn bản trùng). Diff query đối chiếu một revision với bản liền trước.
//
// Kèm theo là VẾT INPUT: mỗi revision ghi mốc TriggerConversationId (lượt user mới nhất lúc ghi), và
// diff query trả về các lượt user nằm giữa hai mốc — phần "vì sao đổi" đứng cạnh phần "đổi chỗ nào".
// Hai luật dễ vỡ nhất nằm ở chỗ hội thoại bị LƯU TRỮ ("New Chat"): lượt lưu trữ SAU khi bản được ghi
// vẫn phải hiện (lịch sử không được bốc hơi vì một cú bấm ở khung chat), lượt lưu trữ TRƯỚC đó thì
// không (vòng soạn chưa từng đọc chúng).
public class DocumentRevisionHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _workspaceRoot;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();
    // Mốc thời gian trong QUÁ KHỨ: revision được ghi ở UtcNow, các lượt hội thoại phải đứng trước nó.
    private readonly DateTime _t0 = DateTime.UtcNow.AddHours(-1);

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
        db.Agents.Add(new Agent { Id = _baId, AiModelId = model.Id });
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

    [Fact]
    public async Task Snapshot_AnchorsRevisionToLatestUserTurn()
    {
        await AddTurnAsync("user", "lượt cũ", _t0);
        var latest = await AddTurnAsync("user", "cú submit đứng sau bản này", _t0.AddMinutes(3));
        await AddTurnAsync("assistant", "BA trả lời", _t0.AddMinutes(4));
        await GenerateDraftAsync("v1");

        await using var db = NewDb();
        var revision = await db.ProjectDocumentRevisions.SingleAsync();

        // Mốc là lượt USER mới nhất — không phải lượt cuối cùng của hội thoại.
        Assert.Equal(latest, revision.TriggerConversationId);
    }

    [Fact]
    public async Task DiffQuery_ReturnsOnlyUserTurnsSincePreviousRevision()
    {
        await AddTurnAsync("user", "lượt trước bản 1", _t0);
        await GenerateDraftAsync("v1");
        await AddTurnAsync("assistant", "BA trả lời", _t0.AddMinutes(1));
        await AddTurnAsync("user", "ghi chú sửa brief", _t0.AddMinutes(2));
        await GenerateDraftAsync("v2");

        await using var db = NewDb();
        var query = new GetDocumentRevisionDiffQuery(db, new DocumentDiffService());
        var first = await db.ProjectDocumentRevisions.SingleAsync(x => x.RevisionNumber == 1);
        var second = await db.ProjectDocumentRevisions.SingleAsync(x => x.RevisionNumber == 2);

        var firstDiff = await query.ExecuteAsync(first.Id);
        var secondDiff = await query.ExecuteAsync(second.Id);

        Assert.Equal(new[] { "lượt trước bản 1" }, firstDiff!.Inputs.Select(i => i.Message));
        // Lượt đã tính cho bản trước KHÔNG được kể lại, lượt assistant không phải input.
        Assert.Equal(new[] { "ghi chú sửa brief" }, secondDiff!.Inputs.Select(i => i.Message));
        Assert.False(secondDiff.InputsTruncated);
    }

    [Fact]
    public async Task DiffQuery_KeepsInputs_WhenConversationArchivedAfterRevision()
    {
        await AddTurnAsync("user", "câu hỏi rồi bị New Chat lưu trữ", _t0);
        await GenerateDraftAsync("v1");

        await using (var arrange = NewDb())
        {
            var turns = await arrange.AgentConversations.IgnoreQueryFilters().ToListAsync();
            turns.ForEach(t => t.ArchivedAt = DateTime.UtcNow);
            await arrange.SaveChangesAsync();
        }

        await using var db = NewDb();
        var revision = await db.ProjectDocumentRevisions.SingleAsync();

        var diff = await new GetDocumentRevisionDiffQuery(db, new DocumentDiffService()).ExecuteAsync(revision.Id);

        Assert.Equal(new[] { "câu hỏi rồi bị New Chat lưu trữ" }, diff!.Inputs.Select(i => i.Message));
    }

    [Fact]
    public async Task Snapshot_AfterNewChat_HasNoTriggerAndNoInputs()
    {
        await AddTurnAsync("user", "buổi chat đã đóng", _t0, archivedAt: _t0.AddMinutes(5));
        await GenerateDraftAsync("v1");

        await using var db = NewDb();
        var revision = await db.ProjectDocumentRevisions.SingleAsync();

        Assert.Null(revision.TriggerConversationId);

        var diff = await new GetDocumentRevisionDiffQuery(db, new DocumentDiffService()).ExecuteAsync(revision.Id);

        Assert.Empty(diff!.Inputs);
    }

    [Fact]
    public async Task DiffQuery_CapsInputTurnsAndFlagsTruncation()
    {
        for (var i = 0; i < 12; i++)
            await AddTurnAsync("user", $"lượt {i}", _t0.AddMinutes(i));
        await GenerateDraftAsync("v1");

        await using var db = NewDb();
        var revision = await db.ProjectDocumentRevisions.SingleAsync();

        var diff = await new GetDocumentRevisionDiffQuery(db, new DocumentDiffService()).ExecuteAsync(revision.Id);

        Assert.True(diff!.InputsTruncated);
        Assert.Equal(10, diff.Inputs.Count);
        // Cắt từ phía CŨ, trả về theo thứ tự thời gian.
        Assert.Equal("lượt 2", diff.Inputs[0].Message);
        Assert.Equal("lượt 11", diff.Inputs[^1].Message);
    }

    private async Task<Guid> AddTurnAsync(string role, string message, DateTime createdAt, DateTime? archivedAt = null)
    {
        await using var db = NewDb();
        var turn = new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = role,
            Message = message,
            CreatedAt = createdAt,
            ArchivedAt = archivedAt
        };
        db.AgentConversations.Add(turn);
        await db.SaveChangesAsync();
        return turn.Id;
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
            new LocalArtifactStorage(resolver, NullLogger<LocalArtifactStorage>.Instance));
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
    }
}
