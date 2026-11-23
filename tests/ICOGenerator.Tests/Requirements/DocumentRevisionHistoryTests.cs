using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
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
// Kèm theo là GHI CHÚ của từng bản: diff query trả về các ghi chú Brief người dùng ghim lên CHÍNH
// revision đó (không phải mọi lượt chat). Luật dễ vỡ nhất: revision cuối của một bản đã duyệt có khoảng
// thời gian mở ở đầu trên, nên ghi chú của bản draft sinh sau không được lọt vào nó.
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

        var diff = await NewDiffQuery(db).ExecuteAsync(latest.Id);

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
    public async Task DiffQuery_ReturnsOnlyNotesPinnedOnThatRevision()
    {
        await GenerateDraftAsync("v1");
        await AddChatTurnAsync("user", "lượt chat thường — không phải ghi chú");
        await AddBriefNoteAsync("ghi chú trên bản 1", quote: "đoạn bôi đen");
        await GenerateDraftAsync("v2");
        await AddBriefNoteAsync("ghi chú trên bản 2");

        await using var db = NewDb();
        var query = NewDiffQuery(db);
        var first = await db.ProjectDocumentRevisions.SingleAsync(x => x.RevisionNumber == 1);
        var second = await db.ProjectDocumentRevisions.SingleAsync(x => x.RevisionNumber == 2);

        var firstDiff = await query.ExecuteAsync(first.Id);
        var secondDiff = await query.ExecuteAsync(second.Id);

        // Ghi chú ghim lên bản 1 thuộc về bản 1 — không lặp lại ở bản 2, và lượt chat không phải ghi chú.
        var firstNote = Assert.Single(firstDiff!.Notes);
        Assert.Equal("ghi chú trên bản 1", firstNote.Comment);
        Assert.Equal("đoạn bôi đen", firstNote.Quote);
        Assert.Equal(new[] { "ghi chú trên bản 2" }, secondDiff!.Notes.Select(n => n.Comment));
    }

    [Fact]
    public async Task DiffQuery_ApprovedVersion_DoesNotPickUpNotesOfLaterDraft()
    {
        await GenerateDraftAsync("v1");
        await AddBriefNoteAsync("ghi chú trước khi duyệt");

        // Duyệt: ApproveRequirementUseCase đổi tên file draft → V1 và nâng ghi chú draft lên V1 cùng lúc.
        await using (var arrange = NewDb())
        {
            var doc = await arrange.ProjectDocuments.SingleAsync();
            doc.VersionName = "V1";
            doc.IsApproved = true;
            (await arrange.PocComments.ToListAsync()).ForEach(c => c.BriefVersion = "V1");
            await arrange.SaveChangesAsync();
        }

        // Ghi chú của bản draft V2 sinh sau — đóng dấu "draft", không thuộc V1.
        await AddBriefNoteAsync("ghi chú trên draft sau");

        await using var db = NewDb();
        var revision = await db.ProjectDocumentRevisions.SingleAsync();

        var diff = await NewDiffQuery(db).ExecuteAsync(revision.Id);

        Assert.Equal(new[] { "ghi chú trước khi duyệt" }, diff!.Notes.Select(n => n.Comment));
    }

    [Fact]
    public async Task DiffQuery_HidesWithdrawnNotes()
    {
        await GenerateDraftAsync("v1");
        await AddBriefNoteAsync("còn giữ");
        await AddBriefNoteAsync("đã thu hồi", withdrawn: true);

        await using var db = NewDb();
        var revision = await db.ProjectDocumentRevisions.SingleAsync();

        var diff = await NewDiffQuery(db).ExecuteAsync(revision.Id);

        Assert.Equal(new[] { "còn giữ" }, diff!.Notes.Select(n => n.Comment));
    }

    private async Task AddChatTurnAsync(string role, string message)
    {
        await using var db = NewDb();
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = role,
            Message = message,
            CreatedAt = await LatestRevisionTimeAsync(db) + TimeSpan.FromMilliseconds(1)
        });
        await db.SaveChangesAsync();
    }

    // Ghi chú luôn đứng SAU revision hiện hành (ghim lên bản đang xem) — đặt mốc tường minh để không phụ
    // thuộc độ phân giải đồng hồ giữa hai lần gọi UtcNow liên tiếp.
    private async Task AddBriefNoteAsync(string comment, string? quote = null, bool withdrawn = false)
    {
        await using var db = NewDb();
        var after = await LatestRevisionTimeAsync(db);
        var existing = await db.PocComments.CountAsync();
        db.PocComments.Add(new PocComment
        {
            ProjectId = _projectId,
            Target = PocCommentTarget.Brief,
            BriefVersion = BriefVersionResolver.DraftVersion,
            Quote = quote,
            Comment = comment,
            CreatedAt = after + TimeSpan.FromMilliseconds(existing + 1),
            WithdrawnAtUtc = withdrawn ? DateTime.UtcNow : null
        });
        await db.SaveChangesAsync();
    }

    private static Task<DateTime> LatestRevisionTimeAsync(AppDbContext db) =>
        db.ProjectDocumentRevisions.MaxAsync(x => x.CreatedAt);

    private static GetDocumentRevisionDiffQuery NewDiffQuery(AppDbContext db) =>
        new(db, new DocumentDiffService(), new ProjectArtifactCatalog());

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
}
