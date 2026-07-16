using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Ghi chú ghim trên POC (PocComment): thêm (validate + cắt gọn dữ liệu client), liệt kê (CanDelete
// theo chủ ghi chú / quyền quản lý), xóa (chủ ghi chú hoặc người có DeliveryAdvance). Đây là dữ liệu
// đầu vào cho "Yêu cầu chỉnh sửa" ở cổng POC — phần gom vào feedback test ở RequestStageRevisionUseCaseTests.
public class PocCommentUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public PocCommentUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Add_TrimsAndClamps_AndReturnsItem()
    {
        await using var db = NewDb();
        var (result, item) = await new AddPocCommentUseCase(db).ExecuteAsync(
            _projectId,
            pageView: "  Overview  ",
            elementLabel: "Nút “Save”",
            elementPath: "#main > button:nth-of-type(2)",
            xPercent: 150,           // ngoài khoảng → kẹp về 100
            yPercent: -3,            // ngoài khoảng → kẹp về 0
            comment: "  Đổi nhãn thành 'Lưu'  ",
            createdByUsername: "user");

        Assert.Equal(AddPocCommentResult.Ok, result);
        Assert.NotNull(item);
        Assert.Equal("Overview", item!.PageView);
        Assert.Equal("Đổi nhãn thành 'Lưu'", item.Comment);
        Assert.Equal(100, item.XPercent);
        Assert.Equal(0, item.YPercent);
        Assert.Equal("Open", item.Status);
        Assert.True(item.CanDelete);

        var saved = await db.PocComments.SingleAsync();
        Assert.Equal(PocCommentStatus.Open, saved.Status);
        Assert.Equal("user", saved.CreatedByUsername);
    }

    [Fact]
    public async Task Add_RejectsBlankComment_AndMissingProject()
    {
        await using var db = NewDb();
        var useCase = new AddPocCommentUseCase(db);

        var (blank, _) = await useCase.ExecuteAsync(_projectId, null, null, null, 0, 0, "   ", "user");
        Assert.Equal(AddPocCommentResult.MissingComment, blank);

        var (missing, _) = await useCase.ExecuteAsync(Guid.NewGuid(), null, null, null, 0, 0, "note", "user");
        Assert.Equal(AddPocCommentResult.ProjectNotFound, missing);

        Assert.Equal(0, await db.PocComments.CountAsync());
    }

    [Fact]
    public async Task List_ComputesCanDelete_ByOwnerOrManager()
    {
        await using (var db = NewDb())
        {
            db.PocComments.AddRange(
                new PocComment { ProjectId = _projectId, Comment = "của user", CreatedByUsername = "user", CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
                new PocComment { ProjectId = _projectId, Comment = "của người khác", CreatedByUsername = "other", CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            // User thường: chỉ xóa được ghi chú của mình.
            var asUser = await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "user", canManage: false);
            Assert.Equal(2, asUser.Count);
            Assert.True(asUser.Single(x => x.Comment == "của user").CanDelete);
            Assert.False(asUser.Single(x => x.Comment == "của người khác").CanDelete);

            // Người duyệt (DeliveryAdvance): xóa được tất cả.
            var asManager = await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "teamdev", canManage: true);
            Assert.All(asManager, x => Assert.True(x.CanDelete));
        }
    }

    [Fact]
    public async Task Delete_EnforcesOwnership()
    {
        Guid ownCommentId, otherCommentId;
        await using (var db = NewDb())
        {
            var own = new PocComment { ProjectId = _projectId, Comment = "a", CreatedByUsername = "user" };
            var other = new PocComment { ProjectId = _projectId, Comment = "b", CreatedByUsername = "other" };
            db.PocComments.AddRange(own, other);
            await db.SaveChangesAsync();
            (ownCommentId, otherCommentId) = (own.Id, other.Id);
        }

        await using (var db = NewDb())
        {
            var useCase = new DeletePocCommentUseCase(db);

            // Không phải chủ, không phải manager → từ chối, không xóa gì.
            Assert.False(await useCase.ExecuteAsync(otherCommentId, "user", canManage: false));
            Assert.Equal(2, await db.PocComments.CountAsync());

            // Chủ ghi chú xóa được của mình; manager xóa được của người khác.
            Assert.True(await useCase.ExecuteAsync(ownCommentId, "user", canManage: false));
            Assert.True(await useCase.ExecuteAsync(otherCommentId, "user", canManage: true));
            Assert.Equal(0, await db.PocComments.CountAsync());
        }
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
