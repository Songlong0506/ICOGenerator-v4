using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Ghi chú CHỈNH TRÌNH BÀY bị hoãn (Deferred) khi cùng lượt gửi có ghi chú về Requirement: vòng DỰNG MỚI
// bản demo kế tiếp đọc chúng vào prompt rồi đóng chúng như một lần sửa POC. Các chốt kiểm ở đây:
//   1. Chỉ ghi chú Deferred còn hiệu lực đi vào prompt — Open (chưa ai quyết), đã thu hồi thì không.
//   2. Khối prompt không mang selector: nó trỏ vào bản demo CŨ, bản mới dựng lại từ template.
//   3. Đóng vòng chỉ đụng ghi chú CÒN Deferred (người dùng có thể thu hồi trong lúc bản demo đang dựng),
//      và nối RevisionTaskId về vòng dựng để bảng lịch sử hiện nó như một "vòng Dev sửa".
//   4. Người dùng rút được một ghi chú đang hoãn — nó có thể nằm chờ lâu.
public class PocDeferredFeedbackServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public PocDeferredFeedbackServiceTests()
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
    public async Task Load_ReturnsOnlyLiveDeferredPocNotes_InPinOrder()
    {
        var second = AddComment("bảng canh lệch", PocCommentStatus.Deferred, offsetSeconds: 2);
        var first = AddComment("nút Lưu sai nhãn", PocCommentStatus.Deferred, offsetSeconds: 1);
        AddComment("chưa ai quyết", PocCommentStatus.Open, offsetSeconds: 3);
        AddComment("đã rút", PocCommentStatus.Deferred, offsetSeconds: 4, withdrawn: true);

        await using var db = NewDb();
        var loaded = await NewSut(db).LoadAsync(_projectId);

        Assert.Equal(new[] { first, second }, loaded.Select(c => c.Id));
    }

    [Fact]
    public void PromptBlock_CarriesScreenElementAndText_ButNotTheOldSelector()
    {
        var block = PocDeferredFeedbackService.BuildPromptBlock(new[]
        {
            new PocComment { PageView = "Danh sách đơn", ElementLabel = "Nút Lưu", ElementPath = "#old-save", Comment = "đổi nhãn thành Gửi duyệt" }
        });

        Assert.Contains("Danh sách đơn", block);
        Assert.Contains("Nút Lưu", block);
        Assert.Contains("đổi nhãn thành Gửi duyệt", block);
        Assert.Contains("THẮNG", block);
        Assert.DoesNotContain("#old-save", block);
    }

    [Fact]
    public void PromptBlock_IsEmpty_WhenNothingIsDeferred()
    {
        Assert.Equal(string.Empty, PocDeferredFeedbackService.BuildPromptBlock(Array.Empty<PocComment>()));
        Assert.Equal(string.Empty, PocDeferredFeedbackService.BuildPromptBlock(null));
    }

    [Fact]
    public async Task MarkSent_ClosesOnlyNotesStillDeferred_AndLinksThemToTheBuild()
    {
        var carried = AddComment("bảng canh lệch", PocCommentStatus.Deferred, offsetSeconds: 1);
        var withdrawnMeanwhile = AddComment("rút giữa chừng", PocCommentStatus.Deferred, offsetSeconds: 2, withdrawn: true);
        var buildTaskId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            var count = await NewSut(db).MarkSentAsync(new[] { carried, withdrawnMeanwhile }, buildTaskId);
            Assert.Equal(1, count);
        }

        await using var verify = NewDb();
        var sent = verify.PocComments.Single(c => c.Id == carried);
        Assert.Equal(PocCommentStatus.Sent, sent.Status);
        Assert.Equal(PocCommentRoute.FixPoc, sent.Route);
        Assert.Equal(buildTaskId, sent.RevisionTaskId);
        Assert.Equal(PocCommentStatus.Deferred, verify.PocComments.Single(c => c.Id == withdrawnMeanwhile).Status);
    }

    [Fact]
    public async Task Withdraw_AcceptsDeferredNote_AsSoftWithdrawal()
    {
        var id = AddComment("góp ý không còn đúng với bản mô tả mới", PocCommentStatus.Deferred, offsetSeconds: 1);

        await using (var db = NewDb())
        {
            Assert.Equal(WithdrawPocCommentResult.Ok,
                await new WithdrawPocCommentUseCase(db, new PocAcceptanceGate(db)).ExecuteAsync(id, "user", canManage: false));
        }

        await using var verify = NewDb();
        // Đã có Route ⇒ thu hồi mềm: dòng ở lại bảng lịch sử, và vòng dựng mới không còn mang nó.
        var kept = verify.PocComments.Single(c => c.Id == id);
        Assert.NotNull(kept.WithdrawnAtUtc);
        Assert.Empty(await NewSut(verify).LoadAsync(_projectId));
    }

    [Fact]
    public async Task History_ShowsTheBuildThatCarriedDeferredNotes_AsADevRound()
    {
        var buildTaskId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            var runId = Guid.NewGuid();
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = _projectId, CurrentStage = WorkflowStageKey.PocPreview });
            // Vòng DỰNG MỚI: không có RevisionFeedback như một vòng chỉnh sửa thường.
            db.AgentTasks.Add(new AgentTask
            {
                Id = buildTaskId, WorkflowRunId = runId, ProjectId = _projectId, Type = AgentTaskType.PocPreview,
                Status = AgentTaskStatus.Completed, Title = "Dựng POC", Input = "spec V2",
                Output = "đã đổi nhãn nút Lưu", FinishedAt = DateTime.UtcNow
            });
            db.PocComments.Add(new PocComment
            {
                ProjectId = _projectId, BriefVersion = "V1", Comment = "nút Lưu sai nhãn", CreatedByUsername = "user",
                Status = PocCommentStatus.Addressed, Route = PocCommentRoute.FixPoc, RevisionTaskId = buildTaskId,
                AddressedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var verify = NewDb();
        var version = Assert.Single(await new GetPocNoteHistoryQuery(verify).ExecuteAsync(_projectId));
        var round = Assert.Single(version.Rows, r => r.Kind == PocNoteHistoryKind.Revision);
        Assert.Equal("đã đổi nhãn nút Lưu", round.RepairLog);
    }

    private Guid AddComment(string text, PocCommentStatus status, int offsetSeconds, bool withdrawn = false)
    {
        using var db = NewDb();
        var comment = new PocComment
        {
            ProjectId = _projectId,
            BriefVersion = "V1",
            PageView = "Danh sách đơn",
            ElementLabel = "Nút Lưu",
            ElementPath = "#save",
            Comment = text,
            Status = status,
            Route = status == PocCommentStatus.Deferred ? PocCommentRoute.FixPoc : null,
            CreatedByUsername = "user",
            CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(offsetSeconds),
            WithdrawnAtUtc = withdrawn ? DateTime.UtcNow : null
        };
        db.PocComments.Add(comment);
        db.SaveChanges();
        return comment.Id;
    }

    private static PocDeferredFeedbackService NewSut(AppDbContext db) =>
        new(db, NullLogger<PocDeferredFeedbackService>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();
}
