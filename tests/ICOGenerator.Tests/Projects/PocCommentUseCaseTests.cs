using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Ghi chú ghim trên POC (PocComment): thêm (validate + cắt gọn dữ liệu client + đóng dấu phiên bản
// Brief), liệt kê (CanDelete theo chủ ghi chú / quyền quản lý), THU HỒI (không còn xóa cứng — dòng ở lại
// làm lịch sử). Đây là dữ liệu đầu vào cho "Yêu cầu chỉnh sửa" ở cổng POC — phần gom vào feedback test ở
// RequestStageRevisionUseCaseTests.
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
        var (result, item) = await NewAddUseCase(db).ExecuteAsync(
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
        var useCase = NewAddUseCase(db);

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
    public async Task Withdraw_EnforcesOwnership_AndKeepsRowAsHistory()
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
            var useCase = new WithdrawPocCommentUseCase(db, new PocAcceptanceGate(db));

            // Không phải chủ, không phải manager → từ chối, không đụng gì.
            Assert.Equal(WithdrawPocCommentResult.NotFoundOrForbidden,
                await useCase.ExecuteAsync(otherCommentId, "user", canManage: false));

            // Chủ ghi chú thu hồi được của mình; manager thu hồi được của người khác.
            Assert.Equal(WithdrawPocCommentResult.Ok, await useCase.ExecuteAsync(ownCommentId, "user", canManage: false));
            Assert.Equal(WithdrawPocCommentResult.Ok, await useCase.ExecuteAsync(otherCommentId, "user", canManage: true));

            // ĐÂY là điểm khác xóa cứng: hai dòng vẫn còn trong DB, chỉ đóng dấu thu hồi.
            Assert.Equal(2, await db.PocComments.CountAsync());
            Assert.All(await db.PocComments.ToListAsync(), c => Assert.NotNull(c.WithdrawnAtUtc));
            Assert.Equal("user", (await db.PocComments.FirstAsync(c => c.Id == ownCommentId)).WithdrawnByUsername);
        }

        // …và biến mất khỏi danh sách làm việc của trang review.
        await using (var db = NewDb())
            Assert.Empty(await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "user", canManage: true));
    }

    [Fact]
    public async Task Withdraw_RefusesDispatchedComment()
    {
        Guid sentId;
        await using (var db = NewDb())
        {
            var sent = new PocComment
            {
                ProjectId = _projectId,
                Comment = "đã gửi Dev",
                CreatedByUsername = "user",
                Status = PocCommentStatus.Sent
            };
            db.PocComments.Add(sent);
            await db.SaveChangesAsync();
            sentId = sent.Id;
        }

        await using (var db = NewDb())
        {
            // Đã gửi đi thì việc đã xảy ra — giấu dòng này đi là nói dối lịch sử.
            Assert.Equal(WithdrawPocCommentResult.AlreadyDispatched,
                await new WithdrawPocCommentUseCase(db, new PocAcceptanceGate(db)).ExecuteAsync(sentId, "user", canManage: true));
            Assert.Null((await db.PocComments.SingleAsync()).WithdrawnAtUtc);
        }
    }

    [Fact]
    public async Task Add_StampsApprovedBriefVersion()
    {
        await using (var db = NewDb())
        {
            db.ProjectDocuments.AddRange(
                new ProjectDocument { ProjectId = _projectId, FileName = "ProductBrief.docx", VersionName = "V1", IsApproved = true },
                new ProjectDocument { ProjectId = _projectId, FileName = "ProductBrief.docx", VersionName = "V2", IsApproved = true },
                // Bản draft chưa duyệt KHÔNG được tính — POC đang phục vụ dựng từ V2.
                new ProjectDocument { ProjectId = _projectId, FileName = "ProductBrief.docx", VersionName = "draft", IsApproved = false });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var (result, item) = await NewAddUseCase(db).ExecuteAsync(
                _projectId, "Overview", "Nút", "#a", 10, 10, "sai nhãn", "user");

            Assert.Equal(AddPocCommentResult.Ok, result);
            Assert.Equal("V2", item!.BriefVersion);
            Assert.Equal(PocCommentTarget.Poc, (await db.PocComments.SingleAsync()).Target);
        }
    }

    [Fact]
    public async Task List_SkipsBriefNotes_AndWithdrawnRows()
    {
        await using (var db = NewDb())
        {
            db.PocComments.AddRange(
                new PocComment { ProjectId = _projectId, Comment = "ghi chú POC", CreatedByUsername = "user" },
                new PocComment { ProjectId = _projectId, Comment = "ghi chú Brief", Target = PocCommentTarget.Brief, CreatedByUsername = "user" },
                new PocComment { ProjectId = _projectId, Comment = "đã thu hồi", CreatedByUsername = "user", WithdrawnAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var items = await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "user", canManage: false);
            Assert.Equal("ghi chú POC", Assert.Single(items).Comment);
        }
    }

    // KHOÁ SAU NGHIỆM THU: bấm "Approve POC" là đóng băng ghi chú. Chốt nằm ở tầng use case chứ không ở
    // giao diện, vì đường ghim còn một cửa thứ hai (link chia sẻ, khách ẩn danh) không thấy nút khoá nào.
    [Fact]
    public async Task Add_And_Withdraw_AreLocked_AfterThePocIsAccepted()
    {
        Guid commentId;
        await using (var db = NewDb())
        {
            var existing = new PocComment { ProjectId = _projectId, Comment = "ghim trước khi nghiệm thu", CreatedByUsername = "user" };
            db.PocComments.Add(existing);
            var project = await db.Projects.SingleAsync();
            project.PocAcceptedAtUtc = DateTime.UtcNow;
            project.PocAcceptedBy = "lan.nguyen";
            await db.SaveChangesAsync();
            commentId = existing.Id;
        }

        await using (var db = NewDb())
        {
            var (result, item) = await NewAddUseCase(db).ExecuteAsync(
                _projectId, "Overview", "Nút", "#a", 10, 10, "ghi chú mới", "user");
            Assert.Equal(AddPocCommentResult.PocAccepted, result);
            Assert.Null(item);

            Assert.Equal(WithdrawPocCommentResult.PocAccepted,
                await new WithdrawPocCommentUseCase(db, new PocAcceptanceGate(db))
                    .ExecuteAsync(commentId, "user", canManage: true));

            // Không dòng nào được thêm, không dòng nào bị đụng.
            Assert.Equal(1, await db.PocComments.CountAsync());
            Assert.Null((await db.PocComments.SingleAsync()).WithdrawnAtUtc);
        }
    }

    // …và rút nghiệm thu là mở khoá thật, không phải chỉ đổi nhãn nút.
    [Fact]
    public async Task Add_WorksAgain_AfterTheAcceptanceIsWithdrawn()
    {
        await using (var db = NewDb())
        {
            var project = await db.Projects.SingleAsync();
            project.PocAcceptedAtUtc = DateTime.UtcNow;
            project.PocAcceptedBy = "lan.nguyen";
            await db.SaveChangesAsync();

            project.PocAcceptedAtUtc = null;
            project.PocAcceptedBy = null;
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var (result, _) = await NewAddUseCase(db).ExecuteAsync(
                _projectId, "Overview", "Nút", "#a", 10, 10, "ghi chú sau khi mở khoá", "user");
            Assert.Equal(AddPocCommentResult.Ok, result);
        }
    }

    private static AddPocCommentUseCase NewAddUseCase(AppDbContext db) =>
        new(db, new BriefVersionResolver(db, new ProjectArtifactCatalog()), new PocAcceptanceGate(db));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
