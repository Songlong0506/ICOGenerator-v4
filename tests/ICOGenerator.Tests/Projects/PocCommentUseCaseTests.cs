using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Ghi chú ghim trên POC (PocComment): thêm (validate + cắt gọn dữ liệu client + đóng dấu phiên bản
// Brief), liệt kê (CanWithdraw theo chủ ghi chú / quyền quản lý + trạng thái, trạng thái hiển thị
// Draft/Waiting/Apply), THU HỒI. Thu hồi có HAI hệ quả tuỳ ghi
// chú đã từng gửi đi chưa: chưa gửi ⇒ xoá thật (không để lại dòng lịch sử nào), đã đi một vòng rồi được
// mở lại ⇒ chỉ đóng dấu WithdrawnAtUtc. Đây là dữ liệu đầu vào cho "Yêu cầu chỉnh sửa" ở cổng POC —
// phần gom vào feedback test ở RequestStageRevisionUseCaseTests.
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
        Assert.Equal(PocNoteDisplayStatus.Draft, item.DisplayStatus);
        Assert.True(item.CanWithdraw);

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
    public async Task List_ComputesCanWithdraw_ByOwnerOrManager()
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
            Assert.True(asUser.Single(x => x.Comment == "của user").CanWithdraw);
            Assert.False(asUser.Single(x => x.Comment == "của người khác").CanWithdraw);

            // Người duyệt (DeliveryAdvance): xóa được tất cả.
            var asManager = await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "teamdev", canManage: true);
            Assert.All(asManager, x => Assert.True(x.CanWithdraw));
        }
    }

    [Fact]
    public async Task Withdraw_EnforcesOwnership_AndDeletesNotesThatWereNeverDispatched()
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

            // Hai ghi chú này chưa gửi đi đâu cả ⇒ xoá thật: không dòng nào ở lại làm lịch sử.
            Assert.Empty(await db.PocComments.ToListAsync());
        }

        // …nên bảng ghi chú (kể cả khi gồm ghi chú Brief) cũng không còn gì để hiện.
        await using (var db = NewDb())
        {
            Assert.Empty(await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "user", canManage: true, includeBriefNotes: true));
        }
    }

    // Ghi chú ĐÃ đi một vòng sửa rồi được "vẫn chưa đạt" mở lại: trạng thái về Open (thu hồi được) nhưng
    // RevisionTaskId còn nguyên. Xoá thật dòng này là làm bàn giao của agent nói về một ghi chú không còn
    // tồn tại — nên nó chỉ được đóng dấu thu hồi: rời bảng ghi chú nhưng dòng còn trong DB.
    [Fact]
    public async Task Withdraw_KeepsRowAsHistory_WhenTheNoteWasDispatchedBefore()
    {
        Guid commentId;
        await using (var db = NewDb())
        {
            var reopened = new PocComment
            {
                ProjectId = _projectId,
                BriefVersion = "V1",
                Comment = "vẫn chưa đạt",
                CreatedByUsername = "user",
                Status = PocCommentStatus.Open,
                RevisionTaskId = Guid.NewGuid()
            };
            db.PocComments.Add(reopened);
            await db.SaveChangesAsync();
            commentId = reopened.Id;
        }

        await using (var db = NewDb())
        {
            Assert.Equal(WithdrawPocCommentResult.Ok,
                await new WithdrawPocCommentUseCase(db, new PocAcceptanceGate(db))
                    .ExecuteAsync(commentId, "user", canManage: false));

            var kept = await db.PocComments.SingleAsync();
            Assert.NotNull(kept.WithdrawnAtUtc);
            Assert.Equal("user", kept.WithdrawnByUsername);
        }

        await using (var db = NewDb())
        {
            // Rời bảng ghi chú, nhưng dòng còn nguyên trong DB với dấu thu hồi.
            Assert.Empty(await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "user", canManage: true, includeBriefNotes: true));
            Assert.NotNull((await db.PocComments.SingleAsync()).WithdrawnAtUtc);
        }
    }

    // Con trỏ harvest (PocFeedbackHarvestedCount) là một SỐ LƯỢNG dùng Skip() trên tập ghi chú POC xếp
    // theo thời gian. Xoá thật một dòng nằm TRƯỚC con trỏ mà không lùi con trỏ thì lượt harvest sau nhảy
    // qua đúng một ghi chú mới — bài học của nó không bao giờ được học.
    [Fact]
    public async Task Withdraw_StepsBackTheHarvestCursor_WhenItDeletesAnAlreadyCountedNote()
    {
        Guid firstId;
        await using (var db = NewDb())
        {
            var first = new PocComment
            {
                ProjectId = _projectId, Comment = "ghi chú thứ nhất", CreatedByUsername = "user",
                CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            db.PocComments.AddRange(
                first,
                new PocComment
                {
                    ProjectId = _projectId, Comment = "ghi chú thứ hai", CreatedByUsername = "user",
                    CreatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)
                });
            // Đã harvest cả hai dòng ở một lượt duyệt trước.
            (await db.Projects.SingleAsync()).PocFeedbackHarvestedCount = 2;
            await db.SaveChangesAsync();
            firstId = first.Id;
        }

        await using (var db = NewDb())
        {
            Assert.Equal(WithdrawPocCommentResult.Ok,
                await new WithdrawPocCommentUseCase(db, new PocAcceptanceGate(db))
                    .ExecuteAsync(firstId, "user", canManage: false));

            // Tập còn một dòng ⇒ con trỏ phải là 1, không phải 2.
            Assert.Equal(1, (await db.Projects.SingleAsync()).PocFeedbackHarvestedCount);
            Assert.Equal("ghi chú thứ hai", (await db.PocComments.SingleAsync()).Comment);
        }
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
                new ProjectDocument { ProjectId = _projectId, FileName = "ProductBrief.docx", VersionName = "V1" },
                new ProjectDocument { ProjectId = _projectId, FileName = "ProductBrief.docx", VersionName = "V2" },
                // Bản draft chưa duyệt KHÔNG được tính — POC đang phục vụ dựng từ V2.
                new ProjectDocument { ProjectId = _projectId, FileName = "ProductBrief.docx", VersionName = "draft" });
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
    public async Task List_SkipsBriefNotes_AndWithdrawnRows_AndFlagsDispatchedOnes()
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
            var item = Assert.Single(items);
            Assert.Equal("ghi chú POC", item.Comment);
            // Chưa gửi đường nào ⇒ client cảnh báo "sẽ bị xoá hẳn" thay vì "vẫn còn trong lịch sử".
            Assert.False(item.WasDispatched);
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

    // Bảng ghi chú chỉ có ba trạng thái; chúng là phép chiếu của năm giá trị enum, và nút 🗑 chỉ hiện ở
    // dòng chưa tới tay agent nào: Draft, hoặc Waiting mà còn HOÃN chờ bản demo dựng lại. Ví dụ điển hình:
    // một lượt gửi có 1 ghi chú về BA + 2 ghi chú chỉnh trình bày ⇒ cả ba Waiting, hai ghi chú hoãn vẫn rút
    // được cho tới khi bản demo dựng lại, ghi chú về BA thì không (BA đã nhận nó).
    [Fact]
    public async Task List_ProjectsStatusesToDraftWaitingApply_AndOnlyLetsUndeliveredNotesBeWithdrawn()
    {
        await using (var db = NewDb())
        {
            var t = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            db.PocComments.AddRange(
                new PocComment { ProjectId = _projectId, Comment = "open", Status = PocCommentStatus.Open, CreatedByUsername = "user", CreatedAt = t.AddSeconds(1) },
                new PocComment { ProjectId = _projectId, Comment = "sent", Status = PocCommentStatus.Sent, Route = PocCommentRoute.FixPoc, CreatedByUsername = "user", CreatedAt = t.AddSeconds(2) },
                new PocComment { ProjectId = _projectId, Comment = "deferred", Status = PocCommentStatus.Deferred, Route = PocCommentRoute.FixPoc, CreatedByUsername = "user", CreatedAt = t.AddSeconds(3) },
                new PocComment { ProjectId = _projectId, Comment = "routed", Status = PocCommentStatus.RoutedToRequirement, Route = PocCommentRoute.Requirement, CreatedByUsername = "user", CreatedAt = t.AddSeconds(4) },
                new PocComment { ProjectId = _projectId, Comment = "addressed", Status = PocCommentStatus.Addressed, Route = PocCommentRoute.FixPoc, CreatedByUsername = "user", CreatedAt = t.AddSeconds(5) });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var items = (await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "user", canManage: false))
                .ToDictionary(x => x.Comment);

            Assert.Equal(PocNoteDisplayStatus.Draft, items["open"].DisplayStatus);
            Assert.Equal(PocNoteDisplayStatus.Waiting, items["sent"].DisplayStatus);
            Assert.Equal(PocNoteDisplayStatus.Waiting, items["deferred"].DisplayStatus);
            Assert.Equal(PocNoteDisplayStatus.Waiting, items["routed"].DisplayStatus);
            Assert.Equal(PocNoteDisplayStatus.Apply, items["addressed"].DisplayStatus);

            Assert.True(items["open"].CanWithdraw);
            Assert.True(items["deferred"].CanWithdraw);
            Assert.False(items["sent"].CanWithdraw);
            Assert.False(items["routed"].CanWithdraw);
            Assert.False(items["addressed"].CanWithdraw);

            // Use case thu hồi dùng CÙNG luật: ghi chú Dev đang sửa bị từ chối, ghi chú hoãn thì được.
            var withdraw = new WithdrawPocCommentUseCase(db, new PocAcceptanceGate(db));
            Assert.Equal(WithdrawPocCommentResult.AlreadyDispatched, await withdraw.ExecuteAsync(items["sent"].Id, "user", canManage: false));
            Assert.Equal(WithdrawPocCommentResult.Ok, await withdraw.ExecuteAsync(items["deferred"].Id, "user", canManage: false));
        }
    }

    // Bảng ở trang review thay cho cả tab Lịch sử, nên ghi chú trên Product Brief là một dòng của nó; trang
    // khách thì không (khách không thấy tài liệu nội bộ). Ghi chú Brief "Apply" khi bản nháp nó chê đã được
    // duyệt (version không còn là "draft"), và không bao giờ rút được.
    [Fact]
    public async Task List_IncludesBriefNotesOnlyWhenAsked_AndAppliesThemOnceTheDraftIsApproved()
    {
        await using (var db = NewDb())
        {
            db.PocComments.AddRange(
                new PocComment { ProjectId = _projectId, Target = PocCommentTarget.Brief, BriefVersion = "draft", Quote = "mục 2", Comment = "brief nháp", Status = PocCommentStatus.RoutedToRequirement, CreatedByUsername = "user", CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
                new PocComment { ProjectId = _projectId, Target = PocCommentTarget.Brief, BriefVersion = "V2", Comment = "brief đã duyệt", Status = PocCommentStatus.RoutedToRequirement, CreatedByUsername = "user", CreatedAt = DateTime.UtcNow.AddMinutes(-1) },
                new PocComment { ProjectId = _projectId, Comment = "poc", CreatedByUsername = "user" });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var guest = await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, null, canManage: false);
            Assert.Equal(new[] { "poc" }, guest.Select(x => x.Comment));

            var review = (await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "user", canManage: true, includeBriefNotes: true))
                .ToDictionary(x => x.Comment);
            Assert.Equal(3, review.Count);
            Assert.Equal("Brief", review["brief nháp"].Target);
            Assert.Equal("mục 2", review["brief nháp"].Quote);
            Assert.Equal(PocNoteDisplayStatus.Waiting, review["brief nháp"].DisplayStatus);
            Assert.Equal(PocNoteDisplayStatus.Apply, review["brief đã duyệt"].DisplayStatus);
            Assert.False(review["brief nháp"].CanWithdraw);
        }
    }

    // Popup "Add Note" ghi chú KHÔNG neo phần tử nào: chỉ có màn hình (hoặc "Chung" = rỗng) + nội dung.
    [Fact]
    public async Task Add_AcceptsAScreenOnlyNote_WithoutAnyElementAnchor()
    {
        await using var db = NewDb();
        var (result, item) = await NewAddUseCase(db).ExecuteAsync(
            _projectId, pageView: "", elementLabel: null, elementPath: null, 0, 0,
            comment: "Thiếu màn hình báo cáo tháng", createdByUsername: "user");

        Assert.Equal(AddPocCommentResult.Ok, result);
        Assert.Equal("", item!.PageView);
        Assert.Equal("", item.ElementPath);
        Assert.True(item.CanWithdraw);
    }

    private static AddPocCommentUseCase NewAddUseCase(AppDbContext db) =>
        new(db, new BriefVersionResolver(db, new ProjectArtifactCatalog()), new PocAcceptanceGate(db));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();
}
