using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Góp ý của reviewer trên Product Brief: ai vào được workspace thì góp ý được (neo tùy chọn vào một
// đoạn trích); resolve chỉ dành cho tác giả, chủ project, hoặc người có ProjectsViewAll. Danh sách
// đưa góp ý còn mở lên trước và tính cờ CanResolve theo người xem.
public class BriefCommentsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BriefCommentsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task AddComment_TrimsAndStoresAnchor()
    {
        var projectId = await SeedProjectAsync("alice");

        await using (var db = NewDb())
        {
            var result = await new AddBriefCommentUseCase(db)
                .ExecuteAsync(projectId, "  Thiếu phần chi phí vận hành.  ", "  mục 3: Ngân sách  ", "bob");
            Assert.Equal(AddBriefCommentResult.Added, result);
        }

        await using (var db = NewDb())
        {
            var comment = await db.BriefComments.SingleAsync();
            Assert.Equal("Thiếu phần chi phí vận hành.", comment.Content);
            Assert.Equal("mục 3: Ngân sách", comment.AnchorText);
            Assert.Equal("bob", comment.AuthorUsername);
            Assert.Null(comment.ResolvedAt);
        }
    }

    [Fact]
    public async Task AddComment_RejectsBlankContent_AndUnknownProject()
    {
        var projectId = await SeedProjectAsync("alice");

        await using var db = NewDb();
        var useCase = new AddBriefCommentUseCase(db);

        Assert.Equal(AddBriefCommentResult.MissingContent, await useCase.ExecuteAsync(projectId, "   ", null, "bob"));
        Assert.Equal(AddBriefCommentResult.ProjectNotFound, await useCase.ExecuteAsync(Guid.NewGuid(), "ok", null, "bob"));
        Assert.Equal(0, await db.BriefComments.CountAsync());
    }

    [Fact]
    public async Task AddComment_TruncatesOverlongAnchorAndContent()
    {
        var projectId = await SeedProjectAsync("alice");

        await using (var db = NewDb())
        {
            await new AddBriefCommentUseCase(db).ExecuteAsync(
                projectId, new string('c', 5000), new string('a', 600), "bob");
        }

        await using (var db = NewDb())
        {
            var comment = await db.BriefComments.SingleAsync();
            // Khớp trần HasMaxLength của cột: cắt ở app thay vì để DB văng lỗi.
            Assert.True(comment.Content.Length <= 4000);
            Assert.True(comment.AnchorText!.Length <= 500);
            Assert.EndsWith("…", comment.Content);
        }
    }

    [Fact]
    public async Task ResolveComment_AllowsAuthorOwnerAndViewAll_Only()
    {
        var projectId = await SeedProjectAsync(owner: "alice");
        var commentId = await SeedCommentAsync(projectId, author: "bob");

        await using (var db = NewDb())
        {
            // "carol": không phải tác giả/chủ, không có quyền → chặn.
            var denied = await new ResolveBriefCommentUseCase(db).ExecuteAsync(commentId, "carol", canManageAll: false);
            Assert.Equal(ResolveBriefCommentResult.NotAllowed, denied);

            // Tác giả resolve được.
            var resolved = await new ResolveBriefCommentUseCase(db).ExecuteAsync(commentId, "bob", canManageAll: false);
            Assert.Equal(ResolveBriefCommentResult.Resolved, resolved);

            // Lần hai: đã resolve rồi (idempotent về dữ liệu).
            var again = await new ResolveBriefCommentUseCase(db).ExecuteAsync(commentId, "alice", canManageAll: false);
            Assert.Equal(ResolveBriefCommentResult.AlreadyResolved, again);
        }

        // Chủ project resolve góp ý của người khác được.
        var second = await SeedCommentAsync(projectId, author: "bob");
        await using (var db = NewDb())
        {
            var byOwner = await new ResolveBriefCommentUseCase(db).ExecuteAsync(second, "alice", canManageAll: false);
            Assert.Equal(ResolveBriefCommentResult.Resolved, byOwner);
        }

        // Người có ProjectsViewAll (TeamDev/Admin) resolve được dù không liên quan project.
        var third = await SeedCommentAsync(projectId, author: "bob");
        await using (var db = NewDb())
        {
            var byAdmin = await new ResolveBriefCommentUseCase(db).ExecuteAsync(third, "root", canManageAll: true);
            Assert.Equal(ResolveBriefCommentResult.Resolved, byAdmin);

            var stored = await db.BriefComments.SingleAsync(c => c.Id == third);
            Assert.NotNull(stored.ResolvedAt);
            Assert.Equal("root", stored.ResolvedByUsername);
        }
    }

    [Fact]
    public async Task CommentsQuery_PutsOpenFirst_AndComputesCanResolvePerViewer()
    {
        var projectId = await SeedProjectAsync(owner: "alice");
        var openId = await SeedCommentAsync(projectId, author: "bob");
        var resolvedId = await SeedCommentAsync(projectId, author: "bob");

        await using (var db = NewDb())
            await new ResolveBriefCommentUseCase(db).ExecuteAsync(resolvedId, "bob", canManageAll: false);

        await using var readDb = NewDb();

        // Người xem là "carol" (không tác giả, không chủ): thấy đủ 2 góp ý, mở trước, không resolve được gì.
        var forCarol = await new GetBriefCommentsQuery(readDb).ExecuteAsync(projectId, "carol", canManageAll: false);
        Assert.NotNull(forCarol);
        Assert.Equal(1, forCarol!.OpenCount);
        Assert.Equal(openId, forCarol.Comments[0].Id);
        Assert.All(forCarol.Comments, c => Assert.False(c.CanResolve));

        // Chủ project resolve được góp ý còn mở (góp ý đã resolve thì thôi).
        var forAlice = await new GetBriefCommentsQuery(readDb).ExecuteAsync(projectId, "alice", canManageAll: false);
        Assert.True(forAlice!.Comments.Single(c => c.Id == openId).CanResolve);
        Assert.False(forAlice.Comments.Single(c => c.Id == resolvedId).CanResolve);

        // Project không tồn tại → null (controller trả 404).
        Assert.Null(await new GetBriefCommentsQuery(readDb).ExecuteAsync(Guid.NewGuid(), "alice", false));
    }

    private async Task<Guid> SeedProjectAsync(string owner)
    {
        await using var db = NewDb();
        var project = new Project { Name = "P", CreatedByUsername = owner };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Guid> SeedCommentAsync(Guid projectId, string author)
    {
        await using var db = NewDb();
        var comment = new BriefComment { ProjectId = projectId, AuthorUsername = author, Content = "góp ý" };
        db.BriefComments.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
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
