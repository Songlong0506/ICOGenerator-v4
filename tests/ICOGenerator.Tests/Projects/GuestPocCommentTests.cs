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

// Góp ý của KHÁCH (người nhận link chia sẻ): ghi vào bản nháp riêng (GuestDraft) — chưa tới tay người tạo
// dự án, chưa vào đường gửi agent nào, xoá được — cho tới khi khách bấm Send Note. Mỗi link chỉ thấy/xoá/gửi
// góp ý của chính nó; người tạo dự án thấy mọi góp ý ĐÃ GỬI.
public class GuestPocCommentTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly PocShareAccess _alice;
    private readonly PocShareAccess _bob;

    public GuestPocCommentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();

        // Cùng tên người nhận: danh tính là LINK, không phải tên.
        _alice = new PocShareAccess(_projectId, "Lan", Guid.NewGuid());
        _bob = new PocShareAccess(_projectId, "Lan", Guid.NewGuid());
    }

    [Fact]
    public async Task GuestNote_IsDraft_HiddenFromOwner_AndOutOfDispatch()
    {
        var id = await AddGuestNote(_alice, "đổi nhãn nút");

        await using var db = NewDb();
        var saved = await db.PocComments.SingleAsync(c => c.Id == id);
        Assert.Equal(PocCommentStatus.GuestDraft, saved.Status);
        Assert.Equal(_alice.LinkId, saved.PocShareLinkId);

        // Người tạo dự án chưa thấy, và mọi đường gửi agent (lọc Status == Open) không chạm tới.
        var owner = await new ListPocCommentsQuery(db).ExecuteAsync(_projectId, "owner", canManage: true, includeBriefNotes: true);
        Assert.Empty(owner);
        Assert.False(await db.PocComments.AnyAsync(c => c.Status == PocCommentStatus.Open));

        var guest = Assert.Single(await new ListGuestPocCommentsQuery(db, new PocAcceptanceGate(db)).ExecuteAsync(_alice));
        Assert.Equal(PocNoteDisplayStatus.Draft, guest.DisplayStatus);
        Assert.True(guest.CanWithdraw);
    }

    [Fact]
    public async Task Guest_SeesOnlyOwnLinkNotes_EvenWithSameRecipientName()
    {
        await AddGuestNote(_alice, "của Alice");
        await AddGuestNote(_bob, "của Bob");
        await using (var db = NewDb())
        {
            db.PocComments.Add(new PocComment { ProjectId = _projectId, Comment = "nội bộ", CreatedByUsername = "owner" });
            await db.SaveChangesAsync();
        }

        await using var verify = NewDb();
        var list = await new ListGuestPocCommentsQuery(verify, new PocAcceptanceGate(verify)).ExecuteAsync(_alice);
        Assert.Equal("của Alice", Assert.Single(list).Comment);
    }

    [Fact]
    public async Task Send_MovesOnlyThisLinksDrafts_ToOwnerAsOpen()
    {
        await AddGuestNote(_alice, "a1");
        await AddGuestNote(_alice, "a2");
        await AddGuestNote(_bob, "b1");

        await using (var db = NewDb())
        {
            var (result, sent) = await new SubmitGuestPocCommentsUseCase(db, new PocAcceptanceGate(db)).ExecuteAsync(_alice);
            Assert.Equal(SubmitGuestPocCommentsResult.Ok, result);
            Assert.Equal(2, sent);
        }

        await using var verify = NewDb();
        var owner = await new ListPocCommentsQuery(verify).ExecuteAsync(_projectId, "owner", canManage: true);
        Assert.Equal(new[] { "a1", "a2" }, owner.Select(c => c.Comment));
        Assert.All(owner, c => Assert.Equal(PocNoteDisplayStatus.Draft, c.DisplayStatus));

        // Phía khách: đã gửi ⇒ Waiting và hết xoá được.
        var guest = await new ListGuestPocCommentsQuery(verify, new PocAcceptanceGate(verify)).ExecuteAsync(_alice);
        Assert.All(guest, c => Assert.Equal(PocNoteDisplayStatus.Waiting, c.DisplayStatus));
        Assert.All(guest, c => Assert.False(c.CanWithdraw));

        // Bản nháp của Bob vẫn là nháp.
        Assert.Equal(PocCommentStatus.GuestDraft, (await verify.PocComments.SingleAsync(c => c.Comment == "b1")).Status);

        var (again, _) = await new SubmitGuestPocCommentsUseCase(verify, new PocAcceptanceGate(verify)).ExecuteAsync(_alice);
        Assert.Equal(SubmitGuestPocCommentsResult.NothingToSend, again);
    }

    [Fact]
    public async Task Delete_OnlyOwnDraft_NotOtherLink_NotAfterSend()
    {
        var draft = await AddGuestNote(_alice, "gõ nhầm");
        var bobs = await AddGuestNote(_bob, "của Bob");

        await using (var db = NewDb())
        {
            var useCase = new DeleteGuestPocCommentUseCase(db, new PocAcceptanceGate(db));
            Assert.Equal(DeleteGuestPocCommentResult.NotFound, await useCase.ExecuteAsync(_alice, bobs));
            Assert.Equal(DeleteGuestPocCommentResult.Ok, await useCase.ExecuteAsync(_alice, draft));
        }

        var sentLater = await AddGuestNote(_alice, "sẽ gửi");
        await using (var db = NewDb())
            await new SubmitGuestPocCommentsUseCase(db, new PocAcceptanceGate(db)).ExecuteAsync(_alice);

        await using var verify = NewDb();
        Assert.Equal(DeleteGuestPocCommentResult.AlreadySent,
            await new DeleteGuestPocCommentUseCase(verify, new PocAcceptanceGate(verify)).ExecuteAsync(_alice, sentLater));
        Assert.False(await verify.PocComments.AnyAsync(c => c.Id == draft));
        Assert.True(await verify.PocComments.AnyAsync(c => c.Id == bobs));
    }

    [Fact]
    public async Task Delete_DraftBeforeHarvestCursor_StepsCursorBack()
    {
        var draft = await AddGuestNote(_alice, "nháp cũ");
        await using (var db = NewDb())
        {
            (await db.Projects.SingleAsync()).PocFeedbackHarvestedCount = 1;
            await db.SaveChangesAsync();
            await new DeleteGuestPocCommentUseCase(db, new PocAcceptanceGate(db)).ExecuteAsync(_alice, draft);
        }

        await using var verify = NewDb();
        Assert.Equal(0, (await verify.Projects.SingleAsync()).PocFeedbackHarvestedCount);
    }

    [Fact]
    public async Task AcceptedPoc_BlocksSendAndDelete()
    {
        var draft = await AddGuestNote(_alice, "nháp");
        await using (var db = NewDb())
        {
            (await db.Projects.SingleAsync()).PocAcceptedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var verify = NewDb();
        var gate = new PocAcceptanceGate(verify);
        Assert.Equal(SubmitGuestPocCommentsResult.PocAccepted, (await new SubmitGuestPocCommentsUseCase(verify, gate).ExecuteAsync(_alice)).Result);
        Assert.Equal(DeleteGuestPocCommentResult.PocAccepted, await new DeleteGuestPocCommentUseCase(verify, gate).ExecuteAsync(_alice, draft));
        Assert.False(Assert.Single(await new ListGuestPocCommentsQuery(verify, gate).ExecuteAsync(_alice)).CanWithdraw);
    }

    private async Task<Guid> AddGuestNote(PocShareAccess access, string text)
    {
        await using var db = NewDb();
        var (result, item) = await new AddPocCommentUseCase(db, new BriefVersionResolver(db, new ProjectArtifactCatalog()), new PocAcceptanceGate(db))
            .ExecuteAsync(_projectId, "Overview", "", "", 0, 0, text, $"Khách · {access.RecipientName}", shareLinkId: access.LinkId);
        Assert.Equal(AddPocCommentResult.Ok, result);
        return item!.Id;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();
}
